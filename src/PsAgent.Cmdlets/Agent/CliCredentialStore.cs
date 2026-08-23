using System.Text.Json;

namespace PsAgent.Cmdlets.Agent;

/// <summary>One credential found in another tool's store.</summary>
/// <param name="Provider">The provider name that tool filed it under (e.g. <c>anthropic</c>).</param>
/// <param name="Kind">
/// <c>api</c> for a plain API key, <c>oauth</c> for a token issued to that tool by a sign-in flow.
/// </param>
/// <param name="Key">The API key, when <see cref="Kind"/> is <c>api</c>. Always null for oauth.</param>
/// <param name="Store">Which store it came from, for reporting.</param>
public sealed record DiscoveredCredential(string Provider, string Kind, string? Key, string Store)
{
    /// <summary>Whether this credential is one ps-agent is willing to use.</summary>
    public bool Usable => Kind == "api" && !string.IsNullOrWhiteSpace(Key);
}

/// <summary>
/// Finds API keys the user has already stored with other CLI tools, so <c>Invoke-Agent</c> does not
/// demand a second copy of a key the machine already has.
/// </summary>
/// <remarks>
/// <para><b>API keys only, deliberately.</b> A credential store can hold two different things. An
/// <c>api</c> entry is a key the user was issued and owns; reusing it here spends their quota
/// exactly as they intended. An <c>oauth</c> entry is an access token a <i>different application</i>
/// obtained by signing the user in to a subscription — it is bound to that application's client
/// registration, and lifting it into another program is not a use those terms allow. Those entries
/// are surfaced by name so the resolver can explain why they were skipped, and their tokens are
/// never read.</para>
/// <para>Parsing is a pure function over the file's text, so the format handling is testable
/// without a credential file present.</para>
/// </remarks>
public static class CliCredentialStore
{
    /// <summary>Provider names whose API keys the Anthropic client can use directly.</summary>
    public static IReadOnlySet<string> AnthropicCompatibleProviders { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "anthropic" };

    /// <summary>
    /// Parse an opencode <c>auth.json</c>. Its shape is
    /// <c>{ "provider": { "type": "api", "key": "..." } }</c> for keys and
    /// <c>{ "provider": { "type": "oauth", "access": "...", "refresh": "...", … } }</c> for sign-ins.
    /// </summary>
    /// <remarks>Malformed or unexpected entries are skipped rather than thrown — a credential store
    /// this code does not own may gain fields at any time, and that is not a reason to fail.</remarks>
    public static IReadOnlyList<DiscoveredCredential> ParseOpenCodeAuth(string json, string store = "opencode")
    {
        List<DiscoveredCredential> found = [];
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return found;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return found;
            }

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var kind = entry.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? string.Empty
                    : string.Empty;

                // Only an `api` entry's secret is ever read; an oauth entry is recorded by name only.
                string? key = null;
                if (kind == "api" &&
                    entry.Value.TryGetProperty("key", out var k) &&
                    k.ValueKind == JsonValueKind.String)
                {
                    key = k.GetString();
                }

                found.Add(new DiscoveredCredential(entry.Name, kind, key, store));
            }
        }

        return found;
    }

    /// <summary>Where opencode keeps its credential file on this platform.</summary>
    public static string? OpenCodeAuthPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        // opencode uses the XDG data dir on POSIX and %USERPROFILE%\.local\share on Windows.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg;
        return Path.Combine(baseDir, "opencode", "auth.json");
    }

    /// <summary>Read every store this machine has. Never throws; an unreadable store contributes nothing.</summary>
    public static IReadOnlyList<DiscoveredCredential> DiscoverAll()
    {
        List<DiscoveredCredential> all = [];

        var openCode = OpenCodeAuthPath();
        if (openCode is not null && File.Exists(openCode))
        {
            try
            {
                all.AddRange(ParseOpenCodeAuth(File.ReadAllText(openCode)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A store we cannot read is a store we do not use.
            }
        }

        return all;
    }
}
