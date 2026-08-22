using PsAgent.Cmdlets.Agent;
using Xunit;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// The confinement check is the agent's security boundary, so it is tested for the ways an escape
/// is actually attempted rather than only for the happy path.
/// </summary>
public sealed class WorkspacePathTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "ps-agent-tests", "repo");

    [Fact]
    public void Relative_path_inside_the_root_resolves()
    {
        var resolved = WorkspacePath.TryResolve(Root, Path.Combine("src", "a.cs"));

        Assert.NotNull(resolved);
        Assert.Equal(Path.Combine(Root, "src", "a.cs"), resolved);
    }

    [Fact]
    public void The_root_itself_resolves()
    {
        Assert.Equal(Path.GetFullPath(Root), WorkspacePath.TryResolve(Root, "."));
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("src/../../escape.txt")]
    public void Dot_dot_traversal_is_refused(string candidate)
    {
        Assert.Null(WorkspacePath.TryResolve(Root, candidate));
    }

    [Fact]
    public void An_absolute_path_outside_the_root_is_refused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else", "x.txt");

        Assert.Null(WorkspacePath.TryResolve(Root, outside));
    }

    [Fact]
    public void An_absolute_path_inside_the_root_is_allowed()
    {
        var inside = Path.Combine(Root, "src", "a.cs");

        Assert.Equal(inside, WorkspacePath.TryResolve(Root, inside));
    }

    /// <summary>
    /// The bug a bare StartsWith would have: a sibling directory whose name merely begins with the
    /// root's. Only a separator boundary distinguishes them.
    /// </summary>
    [Fact]
    public void A_sibling_directory_sharing_the_roots_prefix_is_refused()
    {
        var sibling = Root + "-secrets";

        Assert.Null(WorkspacePath.TryResolve(Root, Path.Combine(sibling, "key.pem")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_operand_is_refused(string candidate)
    {
        Assert.Null(WorkspacePath.TryResolve(Root, candidate));
    }

    [Fact]
    public void Relative_renders_a_path_inside_the_root_relatively()
    {
        var full = Path.Combine(Root, "src", "a.cs");

        Assert.Equal(Path.Combine("src", "a.cs"), WorkspacePath.Relative(Root, full));
    }
}
