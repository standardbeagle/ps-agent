using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;

namespace PsAgent.Cmdlets.Agent.OAuth;

/// <summary>What came back on the loopback redirect.</summary>
/// <param name="Code">The authorization code, when the user consented.</param>
/// <param name="State">The state echoed by the provider, to be compared with the one sent.</param>
/// <param name="Error">The provider's error code, when it refused.</param>
/// <param name="ErrorDescription">Human-readable detail, when given.</param>
public sealed record OAuthCallback(string? Code, string? State, string? Error, string? ErrorDescription)
{
    /// <summary>Whether this callback carries a usable code.</summary>
    public bool Succeeded => Error is null && !string.IsNullOrEmpty(Code);
}

/// <summary>
/// The authorization-code + PKCE flow: build the URL, catch the redirect, exchange the code, and
/// refresh the token later.
/// </summary>
/// <remarks>
/// <para>The parts that decide anything — the authorize URL, the callback parse, the state check —
/// are pure static methods, so the protocol logic is testable without a browser, a listener, or a
/// provider. Only <see cref="RunAsync"/> and the token calls touch the network.</para>
/// <para>A loopback redirect is used rather than an out-of-band paste because it is what public
/// clients are expected to use (RFC 8252): the code never transits a clipboard, and the browser
/// hands it straight back to the process that asked.</para>
/// </remarks>
public static class OAuthFlow
{
    /// <summary>The style that needs neither a client id nor an application registration.</summary>
    public const string OpenRouterStyle = "openrouter";

    /// <summary>Build the authorize URL for a profile. Pure.</summary>
    public static string AuthorizeUrl(OAuthProfile profile, PkceChallenge pkce, string state, int port)
    {
        if (string.Equals(profile.Style, OpenRouterStyle, StringComparison.OrdinalIgnoreCase))
        {
            // OpenRouter names the redirect `callback_url`, takes no client_id and no
            // response_type, and carries no state parameter.
            var or = HttpUtility.ParseQueryString(string.Empty);
            or["callback_url"] = profile.RedirectUri(port);
            or["code_challenge"] = pkce.Challenge;
            or["code_challenge_method"] = PkceChallenge.Method;
            return profile.AuthorizeUrl + (profile.AuthorizeUrl.Contains('?') ? "&" : "?") + or;
        }

        var q = HttpUtility.ParseQueryString(string.Empty);
        q["response_type"] = "code";
        q["client_id"] = profile.ClientId;
        q["redirect_uri"] = profile.RedirectUri(port);
        q["code_challenge"] = pkce.Challenge;
        q["code_challenge_method"] = PkceChallenge.Method;
        q["state"] = state;

        if (profile.Scopes.Length > 0)
        {
            q["scope"] = string.Join(' ', profile.Scopes);
        }

        foreach (var (k, v) in profile.ExtraAuthorizeParams)
        {
            q[k] = v;
        }

        var separator = profile.AuthorizeUrl.Contains('?') ? "&" : "?";
        return profile.AuthorizeUrl + separator + q;
    }

    /// <summary>Parse the query string of the redirect. Pure.</summary>
    public static OAuthCallback ParseCallback(string queryOrUrl)
    {
        var query = queryOrUrl;
        var mark = queryOrUrl.IndexOf('?');
        if (mark >= 0)
        {
            query = queryOrUrl[(mark + 1)..];
        }

        var q = HttpUtility.ParseQueryString(query);
        return new OAuthCallback(q["code"], q["state"], q["error"], q["error_description"]);
    }

    /// <summary>
    /// Validate a callback against the state that was sent.
    /// </summary>
    /// <remarks>
    /// A mismatched <c>state</c> means the redirect did not originate from the request this process
    /// made, so the code is not ours to redeem. Rejecting it is the whole point of the parameter.
    /// </remarks>
    public static void EnsureValid(OAuthCallback callback, string expectedState, bool checkState = true)
    {
        if (callback.Error is not null)
        {
            var detail = callback.ErrorDescription is null ? string.Empty : $": {callback.ErrorDescription}";
            throw new InvalidOperationException($"authorization failed ({callback.Error}){detail}");
        }

        if (string.IsNullOrEmpty(callback.Code))
        {
            throw new InvalidOperationException("authorization returned no code");
        }

        // A provider that carries no state parameter cannot be checked for one. The loopback
        // listener is still single-use and bound to this process, so the code has nowhere else to
        // arrive; PKCE remains what stops a stolen code being redeemed.
        if (checkState && !string.Equals(callback.State, expectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "authorization state did not match the request; the redirect was not ours and the code was discarded");
        }
    }

    /// <summary>The form body for redeeming a code. Pure, so the parameter set is testable.</summary>
    public static IReadOnlyDictionary<string, string> CodeExchangeBody(
        OAuthProfile profile, string code, string verifier, int port) =>
        new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = profile.ClientId,
            ["code"] = code,
            ["redirect_uri"] = profile.RedirectUri(port),
            ["code_verifier"] = verifier,
        };

    /// <summary>The form body for a refresh. Pure.</summary>
    public static IReadOnlyDictionary<string, string> RefreshBody(OAuthProfile profile, string refreshToken) =>
        new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = profile.ClientId,
            ["refresh_token"] = refreshToken,
        };

    /// <summary>
    /// Redeem an OpenRouter authorization code. Its exchange is JSON (not form-encoded) and returns
    /// <c>{ "key": ... }</c> — a user-controlled API key with no expiry and nothing to refresh.
    /// </summary>
    public static async Task<OAuthTokenSet> ExchangeOpenRouterAsync(
        HttpClient http, OAuthProfile profile, string code, string verifier, CancellationToken ct = default)
    {
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["code_challenge_method"] = PkceChallenge.Method,
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(profile.TokenUrl, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"key exchange returned {(int)response.StatusCode}: {Truncate(body, 400)}");
        }

        return ParseOpenRouterKey(body, profile.Name);
    }

    /// <summary>Parse an OpenRouter key exchange response. Pure, so the shape is testable.</summary>
    public static OAuthTokenSet ParseOpenRouterKey(string body, string profileName)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(body)?.AsObject()
            ?? throw new InvalidOperationException("key exchange response was not a JSON object");

        var key = root["key"]?.GetValue<string>();
        if (string.IsNullOrEmpty(key))
        {
            var error = root["error"]?.ToJsonString() ?? Truncate(body, 200);
            throw new InvalidOperationException($"key exchange returned no key: {error}");
        }

        // No expiry and no refresh token: this is a durable key, so NeedsRefresh stays false and
        // nothing ever tries to renew it.
        return new OAuthTokenSet { AccessToken = key, RefreshToken = null, ExpiresAtUnix = 0, Profile = profileName };
    }

    /// <summary>Post a form to the token endpoint and build a token set from the reply.</summary>
    public static async Task<OAuthTokenSet> PostTokenAsync(
        HttpClient http,
        OAuthProfile profile,
        IReadOnlyDictionary<string, string> form,
        DateTimeOffset now,
        string? previousRefresh = null,
        CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(profile.TokenUrl, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The body usually carries the real reason; the status alone rarely does.
            throw new InvalidOperationException(
                $"token endpoint returned {(int)response.StatusCode}: {Truncate(body, 400)}");
        }

        return OAuthTokenSet.FromTokenResponse(body, profile.Name, now, previousRefresh);
    }

    /// <summary>Exchange a refresh token for a fresh access token.</summary>
    public static Task<OAuthTokenSet> RefreshAsync(
        HttpClient http, OAuthProfile profile, OAuthTokenSet current, DateTimeOffset now, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(current.RefreshToken))
        {
            throw new InvalidOperationException(
                $"the stored {profile.Name} login has no refresh token; run Connect-Agent again");
        }

        return PostTokenAsync(
            http, profile, RefreshBody(profile, current.RefreshToken), now, current.RefreshToken, ct);
    }

    /// <summary>
    /// Run the whole browser flow: listen on loopback, open the browser, wait for the redirect,
    /// exchange the code.
    /// </summary>
    /// <param name="openBrowser">
    /// How to show the URL. Injected so a headless caller can print it instead of launching one.
    /// </param>
    public static async Task<OAuthTokenSet> RunAsync(
        OAuthProfile profile,
        HttpClient http,
        Action<string> openBrowser,
        Func<DateTimeOffset> clock,
        CancellationToken ct = default)
    {
        var pkce = PkceChallenge.Create();
        var state = PkceChallenge.NewState();

        using var listener = new LoopbackListener(profile.RedirectPort, profile.RedirectPath);
        listener.Start();

        var url = AuthorizeUrl(profile, pkce, state, listener.Port);
        openBrowser(url);

        var isOpenRouter = string.Equals(profile.Style, OpenRouterStyle, StringComparison.OrdinalIgnoreCase);

        var callback = await listener.WaitForCallbackAsync(ct).ConfigureAwait(false);
        EnsureValid(callback, state, checkState: !isOpenRouter);

        if (isOpenRouter)
        {
            return await ExchangeOpenRouterAsync(http, profile, callback.Code!, pkce.Verifier, ct)
                .ConfigureAwait(false);
        }

        return await PostTokenAsync(
            http,
            profile,
            CodeExchangeBody(profile, callback.Code!, pkce.Verifier, listener.Port),
            clock(),
            previousRefresh: null,
            ct).ConfigureAwait(false);
    }

    /// <summary>Hand a URL to the platform's default browser.</summary>
    public static void OpenInBrowser(string url)
    {
        try
        {
            // UseShellExecute is what makes the OS pick the default handler.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // No browser is not fatal: the caller prints the URL as well, so the flow can be
            // completed by hand.
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// A one-shot HTTP listener on loopback that captures the OAuth redirect and shows the user a
/// page telling them to go back to the terminal.
/// </summary>
internal sealed class LoopbackListener : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _path;

    /// <summary>Bind to a port, or 0 to let the OS choose.</summary>
    public LoopbackListener(int port, string path)
    {
        _path = path.StartsWith('/') ? path : "/" + path;
        Port = port > 0 ? port : FreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
    }

    /// <summary>The port actually bound.</summary>
    public int Port { get; }

    /// <summary>Begin listening.</summary>
    public void Start() => _listener.Start();

    /// <summary>
    /// Wait for the redirect. Requests to any other path are answered 404 and ignored, because
    /// browsers ask for <c>/favicon.ico</c> and that must not be mistaken for the callback.
    /// </summary>
    public async Task<OAuthCallback> WaitForCallbackAsync(CancellationToken ct)
    {
        using var registration = ct.Register(() =>
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
        });

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("the OAuth listener closed before the redirect arrived");
            }

            var request = context.Request;
            if (!string.Equals(request.Url?.AbsolutePath, _path, StringComparison.Ordinal))
            {
                Respond(context, 404, "text/plain", "not the OAuth callback");
                continue;
            }

            var callback = OAuthFlow.ParseCallback(request.Url?.Query ?? string.Empty);
            Respond(context, callback.Succeeded ? 200 : 400, "text/html", ResultPage(callback));
            return callback;
        }
    }

    private static void Respond(HttpListenerContext context, int status, string contentType, string body)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
            context.Response.OutputStream.Close();
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The browser hung up; the code is already in hand.
        }
    }

    private static string ResultPage(OAuthCallback callback) =>
        callback.Succeeded
            ? """
              <!doctype html><meta charset="utf-8"><title>ps-agent</title>
              <body style="font:16px system-ui;padding:3rem;max-width:32rem">
              <h1 style="font-size:1.2rem">Signed in</h1>
              <p>ps-agent has the token. You can close this tab and go back to the terminal.</p>
              """
            : $"""
              <!doctype html><meta charset="utf-8"><title>ps-agent</title>
              <body style="font:16px system-ui;padding:3rem;max-width:32rem">
              <h1 style="font-size:1.2rem">Sign-in failed</h1>
              <p>{WebUtility.HtmlEncode(callback.Error)}: {WebUtility.HtmlEncode(callback.ErrorDescription)}</p>
              <p>Go back to the terminal for the error.</p>
              """;

    private static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }
    }
}
