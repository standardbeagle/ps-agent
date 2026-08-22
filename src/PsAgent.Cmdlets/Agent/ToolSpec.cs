using System.Text.Json;

namespace PsAgent.Cmdlets.Agent;

/// <summary>
/// One tool the agent can call, described independently of any SDK type.
/// </summary>
/// <remarks>
/// Kept SDK-free on purpose: the catalog and the executor are then unit-testable with no
/// Anthropic client, no network, and no API key — the loop is the only place that converts
/// these into <c>Anthropic.Models.Messages.Tool</c>.
/// </remarks>
/// <param name="Name">Wire name the model calls.</param>
/// <param name="Description">What it does and when to reach for it — this is prompt text, so it earns its length.</param>
/// <param name="Properties">JSON-Schema fragment per parameter.</param>
/// <param name="Required">Parameter names the model must supply.</param>
/// <param name="Mutating">
/// Whether running it can change the machine. Mutating tools route through the approval gate;
/// read-only ones never do, which is what keeps the common case free of prompts.
/// </param>
public sealed record ToolSpec(
    string Name,
    string Description,
    IReadOnlyDictionary<string, JsonElement> Properties,
    IReadOnlyList<string> Required,
    bool Mutating)
{
    /// <summary>Helper for building a one-line schema fragment: <c>Prop("string", "City name")</c>.</summary>
    public static JsonElement Prop(string type, string description) =>
        JsonSerializer.SerializeToElement(new { type, description });
}

/// <summary>What a tool run produced.</summary>
/// <param name="Ok">False marks the result as an error to the model (<c>is_error</c>) and to the UI.</param>
/// <param name="Text">The text handed back to the model.</param>
/// <param name="Summary">A one-line description of what happened, for the transcript row.</param>
public readonly record struct ToolOutcome(bool Ok, string Text, string Summary)
{
    /// <summary>A successful run.</summary>
    public static ToolOutcome Success(string text, string summary) => new(true, text, summary);

    /// <summary>
    /// A failed run. The message goes back to the model as a normal tool result so it can
    /// correct itself — throwing instead would end the turn and lose the recovery.
    /// </summary>
    public static ToolOutcome Failure(string message) => new(false, message, message);
}
