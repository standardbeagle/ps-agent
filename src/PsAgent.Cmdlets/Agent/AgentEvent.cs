using System.Management.Automation;

namespace PsAgent.Cmdlets.Agent;

/// <summary>What a transcript row represents. Drives both the stylesheet class and the glyph.</summary>
public enum AgentEventKind
{
    /// <summary>Something the operator said.</summary>
    User,

    /// <summary>Model reasoning (Anthropic thinking blocks / ACP <c>agent_thought_chunk</c>).</summary>
    Thought,

    /// <summary>Model prose.</summary>
    Assistant,

    /// <summary>A tool the model asked for, with its arguments.</summary>
    ToolCall,

    /// <summary>What that tool returned.</summary>
    ToolResult,

    /// <summary>A plan / task list the agent published.</summary>
    Plan,

    /// <summary>Session lifecycle chatter (connected, model, token usage, stop reason).</summary>
    Status,

    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>How a tool call is going. Mirrors ACP's <c>status</c> enum so the ACP client maps 1:1.</summary>
public enum AgentToolStatus
{
    /// <summary>Not applicable — the row is not a tool call.</summary>
    None,

    /// <summary>Requested but not started.</summary>
    Pending,

    /// <summary>Executing.</summary>
    InProgress,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Finished unsuccessfully.</summary>
    Failed,
}

/// <summary>
/// One row of an agent transcript — the single currency both cmdlets deal in.
/// <see cref="InvokeAgentCommand"/> produces these from the Anthropic Messages API and
/// <see cref="InvokeAcpCommand"/> from ACP <c>session/update</c> notifications, so one
/// renderer (and one stylesheet) serves both, and both pipe into <c>Show-Styled</c>.
/// </summary>
/// <remarks>
/// This is a plain immutable record rather than a <see cref="PSObject"/> so the pure
/// transcript logic is unit-testable without a runspace; <see cref="ToPSObject"/> is the
/// only place the PowerShell shape is built.
/// </remarks>
public sealed record AgentEvent
{
    /// <summary>What this row is.</summary>
    public required AgentEventKind Kind { get; init; }

    /// <summary>The collapsed one-line summary. Becomes the <c>Name</c> property.</summary>
    public required string Title { get; init; }

    /// <summary>The expanded body (tool arguments, tool output, full message text). May be empty.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Tool name, when <see cref="Kind"/> is a tool row.</summary>
    public string? ToolName { get; init; }

    /// <summary>Correlates a <see cref="AgentEventKind.ToolCall"/> with its <see cref="AgentEventKind.ToolResult"/>.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Progress of a tool row.</summary>
    public AgentToolStatus Status { get; init; } = AgentToolStatus.None;

    /// <summary>When the row was created (UTC).</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Space-separated stylesheet classes. <c>Show-Styled</c>'s default <c>-ClassProperty</c> is
    /// <c>class</c>, so emitting this property is what lets an unmodified <c>Show-Styled</c> colour
    /// our rows without any per-call configuration.
    /// </summary>
    public string Class => Kind switch
    {
        AgentEventKind.User => "user",
        AgentEventKind.Thought => "thought muted",
        AgentEventKind.Assistant => "assistant",
        AgentEventKind.Plan => "plan",
        AgentEventKind.Status => "muted",
        AgentEventKind.Error => "error",
        AgentEventKind.ToolCall or AgentEventKind.ToolResult => Status switch
        {
            AgentToolStatus.Failed => "tool error",
            AgentToolStatus.Completed => "tool ok",
            AgentToolStatus.InProgress => "tool running",
            _ => "tool",
        },
        _ => string.Empty,
    };

    /// <summary>A leading glyph so the transcript reads without colour (piped, NO_COLOR, or a dumb terminal).</summary>
    public string Glyph => Kind switch
    {
        AgentEventKind.User => "›",
        AgentEventKind.Thought => "·",
        AgentEventKind.Assistant => "●",
        AgentEventKind.Plan => "☰",
        AgentEventKind.Status => "—",
        AgentEventKind.Error => "✘",
        AgentEventKind.ToolCall => "⚙",
        AgentEventKind.ToolResult => Status == AgentToolStatus.Failed ? "✘" : "✔",
        _ => " ",
    };

    /// <summary>The rendered summary line: glyph + title, single-line (newlines collapsed).</summary>
    public string Display => $"{Glyph} {Flatten(Title)}";

    /// <summary>
    /// Project to the pipeline shape: <c>PsAgent.AgentEvent</c> with a <c>Name</c> (what
    /// <c>Show-Styled</c> shows collapsed) and a <c>class</c> (what the stylesheet selects on).
    /// </summary>
    public PSObject ToPSObject()
    {
        var o = new PSObject();
        o.TypeNames.Insert(0, "PsAgent.AgentEvent");
        o.Properties.Add(new PSNoteProperty("Name", Display));
        o.Properties.Add(new PSNoteProperty("class", Class));
        o.Properties.Add(new PSNoteProperty("Kind", Kind.ToString()));
        o.Properties.Add(new PSNoteProperty("Title", Title));
        o.Properties.Add(new PSNoteProperty("Body", Body));
        o.Properties.Add(new PSNoteProperty("Tool", ToolName ?? string.Empty));
        o.Properties.Add(new PSNoteProperty("ToolCallId", ToolCallId ?? string.Empty));
        o.Properties.Add(new PSNoteProperty("Status", Status.ToString()));
        o.Properties.Add(new PSNoteProperty("Timestamp", Timestamp));
        return o;
    }

    /// <summary>Collapse a multi-line string to one line so a summary row can never break the layout.</summary>
    public static string Flatten(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var one = text.ReplaceLineEndings(" ").Replace('\t', ' ').Trim();
        while (one.Contains("  ", StringComparison.Ordinal))
        {
            one = one.Replace("  ", " ", StringComparison.Ordinal);
        }

        return one;
    }

    /// <summary>Shorten to <paramref name="max"/> characters with an ellipsis, for summary lines.</summary>
    public static string Truncate(string text, int max)
    {
        if (max <= 1 || text.Length <= max)
        {
            return text;
        }

        return text[..(max - 1)] + "…";
    }
}
