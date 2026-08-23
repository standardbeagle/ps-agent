using System.Net.Http;

namespace PsAgent.Cmdlets.Agent.OAuth;

/// <summary>
/// Turns a stored sign-in into something <c>Invoke-Agent</c> can authenticate with, refreshing it
/// first when it is close to expiry.
/// </summary>
public static class OAuthCredentialProvider
{
    /// <summary>
    /// Pick the profile to use: the one named, or the only one signed in.
    /// </summary>
    /// <remarks>
    /// With several logins and no name, nothing is chosen — guessing which account to spend is
    /// worse than saying so. The caller reports the ambiguity.
    /// </remarks>
    public static string? ChooseProfileName(string? requested, IReadOnlyList<string> signedIn)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested;
        }

        return signedIn.Count == 1 ? signedIn[0] : null;
    }

    /// <summary>Profiles that have a stored token.</summary>
    public static IReadOnlyList<string> SignedInProfiles() =>
        [.. OAuthProfile.ListNames().Where(n => OAuthTokenStore.Load(n) is not null)];

    /// <summary>
    /// Load, refresh if needed, persist, and project to a <see cref="ResolvedOAuth"/>.
    /// Returns null when the profile or its token is missing.
    /// </summary>
    /// <param name="requestedProfile">Profile named by the caller, or null to auto-detect.</param>
    /// <param name="http">Client for the refresh call.</param>
    /// <param name="now">Current time, injected for testability.</param>
    /// <param name="onNote">Receives a line when a refresh happens, for the transcript.</param>
    public static async Task<ResolvedOAuth?> TryResolveAsync(
        string? requestedProfile,
        HttpClient http,
        DateTimeOffset now,
        Action<string>? onNote = null,
        CancellationToken ct = default)
    {
        var name = ChooseProfileName(requestedProfile, SignedInProfiles());
        if (name is null)
        {
            return null;
        }

        var profile = OAuthProfile.Load(name);
        var tokens = OAuthTokenStore.Load(name);
        if (profile is null || tokens is null)
        {
            return null;
        }

        if (tokens.NeedsRefresh(now))
        {
            try
            {
                tokens = await OAuthFlow.RefreshAsync(http, profile, tokens, now, ct).ConfigureAwait(false);
                OAuthTokenStore.Save(name, tokens);
                onNote?.Invoke($"refreshed the {name} sign-in");
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException)
            {
                // An expired login that cannot be refreshed is reported, not thrown: the chain has
                // later steps, and a stale token file should not block an otherwise usable machine.
                onNote?.Invoke($"could not refresh the {name} sign-in ({e.Message}); run Connect-Agent again");
                return null;
            }
        }

        return new ResolvedOAuth(
            name,
            tokens.AccessToken,
            Requested: !string.IsNullOrWhiteSpace(requestedProfile),
            profile.BaseUrl,
            profile.ExtraHeaders,
            tokens.ExpiresAt);
    }
}
