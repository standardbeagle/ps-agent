using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace PsAgent.Cmdlets.Agent;

/// <summary>Knobs for one <see cref="CodingAgent"/> session.</summary>
public sealed class CodingAgentOptions
{
    /// <summary>Model id. Defaults to Claude Opus 5.</summary>
    public string Model { get; set; } = "claude-opus-5";

    /// <summary>
    /// Per-response output ceiling. 16000 is the documented default for the non-streaming path —
    /// enough for a substantial edit, low enough to stay inside the SDK's HTTP timeout.
    /// </summary>
    public int MaxTokens { get; set; } = 16_000;

    /// <summary>Reasoning effort. <c>high</c> is the API default; drop it for cheap mechanical work.</summary>
    public Effort Effort { get; set; } = Effort.High;

    /// <summary>
    /// Hard ceiling on model round-trips in one turn, so a tool-call loop that fails to converge
    /// stops instead of billing forever.
    /// </summary>
    public int MaxTurns { get; set; } = 40;

    /// <summary>Emit summarized reasoning as <see cref="AgentEventKind.Thought"/> rows.</summary>
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
/// <para>Deliberately the <b>manual</b> loop rather than the SDK's <c>BetaToolRunner</c>: the
/// runner owns tool execution, and this agent needs to interleave an approval prompt and a
/// transcript event around every single call. Owning the loop is what makes that possible.</para>
/// <para>Non-streaming. Each turn is one <c>Messages.Create</c>, which keeps the reconstruction of
/// the assistant echo (thinking signatures, tool_use inputs) exact — a streamed rebuild of those
/// blocks is the one place this could silently corrupt a conversation. The UI shows a live
/// status row instead, so a long turn does not read as a frozen terminal.</para>
/// </remarks>
public sealed class CodingAgent
{
    private readonly AnthropicClient _client;
    private readonly AgentTools _tools;
    private readonly CodingAgentOptions _options;
    private readonly List<MessageParam> _messages = [];

    /// <summary>Create an agent over an existing client and tool executor.</summary>
    public CodingAgent(AnthropicClient client, AgentTools tools, CodingAgentOptions options)
    {
        _client = client;
        _tools = tools;
        _options = options;
    }

    /// <summary>The running conversation. Survives across turns, which is what makes this a session.</summary>
    public IReadOnlyList<MessageParam> History => _messages;

    /// <summary>Cumulative input tokens billed this session.</summary>
    public long InputTokens { get; private set; }

    /// <summary>Cumulative output tokens billed this session.</summary>
    public long OutputTokens { get; private set; }

    /// <summary>The system prompt actually in use.</summary>
    public string SystemPrompt => _options.SystemPrompt ?? DefaultSystemPrompt(_tools.Root);

    /// <summary>
    /// Run one user turn to completion, calling <paramref name="emit"/> for every transcript row as
    /// it happens (so a UI can paint mid-turn rather than after it).
    /// </summary>
    public async Task RunTurnAsync(string userText, Action<AgentEvent> emit, CancellationToken ct = default)
    {
        emit(new AgentEvent { Kind = AgentEventKind.User, Title = userText, Body = userText });
        _messages.Add(new MessageParam { Role = Role.User, Content = userText });

        for (var turn = 0; turn < _options.MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();

            Message response;
            try
            {
                response = await _client.Messages.Create(BuildParams(), cancellationToken: ct).ConfigureAwait(false);
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

            InputTokens += response.Usage.InputTokens;
            OutputTokens += response.Usage.OutputTokens;

            // A refusal is an HTTP 200 with no usable content — check before reading blocks.
            if (response.StopReason == "refusal")
            {
                emit(new AgentEvent
                {
                    Kind = AgentEventKind.Error,
                    Title = "The model declined this request.",
                    Body = "stop_reason = refusal",
                });
                return;
            }

            var (assistantBlocks, toolUses) = Project(response, emit);
            _messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantBlocks });

            if (toolUses.Count == 0)
            {
                emit(new AgentEvent
                {
                    Kind = AgentEventKind.Status,
                    Title = $"done · {InputTokens} in / {OutputTokens} out tokens",
                });
                return;
            }

            // Every tool_use needs exactly one tool_result, and they must all travel in ONE user
            // message — splitting them trains the model out of asking for parallel calls.
            List<ContentBlockParam> results = [];
            foreach (var use in toolUses)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = _tools.Execute(use.Name, use.Input);

                emit(new AgentEvent
                {
                    Kind = AgentEventKind.ToolResult,
                    Title = outcome.Summary,
                    Body = outcome.Text,
                    ToolName = use.Name,
                    ToolCallId = use.ID,
                    Status = outcome.Ok ? AgentToolStatus.Completed : AgentToolStatus.Failed,
                });

                results.Add(new ToolResultBlockParam
                {
                    ToolUseID = use.ID,
                    Content = outcome.Text,
                    IsError = !outcome.Ok,
                });
            }

            _messages.Add(new MessageParam { Role = Role.User, Content = results });
        }

        emit(new AgentEvent
        {
            Kind = AgentEventKind.Error,
            Title = $"Stopped after {_options.MaxTurns} model turns without finishing.",
            Body = "Raise -MaxTurns, or narrow the request.",
        });
    }

    private MessageCreateParams BuildParams() => new()
    {
        Model = _options.Model,
        MaxTokens = _options.MaxTokens,

        // The system prompt and tool list are the stable prefix; the breakpoint goes at its end so
        // the growing message history never invalidates it.
        System = new List<TextBlockParam>
        {
            new() { Text = SystemPrompt, CacheControl = new CacheControlEphemeral() },
        },
        Thinking = new ThinkingConfigAdaptive { Display = Display.Summarized },
        OutputConfig = new OutputConfig { Effort = _options.Effort },
        Tools = [.. AgentTools.Catalog.Select(ToSdkTool)],
        Messages = _messages,
    };

    /// <summary>
    /// Turn one API response into (a) the assistant blocks to echo back and (b) the tool calls to
    /// run, emitting a transcript row per block on the way.
    /// </summary>
    /// <remarks>
    /// There is no <c>.ToParam()</c> in the C# SDK — each response block has to be rebuilt as its
    /// <c>*Param</c> counterpart by hand. Thinking blocks must carry their <c>Signature</c>
    /// verbatim; the API rejects a tampered one.
    /// </remarks>
    private (List<ContentBlockParam> Blocks, List<ToolUseBlock> ToolUses) Project(
        Message response, Action<AgentEvent> emit)
    {
        List<ContentBlockParam> blocks = [];
        List<ToolUseBlock> toolUses = [];

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                blocks.Add(new TextBlockParam { Text = text.Text });
                if (!string.IsNullOrWhiteSpace(text.Text))
                {
                    emit(new AgentEvent
                    {
                        Kind = AgentEventKind.Assistant,
                        Title = AgentEvent.Truncate(AgentEvent.Flatten(text.Text), 120),
                        Body = text.Text,
                    });
                }
            }
            else if (block.TryPickThinking(out ThinkingBlock? thinking))
            {
                blocks.Add(new ThinkingBlockParam
                {
                    Thinking = thinking.Thinking,
                    Signature = thinking.Signature,
                });

                // With Display.Summarized the text is a readable summary; with the default it is
                // empty, and an empty thought row is noise.
                if (_options.ShowThinking && !string.IsNullOrWhiteSpace(thinking.Thinking))
                {
                    emit(new AgentEvent
                    {
                        Kind = AgentEventKind.Thought,
                        Title = AgentEvent.Truncate(AgentEvent.Flatten(thinking.Thinking), 120),
                        Body = thinking.Thinking,
                    });
                }
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
            {
                blocks.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? use))
            {
                blocks.Add(new ToolUseBlockParam
                {
                    ID = use.ID,
                    Name = use.Name,
                    Input = use.Input,
                });
                toolUses.Add(use);

                emit(new AgentEvent
                {
                    Kind = AgentEventKind.ToolCall,
                    Title = $"{use.Name} {DescribeInput(use.Input)}",
                    Body = FormatInput(use.Input),
                    ToolName = use.Name,
                    ToolCallId = use.ID,
                    Status = AgentToolStatus.InProgress,
                });
            }
        }

        return (blocks, toolUses);
    }

    /// <summary>Convert a transport-free <see cref="ToolSpec"/> into the SDK's tool shape.</summary>
    internal static ToolUnion ToSdkTool(ToolSpec spec) => new Tool
    {
        Name = spec.Name,
        Description = spec.Description,
        InputSchema = new()
        {
            Properties = spec.Properties.ToDictionary(kv => kv.Key, kv => kv.Value),
            Required = [.. spec.Required],
        },
    };

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
