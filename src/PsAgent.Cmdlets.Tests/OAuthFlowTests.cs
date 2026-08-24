using System.Net.Http;
using System.Text.Json;
using System.Web;
using PsAgent.Cmdlets.Agent;
using PsAgent.Cmdlets.Agent.OAuth;
using Xunit;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// The OAuth protocol logic — PKCE, the authorize URL, the callback and its state check, token
/// parsing, expiry and refresh. All of it is pure, so none of it needs a browser, a listener or a
/// provider.
/// </summary>
public sealed class OAuthFlowTests
{
    private static OAuthProfile Profile => new()
    {
        Name = "test",
        AuthorizeUrl = "https://auth.example.com/oauth/authorize",
        TokenUrl = "https://auth.example.com/oauth/token",
        ClientId = "client-123",
        Scopes = ["openid", "offline_access"],
        RedirectPort = 1455,
        RedirectPath = "/auth/callback",
        BaseUrl = "https://api.example.com",
        ExtraHeaders = new Dictionary<string, string> { ["x-beta"] = "oauth-2025-04-20" },
    };

    // ---------------------------------------------------------------- PKCE

    /// <summary>
    /// RFC 7636 requires base64url with no padding. A `+`, `/` or `=` is rejected by the server
    /// with a bare "invalid grant", which is a long way from the cause.
    /// </summary>
    [Fact]
    public void Pkce_values_are_base64url_without_padding()
    {
        var pkce = PkceChallenge.Create();

        foreach (var value in (string[])[pkce.Verifier, pkce.Challenge])
        {
            Assert.DoesNotContain('+', value);
            Assert.DoesNotContain('/', value);
            Assert.DoesNotContain('=', value);
        }
    }

    [Fact]
    public void A_verifier_is_at_least_the_rfc_minimum_length()
    {
        Assert.InRange(PkceChallenge.Create().Verifier.Length, 43, 128);
    }

    /// <summary>The challenge is the S256 of the verifier — checked against a published test vector.</summary>
    [Fact]
    public void The_challenge_matches_the_rfc_7636_appendix_b_vector()
    {
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            PkceChallenge.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Each_generation_is_unique()
    {
        var values = Enumerable.Range(0, 20).Select(_ => PkceChallenge.Create().Verifier).ToHashSet();
        Assert.Equal(20, values.Count);
    }

    [Fact]
    public void State_values_are_unique_and_url_safe()
    {
        var states = Enumerable.Range(0, 20).Select(_ => PkceChallenge.NewState()).ToHashSet();

        Assert.Equal(20, states.Count);
        Assert.All(states, s => Assert.DoesNotContain('=', s));
    }

    // ---------------------------------------------------------------- authorize URL

    [Fact]
    public void The_authorize_url_carries_every_required_parameter()
    {
        var pkce = PkceChallenge.Create();
        var url = OAuthFlow.AuthorizeUrl(Profile, pkce, "state-abc", 1455);

        var q = HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("client-123", q["client_id"]);
        Assert.Equal("http://localhost:1455/auth/callback", q["redirect_uri"]);
        Assert.Equal(pkce.Challenge, q["code_challenge"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("state-abc", q["state"]);
        Assert.Equal("openid offline_access", q["scope"]);
    }

    /// <summary>The verifier is the secret half — sending it to the authorize endpoint defeats PKCE.</summary>
    [Fact]
    public void The_authorize_url_never_carries_the_verifier()
    {
        var pkce = PkceChallenge.Create();

        Assert.DoesNotContain(pkce.Verifier, OAuthFlow.AuthorizeUrl(Profile, pkce, "s", 1455));
    }

    [Fact]
    public void Extra_authorize_parameters_are_included()
    {
        var profile = Profile with
        {
            ExtraAuthorizeParams = new Dictionary<string, string> { ["prompt"] = "login" },
        };

        var q = HttpUtility.ParseQueryString(
            new Uri(OAuthFlow.AuthorizeUrl(profile, PkceChallenge.Create(), "s", 1)).Query);

        Assert.Equal("login", q["prompt"]);
    }

    [Fact]
    public void An_authorize_url_that_already_has_a_query_gets_an_ampersand()
    {
        var profile = Profile with { AuthorizeUrl = "https://auth.example.com/authorize?tenant=acme" };

        var q = HttpUtility.ParseQueryString(
            new Uri(OAuthFlow.AuthorizeUrl(profile, PkceChallenge.Create(), "s", 1)).Query);

        Assert.Equal("acme", q["tenant"]);
        Assert.Equal("code", q["response_type"]);
    }

    // ---------------------------------------------------------------- callback

    [Fact]
    public void A_successful_callback_parses()
    {
        var cb = OAuthFlow.ParseCallback("?code=abc123&state=xyz");

        Assert.True(cb.Succeeded);
        Assert.Equal("abc123", cb.Code);
        Assert.Equal("xyz", cb.State);
    }

    [Fact]
    public void A_denied_callback_carries_the_providers_error()
    {
        var cb = OAuthFlow.ParseCallback("?error=access_denied&error_description=User%20said%20no");

        Assert.False(cb.Succeeded);
        Assert.Equal("access_denied", cb.Error);
        Assert.Equal("User said no", cb.ErrorDescription);
    }

    [Fact]
    public void A_full_url_parses_as_well_as_a_bare_query()
    {
        Assert.Equal("abc", OAuthFlow.ParseCallback("http://localhost:1455/auth/callback?code=abc&state=s").Code);
    }

    /// <summary>
    /// The state check is the entire point of the parameter: a redirect that did not originate
    /// from this process's request carries a code that is not ours to redeem.
    /// </summary>
    [Fact]
    public void A_mismatched_state_is_rejected()
    {
        var cb = OAuthFlow.ParseCallback("?code=abc&state=forged");

        var ex = Assert.Throws<InvalidOperationException>(() => OAuthFlow.EnsureValid(cb, "expected"));
        Assert.Contains("state did not match", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_matching_state_passes()
    {
        OAuthFlow.EnsureValid(OAuthFlow.ParseCallback("?code=abc&state=ok"), "ok");
    }

    [Fact]
    public void A_provider_error_is_raised_before_the_state_check()
    {
        var cb = OAuthFlow.ParseCallback("?error=access_denied&state=whatever");

        var ex = Assert.Throws<InvalidOperationException>(() => OAuthFlow.EnsureValid(cb, "expected"));
        Assert.Contains("access_denied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_callback_with_no_code_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => OAuthFlow.EnsureValid(OAuthFlow.ParseCallback("?state=s"), "s"));
    }

    // ---------------------------------------------------------------- token bodies

    [Fact]
    public void The_code_exchange_sends_the_verifier_and_matching_redirect()
    {
        var body = OAuthFlow.CodeExchangeBody(Profile, "the-code", "the-verifier", 1455);

        Assert.Equal("authorization_code", body["grant_type"]);
        Assert.Equal("client-123", body["client_id"]);
        Assert.Equal("the-code", body["code"]);
        Assert.Equal("the-verifier", body["code_verifier"]);
        Assert.Equal("http://localhost:1455/auth/callback", body["redirect_uri"]);
    }

    [Fact]
    public void The_refresh_body_is_a_refresh_grant()
    {
        var body = OAuthFlow.RefreshBody(Profile, "refresh-abc");

        Assert.Equal("refresh_token", body["grant_type"]);
        Assert.Equal("refresh-abc", body["refresh_token"]);
        Assert.DoesNotContain("code_verifier", body.Keys);
    }

    // ---------------------------------------------------------------- token set

    [Fact]
    public void A_token_response_becomes_a_token_set_with_an_absolute_expiry()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var tokens = OAuthTokenSet.FromTokenResponse(
            """{"access_token":"at","refresh_token":"rt","expires_in":3600}""", "test", now);

        Assert.Equal("at", tokens.AccessToken);
        Assert.Equal("rt", tokens.RefreshToken);
        Assert.Equal(now.AddHours(1), tokens.ExpiresAt);
    }

    /// <summary>
    /// Most providers return the refresh token only on the first exchange. Dropping it on a
    /// refresh silently turns a renewable login into a one-shot one, and the failure appears an
    /// hour later.
    /// </summary>
    [Fact]
    public void A_refresh_without_a_new_refresh_token_keeps_the_old_one()
    {
        var tokens = OAuthTokenSet.FromTokenResponse(
            """{"access_token":"at2","expires_in":3600}""",
            "test",
            DateTimeOffset.UnixEpoch,
            previousRefresh: "original-refresh");

        Assert.Equal("original-refresh", tokens.RefreshToken);
    }

    [Fact]
    public void A_token_error_response_is_raised_with_its_description()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OAuthTokenSet.FromTokenResponse(
            """{"error":"invalid_grant","error_description":"code expired"}""",
            "test", DateTimeOffset.UnixEpoch));

        Assert.Contains("invalid_grant", ex.Message, StringComparison.Ordinal);
        Assert.Contains("code expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_response_with_no_access_token_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OAuthTokenSet.FromTokenResponse("""{"token_type":"bearer"}""", "t", DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(0, true)]      // already expired
    [InlineData(60, true)]     // inside the skew
    [InlineData(600, false)]   // comfortably valid
    public void Expiry_is_judged_with_a_margin(int secondsUntilExpiry, bool expected)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var tokens = new OAuthTokenSet
        {
            AccessToken = "a",
            ExpiresAtUnix = now.ToUnixTimeSeconds() + secondsUntilExpiry,
        };

        Assert.Equal(expected, tokens.NeedsRefresh(now));
    }

    [Fact]
    public void A_token_with_no_stated_expiry_is_used_until_the_api_objects()
    {
        var tokens = new OAuthTokenSet { AccessToken = "a", ExpiresAtUnix = 0 };

        Assert.False(tokens.NeedsRefresh(DateTimeOffset.UtcNow));
        Assert.Null(tokens.ExpiresAt);
    }

    [Fact]
    public void A_token_set_round_trips_through_its_file_format()
    {
        var original = new OAuthTokenSet
        {
            AccessToken = "at", RefreshToken = "rt", ExpiresAtUnix = 1_700_000_000, Profile = "test",
        };

        var restored = OAuthTokenSet.Parse(original.ToJson());

        Assert.Equal(original, restored);
    }

    // ---------------------------------------------------------------- profile

    [Fact]
    public void A_profile_round_trips_and_the_example_is_valid()
    {
        var restored = OAuthProfile.Parse(Profile.ToJson());
        Assert.Equal(Profile.ClientId, restored.ClientId);
        Assert.Equal(Profile.Scopes, restored.Scopes);

        var example = OAuthProfile.Parse(OAuthProfile.ExampleJson);
        Assert.Equal("my-provider", example.Name);
        Assert.NotEmpty(example.TokenUrl);
    }

    [Fact]
    public void A_redirect_path_without_a_leading_slash_still_builds_a_valid_uri()
    {
        var profile = Profile with { RedirectPath = "callback" };

        Assert.Equal("http://localhost:99/callback", profile.RedirectUri(99));
    }

    // ---------------------------------------------------------------- provider choice

    [Fact]
    public void A_named_profile_is_chosen_even_when_several_are_signed_in()
    {
        Assert.Equal("b", OAuthCredentialProvider.ChooseProfileName("b", ["a", "b", "c"]));
    }

    [Fact]
    public void A_single_sign_in_is_chosen_automatically()
    {
        Assert.Equal("only", OAuthCredentialProvider.ChooseProfileName(null, ["only"]));
    }

    /// <summary>Guessing which account to spend is worse than saying it is ambiguous.</summary>
    [Fact]
    public void With_several_sign_ins_and_no_name_nothing_is_chosen()
    {
        Assert.Null(OAuthCredentialProvider.ChooseProfileName(null, ["a", "b"]));
        Assert.Null(OAuthCredentialProvider.ChooseProfileName(null, []));
    }

    // ---------------------------------------------------------------- resolver integration

    [Fact]
    public void A_named_sign_in_authenticates_as_a_bearer_token_and_outranks_an_env_key()
    {
        var oauth = new ResolvedOAuth(
            "test", "the-token", Requested: true, "https://api.example.com",
            new Dictionary<string, string> { ["x-beta"] = "v" });

        var c = AgentCredentialResolver.Resolve(
            null, null, name => name == "ANTHROPIC_API_KEY" ? "sk-env" : null, null, null, oauth);

        Assert.Equal(AgentCredentialSource.OAuth, c.Source);
        Assert.Equal("the-token", c.AuthToken);
        Assert.Null(c.ApiKey);
        Assert.Equal("https://api.example.com", c.BaseUrl);
        Assert.Equal("v", c.ExtraHeaders["x-beta"]);
    }

    /// <summary>An ambient env var is a deliberate act too, and it wins over a login merely found on disk.</summary>
    [Fact]
    public void An_auto_detected_sign_in_yields_to_an_environment_key()
    {
        var oauth = new ResolvedOAuth("test", "the-token", Requested: false);

        var c = AgentCredentialResolver.Resolve(
            null, null, name => name == "ANTHROPIC_API_KEY" ? "sk-env" : null, null, null, oauth);

        Assert.Equal(AgentCredentialSource.Environment, c.Source);
    }

    [Fact]
    public void An_auto_detected_sign_in_is_used_when_nothing_else_is_set()
    {
        var oauth = new ResolvedOAuth("test", "the-token", Requested: false);

        var c = AgentCredentialResolver.Resolve(null, null, _ => null, null, null, oauth);

        Assert.Equal(AgentCredentialSource.OAuth, c.Source);
        Assert.Contains("test sign-in", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_base_url_overrides_the_profiles()
    {
        var oauth = new ResolvedOAuth("test", "t", Requested: true, "https://profile.example.com");

        var c = AgentCredentialResolver.Resolve(null, "https://override.example.com", _ => null, null, null, oauth);

        Assert.Equal("https://override.example.com", c.BaseUrl);
    }

    // ---------------------------------------------------------------- header handler

    [Fact]
    public void The_handler_adds_required_headers()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com");

        HeaderInjectingHandler.Apply(request, new Dictionary<string, string> { ["anthropic-beta"] = "oauth-2025-04-20" });

        Assert.Equal("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta").Single());
    }

    /// <summary>It must never displace the header the SDK set to authenticate the request.</summary>
    [Fact]
    public void The_handler_does_not_overwrite_an_existing_header()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer real");

        HeaderInjectingHandler.Apply(request, new Dictionary<string, string> { ["Authorization"] = "Bearer forged" });

        Assert.Equal("Bearer real", request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public void A_malformed_profile_is_a_json_error_not_a_crash()
    {
        Assert.ThrowsAny<JsonException>(() => OAuthProfile.Parse("{ not json"));
    }

    // ---------------------------------------------------------------- OpenRouter style

    /// <summary>
    /// The one browser sign-in a third party can complete today without being issued a client id:
    /// no client_id, no response_type, and the redirect named callback_url.
    /// </summary>
    [Fact]
    public void The_openrouter_authorize_url_carries_no_client_id()
    {
        var profile = OAuthProfile.BuiltIn["openrouter"];
        var pkce = PkceChallenge.Create();

        var url = OAuthFlow.AuthorizeUrl(profile, pkce, "unused-state", 8787);
        var q = HttpUtility.ParseQueryString(new Uri(url).Query);

        Assert.StartsWith("https://openrouter.ai/auth?", url, StringComparison.Ordinal);
        Assert.Equal("http://localhost:8787/callback", q["callback_url"]);
        Assert.Equal(pkce.Challenge, q["code_challenge"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Null(q["client_id"]);
        Assert.Null(q["response_type"]);
        Assert.Null(q["redirect_uri"]);
    }

    /// <summary>
    /// OpenRouter echoes no state, so requiring one would reject every real callback. PKCE plus a
    /// single-use loopback listener is what carries the security there.
    /// </summary>
    [Fact]
    public void A_provider_without_state_is_not_failed_for_missing_it()
    {
        var callback = OAuthFlow.ParseCallback("?code=abc");

        OAuthFlow.EnsureValid(callback, "whatever", checkState: false);

        Assert.Throws<InvalidOperationException>(() => OAuthFlow.EnsureValid(callback, "whatever"));
    }

    [Fact]
    public void A_missing_code_is_still_rejected_without_a_state_check()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OAuthFlow.EnsureValid(OAuthFlow.ParseCallback("?nothing=here"), "s", checkState: false));
    }

    /// <summary>The exchange returns a durable API key, so there is nothing to refresh.</summary>
    [Fact]
    public void The_openrouter_exchange_yields_a_key_with_no_expiry()
    {
        var tokens = OAuthFlow.ParseOpenRouterKey(@"{""key"":""sk-or-v1-abc""}", "openrouter");

        Assert.Equal("sk-or-v1-abc", tokens.AccessToken);
        Assert.Null(tokens.RefreshToken);
        Assert.Null(tokens.ExpiresAt);
        Assert.False(tokens.NeedsRefresh(DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(@"{""error"":""bad code""}")]
    [InlineData("{}")]
    public void A_key_exchange_without_a_key_is_a_clear_error(string body)
    {
        Assert.Throws<InvalidOperationException>(() => OAuthFlow.ParseOpenRouterKey(body, "openrouter"));
    }

    [Fact]
    public void The_openrouter_profile_is_built_in_and_needs_no_file()
    {
        var profile = OAuthProfile.Load("openrouter");

        Assert.NotNull(profile);
        Assert.Equal("openrouter", profile!.Style);
        Assert.Equal("openai", profile.Api);
        Assert.Equal("https://openrouter.ai/api/v1", profile.BaseUrl);
        Assert.Empty(profile.ClientId);
        Assert.Contains("openrouter", OAuthProfile.ListNames());
    }

    [Fact]
    public void A_profiles_api_choice_drives_the_transport()
    {
        var credential = new AgentCredential(AgentCredentialSource.OAuth, "signed in") { AuthToken = "t" };

        Assert.Equal("anthropic", InvokeAgentCommand.ChooseApi("auto", credential, "anthropic"));
        Assert.Equal("openai", InvokeAgentCommand.ChooseApi("auto", credential, "openai"));
        Assert.Equal("openai", InvokeAgentCommand.ChooseApi("auto", credential, null));
    }

    /// <summary>
    /// Token files share the profile directory, so a naive *.json listing invents a phantom
    /// profile per sign-in — `stub.token.json` reading as a provider named `stub.token`, which
    /// then shows in `Connect-Agent -List` as a second, never-signed-in entry. Found by running
    /// the flow, not by reading the code.
    /// </summary>
    [Fact]
    public void Listing_profiles_ignores_token_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ps-agent-oauth-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            // ProfileDirectory() appends ps-agent/oauth under XDG_CONFIG_HOME.
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
            var profiles = OAuthProfile.ProfileDirectory();
            Directory.CreateDirectory(profiles);
            File.WriteAllText(Path.Combine(profiles, "stub.json"), Profile.ToJson());
            File.WriteAllText(Path.Combine(profiles, "stub" + OAuthProfile.TokenSuffix), "{}");

            var names = OAuthProfile.ListNames();
            Assert.Contains("stub", names);
            Assert.DoesNotContain("stub.token", names);

            // Built-ins are listed alongside files; nothing else should be.
            Assert.Equal(
                OAuthProfile.BuiltIn.Keys.Concat(["stub"]).Order(StringComparer.OrdinalIgnoreCase),
                names.Order(StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
