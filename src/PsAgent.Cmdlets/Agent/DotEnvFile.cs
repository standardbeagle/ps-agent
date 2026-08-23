namespace PsAgent.Cmdlets.Agent;

/// <summary>
/// Reads <c>KEY=value</c> pairs out of a <c>.env.local</c> beside the workspace.
/// </summary>
/// <remarks>
/// <para>Supported because it is where people already put a key for a single project, and asking
/// them to also export it into the session is friction with no safety benefit.</para>
/// <para>Only the variable names <see cref="AgentCredentialResolver"/> asks for are ever read, and
/// the file is only consulted under the workspace root the caller already chose — so entering a
/// directory cannot quietly hand ps-agent a stranger's credential to spend.</para>
/// <para>Add <c>.env*</c> to <c>.gitignore</c>. A key in a tracked file is a key in the history.</para>
/// </remarks>
public static class DotEnvFile
{
    /// <summary>The file name looked for in the workspace root.</summary>
    public const string FileName = ".env.local";

    /// <summary>
    /// Parse <c>.env</c> text into a dictionary. Blank lines and <c>#</c> comments are skipped;
    /// a leading <c>export </c> is tolerated; surrounding single or double quotes are stripped.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (name.Length > 0)
            {
                map[name] = value;
            }
        }

        return map;
    }

    /// <summary>Read and parse the workspace's file, or an empty map when there is none.</summary>
    public static IReadOnlyDictionary<string, string> Read(string workspaceRoot)
    {
        try
        {
            var path = Path.Combine(workspaceRoot, FileName);
            return File.Exists(path)
                ? Parse(File.ReadAllText(path))
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
