using System.Management.Automation;
using System.Net.Http;
using PsAgent.Cmdlets.Agent.OAuth;

namespace PsAgent.Cmdlets;

/// <summary>
/// Sign in to a provider through the browser, so <c>Invoke-Agent</c> can authenticate without an
/// API key.
/// </summary>
/// <remarks>
/// <para>Runs an authorization-code flow with PKCE (RFC 7636) and a loopback redirect (RFC 8252):
/// ps-agent starts a listener on <c>localhost</c>, opens your browser at the provider's consent
/// page, and catches the redirect. The code never transits a clipboard, and no client secret is
/// shipped — the flow proves possession of a one-time verifier instead.</para>
/// <para>The provider is described by a JSON profile in
/// <c>~/.config/ps-agent/oauth/&lt;name&gt;.json</c>. That includes the <c>clientId</c>, because a
/// public OAuth client is an identity the provider issues to a named application: ps-agent ships
/// the mechanism, and you supply the identity you are entitled to use. <c>-ShowExample</c> prints
/// the shape.</para>
/// </remarks>
/// <example>
///   <code>Connect-Agent -Provider my-provider</code>
/// </example>
[Cmdlet(VerbsCommunications.Connect, "Agent", DefaultParameterSetName = "Login")]
[OutputType(typeof(PSObject))]
public sealed class ConnectAgentCommand : PSCmdlet
{
    /// <summary>The profile to sign in with.</summary>
    [Parameter(Position = 0, ParameterSetName = "Login")]
    [Alias("Profile")]
    public string? Provider { get; set; }

    /// <summary>List the profiles on disk and whether each is signed in.</summary>
    [Parameter(ParameterSetName = "List")]
    public SwitchParameter List { get; set; }

    /// <summary>Print an example profile and where to put it.</summary>
    [Parameter(ParameterSetName = "Example")]
    public SwitchParameter ShowExample { get; set; }

    /// <summary>Print the URL instead of opening a browser — for a machine with no browser.</summary>
    [Parameter(ParameterSetName = "Login")]
    public SwitchParameter NoBrowser { get; set; }

    /// <summary>Seconds to wait for the browser redirect.</summary>
    [Parameter(ParameterSetName = "Login")]
    [ValidateRange(10, 900)]
    public int TimeoutSeconds { get; set; } = 300;

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (ShowExample)
        {
            WriteObject($"Put a profile at {Path.Combine(OAuthProfile.ProfileDirectory(), "<name>.json")}:");
            WriteObject(OAuthProfile.ExampleJson);
            return;
        }

        if (List)
        {
            WriteProfiles();
            return;
        }

        var name = Provider;
        if (string.IsNullOrWhiteSpace(name))
        {
            var names = OAuthProfile.ListNames();
            if (names.Count == 1)
            {
                name = names[0];
            }
            else
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException(names.Count == 0
                        ? $"No OAuth profiles. Run Connect-Agent -ShowExample, then put one in {OAuthProfile.ProfileDirectory()}."
                        : $"Several profiles exist ({string.Join(", ", names)}); name one with -Provider."),
                    "NoProfile", ErrorCategory.InvalidArgument, null));
                return;
            }
        }

        var profile = OAuthProfile.Load(name!);
        if (profile is null)
        {
            ThrowTerminatingError(new ErrorRecord(
                new FileNotFoundException($"No OAuth profile named '{name}' in {OAuthProfile.ProfileDirectory()}."),
                "ProfileNotFound", ErrorCategory.ObjectNotFound, name));
            return;
        }

        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));

        try
        {
            var tokens = OAuthFlow.RunAsync(
                profile,
                http,
                url =>
                {
                    // Always print it: a browser that fails to launch, or one on another machine,
                    // still leaves the user a way through.
                    WriteInformation(new InformationRecord($"Open this to sign in:\n{url}", "Connect-Agent"));
                    Host.UI.WriteLine($"Open this to sign in:\n{url}\n");
                    if (!NoBrowser)
                    {
                        OAuthFlow.OpenInBrowser(url);
                    }
                },
                () => DateTimeOffset.UtcNow,
                cts.Token).GetAwaiter().GetResult();

            OAuthTokenStore.Save(profile.Name, tokens);

            var result = new PSObject();
            result.TypeNames.Insert(0, "PsAgent.OAuthLogin");
            result.Properties.Add(new PSNoteProperty("Provider", profile.Name));
            result.Properties.Add(new PSNoteProperty("ExpiresAt", tokens.ExpiresAt));
            result.Properties.Add(new PSNoteProperty("Renewable", tokens.RefreshToken is not null));
            result.Properties.Add(new PSNoteProperty("TokenFile", OAuthTokenStore.PathFor(profile.Name)));
            WriteObject(result);
        }
        catch (OperationCanceledException)
        {
            ThrowTerminatingError(new ErrorRecord(
                new TimeoutException($"No redirect within {TimeoutSeconds}s. The browser was never completed."),
                "OAuthTimeout", ErrorCategory.OperationTimeout, profile.Name));
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException)
        {
            ThrowTerminatingError(new ErrorRecord(e, "OAuthFailed", ErrorCategory.AuthenticationError, profile.Name));
        }
    }

    private void WriteProfiles()
    {
        foreach (var name in OAuthProfile.ListNames())
        {
            var tokens = OAuthTokenStore.Load(name);
            var o = new PSObject();
            o.TypeNames.Insert(0, "PsAgent.OAuthProfileInfo");
            o.Properties.Add(new PSNoteProperty("Provider", name));
            o.Properties.Add(new PSNoteProperty("SignedIn", tokens is not null));
            o.Properties.Add(new PSNoteProperty("ExpiresAt", tokens?.ExpiresAt));
            o.Properties.Add(new PSNoteProperty("Renewable", tokens?.RefreshToken is not null));
            WriteObject(o);
        }
    }
}

/// <summary>Remove a stored sign-in.</summary>
/// <remarks>Deletes the token file only. The profile stays, so signing in again needs no setup.</remarks>
[Cmdlet(VerbsCommunications.Disconnect, "Agent")]
[OutputType(typeof(bool))]
public sealed class DisconnectAgentCommand : PSCmdlet
{
    /// <summary>Profile to sign out of.</summary>
    [Parameter(Position = 0, Mandatory = true)]
    [Alias("Profile")]
    public string Provider { get; set; } = string.Empty;

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        var removed = OAuthTokenStore.Delete(Provider);
        WriteVerbose(removed
            ? $"Removed the stored {Provider} sign-in."
            : $"No stored {Provider} sign-in.");
        WriteObject(removed);
    }
}
