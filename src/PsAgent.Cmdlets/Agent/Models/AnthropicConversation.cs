using Anthropic;
using Anthropic.Models.Messages;

namespace PsAgent.Cmdlets.Agent.Models;

/// <summary>
/// A conversation over the Anthropic Messages API.
/// </summary>
/// <remarks>
/// History is kept in the SDK's own <see cref="MessageParam"/> shape. There is no
/// <c>.ToParam()</c> helper, so each response block is rebuilt as its <c>*Param</c> counterpart by
/// hand — and thinking blocks must carry their <c>Signature</c> verbatim, because the API rejects
/// a tampered one.
/// </remarks>
public sealed class AnthropicConversation : IModelConversation
{
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly Effort _effort;
    private readonly string _systemPrompt;
    private readonly List<ToolUnion> _tools;
    private readonly List<MessageParam> _messages = [];

    /// <summary>Build a conversation against the Messages API.</summary>
    public AnthropicConversation(
        AnthropicClient client,
        string model,
        string systemPrompt,
        IReadOnlyList<ToolSpec> tools,
        int maxTokens,
        Effort effort)
    {
        _client = client;
        _model = model;
        _systemPrompt = systemPrompt;
        _maxTokens = maxTokens;
        _effort = effort;
        _tools = [.. tools.Select(ToSdkTool)];
    }

    /// <inheritdoc/>
    public string Describe => "Anthropic Messages API";

    /// <summary>The running history, for inspection.</summary>
    public IReadOnlyList<MessageParam> History => _messages;

    /// <inheritdoc/>
    public void AddUser(string text) =>
        _messages.Add(new MessageParam { Role = Role.User, Content = text });

    /// <inheritdoc/>
    public void AddToolResults(IReadOnlyList<ModelToolResult> results)
    {
        // Every tool_use needs exactly one tool_result, and they must all travel in ONE user
        // message — splitting them trains the model out of asking for parallel calls.
        List<ContentBlockParam> blocks = [];
        foreach (var result in results)
        {
            blocks.Add(new ToolResultBlockParam
            {
                ToolUseID = result.Id,
                Content = result.Text,
                IsError = result.IsError,
            });
        }

        _messages.Add(new MessageParam { Role = Role.User, Content = blocks });
    }

    /// <inheritdoc/>
    public async Task<ModelReply> SendAsync(CancellationToken ct)
    {
        var response = await _client.Messages.Create(BuildParams(), cancellationToken: ct).ConfigureAwait(false);

        // A refusal is an HTTP 200 with no usable content — check before reading blocks.
        if (response.StopReason == "refusal")
        {
            return new ModelReply([], [], [], Refused: true,
                response.Usage.InputTokens, response.Usage.OutputTokens);
        }

        List<ContentBlockParam> echo = [];
        List<string> texts = [];
        List<string> thoughts = [];
        List<ModelToolCall> calls = [];

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                echo.Add(new TextBlockParam { Text = text.Text });
                if (!string.IsNullOrWhiteSpace(text.Text))
                {
                    texts.Add(text.Text);
                }
            }
            else if (block.TryPickThinking(out ThinkingBlock? thinking))
            {
                echo.Add(new ThinkingBlockParam
                {
                    Thinking = thinking.Thinking,
                    Signature = thinking.Signature,
                });

                if (!string.IsNullOrWhiteSpace(thinking.Thinking))
                {
                    thoughts.Add(thinking.Thinking);
                }
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
            {
                echo.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? use))
            {
                echo.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                calls.Add(new ModelToolCall(use.ID, use.Name, use.Input));
            }
        }

        _messages.Add(new MessageParam { Role = Role.Assistant, Content = echo });

        return new ModelReply(
            texts, thoughts, calls, Refused: false,
            response.Usage.InputTokens, response.Usage.OutputTokens);
    }

    private MessageCreateParams BuildParams() => new()
    {
        Model = _model,
        MaxTokens = _maxTokens,

        // The system prompt and tool list are the stable prefix; the breakpoint goes at its end so
        // the growing message history never invalidates it.
        System = new List<TextBlockParam>
        {
            new() { Text = _systemPrompt, CacheControl = new CacheControlEphemeral() },
        },
        Thinking = new ThinkingConfigAdaptive { Display = Display.Summarized },
        OutputConfig = new OutputConfig { Effort = _effort },
        Tools = _tools,
        Messages = _messages,
    };

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
}
