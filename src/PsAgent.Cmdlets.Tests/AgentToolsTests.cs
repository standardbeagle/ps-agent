using System.Text.Json;
using PsAgent.Cmdlets.Agent;
using Xunit;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// The tool executor against a real temporary workspace — the filesystem behaviour is the point,
/// so it is exercised rather than mocked.
/// </summary>
public sealed class AgentToolsTests : IDisposable
{
    private readonly string _root;

    /// <summary>Create an isolated workspace per test.</summary>
    public AgentToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ps-agent-tools", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private AgentTools Tools(bool approve = true) =>
        new(_root, (_, _) => approve, TimeSpan.FromSeconds(20));

    private static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    [Fact]
    public void Catalog_declares_the_four_tools_with_required_parameters()
    {
        Assert.Equal(
            ["read_file", "list_files", "edit_file", "run_bash"],
            AgentTools.Catalog.Select(t => t.Name));

        Assert.Equal(["path"], AgentTools.Find("read_file")!.Required);
        Assert.Empty(AgentTools.Find("list_files")!.Required);
    }

    [Fact]
    public void Only_the_writing_tools_are_marked_mutating()
    {
        Assert.False(AgentTools.Find("read_file")!.Mutating);
        Assert.False(AgentTools.Find("list_files")!.Mutating);
        Assert.True(AgentTools.Find("edit_file")!.Mutating);
        Assert.True(AgentTools.Find("run_bash")!.Mutating);
    }

    [Fact]
    public void An_unknown_tool_fails_rather_than_throwing()
    {
        var outcome = Tools().Execute("rm_rf", Args());

        Assert.False(outcome.Ok);
        Assert.Contains("Unknown tool", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_file_returns_the_contents()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");

        var outcome = Tools().Execute("read_file", Args(("path", "a.txt")));

        Assert.True(outcome.Ok);
        Assert.Equal("hello", outcome.Text);
    }

    [Fact]
    public void Read_file_outside_the_workspace_is_refused()
    {
        var outcome = Tools().Execute("read_file", Args(("path", "../escape.txt")));

        Assert.False(outcome.Ok);
        Assert.Contains("outside the workspace root", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void List_files_marks_directories_with_a_trailing_slash()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "z.txt"), string.Empty);

        var outcome = Tools().Execute("list_files", Args());

        Assert.True(outcome.Ok);
        Assert.Contains("src/", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("z.txt", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_file_replaces_a_unique_anchor()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "one two three");

        var outcome = Tools().Execute("edit_file", Args(("path", "a.txt"), ("old_str", "two"), ("new_str", "2")));

        Assert.True(outcome.Ok);
        Assert.Equal("one 2 three", File.ReadAllText(path));
    }

    /// <summary>
    /// The failure that matters most: replacing the first of several matches would silently edit a
    /// line the model never looked at.
    /// </summary>
    [Fact]
    public void Edit_file_refuses_an_ambiguous_anchor_and_changes_nothing()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "x x");

        var outcome = Tools().Execute("edit_file", Args(("path", "a.txt"), ("old_str", "x"), ("new_str", "y")));

        Assert.False(outcome.Ok);
        Assert.Contains("appears 2 times", outcome.Text, StringComparison.Ordinal);
        Assert.Equal("x x", File.ReadAllText(path));
    }

    [Fact]
    public void Edit_file_reports_a_missing_anchor()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "abc");

        var outcome = Tools().Execute("edit_file", Args(("path", "a.txt"), ("old_str", "zzz"), ("new_str", "y")));

        Assert.False(outcome.Ok);
        Assert.Contains("not found", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_file_creates_a_missing_file_when_old_str_is_empty()
    {
        var outcome = Tools().Execute("edit_file", Args(("path", "new/deep.txt"), ("old_str", ""), ("new_str", "body")));

        Assert.True(outcome.Ok);
        Assert.Equal("body", File.ReadAllText(Path.Combine(_root, "new", "deep.txt")));
    }

    [Fact]
    public void Edit_file_will_not_create_a_missing_file_when_an_anchor_was_given()
    {
        var outcome = Tools().Execute("edit_file", Args(("path", "gone.txt"), ("old_str", "a"), ("new_str", "b")));

        Assert.False(outcome.Ok);
        Assert.False(File.Exists(Path.Combine(_root, "gone.txt")));
    }

    [Fact]
    public void A_refused_approval_leaves_the_file_untouched()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "original");

        var outcome = Tools(approve: false)
            .Execute("edit_file", Args(("path", "a.txt"), ("old_str", "original"), ("new_str", "changed")));

        Assert.False(outcome.Ok);
        Assert.Contains("declined", outcome.Text, StringComparison.Ordinal);
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void An_identical_replacement_is_rejected_before_any_io()
    {
        var outcome = Tools().Execute("edit_file", Args(("path", "a.txt"), ("old_str", "same"), ("new_str", "same")));

        Assert.False(outcome.Ok);
        Assert.Contains("identical", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_bash_reports_stdout_and_the_exit_code()
    {
        var outcome = Tools().Execute("run_bash", Args(("command", "echo marker")));

        Assert.True(outcome.Ok);
        Assert.Contains("marker", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("[exit 0]", outcome.Text, StringComparison.Ordinal);
    }

    /// <summary>A failing command is information for the model, not a tool failure.</summary>
    [Fact]
    public void A_nonzero_exit_is_still_a_successful_tool_call()
    {
        var outcome = Tools().Execute("run_bash", Args(("command", "exit 3")));

        Assert.True(outcome.Ok);
        Assert.Contains("[exit 3]", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_bash_is_refused_when_approval_is_declined()
    {
        var outcome = Tools(approve: false).Execute("run_bash", Args(("command", "echo nope")));

        Assert.False(outcome.Ok);
        Assert.Contains("declined", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_bash_rejects_an_empty_command()
    {
        Assert.False(Tools().Execute("run_bash", Args(("command", "  "))).Ok);
    }

    [Theory]
    [InlineData("", "x", 0)]
    [InlineData("aaa", "a", 3)]
    [InlineData("aaaa", "aa", 2)]      // non-overlapping
    [InlineData("abc", "z", 0)]
    public void CountOccurrences_counts_non_overlapping_matches(string haystack, string needle, int expected)
    {
        Assert.Equal(expected, AgentTools.CountOccurrences(haystack, needle));
    }

    [Fact]
    public void ShellInvocation_passes_the_command_as_a_single_argument()
    {
        var (file, args) = AgentTools.ShellInvocation("echo a && echo b");

        Assert.NotEmpty(file);
        Assert.Equal(2, args.Length);
        Assert.Equal("echo a && echo b", args[1]);
    }
}
