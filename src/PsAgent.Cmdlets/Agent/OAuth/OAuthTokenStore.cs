using System.Runtime.InteropServices;

namespace PsAgent.Cmdlets.Agent.OAuth;

/// <summary>
/// Persists tokens beside their profile, readable only by the owner.
/// </summary>
/// <remarks>
/// On POSIX the file is chmod 600. Windows inherits the user profile's ACL, which already excludes
/// other users; there is no portable equivalent worth faking, so the difference is stated rather
/// than papered over.
/// </remarks>
public static class OAuthTokenStore
{
    /// <summary>Path of the token file for a profile.</summary>
    public static string PathFor(string profileName) =>
        Path.Combine(OAuthProfile.ProfileDirectory(), profileName + ".token.json");

    /// <summary>Read the stored tokens, or null when there is no usable file.</summary>
    public static OAuthTokenSet? Load(string profileName)
    {
        try
        {
            var path = PathFor(profileName);
            return File.Exists(path) ? OAuthTokenSet.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Write tokens, creating the directory and restricting the file.</summary>
    public static void Save(string profileName, OAuthTokenSet tokens)
    {
        var path = PathFor(profileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, tokens.ToJson());
        RestrictToOwner(path);
    }

    /// <summary>Remove a stored login. Returns whether anything was there.</summary>
    public static bool Delete(string profileName)
    {
        try
        {
            var path = PathFor(profileName);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RestrictToOwner(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // A filesystem that cannot express the mode is not a reason to lose the token.
        }
    }
}
