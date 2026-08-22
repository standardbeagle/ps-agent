namespace PsAgent.Cmdlets.Agent;

/// <summary>
/// Confines every path the model names to the workspace root.
/// </summary>
/// <remarks>
/// <para>A coding agent's file tools take their operand straight from model output, so
/// <c>../../.ssh/id_rsa</c> is a request the tool layer will happily honour unless something
/// refuses it. This is that something, and it is a pure static function precisely so the refusal
/// is unit-testable without touching a filesystem.</para>
/// <para>Comparison is on the <b>fully resolved</b> path (<see cref="Path.GetFullPath(string)"/>
/// collapses <c>..</c> segments) and requires a directory-separator boundary, so a sibling
/// directory whose name merely starts with the root — <c>C:\work\repo-secrets</c> against root
/// <c>C:\work\repo</c> — is rejected rather than passed by a bare <c>StartsWith</c>.</para>
/// </remarks>
public static class WorkspacePath
{
    /// <summary>
    /// Resolve <paramref name="candidate"/> (absolute, or relative to <paramref name="root"/>) and
    /// return it only if it stays inside <paramref name="root"/>; otherwise <see langword="null"/>.
    /// </summary>
    public static string? TryResolve(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string fullRoot, full;
        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            full = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(fullRoot, candidate));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;   // an unrepresentable path is a refusal, not a crash
        }

        if (string.Equals(full, fullRoot, PathComparison))
        {
            return full;
        }

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, PathComparison) ? full : null;
    }

    /// <summary>Render <paramref name="full"/> relative to <paramref name="root"/> for display.</summary>
    public static string Relative(string root, string full)
    {
        try
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(root), full);
            return rel.StartsWith("..", StringComparison.Ordinal) ? full : rel;
        }
        catch (ArgumentException)
        {
            return full;
        }
    }

    /// <summary>Windows and macOS resolve paths case-insensitively; Linux does not.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
