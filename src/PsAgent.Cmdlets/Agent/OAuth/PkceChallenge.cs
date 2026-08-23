using System.Security.Cryptography;
using System.Text;

namespace PsAgent.Cmdlets.Agent.OAuth;

/// <summary>
/// A PKCE verifier/challenge pair (RFC 7636).
/// </summary>
/// <remarks>
/// <para>PKCE is what makes an authorization-code flow safe for a program that cannot keep a
/// secret. ps-agent is installed on the user's machine, so any client secret shipped with it would
/// be readable; instead the client proves possession of a one-time verifier it generated. The
/// authorization server only ever sees its SHA-256 hash until the code is redeemed.</para>
/// <para>Base64url, no padding — a `+`, `/` or `=` in either value is rejected by the server, and
/// the resulting error says only "invalid grant", which is a long way from the cause.</para>
/// </remarks>
/// <param name="Verifier">The secret held locally until the token exchange.</param>
/// <param name="Challenge">The S256 hash sent on the authorize request.</param>
public sealed record PkceChallenge(string Verifier, string Challenge)
{
    /// <summary>The only transform this supports. `plain` is accepted by some servers and is pointless.</summary>
    public const string Method = "S256";

    /// <summary>Generate a fresh pair from a cryptographically random verifier.</summary>
    /// <param name="verifierBytes">
    /// Entropy for the verifier. RFC 7636 permits 43–128 characters after encoding; 32 bytes
    /// yields 43.
    /// </param>
    public static PkceChallenge Create(int verifierBytes = 32)
    {
        var buffer = RandomNumberGenerator.GetBytes(verifierBytes);
        var verifier = Base64Url(buffer);
        return new PkceChallenge(verifier, ChallengeFor(verifier));
    }

    /// <summary>The S256 challenge for a given verifier. Pure, so the transform is testable.</summary>
    public static string ChallengeFor(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Base64url without padding, per RFC 7636 §4.2.</summary>
    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>A random value for the <c>state</c> parameter, which guards against a forged callback.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(16));
}
