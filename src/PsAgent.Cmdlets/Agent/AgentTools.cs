using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PsBash.Cmdlets;

namespace PsAgent.Cmdlets.Agent;

/// <summary>
/// The agent's whole capability surface: read a file, list a directory, edit a file, run a
/// command. Four tools is the deliberate floor — it is what a coding agent needs to be useful and
/// what keeps the loop legible.
/// </summary>
/// <remarks>
/// <para>Every path operand is resolved through <see cref="WorkspacePath.TryResolve"/> before it
/// reaches the filesystem, and every mutating tool goes through an <see cref="ApprovalGate"/>
/// before it runs. Those two checks are the security model; they live here, in the executor, and
/// not in the prompt — a system prompt is a request, not a boundary.</para>
/// <para><c>run_bash</c> spawns through <see cref="BashRuntime.RunChildProcess(ProcessStartInfo, TimeSpan?)"/>
/// rather than a bare <see cref="Process.Start()"/>: that helper drains stdout and stderr
/// concurrently and kills the whole process tree on timeout, which is the difference between a
/// wedged command and a wedged session.</para>
/// </remarks>
public sealed class AgentTools
{
    private readonly string _root;
    private readonly ApprovalGate _approve;
    private readonly TimeSpan _commandTimeout;
    private readonly int _maxOutputChars;

    /// <summary>Asked before a mutating tool runs. Returning false refuses the call.</summary>
    /// <param name="tool">Tool name.</param>
    /// <param name="detail">Human-readable description of exactly what is about to happen.</param>
    public delegate bool ApprovalGate(string tool, string detail);

    /// <summary>An approval gate that permits everything — the <c>-AutoApprove</c> path.</summary>
    public static readonly ApprovalGate AllowAll = static (_, _) => true;

    /// <summary>Create an executor rooted at <paramref name="workspaceRoot"/>.</summary>
    public AgentTools(
        string workspaceRoot,
        ApprovalGate approve,
        TimeSpan? commandTimeout = null,
        int maxOutputChars = 20_000)
    {
        _root = Path.GetFullPath(workspaceRoot);
        _approve = approve;
        _commandTimeout = commandTimeout ?? TimeSpan.FromMinutes(2);
        _maxOutputChars = maxOutputChars;
    }

    /// <summary>The workspace every path is confined to.</summary>
    public string Root => _root;

    /// <summary>
    /// The catalog handed to the model. Static and pure, so a test can assert the schema without
    /// constructing an executor — and so the tool list is byte-identical across turns, which is
    /// what lets the prompt prefix cache.
    /// </summary>
    public static IReadOnlyList<ToolSpec> Catalog { get; } =
    [
        new ToolSpec(
            "read_file",
            "Read the contents of a text file at the given path, relative to the workspace root. "
            + "Use this before editing a file so the old_str you supply to edit_file matches exactly.",
            new Dictionary<string, JsonElement>
            {
                ["path"] = ToolSpec.Prop("string", "Path to the file, relative to the workspace root."),
            },
            ["path"],
            Mutating: false),

        new ToolSpec(
            "list_files",
            "List files and directories at the given path (defaults to the workspace root). "
            + "Directory names end with a trailing slash.",
            new Dictionary<string, JsonElement>
            {
                ["path"] = ToolSpec.Prop("string", "Directory to list, relative to the workspace root. Optional."),
            },
            [],
            Mutating: false),

        new ToolSpec(
            "edit_file",
            "Replace an exact substring in a file. old_str must appear EXACTLY ONCE in the file and "
            + "must differ from new_str. If the file does not exist and old_str is empty, the file is "
            + "created with new_str as its contents.",
            new Dictionary<string, JsonElement>
            {
                ["path"] = ToolSpec.Prop("string", "Path to the file, relative to the workspace root."),
                ["old_str"] = ToolSpec.Prop("string", "Exact text to replace. Empty to create a new file."),
                ["new_str"] = ToolSpec.Prop("string", "Text to replace it with."),
            },
            ["path", "old_str", "new_str"],
            Mutating: true),

        new ToolSpec(
            "run_bash",
            "Run a shell command in the workspace root and return its stdout, stderr and exit code. "
            + "Use it for builds, tests, git and search. It is not interactive: never run a command that "
            + "waits for input or never exits.",
            new Dictionary<string, JsonElement>
            {
                ["command"] = ToolSpec.Prop("string", "The command line to run."),
            },
            ["command"],
            Mutating: true),
    ];

    /// <summary>Look up a spec by wire name.</summary>
    public static ToolSpec? Find(string name) =>
        Catalog.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Run <paramref name="name"/> with <paramref name="input"/>. Never throws for an ordinary
    /// failure — a bad path, a missing file, or a refused approval all come back as a failed
    /// <see cref="ToolOutcome"/> so the model gets to read the error and try again.
    /// </summary>
    public ToolOutcome Execute(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (Find(name) is null)
        {
            return ToolOutcome.Failure($"Unknown tool '{name}'.");
        }

        try
        {
            return name switch
            {
                "read_file" => ReadFile(Str(input, "path")),
                "list_files" => ListFiles(Str(input, "path")),
                "edit_file" => EditFile(Str(input, "path"), Str(input, "old_str"), Str(input, "new_str")),
                "run_bash" => RunBash(Str(input, "command")),
                _ => ToolOutcome.Failure($"Unknown tool '{name}'."),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return ToolOutcome.Failure($"{name} failed: {e.Message}");
        }
    }

    private ToolOutcome ReadFile(string path)
    {
        var full = WorkspacePath.TryResolve(_root, path);
        if (full is null)
        {
            return Escaped(path);
        }

        if (!File.Exists(full))
        {
            return ToolOutcome.Failure($"No such file: {path}");
        }

        var text = File.ReadAllText(full);
        var shown = Clamp(text, out var truncated);
        var note = truncated ? ", truncated" : string.Empty;
        return ToolOutcome.Success(
            shown,
            $"read {WorkspacePath.Relative(_root, full)} ({text.Length} chars{note})");
    }

    private ToolOutcome ListFiles(string path)
    {
        var target = string.IsNullOrWhiteSpace(path) ? _root : WorkspacePath.TryResolve(_root, path);
        if (target is null)
        {
            return Escaped(path);
        }

        if (!Directory.Exists(target))
        {
            var shown = string.IsNullOrWhiteSpace(path) ? "." : path;
            return ToolOutcome.Failure($"No such directory: {shown}");
        }

        var sb = new StringBuilder();
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(target).Order(StringComparer.Ordinal))
        {
            sb.Append(Path.GetFileName(dir)).Append('/').Append('\n');
            count++;
        }

        foreach (var file in Directory.EnumerateFiles(target).Order(StringComparer.Ordinal))
        {
            sb.Append(Path.GetFileName(file)).Append('\n');
            count++;
        }

        return ToolOutcome.Success(
            sb.Length == 0 ? "(empty directory)" : sb.ToString(),
            $"list {WorkspacePath.Relative(_root, target)} ({count} entries)");
    }

    private ToolOutcome EditFile(string path, string oldStr, string newStr)
    {
        if (oldStr == newStr)
        {
            return ToolOutcome.Failure("edit_file: old_str and new_str are identical — nothing to do.");
        }

        var full = WorkspacePath.TryResolve(_root, path);
        if (full is null)
        {
            return Escaped(path);
        }

        var rel = WorkspacePath.Relative(_root, full);

        if (!File.Exists(full))
        {
            if (oldStr.Length != 0)
            {
                return ToolOutcome.Failure($"No such file: {path} (pass an empty old_str to create it).");
            }

            if (!_approve("edit_file", $"create {rel} ({newStr.Length} chars)"))
            {
                return Refused("edit_file");
            }

            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(full, newStr);
            return ToolOutcome.Success($"Created {rel}.", $"create {rel}");
        }

        var content = File.ReadAllText(full);

        // Exactly-once is the contract: replacing the first of several matches silently edits a
        // line the model never looked at, and the result text gives it no way to notice.
        var occurrences = CountOccurrences(content, oldStr);
        if (occurrences == 0)
        {
            return ToolOutcome.Failure($"edit_file: old_str not found in {rel}.");
        }

        if (occurrences > 1)
        {
            return ToolOutcome.Failure(
                $"edit_file: old_str appears {occurrences} times in {rel} — include more surrounding context so it is unique.");
        }

        if (!_approve("edit_file", $"edit {rel} (replace {oldStr.Length} chars with {newStr.Length})"))
        {
            return Refused("edit_file");
        }

        File.WriteAllText(full, content.Replace(oldStr, newStr, StringComparison.Ordinal));
        return ToolOutcome.Success($"Edited {rel}.", $"edit {rel}");
    }

    private ToolOutcome RunBash(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return ToolOutcome.Failure("run_bash: empty command.");
        }

        if (!_approve("run_bash", command))
        {
            return Refused("run_bash");
        }

        var (file, args) = ShellInvocation(command);
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _root,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var result = BashRuntime.RunChildProcess(psi, _commandTimeout);

        var sb = new StringBuilder();
        if (result.Stdout.Length > 0)
        {
            sb.Append(result.Stdout);
        }

        if (result.Stderr.Length > 0)
        {
            if (sb.Length > 0 && sb[^1] != '\n')
            {
                sb.Append('\n');
            }

            sb.Append("[stderr]\n").Append(result.Stderr);
        }

        if (result.TimedOut)
        {
            sb.Append($"\n[timed out after {_commandTimeout.TotalSeconds:0}s - process tree killed]");
        }

        sb.Append($"\n[exit {result.ExitCode}]");

        var text = Clamp(sb.ToString(), out _);
        var shown = AgentEvent.Truncate(AgentEvent.Flatten(command), 60);
        var summary = $"$ {shown} -> exit {result.ExitCode}";

        // A non-zero exit is information, not a tool failure: the model is supposed to read the
        // compiler error and fix it. Only a killed run is reported as an error.
        return result.TimedOut ? new ToolOutcome(false, text, summary) : ToolOutcome.Success(text, summary);
    }

    /// <summary>
    /// Pick the shell. <c>ps-bash</c> when it is on PATH — it is a real bash front end on Windows
    /// with no MSYS install — otherwise the platform default.
    /// </summary>
    internal static (string File, string[] Args) ShellInvocation(string command)
    {
        var psbash = FindOnPath(OperatingSystem.IsWindows() ? "ps-bash.exe" : "ps-bash");
        if (psbash is not null)
        {
            return (psbash, ["-c", command]);
        }

        return OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", command])
            : ("/bin/sh", ["-c", command]);
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped, not fatal.
            }
        }

        return null;
    }

    /// <summary>Count non-overlapping occurrences of <paramref name="needle"/>.</summary>
    internal static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }

        return count;
    }

    private string Clamp(string text, out bool truncated)
    {
        truncated = text.Length > _maxOutputChars;
        return truncated
            ? text[.._maxOutputChars] + $"\n[... truncated at {_maxOutputChars} chars]"
            : text;
    }

    private static ToolOutcome Escaped(string path) =>
        ToolOutcome.Failure($"Refused: '{path}' resolves outside the workspace root.");

    private static ToolOutcome Refused(string tool) =>
        ToolOutcome.Failure($"The user declined to run {tool}. Ask what they would like to do instead.");

    private static string Str(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}
