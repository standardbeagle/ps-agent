using Strata;
using Strata.Interaction;

namespace PsAgent.Cmdlets.Ui;

/// <summary>
/// A mutable Strata tree node for the transcript viewer.
/// </summary>
/// <remarks>
/// <para>Strata's interactive contract needs two things a plain immutable adapter cannot give:
/// <b>stable reference identity</b> across re-cascades, so a projection reconciles the same node in
/// place, and <b>mutable pseudo-state</b>, so <c>:focused</c> and <c>:expanded</c> can toggle
/// between frames. This is the same authoring shape as <c>Show-Styled</c>'s internal node — mirrored
/// rather than reused because that type is <see langword="internal"/> to PsBash.Cmdlets, and
/// re-declaring 60 lines against Strata's public interfaces beats making a downstream repo's name
/// part of ps-bash's public surface.</para>
/// </remarks>
internal sealed class AgentNode : ITreeNode, IPseudoStateMutable
{
    private readonly List<AgentNode> _children = [];
    private readonly HashSet<string> _pseudoStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _attributes = new(StringComparer.Ordinal);

    /// <summary>Create a node of <paramref name="kind"/> with optional id and classes.</summary>
    public AgentNode(string kind, string? id = null, IEnumerable<string>? classes = null)
    {
        Kind = kind;
        Id = id;
        Classes = (classes ?? []).ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public string Kind { get; }

    /// <inheritdoc/>
    public string? Id { get; }

    /// <inheritdoc/>
    public IReadOnlySet<string> Classes { get; }

    /// <inheritdoc/>
    public IReadOnlySet<string> PseudoStates => _pseudoStates;

    /// <inheritdoc/>
    public bool AddPseudoState(string state) => _pseudoStates.Add(state);

    /// <inheritdoc/>
    public bool RemovePseudoState(string state) => _pseudoStates.Remove(state);

    /// <inheritdoc/>
    public ITreeNode? Parent { get; private set; }

    /// <inheritdoc/>
    public IEnumerable<ITreeNode> Children => _children;

    /// <summary>The children as concrete nodes, for host-side wiring (focus ring, expand targets).</summary>
    public IReadOnlyList<AgentNode> ChildNodes => _children;

    /// <inheritdoc/>
    public object? Underlying => this;

    /// <summary>Append a child and adopt it.</summary>
    public AgentNode Add(AgentNode child)
    {
        child.Parent = this;
        _children.Add(child);
        return child;
    }

    /// <summary>Replace this node's children wholesale — how a frame is rebuilt.</summary>
    public void SetChildren(IEnumerable<AgentNode> children)
    {
        _children.Clear();
        foreach (var c in children)
        {
            c.Parent = this;
            _children.Add(c);
        }
    }

    /// <summary>Set an attribute the text selector or a stylesheet can read.</summary>
    public void SetAttribute(string name, object? value) => _attributes[name] = value;

    /// <inheritdoc/>
    public bool TryGetAttribute(string name, out object? value) => _attributes.TryGetValue(name, out value);
}
