using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsAgent.Cmdlets.Agent.OAuth;

/// <summary>
/// The tokens a completed flow produced, plus when the access token stops working.
/// </summary>
/// <remarks>
/// Stored under <c>~/.config/ps-agent/oauth/&lt;name&gt;.token.json</c>, separate from the profile
/// so a profile can be shared or committed while the secret never is.
/// </remarks>
public sealed record OAuthTokenSet
{
    /// <summary>The bearer token sent on API requests.</summary>
    [JsonPropertyName("access")]
    public required string AccessToken { get; init; }

    /// <summary>Used to obtain a new access token without another browser round trip.</summary>
    [JsonPropertyName("refresh")]
    public string? RefreshToken { get; init; }

    /// <summary>Absolute expiry, in Unix seconds. Zero means the provider did not say.</summary>
    [JsonPropertyName("expiresAt")]
    public long ExpiresAtUnix { get; init; }

    /// <summary>Which profile issued it.</summary>
    [JsonPropertyName("profile")]
    public string Profile { get; init; } = string.Empty;

    /// <summary>Expiry as a timestamp, or null when unknown.</summary>
    [JsonIgnore]
    public DateTimeOffset? ExpiresAt =>
        ExpiresAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix) : null;

    /// <summary>
    /// Whether the token should be refreshed before use.
    /// </summary>
    /// <param name="now">Current time, injected so the decision is testable.</param>
    /// <param name="skew">
    /// Refresh this far ahead of the stated expiry. A token that expires mid-flight fails the
    /// request rather than the check, so the margin is deliberately generous.
    /// </param>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan? skew = null)
    {
        if (ExpiresAt is not { } expiry)
        {
            return false;   // no stated expiry: use it until the API says otherwise
        }

        return now + (skew ?? TimeSpan.FromMinutes(2)) >= expiry;
    }

    /// <summary>Build from a token endpoint's JSON response.</summary>
    /// <param name="json">The raw response body.</param>
    /// <param name="profile">Profile name to stamp.</param>
    /// <param name="now">Current time, for turning <c>expires_in</c> into an absolute expiry.</param>
    /// <param name="previousRefresh">
    /// Carried forward when a refresh response omits <c>refresh_token</c> — many providers only
    /// return it on the first exchange, and dropping it silently turns a renewable login into a
    /// one-shot one.
    /// </param>
    public static OAuthTokenSet FromTokenResponse(
        string json, string profile, DateTimeOffset now, string? previousRefresh = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            var description = root.TryGetProperty("error_description", out var d)
                ? d.GetString()
                : null;
            throw new InvalidOperationException(
                $"token endpoint returned {err.GetString()}{(description is null ? "" : ": " + description)}");
        }

        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(access))
        {
            throw new InvalidOperationException("token endpoint returned no access_token");
        }

        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;

        long expiresAt = 0;
        if (root.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var seconds) && seconds > 0)
        {
            expiresAt = now.ToUnixTimeSeconds() + seconds;
        }

        return new OAuthTokenSet
        {
            AccessToken = access,
            RefreshToken = string.IsNullOrEmpty(refresh) ? previousRefresh : refresh,
            ExpiresAtUnix = expiresAt,
            Profile = profile,
        };
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Serialize for the token file.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parse a token file.</summary>
    public static OAuthTokenSet Parse(string json) =>
        JsonSerializer.Deserialize<OAuthTokenSet>(json, Json)
        ?? throw new JsonException("empty token file");
}
