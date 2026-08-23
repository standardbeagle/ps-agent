namespace PsAgent.Cmdlets.Agent;

/// <summary>Where a credential came from, so the transcript can say.</summary>
public enum AgentCredentialSource
{
    /// <summary>Passed on the command line.</summary>
    Explicit,

    /// <summary>An environment variable.</summary>
    Environment,

    /// <summary>An API key another CLI had already stored.</summary>
    CliStore,

    /// <summary>A key in the workspace's <c>.env.local</c>.</summary>
    DotEnv,

    /// <summary>Left to the SDK: its own env lookup, then an `ant auth login` profile.</summary>
    SdkDefault,
}

/// <summary>What <c>Invoke-Agent</c> will authenticate with.</summary>
/// <param name="Source">Which step of the chain produced it.</param>
/// <param name="Detail">Human-readable provenance for the transcript (never the secret itself).</param>
/// <param name="ApiKey">The key, when one was found. Null means "let the SDK resolve".</param>
/// <param name="BaseUrl">An override endpoint, when one was given.</param>
public sealed record AgentCredential(
    AgentCredentialSource Source,
    string Detail,
    string? ApiKey = null,
    string? BaseUrl = null);

/// <summary>
/// Decides how <c>Invoke-Agent</c> authenticates, in a fixed order, and can say which step won.
/// </summary>
/// <remarks>
/// <para>The chain exists because "it says I am not authenticated" is otherwise unanswerable: an
/// unset <c>ANTHROPIC_API_KEY</c> does not mean unauthenticated (the SDK also honours
/// <c>ANTHROPIC_AUTH_TOKEN</c> and an <c>ant auth login</c> profile), and a machine can have a
/// perfectly good key sitting in another tool's credential store. Resolution reports its source so
/// the answer is visible rather than inferred.</para>
/// <para>Order: explicit parameter, environment, an API key discovered in a CLI store, then the
/// SDK's own resolution. The SDK step is last and always available, so this never blocks a user
/// whose credentials the discovery step does not understand.</para>
/// <para>Pure: environment access and store discovery are injected, so every branch is testable
/// without touching the machine's real credentials.</para>
/// </remarks>
public static class AgentCredentialResolver
{
    /// <summary>Environment variables consulted, in order.</summary>
    public static IReadOnlyList<string> EnvironmentVariables { get; } =
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"];

    /// <summary>
    /// Gateways that serve the Anthropic Messages API under a different host, so a key issued by
    /// that gateway is usable once <c>-BaseUrl</c> points at it.
    /// </summary>
    /// <remarks>
    /// <para>OpenRouter answers <c>POST /api/v1/messages</c> with a genuine Anthropic-shaped
    /// response — content blocks, <c>stop_reason</c>, <c>usage</c> — which is what makes this work
    /// at all rather than merely look like it should. A gateway that only speaks the OpenAI shape
    /// fails on the first call; that is why this is a short allow-list and not a guess from the
    /// host name.</para>
    /// <para><b>Pass the base URL without the version segment</b>: the SDK appends
    /// <c>/v1/messages</c> itself, so OpenRouter's is <c>https://openrouter.ai/api</c>. Passing
    /// <c>…/api/v1</c> builds <c>/api/v1/v1/messages</c> and returns an HTML 404 rather than a JSON
    /// error, which reads like an outage instead of a typo. Verified end to end against
    /// <c>https://openrouter.ai/api</c> with <c>anthropic/claude-sonnet-4.6</c>.</para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string[]> GatewayKeyNames { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["openrouter.ai"] = ["OPENROUTER_API_KEY", "OPEN_ROUTER_KEY"],
        };

    /// <summary>The gateway provider name for a base URL, or null when it is not a known gateway.</summary>
    public static string? GatewayFor(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return GatewayKeyNames.Keys.FirstOrDefault(host =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolve a credential. Never throws; the SDK-default step is the floor.</summary>
    /// <param name="explicitKey">A key passed on the command line.</param>
    /// <param name="explicitBaseUrl">A base URL passed on the command line.</param>
    /// <param name="env">Environment lookup. Defaults to the real environment.</param>
    /// <param name="discovered">Credentials found in CLI stores. Null means do not look.</param>
    public static AgentCredential Resolve(
        string? explicitKey = null,
        string? explicitBaseUrl = null,
        Func<string, string?>? env = null,
        IReadOnlyList<DiscoveredCredential>? discovered = null,
        IReadOnlyDictionary<string, string>? dotEnv = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var baseUrl = Blank(explicitBaseUrl) ? env("ANTHROPIC_BASE_URL") : explicitBaseUrl;
        baseUrl = Blank(baseUrl) ? null : baseUrl;
        var gateway = GatewayFor(baseUrl);

        if (!Blank(explicitKey))
        {
            return new AgentCredential(
                AgentCredentialSource.Explicit, "-ApiKey parameter", explicitKey, baseUrl);
        }

        foreach (var name in EnvironmentVariables)
        {
            if (!Blank(env(name)))
            {
                // Hand it back to the SDK rather than copying the value: the SDK reads these
                // itself, and passing the secret around adds a place for it to leak.
                return new AgentCredential(
                    AgentCredentialSource.Environment, $"{name} environment variable", null, baseUrl);
            }
        }

        // A gateway's own key, named the way that gateway names it, once -BaseUrl says to use it.
        if (gateway is not null)
        {
            foreach (var name in GatewayKeyNames[gateway])
            {
                if (!Blank(env(name)))
                {
                    return new AgentCredential(
                        AgentCredentialSource.Environment,
                        $"{name} environment variable (via {gateway})",
                        env(name),
                        baseUrl);
                }

                if (dotEnv is not null && dotEnv.TryGetValue(name, out var fromFile) && !Blank(fromFile))
                {
                    return new AgentCredential(
                        AgentCredentialSource.DotEnv,
                        $"{name} in {DotEnvFile.FileName} (via {gateway})",
                        fromFile,
                        baseUrl);
                }
            }
        }

        if (dotEnv is not null &&
            dotEnv.TryGetValue("ANTHROPIC_API_KEY", out var dotKey) && !Blank(dotKey))
        {
            return new AgentCredential(
                AgentCredentialSource.DotEnv,
                $"ANTHROPIC_API_KEY in {DotEnvFile.FileName}",
                dotKey,
                baseUrl);
        }

        if (discovered is { Count: > 0 })
        {
            // Without a -BaseUrl only a real Anthropic key can work; with one, the gateway's key does.
            var wanted = gateway is null
                ? CliCredentialStore.AnthropicCompatibleProviders
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { gateway.Split('.')[0] };

            var match = discovered.FirstOrDefault(c => c.Usable && wanted.Contains(c.Provider));
            if (match is not null)
            {
                return new AgentCredential(
                    AgentCredentialSource.CliStore,
                    $"{match.Provider} API key from the {match.Store} credential store",
                    match.Key,
                    baseUrl);
            }
        }

        return new AgentCredential(
            AgentCredentialSource.SdkDefault,
            "SDK resolution (environment, then an `ant auth login` profile)",
            null,
            baseUrl);
    }

    /// <summary>
    /// A sentence explaining what was available but unusable, for the error a failed call produces.
    /// Returns an empty string when there is nothing worth saying.
    /// </summary>
    /// <remarks>
    /// The common confusion this answers: a machine holding a dozen provider credentials, none of
    /// which this cmdlet can use, looks broken until you can see the list.
    /// </remarks>
    public static string DescribeUnused(IReadOnlyList<DiscoveredCredential> discovered)
    {
        if (discovered.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        var keys = discovered
            .Where(c => c.Usable && !CliCredentialStore.AnthropicCompatibleProviders.Contains(c.Provider))
            .Select(c => c.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count > 0)
        {
            parts.Add(
                $"Found API keys for {string.Join(", ", keys)}, which this cmdlet cannot use: it talks " +
                "to the Anthropic Messages API. Reach those providers with Invoke-Acp instead.");
        }

        var oauth = discovered
            .Where(c => c.Kind == "oauth")
            .Select(c => c.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (oauth.Count > 0)
        {
            parts.Add(
                $"Skipped {string.Join(", ", oauth)} sign-in tokens: those were issued to another " +
                "application, and are not ps-agent's to spend.");
        }

        return string.Join(" ", parts);
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}
