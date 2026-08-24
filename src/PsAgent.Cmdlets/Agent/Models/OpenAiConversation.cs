using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsAgent.Cmdlets.Agent.Models;

/// <summary>
/// A conversation over the OpenAI-compatible <c>/chat/completions</c> shape.
/// </summary>
/// <remarks>
/// <para>Written directly against the wire format with <see cref="HttpClient"/> rather than a
/// vendor SDK, because "OpenAI-compatible" is the point: the same code has to work against
/// whatever endpoint a browser sign-in gave a token for, and an SDK that validates its own
/// vendor's quirks gets in the way of that.</para>
/// <para>Deliberate shape choices, each of which breaks something if done the other way:</para>
/// <list type="bullet">
/// <item>A tool result is a <c>role: "tool"</c> message carrying <c>tool_call_id</c>, and it must
/// follow the assistant message that requested it. Out of order, the API rejects the whole
/// conversation rather than the offending message.</item>
/// <item><c>function.arguments</c> is a <b>JSON string</b>, not an object, in both directions.</item>
/// <item>Reasoning arrives under different names by vendor (<c>reasoning_content</c>,
/// <c>reasoning</c>), and from none of them on the OpenAI endpoint itself, so it is read
/// opportunistically and never required.</item>
/// </list>
/// </remarks>
public sealed class OpenAiConversation : IModelConversation
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly JsonArray _messages = [];
    private readonly JsonArray _tools;

    /// <summary>Build a conversation against an OpenAI-compatible endpoint.</summary>
    /// <param name="http">Client carrying the Authorization header.</param>
    /// <param name="baseUrl">API root, e.g. <c>https://api.openai.com/v1</c>.</param>
    /// <param name="model">Model id.</param>
    /// <param name="systemPrompt">The system message.</param>
    /// <param name="tools">Tools to advertise.</param>
    /// <param name="maxTokens">Output ceiling.</param>
    public OpenAiConversation(
        HttpClient http,
        string baseUrl,
        string model,
        string systemPrompt,
        IReadOnlyList<ToolSpec> tools,
        int maxTokens)
    {
        _http = http;
        _endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        _model = model;
        _maxTokens = maxTokens;
        _tools = ToolsPayload(tools);
        _messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
    }

    /// <inheritdoc/>
    public string Describe => $"OpenAI-compatible ({_endpoint})";

    /// <summary>The tool list in OpenAI's function-calling shape. Pure, so the schema is testable.</summary>
    public static JsonArray ToolsPayload(IReadOnlyList<ToolSpec> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            var properties = new JsonObject();
            foreach (var (name, schema) in tool.Properties)
            {
                properties[name] = JsonNode.Parse(schema.GetRawText());
            }

            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = new JsonArray([.. tool.Required.Select(r => (JsonNode)r!)]),
                    },
                },
            });
        }

        return array;
    }

    /// <inheritdoc/>
    public void AddUser(string text) =>
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });

    /// <inheritdoc/>
    public void AddToolResults(IReadOnlyList<ModelToolResult> results)
    {
        foreach (var result in results)
        {
            _messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = result.Id,
                ["name"] = result.Name,
                // There is no is_error flag in this shape, so a failure has to read as one in the
                // text or the model treats it as a successful result.
                ["content"] = result.IsError ? "ERROR: " + result.Text : result.Text,
            });
        }
    }

    /// <summary>The request body. Pure, so the payload is testable without a server.</summary>
    public JsonObject BuildRequest() => new()
    {
        ["model"] = _model,
        ["messages"] = _messages.DeepClone(),
        ["tools"] = _tools.DeepClone(),
        ["max_tokens"] = _maxTokens,
        ["stream"] = false,
    };

    /// <inheritdoc/>
    public async Task<ModelReply> SendAsync(CancellationToken ct)
    {
        using var content = new StringContent(
            BuildRequest().ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{(int)response.StatusCode} from {_endpoint}: {Truncate(body, 500)}");
        }

        var (reply, assistantMessage) = ParseResponse(body);
        _messages.Add(assistantMessage);
        return reply;
    }

    /// <summary>
    /// Parse a completion into a normalised reply plus the assistant message to echo back.
    /// Pure, so every shape this has to survive is testable from a captured body.
    /// </summary>
    public static (ModelReply Reply, JsonObject AssistantMessage) ParseResponse(string body)
    {
        var root = JsonNode.Parse(body)?.AsObject()
            ?? throw new InvalidOperationException("completion response was not a JSON object");

        if (root["error"] is JsonObject error)
        {
            throw new InvalidOperationException(
                $"API error: {error["message"]?.ToString() ?? error.ToJsonString()}");
        }

        var choice = root["choices"]?.AsArray().FirstOrDefault()?.AsObject()
            ?? throw new InvalidOperationException("completion response carried no choices");
        var message = choice["message"]?.AsObject()
            ?? throw new InvalidOperationException("completion choice carried no message");

        List<string> texts = [];
        if (message["content"] is { } contentNode && contentNode.GetValueKind() == JsonValueKind.String)
        {
            var text = contentNode.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text);
            }
        }

        List<string> thoughts = [];
        foreach (var field in (string[])["reasoning_content", "reasoning"])
        {
            if (message[field] is { } r && r.GetValueKind() == JsonValueKind.String)
            {
                var thought = r.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(thought))
                {
                    thoughts.Add(thought);
                }
            }
        }

        List<ModelToolCall> calls = [];
        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var entry in toolCalls)
            {
                if (entry?["function"] is not JsonObject fn)
                {
                    continue;
                }

                var name = fn["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                calls.Add(new ModelToolCall(
                    entry["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n"),
                    name,
                    ParseArguments(fn["arguments"])));
            }
        }

        var usage = root["usage"]?.AsObject();
        var reply = new ModelReply(
            texts,
            thoughts,
            calls,
            Refused: string.Equals(choice["finish_reason"]?.GetValue<string>(), "content_filter", StringComparison.Ordinal),
            usage?["prompt_tokens"]?.GetValue<long>() ?? 0,
            usage?["completion_tokens"]?.GetValue<long>() ?? 0);

        // Echo the assistant message verbatim rather than rebuilding it: tool_calls must come back
        // exactly as issued or the follow-up tool messages have nothing to attach to.
        return (reply, message.DeepClone().AsObject());
    }

    /// <summary>
    /// Parse a tool call's arguments. The field is a JSON <b>string</b> containing an object, and
    /// models occasionally emit an empty string or malformed JSON for a no-argument call.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> ParseArguments(JsonNode? arguments)
    {
        var empty = new Dictionary<string, JsonElement>();
        if (arguments is null)
        {
            return empty;
        }

        var raw = arguments.GetValueKind() == JsonValueKind.String
            ? arguments.GetValue<string>()
            : arguments.ToJsonString();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return empty;
            }

            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone());
        }
        catch (JsonException)
        {
            // A malformed argument blob is the model's mistake; the tool reports it as a failed
            // call and the model gets to correct itself, which beats ending the turn.
            return empty;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
