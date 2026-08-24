using System.Text.Json;

namespace PsAgent.Cmdlets.Agent.Models;

/// <summary>A tool the model asked for.</summary>
/// <param name="Id">Correlates the call with its result.</param>
/// <param name="Name">Tool name.</param>
/// <param name="Input">Parsed arguments.</param>
public sealed record ModelToolCall(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input);

/// <summary>What a tool run produced, on its way back to the model.</summary>
/// <param name="Id">The call's id.</param>
/// <param name="Name">Tool name — some APIs require it on the result as well as the call.</param>
/// <param name="Text">Output text.</param>
/// <param name="IsError">Whether the model should read this as a failure.</param>
public sealed record ModelToolResult(string Id, string Name, string Text, bool IsError);

/// <summary>One model response, normalised across APIs.</summary>
/// <param name="Texts">Prose blocks.</param>
/// <param name="Thoughts">Reasoning, where the API returns any.</param>
/// <param name="ToolCalls">Tools to run before continuing.</param>
/// <param name="Refused">The model declined; there is no usable content.</param>
/// <param name="InputTokens">Billed input for this call.</param>
/// <param name="OutputTokens">Billed output for this call.</param>
public sealed record ModelReply(
    IReadOnlyList<string> Texts,
    IReadOnlyList<string> Thoughts,
    IReadOnlyList<ModelToolCall> ToolCalls,
    bool Refused,
    long InputTokens,
    long OutputTokens);

/// <summary>
/// A running conversation with one model API, holding its own history in that API's own shape.
/// </summary>
/// <remarks>
/// <para>The agent loop is identical across providers — send, run whatever tools come back, send
/// the results, repeat — while the wire format is not. This is the seam: everything above it
/// (tools, approval, transcript, the viewer) is written once, and each API supplies only the
/// encoding of a turn.</para>
/// <para>Each implementation keeps history in its native form rather than a neutral one that gets
/// translated per call. Anthropic requires thinking blocks to be echoed back with their signatures
/// intact, and OpenAI requires a tool result to follow the assistant message that requested it; a
/// lossy intermediate representation is exactly how those constraints get broken.</para>
/// </remarks>
public interface IModelConversation
{
    /// <summary>Human-readable description of the transport, for the transcript.</summary>
    string Describe { get; }

    /// <summary>Append a user turn.</summary>
    void AddUser(string text);

    /// <summary>
    /// Send the conversation and return the reply, appending the assistant turn to history so the
    /// next call carries it.
    /// </summary>
    Task<ModelReply> SendAsync(CancellationToken ct);

    /// <summary>Append the results of the tools the last reply asked for.</summary>
    void AddToolResults(IReadOnlyList<ModelToolResult> results);
}
