using System.Text;
using System.Text.Json.Nodes;
using PsAgent.Cmdlets.Agent;

namespace PsAgent.Cmdlets.Acp;

/// <summary>
/// Folds the ACP <c>session/update</c> stream into the same <see cref="AgentEvent"/> rows the
/// local agent produces, so one renderer and one stylesheet serve both cmdlets.
/// </summary>
/// <remarks>
/// <para>Two things make this more than a <c>switch</c>. First, message and thought updates arrive
/// as many small <b>chunks</b>; appending one transcript row per chunk would produce a row per
/// token. Consecutive chunks of the same kind are therefore coalesced into the row that is
/// currently open. Second, a tool call is reported twice — <c>tool_call</c> when it starts and
/// <c>tool_call_update</c> as it progresses — so updates are matched back to the original row by
/// <c>toolCallId</c> and applied in place rather than appended.</para>
/// <para>Pure and process-free: it takes parsed JSON and returns rows, so the whole ACP rendering
/// path is unit-testable with no subprocess and no agent installed.</para>
/// </remarks>
public sealed class AcpTranscript
{
    private readonly List<AgentEvent> _rows = [];
    private readonly Dictionary<string, int> _toolRows = [];
    private int _openChunkRow = -1;
    private AgentEventKind _openChunkKind;

    /// <summary>The transcript so far, oldest first.</summary>
    public IReadOnlyList<AgentEvent> Rows => _rows;

    /// <summary>Append a row that did not come from the agent (an operator prompt, a status note).</summary>
    public void Add(AgentEvent row)
    {
        CloseChunk();
        _rows.Add(row);
    }

    /// <summary>
    /// Apply one <c>session/update</c> notification's <c>params</c>. Unknown
    /// <c>sessionUpdate</c> kinds are ignored rather than rejected — ACP adds variants over time,
    /// and an unrecognised one is not a reason to break the session.
    /// </summary>
    public void Apply(JsonNode? notificationParams)
    {
        if (notificationParams?["update"] is not JsonObject update)
        {
            return;
        }

        var kind = Str(update, "sessionUpdate");
        switch (kind)
        {
            case "agent_message_chunk":
                Chunk(AgentEventKind.Assistant, ContentText(update["content"]));
                break;

            case "agent_thought_chunk":
                Chunk(AgentEventKind.Thought, ContentText(update["content"]));
                break;

            case "user_message_chunk":
                Chunk(AgentEventKind.User, ContentText(update["content"]));
                break;

            case "tool_call":
                StartToolCall(update);
                break;

            case "tool_call_update":
                UpdateToolCall(update);
                break;

            case "plan":
                CloseChunk();
                _rows.Add(new AgentEvent
                {
                    Kind = AgentEventKind.Plan,
                    Title = PlanTitle(update["entries"]),
                    Body = PlanBody(update["entries"]),
                });
                break;

            default:
                // available_commands_update / current_mode_update / usage_update and anything
                // newer: no transcript row.
                break;
        }
    }

    private void Chunk(AgentEventKind kind, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (_openChunkRow >= 0 && _openChunkKind == kind)
        {
            var row = _rows[_openChunkRow];
            var body = row.Body + text;
            _rows[_openChunkRow] = row with
            {
                Body = body,
                Title = AgentEvent.Truncate(AgentEvent.Flatten(body), 120),
            };
            return;
        }

        CloseChunk();
        _rows.Add(new AgentEvent
        {
            Kind = kind,
            Title = AgentEvent.Truncate(AgentEvent.Flatten(text), 120),
            Body = text,
        });
        _openChunkRow = _rows.Count - 1;
        _openChunkKind = kind;
    }

    /// <summary>Stop appending to the currently open chunk row, so the next chunk starts a new one.</summary>
    private void CloseChunk() => _openChunkRow = -1;

    private void StartToolCall(JsonObject update)
    {
        CloseChunk();

        var id = Str(update, "toolCallId");
        var title = Str(update, "title");
        var toolKind = Str(update, "kind");

        var row = new AgentEvent
        {
            Kind = AgentEventKind.ToolCall,
            Title = title.Length > 0 ? title : (toolKind.Length > 0 ? toolKind : "tool call"),
            Body = ToolBody(update),
            ToolName = toolKind.Length > 0 ? toolKind : null,
            ToolCallId = id.Length > 0 ? id : null,
            Status = ParseStatus(Str(update, "status")),
        };

        _rows.Add(row);
        if (id.Length > 0)
        {
            _toolRows[id] = _rows.Count - 1;
        }
    }

    private void UpdateToolCall(JsonObject update)
    {
        CloseChunk();

        var id = Str(update, "toolCallId");
        if (id.Length == 0 || !_toolRows.TryGetValue(id, out var index))
        {
            // An update for a call we never saw start: show it rather than drop it.
            StartToolCall(update);
            return;
        }

        var row = _rows[index];
        var title = Str(update, "title");
        var status = Str(update, "status");
        var body = ToolBody(update);

        _rows[index] = row with
        {
            Title = title.Length > 0 ? title : row.Title,
            Status = status.Length > 0 ? ParseStatus(status) : row.Status,
            Body = body.Length > 0 ? (row.Body.Length > 0 ? row.Body + "\n" + body : body) : row.Body,
        };
    }

    /// <summary>Map ACP's <c>status</c> enum onto ours.</summary>
    internal static AgentToolStatus ParseStatus(string status) => status switch
    {
        "pending" => AgentToolStatus.Pending,
        "in_progress" => AgentToolStatus.InProgress,
        "completed" => AgentToolStatus.Completed,
        "failed" => AgentToolStatus.Failed,
        _ => AgentToolStatus.None,
    };

    /// <summary>
    /// Text of an ACP <c>ContentBlock</c>. Only <c>text</c> renders as prose; an image or an
    /// embedded resource is announced by type so the transcript does not silently lose it.
    /// </summary>
    internal static string ContentText(JsonNode? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonArray array)
        {
            var sb = new StringBuilder();
            foreach (var item in array)
            {
                sb.Append(ContentText(item));
            }

            return sb.ToString();
        }

        if (content is not JsonObject obj)
        {
            return content.ToString();
        }

        // Some agents put the reasoning under `thinking` rather than a text block.
        if (obj["text"] is { } text)
        {
            return text.GetValue<string>();
        }

        if (obj["thinking"] is { } thinking)
        {
            return thinking.GetValue<string>();
        }

        return Str(obj, "type") switch
        {
            "image" => "[image]",
            "audio" => "[audio]",
            "resource_link" => $"[link {Str(obj, "uri")}]",
            "resource" => ContentText(obj["resource"]?["contents"]),
            _ => string.Empty,
        };
    }

    /// <summary>Render a tool call's payload: its content list, plus any diff, as expandable text.</summary>
    internal static string ToolBody(JsonObject update)
    {
        var sb = new StringBuilder();

        if (update["rawInput"] is { } raw)
        {
            sb.Append(raw.ToJsonString()).Append('\n');
        }

        if (update["content"] is JsonArray items)
        {
            foreach (var item in items)
            {
                if (item is not JsonObject entry)
                {
                    continue;
                }

                switch (Str(entry, "type"))
                {
                    case "diff":
                        sb.Append("--- ").Append(Str(entry, "path")).Append('\n');
                        sb.Append(Str(entry, "newText")).Append('\n');
                        break;

                    case "terminal":
                        sb.Append("[terminal ").Append(Str(entry, "terminalId")).Append("]\n");
                        break;

                    default:
                        sb.Append(ContentText(entry["content"] ?? entry)).Append('\n');
                        break;
                }
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string PlanTitle(JsonNode? entries)
    {
        if (entries is not JsonArray array || array.Count == 0)
        {
            return "plan";
        }

        var done = array.Count(e => e is JsonObject o && Str(o, "status") == "completed");
        return $"plan ({done}/{array.Count} done)";
    }

    private static string PlanBody(JsonNode? entries)
    {
        if (entries is not JsonArray array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var entry in array)
        {
            if (entry is not JsonObject o)
            {
                continue;
            }

            var mark = Str(o, "status") switch
            {
                "completed" => "[x]",
                "in_progress" => "[~]",
                _ => "[ ]",
            };
            sb.Append(mark).Append(' ').Append(Str(o, "content")).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string Str(JsonObject obj, string key) =>
        obj[key] is { } n && n.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? n.GetValue<string>()
            : string.Empty;
}
