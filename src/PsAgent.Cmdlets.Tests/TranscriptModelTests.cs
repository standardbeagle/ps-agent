using System.Text.Json;
using PsAgent.Cmdlets;
using PsAgent.Cmdlets.Agent;
using PsAgent.Cmdlets.Ui;
using Xunit;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// The row model and the viewer's pure seams — the key map, the scroll window, and the pipeline
/// projection that lets <c>Show-Styled</c> colour these rows with no configuration.
/// </summary>
public sealed class TranscriptModelTests
{
    [Fact]
    public void Flatten_collapses_newlines_and_runs_of_space()
    {
        Assert.Equal("a b c", AgentEvent.Flatten("a\n b\t\tc"));
    }

    [Fact]
    public void Flatten_of_empty_stays_empty()
    {
        Assert.Equal(string.Empty, AgentEvent.Flatten(string.Empty));
    }

    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello", 4, "hel…")]
    [InlineData("hello", 1, "hello")]
    public void Truncate_keeps_the_string_within_the_budget(string text, int max, string expected)
    {
        Assert.Equal(expected, AgentEvent.Truncate(text, max));
    }

    /// <summary>
    /// The class string is what the stylesheet selects on, so its mapping is behaviour, not detail.
    /// </summary>
    [Theory]
    [InlineData(AgentEventKind.User, "user")]
    [InlineData(AgentEventKind.Assistant, "assistant")]
    [InlineData(AgentEventKind.Thought, "thought")]
    [InlineData(AgentEventKind.Error, "error")]
    [InlineData(AgentEventKind.Plan, "plan")]
    public void The_kind_projects_a_stylesheet_class(AgentEventKind kind, string expected)
    {
        var row = new AgentEvent { Kind = kind, Title = "t" };

        Assert.Contains(expected, row.Class, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentToolStatus.Completed, "ok")]
    [InlineData(AgentToolStatus.Failed, "error")]
    [InlineData(AgentToolStatus.InProgress, "running")]
    public void A_tool_rows_class_carries_its_outcome(AgentToolStatus status, string expected)
    {
        var row = new AgentEvent { Kind = AgentEventKind.ToolCall, Title = "t", Status = status };

        Assert.Contains("tool", row.Class, StringComparison.Ordinal);
        Assert.Contains(expected, row.Class, StringComparison.Ordinal);
    }

    /// <summary>Colour is never the only channel: the glyph has to distinguish rows under NO_COLOR.</summary>
    [Fact]
    public void Every_kind_has_a_distinguishing_glyph()
    {
        var glyphs = Enum.GetValues<AgentEventKind>()
            .Select(k => new AgentEvent { Kind = k, Title = "t" }.Glyph)
            .ToArray();

        Assert.DoesNotContain(string.Empty, glyphs);
        Assert.True(glyphs.Distinct().Count() >= 6);
    }

    [Fact]
    public void A_failed_tool_result_gets_the_failure_glyph()
    {
        var row = new AgentEvent
        {
            Kind = AgentEventKind.ToolResult,
            Title = "t",
            Status = AgentToolStatus.Failed,
        };

        Assert.Equal("✘", row.Glyph);
    }

    [Fact]
    public void The_display_line_is_a_single_line()
    {
        var row = new AgentEvent { Kind = AgentEventKind.Assistant, Title = "one\ntwo" };

        Assert.DoesNotContain('\n', row.Display);
    }

    /// <summary>
    /// Show-Styled defaults to -ClassProperty class and summarises on Name. Emitting exactly those
    /// two property names is what makes `Invoke-Agent -NoUi | Show-Styled` work unconfigured.
    /// </summary>
    [Fact]
    public void The_pipeline_shape_matches_what_Show_Styled_reads_by_default()
    {
        var ps = new AgentEvent
        {
            Kind = AgentEventKind.ToolCall,
            Title = "run_bash",
            Body = "dotnet test",
            ToolName = "run_bash",
            ToolCallId = "c1",
            Status = AgentToolStatus.Completed,
        }.ToPSObject();

        Assert.Equal("PsAgent.AgentEvent", ps.TypeNames[0]);
        Assert.NotNull(ps.Properties["Name"]);
        Assert.NotNull(ps.Properties["class"]);
        Assert.Contains("tool", ps.Properties["class"].Value!.ToString()!, StringComparison.Ordinal);
        Assert.Equal("dotnet test", ps.Properties["Body"].Value);
        Assert.Equal("c1", ps.Properties["ToolCallId"].Value);
        Assert.Equal("Completed", ps.Properties["Status"].Value);
    }

    // The expectation is spelled as a string because ViewAction is internal to the cmdlets
    // assembly: an xunit [Theory] parameter has to be at least as accessible as the public test
    // method, and widening a UI enum just to name it in a test is the wrong trade.
    [Theory]
    [InlineData(ConsoleKey.DownArrow, '\0', false, "Down")]
    [InlineData(ConsoleKey.UpArrow, '\0', false, "Up")]
    [InlineData(ConsoleKey.Enter, '\r', false, "ToggleExpand")]
    [InlineData(ConsoleKey.NoName, 'j', false, "Down")]
    [InlineData(ConsoleKey.NoName, 'k', false, "Up")]
    [InlineData(ConsoleKey.NoName, 'i', false, "Prompt")]
    [InlineData(ConsoleKey.NoName, 'q', false, "Quit")]
    [InlineData(ConsoleKey.NoName, 'z', false, "None")]
    public void The_key_map_is_stable(ConsoleKey key, char ch, bool busy, string expected)
    {
        Assert.Equal(expected, TranscriptView.Decide(key, ch, busy).ToString());
    }

    /// <summary>Esc is context-sensitive: interrupt a running turn, leave an idle viewer.</summary>
    [Fact]
    public void Escape_interrupts_while_busy_and_quits_when_idle()
    {
        Assert.Equal("Interrupt", TranscriptView.Decide(ConsoleKey.Escape, '\0', busy: true).ToString());
        Assert.Equal("Quit", TranscriptView.Decide(ConsoleKey.Escape, '\0', busy: false).ToString());
    }

    [Fact]
    public void The_scroll_window_shows_everything_when_it_fits()
    {
        Assert.Equal((0, 5), TranscriptView.Window(total: 5, focus: 2, window: 10));
    }

    [Fact]
    public void The_scroll_window_keeps_the_focus_visible()
    {
        var (start, end) = TranscriptView.Window(total: 100, focus: 50, window: 10);

        Assert.InRange(50, start, end - 1);
        Assert.Equal(10, end - start);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void The_scroll_window_never_runs_past_either_end(int focus)
    {
        var (start, end) = TranscriptView.Window(total: 100, focus: focus, window: 10);

        Assert.True(start >= 0);
        Assert.True(end <= 100);
        Assert.Equal(10, end - start);
        Assert.InRange(focus, start, end - 1);
    }

    [Fact]
    public void An_empty_transcript_has_an_empty_window()
    {
        Assert.Equal((0, 0), TranscriptView.Window(total: 0, focus: 0, window: 10));
    }

    [Fact]
    public void The_built_in_agent_stylesheet_is_embedded_and_parses_as_css()
    {
        var css = AgentStyles.Resolve(null);

        Assert.Contains("Row", css, StringComparison.Ordinal);
        Assert.Contains(".tool", css, StringComparison.Ordinal);
        Assert.Contains("agent", AgentStyles.BuiltinNames());
    }

    [Fact]
    public void Inline_css_passes_through_unresolved()
    {
        const string inline = "Row { color: red }";

        Assert.Equal(inline, AgentStyles.Resolve(inline));
    }

    [Fact]
    public void An_unknown_stylesheet_name_is_an_error_naming_the_built_ins()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => AgentStyles.Resolve("no-such-sheet"));

        Assert.Contains("agent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_catalog_tool_converts_to_an_sdk_tool()
    {
        foreach (var spec in AgentTools.Catalog)
        {
            Assert.NotNull(CodingAgent.ToSdkTool(spec));
        }
    }

    [Fact]
    public void The_tool_summary_leads_with_the_interesting_operand()
    {
        var input = new Dictionary<string, JsonElement>
        {
            ["path"] = JsonSerializer.SerializeToElement("src/a.cs"),
            ["old_str"] = JsonSerializer.SerializeToElement("x"),
        };

        Assert.Equal("src/a.cs", CodingAgent.DescribeInput(input));
    }

    [Fact]
    public void The_tool_summary_of_a_command_shows_the_command()
    {
        var input = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement("dotnet test"),
        };

        Assert.Equal("dotnet test", CodingAgent.DescribeInput(input));
    }

    [Fact]
    public void The_default_system_prompt_states_the_workspace_and_the_edit_contract()
    {
        var prompt = CodingAgent.DefaultSystemPrompt("/work/repo");

        Assert.Contains("/work/repo", prompt, StringComparison.Ordinal);
        Assert.Contains("exactly once", prompt, StringComparison.Ordinal);
        Assert.Contains("not interactive", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("max")]
    [InlineData("HIGH")]
    public void Every_advertised_effort_level_maps(string level)
    {
        Assert.NotNull(InvokeAgentCommand.ParseEffort(level).ToString());
    }
}
