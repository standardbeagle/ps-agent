using System.Reflection;

namespace PsAgent.Cmdlets.Ui;

/// <summary>
/// Resolves a stylesheet name, an inline CSS string, or a <c>.pcss</c>/<c>.css</c> path to CSS text.
/// </summary>
/// <remarks>
/// Built-in sheets are embedded in the assembly rather than read from a styles directory beside the
/// DLL, so they travel with the module whatever the deployment layout — the same reason
/// <c>PsBash.Cmdlets</c> embeds its own. A same-named file under
/// <c>~/.config/ps-agent/styles/</c> overrides the built-in, which is how a user re-themes the
/// transcript without rebuilding.
/// </remarks>
internal static class AgentStyles
{
    /// <summary>The sheet used when none is named.</summary>
    public const string Default = "agent";

    /// <summary>Resolve <paramref name="nameOrInline"/> to CSS text.</summary>
    public static string Resolve(string? nameOrInline)
    {
        var name = string.IsNullOrWhiteSpace(nameOrInline) ? Default : nameOrInline!;

        // Anything with a brace or a newline is CSS, not a name — the same test Show-Styled uses.
        if (name.Contains('{') || name.Contains('\n'))
        {
            return name;
        }

        if (File.Exists(name))
        {
            return File.ReadAllText(name);
        }

        return ReadUserOverride(name)
            ?? ReadEmbedded(name)
            ?? throw new FileNotFoundException($"No stylesheet named '{name}'. Built-in: {string.Join(", ", BuiltinNames())}.");
    }

    /// <summary>The names of the embedded sheets.</summary>
    public static IEnumerable<string> BuiltinNames()
    {
        const string prefix = "PsAgent.Cmdlets.styles.";
        foreach (var resource in typeof(AgentStyles).Assembly.GetManifestResourceNames())
        {
            if (resource.StartsWith(prefix, StringComparison.Ordinal) &&
                resource.EndsWith(".pcss", StringComparison.Ordinal))
            {
                yield return resource[prefix.Length..^".pcss".Length];
            }
        }
    }

    private static string? ReadEmbedded(string name)
    {
        var assembly = typeof(AgentStyles).Assembly;
        using var stream = assembly.GetManifestResourceStream($"PsAgent.Cmdlets.styles.{name}.pcss");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? ReadUserOverride(string name)
    {
        foreach (var dir in UserStyleDirs())
        {
            var path = Path.Combine(dir, name + ".pcss");
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        return null;
    }

    private static IEnumerable<string> UserStyleDirs()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            yield return Path.Combine(xdg, "ps-agent", "styles");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".config", "ps-agent", "styles");
        }
    }

    /// <summary>Assembly-qualification anchor so <see cref="Assembly"/> is a used import.</summary>
    internal static Assembly Owner => typeof(AgentStyles).Assembly;
}
