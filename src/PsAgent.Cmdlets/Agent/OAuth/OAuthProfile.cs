using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsAgent.Cmdlets.Agent.OAuth;

/// <summary>
/// Everything ps-agent needs to run an authorization-code flow against one provider.
/// </summary>
/// <remarks>
/// <para>This is <b>configuration, not code</b>, and deliberately so. A public OAuth client is
/// identified by a <c>ClientId</c> that the provider issues to a named application; ps-agent is not
/// registered with Anthropic or OpenAI, and hard-coding some other product's client id would make
/// every request claim to be that product. So the id is supplied by whoever runs the flow — your
/// own registered application, or one you are entitled to act as — and ps-agent ships the
/// mechanism rather than an identity.</para>
/// <para>Profiles live in <c>~/.config/ps-agent/oauth/&lt;name&gt;.json</c>. See
/// <see cref="ExampleJson"/> for the shape.</para>
/// </remarks>
public sealed record OAuthProfile
{
    /// <summary>Profile name, used for the token file and <c>-Provider</c>.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Which dialect of the flow this provider speaks.
    /// </summary>
    /// <remarks>
    /// <c>oauth2</c> is the RFC 6749 authorization-code flow. <c>openrouter</c> is OpenRouter's
    /// variant: no client id at all, the redirect named <c>callback_url</c>, and an exchange that
    /// returns a user-controlled API <c>key</c> rather than an expiring access token. The variant
    /// exists because it is the one flow a third party can complete today without being issued an
    /// identity first.
    /// </remarks>
    [JsonPropertyName("style")]
    public string Style { get; init; } = "oauth2";

    /// <summary>Which transport the resulting credential is good for: <c>openai</c> or <c>anthropic</c>.</summary>
    [JsonPropertyName("api")]
    public string Api { get; init; } = "openai";

    /// <summary>Where the browser is sent to obtain consent.</summary>
    [JsonPropertyName("authorizeUrl")]
    public required string AuthorizeUrl { get; init; }

    /// <summary>Where the code is exchanged, and refreshes are made.</summary>
    [JsonPropertyName("tokenUrl")]
    public required string TokenUrl { get; init; }

    /// <summary>
    /// The client identifier the provider issued for the application running this flow. Empty for
    /// styles that do not use one.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Scopes to request, space-joined into the authorize URL.</summary>
    [JsonPropertyName("scopes")]
    public string[] Scopes { get; init; } = [];

    /// <summary>
    /// Loopback port for the redirect. 0 asks the OS for a free one — only usable when the
    /// provider allows a wildcard port, which most do not, so profiles usually pin it.
    /// </summary>
    [JsonPropertyName("redirectPort")]
    public int RedirectPort { get; init; }

    /// <summary>Path component of the redirect URI.</summary>
    [JsonPropertyName("redirectPath")]
    public string RedirectPath { get; init; } = "/callback";

    /// <summary>Extra query parameters some providers require on the authorize request.</summary>
    [JsonPropertyName("extraAuthorizeParams")]
    public Dictionary<string, string> ExtraAuthorizeParams { get; init; } = [];

    /// <summary>
    /// The API endpoint this credential is good for, so a successful login also configures where
    /// to send requests.
    /// </summary>
    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Headers the API requires when authenticating with a bearer token rather than an API key.
    /// Anthropic's OAuth path needs <c>anthropic-beta: oauth-2025-04-20</c>.
    /// </summary>
    [JsonPropertyName("extraHeaders")]
    public Dictionary<string, string> ExtraHeaders { get; init; } = [];

    /// <summary>The redirect URI, assembled from the port and path.</summary>
    public string RedirectUri(int actualPort) =>
        $"http://localhost:{actualPort}{(RedirectPath.StartsWith('/') ? RedirectPath : "/" + RedirectPath)}";

    /// <summary>
    /// Providers that need no setup at all, available to <c>-Provider</c> without a profile file.
    /// </summary>
    /// <remarks>
    /// OpenRouter is here because its PKCE flow requires no client id and no application
    /// registration: the exchange hands back a user-controlled API key. That makes it the one
    /// browser sign-in ps-agent can offer out of the box.
    /// </remarks>
    public static IReadOnlyDictionary<string, OAuthProfile> BuiltIn { get; } =
        new Dictionary<string, OAuthProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["openrouter"] = new()
            {
                Name = "openrouter",
                Style = "openrouter",
                Api = "openai",
                AuthorizeUrl = "https://openrouter.ai/auth",
                TokenUrl = "https://openrouter.ai/api/v1/auth/keys",
                BaseUrl = "https://openrouter.ai/api/v1",
                RedirectPort = 8787,
                RedirectPath = "/callback",
            },
        };

    /// <summary>A worked example, printed by <c>Connect-Agent -ShowExample</c>.</summary>
    public static string ExampleJson => """
        {
          "name": "my-provider",
          "authorizeUrl": "https://auth.example.com/oauth/authorize",
          "tokenUrl": "https://auth.example.com/oauth/token",
          "clientId": "<the client id the provider issued to your application>",
          "scopes": ["openid", "profile", "offline_access"],
          "redirectPort": 1455,
          "redirectPath": "/auth/callback",
          "extraAuthorizeParams": {},
          "baseUrl": "https://api.example.com",
          "extraHeaders": {}
        }
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Parse a profile. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static OAuthProfile Parse(string json) =>
        JsonSerializer.Deserialize<OAuthProfile>(json, Json)
        ?? throw new JsonException("empty OAuth profile");

    /// <summary>Serialize, for writing a profile back out.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Where profiles are kept.</summary>
    public static string ProfileDirectory()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(baseDir, "ps-agent", "oauth");
    }

    /// <summary>
    /// Load a named profile: a file on disk first, then a built-in. A file wins so a built-in can
    /// be overridden without renaming it.
    /// </summary>
    public static OAuthProfile? Load(string name)
    {
        var path = Path.Combine(ProfileDirectory(), name + ".json");
        try
        {
            if (File.Exists(path))
            {
                return Parse(File.ReadAllText(path));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fall through to the built-ins.
        }

        return BuiltIn.GetValueOrDefault(name);
    }

    /// <summary>The suffix of a token file, which shares the profile directory.</summary>
    public const string TokenSuffix = ".token.json";

    /// <summary>
    /// Names of every profile on disk.
    /// </summary>
    /// <remarks>
    /// Token files live in the same directory, so a plain <c>*.json</c> enumeration invents a
    /// phantom profile per sign-in: <c>stub.token.json</c> reads as a profile named
    /// <c>stub.token</c>, which then shows up in listings as a second, never-signed-in provider.
    /// </remarks>
    public static IReadOnlyList<string> ListNames()
    {
        try
        {
            var dir = ProfileDirectory();
            return Directory.Exists(dir)
                ? [.. Directory.EnumerateFiles(dir, "*.json")
                        .Where(f => !f.EndsWith(TokenSuffix, StringComparison.OrdinalIgnoreCase))
                        .Select(Path.GetFileNameWithoutExtension)
                        .OfType<string>()
                        .Concat(BuiltIn.Keys)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)]
                : [.. BuiltIn.Keys.Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
