using PsAgent.Cmdlets.Agent;
using Xunit;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// Credential resolution: the order of the chain, and the line about whose credentials ps-agent is
/// willing to spend.
/// </summary>
public sealed class CredentialResolutionTests
{
    private static Func<string, string?> NoEnv => _ => null;

    private static Func<string, string?> Env(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void An_explicit_key_wins_over_everything()
    {
        var c = AgentCredentialResolver.Resolve(
            "sk-explicit",
            explicitBaseUrl: null,
            env: Env(("ANTHROPIC_API_KEY", "sk-env")),
            discovered: [new DiscoveredCredential("anthropic", "api", "sk-store", "opencode")]);

        Assert.Equal(AgentCredentialSource.Explicit, c.Source);
        Assert.Equal("sk-explicit", c.ApiKey);
    }

    /// <summary>
    /// An env var resolves to a credential with no key attached: the SDK reads those itself, and
    /// copying the secret out only adds somewhere for it to leak.
    /// </summary>
    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    public void An_environment_variable_defers_to_the_sdk_without_copying_the_secret(string name)
    {
        var c = AgentCredentialResolver.Resolve(null, null, Env((name, "secret-value")));

        Assert.Equal(AgentCredentialSource.Environment, c.Source);
        Assert.Null(c.ApiKey);
        Assert.Contains(name, c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void With_nothing_available_it_falls_through_to_the_sdk()
    {
        var c = AgentCredentialResolver.Resolve(null, null, NoEnv);

        Assert.Equal(AgentCredentialSource.SdkDefault, c.Source);
        Assert.Contains("ant auth login", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_anthropic_key_in_a_cli_store_is_used()
    {
        var c = AgentCredentialResolver.Resolve(
            null, null, NoEnv,
            discovered: [new DiscoveredCredential("anthropic", "api", "sk-store", "opencode")]);

        Assert.Equal(AgentCredentialSource.CliStore, c.Source);
        Assert.Equal("sk-store", c.ApiKey);
    }

    /// <summary>
    /// Without a base URL, a key for some other provider cannot work — the client speaks to
    /// Anthropic. Using it would produce a confusing 401 instead of a clear "nothing usable".
    /// </summary>
    [Fact]
    public void A_non_anthropic_key_is_not_used_without_a_gateway()
    {
        var c = AgentCredentialResolver.Resolve(
            null, null, NoEnv,
            discovered: [new DiscoveredCredential("deepseek", "api", "sk-deepseek", "opencode")]);

        Assert.Equal(AgentCredentialSource.SdkDefault, c.Source);
    }

    [Fact]
    public void A_gateway_base_url_makes_that_gateways_key_usable()
    {
        var c = AgentCredentialResolver.Resolve(
            null,
            "https://openrouter.ai/api/v1",
            NoEnv,
            discovered: [new DiscoveredCredential("openrouter", "api", "sk-or", "opencode")]);

        Assert.Equal(AgentCredentialSource.CliStore, c.Source);
        Assert.Equal("sk-or", c.ApiKey);
        Assert.Equal("https://openrouter.ai/api/v1", c.BaseUrl);
    }

    [Fact]
    public void A_gateway_key_in_dot_env_is_found_under_either_spelling()
    {
        foreach (var name in (string[])["OPENROUTER_API_KEY", "OPEN_ROUTER_KEY"])
        {
            var c = AgentCredentialResolver.Resolve(
                null, "https://openrouter.ai/api/v1", NoEnv,
                discovered: null,
                dotEnv: new Dictionary<string, string> { [name] = "sk-or-file" });

            Assert.Equal(AgentCredentialSource.DotEnv, c.Source);
            Assert.Equal("sk-or-file", c.ApiKey);
        }
    }

    [Fact]
    public void The_base_url_can_come_from_the_environment()
    {
        var c = AgentCredentialResolver.Resolve(
            "k", null, Env(("ANTHROPIC_BASE_URL", "https://proxy.internal/v1")));

        Assert.Equal("https://proxy.internal/v1", c.BaseUrl);
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", "openrouter.ai")]
    [InlineData("https://api.anthropic.com", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void Gateways_are_recognised_from_an_allow_list_not_guessed(string? url, string? expected)
    {
        Assert.Equal(expected, AgentCredentialResolver.GatewayFor(url));
    }

    // ---------------------------------------------------------------- store parsing

    private const string OpenCodeAuth = """
        {
          "anthropic":  { "type": "api",   "key": "sk-ant-xyz" },
          "deepseek":   { "type": "api",   "key": "sk-ds-xyz"  },
          "openai":     { "type": "oauth", "access": "tok", "refresh": "r", "expires": 1, "accountId": "a" },
          "broken":     "not-an-object"
        }
        """;

    [Fact]
    public void The_store_parser_reads_api_keys()
    {
        var found = CliCredentialStore.ParseOpenCodeAuth(OpenCodeAuth);

        var anthropic = Assert.Single(found.Where(c => c.Provider == "anthropic"));
        Assert.True(anthropic.Usable);
        Assert.Equal("sk-ant-xyz", anthropic.Key);
    }

    /// <summary>
    /// The line that matters: an oauth entry is a token issued to another application. It is listed
    /// so the resolver can explain the skip, and its secret is never read.
    /// </summary>
    [Fact]
    public void An_oauth_entry_is_recorded_but_its_token_is_never_read()
    {
        var found = CliCredentialStore.ParseOpenCodeAuth(OpenCodeAuth);

        var openai = Assert.Single(found.Where(c => c.Provider == "openai"));
        Assert.Equal("oauth", openai.Kind);
        Assert.Null(openai.Key);
        Assert.False(openai.Usable);
    }

    /// <summary>
    /// An entry whose value is not an object is not a credential at all, so it is dropped rather
    /// than listed as unusable — listing it would put noise in the "here is what I could not use"
    /// message that the user cannot act on.
    /// </summary>
    [Fact]
    public void A_malformed_entry_is_skipped_not_thrown()
    {
        var found = CliCredentialStore.ParseOpenCodeAuth(OpenCodeAuth);

        Assert.DoesNotContain(found, c => c.Provider == "broken");
        Assert.Equal(["anthropic", "deepseek", "openai"], found.Select(c => c.Provider));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    public void Unreadable_store_content_yields_nothing(string json)
    {
        Assert.Empty(CliCredentialStore.ParseOpenCodeAuth(json));
    }

    [Fact]
    public void The_unused_summary_names_what_it_could_not_use_and_why()
    {
        var summary = AgentCredentialResolver.DescribeUnused(
            CliCredentialStore.ParseOpenCodeAuth(OpenCodeAuth));

        Assert.Contains("deepseek", summary, StringComparison.Ordinal);
        Assert.Contains("Invoke-Acp", summary, StringComparison.Ordinal);
        Assert.Contains("openai", summary, StringComparison.Ordinal);
        Assert.Contains("another application", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unused_summary_is_empty_when_there_is_nothing_to_report()
    {
        Assert.Equal(string.Empty, AgentCredentialResolver.DescribeUnused([]));
    }

    // ---------------------------------------------------------------- .env parsing

    [Fact]
    public void Dot_env_parsing_handles_the_shapes_people_actually_write()
    {
        var map = DotEnvFile.Parse("""
            # a comment
            OPEN_ROUTER_KEY=sk-plain

            export QUOTED="sk-quoted"
            SINGLE='sk-single'
            SPACED = sk-spaced
            NO_EQUALS_LINE
            """);

        Assert.Equal("sk-plain", map["OPEN_ROUTER_KEY"]);
        Assert.Equal("sk-quoted", map["QUOTED"]);
        Assert.Equal("sk-single", map["SINGLE"]);
        Assert.Equal("sk-spaced", map["SPACED"]);
        Assert.False(map.ContainsKey("NO_EQUALS_LINE"));
    }

    [Fact]
    public void A_value_containing_an_equals_sign_survives()
    {
        Assert.Equal("a=b=c", DotEnvFile.Parse("K=a=b=c")["K"]);
    }

    [Fact]
    public void Reading_a_workspace_without_the_file_is_empty_not_an_error()
    {
        Assert.Empty(DotEnvFile.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))));
    }
}
