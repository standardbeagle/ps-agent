using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using PsAgent.Cmdlets.Agent.Models;

namespace PsAgent.Cmdlets.Agent;

/// <summary>Knobs for one <see cref="CodingAgent"/> session.</summary>
public sealed class CodingAgentOptions
{
    /// <summary>Model id.</summary>
    public string Model { get; set; } = "claude-opus-5";

    /// <summary>
    /// Per-response output ceiling. 16000 is the documented default for the non-streaming path —
    /// enough for a substantial edit, low enough to stay inside the SDK's HTTP timeout.
    /// </summary>
    public int MaxTokens { get; set; } = 16_000;

    /// <summary>Reasoning effort, where the API has the concept. Anthropic only.</summary>
    public Effort Effort { get; set; } = Effort.High;

    /// <summary>
    /// Hard ceiling on model round-trips in one turn, so a tool-call loop that fails to converge
    /// stops instead of billing forever.
    /// </summary>
    public int MaxTurns { get; set; } = 40;

    /// <summary>Emit reasoning as <see cref="AgentEventKind.Thought"/> rows.</summary>
    public bool ShowThinking { get; set; } = true;

    /// <summary>Overrides the built-in system prompt when set.</summary>
    public string? SystemPrompt { get; set; }
}

/// <summary>
/// The agent loop: send the conversation, run whatever tools come back, send the results, repeat
/// until the model stops asking for tools. That loop is the whole of a minimal coding agent — the
/// capability comes from <see cref="AgentTools"/>, not from the orchestration.
/// </summary>
/// <remarks>
/// <para>The loop is written once and works against any API, because the wire format lives behind
/// <see cref="IModelConversation"/>. Anthropic and any OpenAI-compatible endpoint therefore share
/// the tool dispatch, the approval gate, the transcript and the viewer.</para>
/// <para>Deliberately the <b>manual</b> loop rather than an SDK's tool runner: this agent needs to
/// interleave an approval prompt and a transcript event around every single call, and observe each
/// result as it happens so the UI can paint mid-turn.</para>
/// <para>Non-streaming. Each turn is one request, which keeps the reconstruction of the assistant
/// echo (thinking signatures, tool-call arguments) exact — a streamed rebuild of those is the one
/// place this could silently corrupt a conversation.</para>
/// </remarks>
public sealed class CodingAgent
{
    private readonly IModelConversation _conversation;
    private readonly AgentTools _tools;
    private readonly CodingAgentOptions _options;

    /// <summary>Create an agent over a conversation and a tool executor.</summary>
    public CodingAgent(IModelConversation conversation, AgentTools tools, CodingAgentOptions options)
    {
        _conversation = conversation;
        _tools = tools;
        _options = options;
    }

    /// <summary>Cumulative input tokens billed this session.</summary>
    public long InputTokens { get; private set; }

    /// <summary>Cumulative output tokens billed this session.</summary>
    public long OutputTokens { get; private set; }

    /// <summary>Which transport is in use, for the transcript.</summary>
    public string Transport => _conversation.Describe;

    /// <summary>
    /// Run one user turn to completion, calling <paramref name="emit"/> for every transcript row as
    /// it happens (so a UI can paint mid-turn rather than after it).
    /// </summary>
    public async Task RunTurnAsync(string userText, Action<AgentEvent> emit, CancellationToken ct = default)
    {
        emit(new AgentEvent { Kind = AgentEventKind.User, Title = userText, Body = userText });
        _conversation.AddUser(userText);

        for (var turn = 0; turn < _options.MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();

            ModelReply reply;
            try
            {
                reply = await _conversation.SendAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                emit(new AgentEvent
                {
                    Kind = AgentEventKind.Error,
                    Title = $"API call failed: {e.Message}",
                    Body = e.ToString(),
                });
                return;
            }

            InputTokens += reply.InputTokens;
            OutputTokens += reply.OutputTokens;

            if (reply.Refused)
            {
                emit(new AgentEvent
                {
                    Kind = AgentEventKind.Error,
                    Title = "The model declined this request.",
                    Body = "the API reported a refusal, so the response carried no usable content",
                });
                return;
            }

            if (_options.ShowThinking)
            {
                foreach (var thought in reply.Thoughts)
                {
                    emit(new AgentEvent
                    {
                        Kind = AgentEventKind.Thought,
                        Title = AgentEvent.Truncate(AgentEvent.Flatten(thought), 120),
                        Body = thought,
                    });
                }
            }

            foreach (var text in reply.Texts)
            {
                emit(new AgentEvent
                {
                    Kind = AgentEventKind.Assistant,
                    Title = AgentEvent.Truncate(AgentEvent.Flatten(text), 120),
                    Body = text,
                });
            }

            if (reply.ToolCalls.Count == 0)
            {
                emit(new AgentEvent
                {
                    Kind = AgentEventKind.Status,
                    Title = $"done · {InputTokens} in / {OutputTokens} out tokens",
                });
                return;
            }

            List<ModelToolResult> results = [];
            foreach (var call in reply.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();

                emit(new AgentEvent
                {
                    Kind = AgentEventKind.ToolCall,
                    Title = $"{call.Name} {DescribeInput(call.Input)}",
                    Body = FormatInput(call.Input),
                    ToolName = call.Name,
                    ToolCallId = call.Id,
                    Status = AgentToolStatus.InProgress,
                });

                var outcome = _tools.Execute(call.Name, call.Input);

                emit(new AgentEvent
                {
                    Kind = AgentEventKind.ToolResult,
                    Title = outcome.Summary,
                    Body = outcome.Text,
                    ToolName = call.Name,
                    ToolCallId = call.Id,
                    Status = outcome.Ok ? AgentToolStatus.Completed : AgentToolStatus.Failed,
                });

                results.Add(new ModelToolResult(call.Id, call.Name, outcome.Text, !outcome.Ok));
            }

            _conversation.AddToolResults(results);
        }

        emit(new AgentEvent
        {
            Kind = AgentEventKind.Error,
            Title = $"Stopped after {_options.MaxTurns} model turns without finishing.",
            Body = "Raise -MaxTurns, or narrow the request.",
        });
    }

    /// <summary>A one-line rendering of a tool's arguments for the collapsed transcript row.</summary>
    internal static string DescribeInput(IReadOnlyDictionary<string, JsonElement> input)
    {
        // The interesting operand first, so the summary reads like a command rather than a dump.
        foreach (var key in (string[])["command", "path", "old_str"])
        {
            if (input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return AgentEvent.Truncate(AgentEvent.Flatten(v.GetString() ?? string.Empty), 80);
            }
        }

        return input.Count == 0 ? string.Empty : $"({input.Count} args)";
    }

    /// <summary>The expanded, per-argument rendering.</summary>
    internal static string FormatInput(IReadOnlyDictionary<string, JsonElement> input)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in input)
        {
            var rendered = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            sb.Append(key).Append(": ").Append(rendered).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Convert a transport-free tool spec into the Anthropic SDK's shape.</summary>
    internal static ToolUnion ToSdkTool(ToolSpec spec) => AnthropicConversation.ToSdkTool(spec);

    /// <summary>
    /// The built-in system prompt. Short on persona, specific about the two things the model
    /// cannot discover for itself: that edits must be uniquely anchored, and that it should verify
    /// its own work rather than declare success.
    /// </summary>
    internal static string DefaultSystemPrompt(string workspaceRoot) =>
        $"""
        You are a coding agent working in the directory {workspaceRoot} on behalf of a developer at a terminal.

        Work directly: use your tools to look at the code before answering questions about it, and to
        make changes rather than describing changes for the user to make. Prefer several small,
        verifiable steps over one large speculative one.

        Tool notes:
        - read_file before edit_file. The old_str you pass must appear exactly once in the file, so
          include enough surrounding lines to be unique.
        - run_bash is not interactive. Never start a command that waits for input, tails a log, or
          runs a server in the foreground.
        - Paths are confined to the workspace root. A path outside it will be refused; do not retry
          a refused path, tell the user instead.

        When you have made a change, verify it — build it, run the test, or read the file back —
        before you report it as done. If a command fails, read the error and fix the cause. If you
        cannot finish something, say plainly what is left and why.

        Keep prose short. The user is reading a terminal transcript, not a report.
        """;
}
