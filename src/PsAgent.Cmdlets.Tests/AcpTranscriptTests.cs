using System.Text.Json.Nodes;
using PsAgent.Cmdlets.Acp;
using PsAgent.Cmdlets.Agent;
using Xunit;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// The ACP update folder. Every case here is a wire shape taken from the protocol docs, so a
/// rename in the spec (or a wrong guess about one) fails a test rather than silently rendering an
/// empty transcript.
/// </summary>
public sealed class AcpTranscriptTests
{
    private static JsonNode Update(JsonObject update) =>
        new JsonObject { ["sessionId"] = "s1", ["update"] = update };

    private static JsonObject Chunk(string kind, string text) => new()
    {
        ["sessionUpdate"] = kind,
        ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
    };

    [Fact]
    public void An_agent_message_chunk_becomes_an_assistant_row()
    {
        var t = new AcpTranscript();

        t.Apply(Update(Chunk("agent_message_chunk", "hello")));

        var row = Assert.Single(t.Rows);
        Assert.Equal(AgentEventKind.Assistant, row.Kind);
        Assert.Equal("hello", row.Body);
    }

    /// <summary>Without coalescing, a streamed sentence would produce one transcript row per token.</summary>
    [Fact]
    public void Consecutive_chunks_of_the_same_kind_coalesce_into_one_row()
    {
        var t = new AcpTranscript();

        t.Apply(Update(Chunk("agent_message_chunk", "one ")));
        t.Apply(Update(Chunk("agent_message_chunk", "two ")));
        t.Apply(Update(Chunk("agent_message_chunk", "three")));

        var row = Assert.Single(t.Rows);
        Assert.Equal("one two three", row.Body);
    }

    [Fact]
    public void A_different_chunk_kind_starts_a_new_row()
    {
        var t = new AcpTranscript();

        t.Apply(Update(Chunk("agent_thought_chunk", "hmm")));
        t.Apply(Update(Chunk("agent_message_chunk", "answer")));

        Assert.Equal(
            [AgentEventKind.Thought, AgentEventKind.Assistant],
            t.Rows.Select(r => r.Kind));
    }

    [Fact]
    public void A_thought_chunk_carrying_a_thinking_field_is_still_read()
    {
        var t = new AcpTranscript();

        t.Apply(Update(new JsonObject
        {
            ["sessionUpdate"] = "agent_thought_chunk",
            ["content"] = new JsonObject { ["thinking"] = "reasoning" },
        }));

        Assert.Equal("reasoning", Assert.Single(t.Rows).Body);
    }

    [Fact]
    public void A_tool_call_becomes_a_tool_row_carrying_its_status()
    {
        var t = new AcpTranscript();

        t.Apply(Update(new JsonObject
        {
            ["sessionUpdate"] = "tool_call",
            ["toolCallId"] = "call_001",
            ["title"] = "Reading configuration file",
            ["kind"] = "read",
            ["status"] = "pending",
        }));

        var row = Assert.Single(t.Rows);
        Assert.Equal(AgentEventKind.ToolCall, row.Kind);
        Assert.Equal("Reading configuration file", row.Title);
        Assert.Equal("read", row.ToolName);
        Assert.Equal(AgentToolStatus.Pending, row.Status);
    }

    /// <summary>
    /// A tool_call_update must land on the row the tool_call created, matched by toolCallId —
    /// appending instead would show every call twice.
    /// </summary>
    [Fact]
    public void A_tool_call_update_mutates_the_original_row_in_place()
    {
        var t = new AcpTranscript();
        t.Apply(Update(new JsonObject
        {
            ["sessionUpdate"] = "tool_call",
            ["toolCallId"] = "call_001",
            ["title"] = "Reading",
            ["kind"] = "read",
            ["status"] = "pending",
        }));

        t.Apply(Update(new JsonObject
        {
            ["sessionUpdate"] = "tool_call_update",
            ["toolCallId"] = "call_001",
            ["status"] = "completed",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "content",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "file body" },
            }),
        }));

        var row = Assert.Single(t.Rows);
        Assert.Equal(AgentToolStatus.Completed, row.Status);
        Assert.Contains("file body", row.Body, StringComparison.Ordinal);
        Assert.Contains("ok", row.Class, StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_for_an_unseen_call_is_shown_rather_than_dropped()
    {
        var t = new AcpTranscript();

        t.Apply(Update(new JsonObject
        {
            ["sessionUpdate"] = "tool_call_update",
            ["toolCallId"] = "orphan",
            ["status"] = "failed",
        }));

        Assert.Equal(AgentToolStatus.Failed, Assert.Single(t.Rows).Status);
    }

    [Fact]
    public void A_failed_tool_row_is_classed_as_an_error()
    {
        var t = new AcpTranscript();

        t.Apply(Update(new JsonObject
        {
            ["sessionUpdate"] = "tool_call",
            ["toolCallId"] = "c",
            ["title"] = "boom",
            ["status"] = "failed",
        }));

        Assert.Contains("error", Assert.Single(t.Rows).Class, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_renders_its_entries_with_completion_marks()
    {
        var t = new AcpTranscript();

        t.Apply(Update(new JsonObject
        {
            ["sessionUpdate"] = "plan",
            ["entries"] = new JsonArray(
                new JsonObject { ["content"] = "step one", ["status"] = "completed" },
                new JsonObject { ["content"] = "step two", ["status"] = "pending" }),
        }));

        var row = Assert.Single(t.Rows);
        Assert.Equal(AgentEventKind.Plan, row.Kind);
        Assert.Equal("plan (1/2 done)", row.Title);
        Assert.Contains("[x] step one", row.Body, StringComparison.Ordinal);
        Assert.Contains("[ ] step two", row.Body, StringComparison.Ordinal);
    }

    /// <summary>ACP grows new update kinds; an unknown one must not break a live session.</summary>
    [Theory]
    [InlineData("usage_update")]
    [InlineData("current_mode_update")]
    [InlineData("something_invented_next_year")]
    public void An_unknown_update_kind_is_ignored(string kind)
    {
        var t = new AcpTranscript();

        t.Apply(Update(new JsonObject { ["sessionUpdate"] = kind }));

        Assert.Empty(t.Rows);
    }

    [Fact]
    public void A_malformed_notification_is_ignored()
    {
        var t = new AcpTranscript();

        t.Apply(null);
        t.Apply(new JsonObject { ["sessionId"] = "s1" });

        Assert.Empty(t.Rows);
    }

    [Fact]
    public void A_manually_added_row_closes_the_open_chunk()
    {
        var t = new AcpTranscript();
        t.Apply(Update(Chunk("agent_message_chunk", "first")));

        t.Add(new AgentEvent { Kind = AgentEventKind.User, Title = "next question" });
        t.Apply(Update(Chunk("agent_message_chunk", "second")));

        Assert.Equal(3, t.Rows.Count);
        Assert.Equal("second", t.Rows[2].Body);
    }

    [Fact]
    public void ContentText_names_non_text_blocks_rather_than_dropping_them()
    {
        Assert.Equal("[image]", AcpTranscript.ContentText(new JsonObject { ["type"] = "image" }));
        Assert.Equal(
            "[link file:///a]",
            AcpTranscript.ContentText(new JsonObject { ["type"] = "resource_link", ["uri"] = "file:///a" }));
    }

    [Fact]
    public void ContentText_reads_an_embedded_resource()
    {
        var block = new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = "file:///a",
                ["contents"] = new JsonObject { ["type"] = "text", ["text"] = "inner" },
            },
        };

        Assert.Equal("inner", AcpTranscript.ContentText(block));
    }

    [Theory]
    [InlineData("pending", AgentToolStatus.Pending)]
    [InlineData("in_progress", AgentToolStatus.InProgress)]
    [InlineData("completed", AgentToolStatus.Completed)]
    [InlineData("failed", AgentToolStatus.Failed)]
    [InlineData("nonsense", AgentToolStatus.None)]
    public void ParseStatus_maps_the_acp_enum(string wire, AgentToolStatus expected)
    {
        Assert.Equal(expected, AcpTranscript.ParseStatus(wire));
    }

    [Fact]
    public void A_diff_in_a_tool_call_renders_its_path_and_new_text()
    {
        var body = AcpTranscript.ToolBody(new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "diff",
                ["path"] = "src/a.cs",
                ["newText"] = "var x = 1;",
            }),
        });

        Assert.Contains("src/a.cs", body, StringComparison.Ordinal);
        Assert.Contains("var x = 1;", body, StringComparison.Ordinal);
    }
}
