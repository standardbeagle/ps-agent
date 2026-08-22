using System.Text.Json.Nodes;
using PsAgent.Cmdlets;
using PsAgent.Cmdlets.Acp;
using Xunit;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// Protocol shapes that are easy to get subtly wrong and produce a silently dead session rather
/// than an error: the nested permission outcome, the agent launch table, and auto-approval keying
/// on <c>kind</c> rather than a guessed <c>optionId</c>.
/// </summary>
public sealed class AcpProtocolShapeTests
{
    [Fact]
    public void The_protocol_version_is_the_integer_major()
    {
        Assert.Equal(1, AcpClient.ProtocolVersion);
    }

    [Fact]
    public void ParsePermission_reads_the_tool_call_and_its_options()
    {
        var request = AcpClient.ParsePermission(new JsonObject
        {
            ["sessionId"] = "s1",
            ["toolCall"] = new JsonObject { ["toolCallId"] = "call_001", ["title"] = "Run tests" },
            ["options"] = new JsonArray(
                new JsonObject { ["optionId"] = "allow-once", ["name"] = "Allow once", ["kind"] = "allow_once" },
                new JsonObject { ["optionId"] = "reject-once", ["name"] = "Reject", ["kind"] = "reject_once" }),
        });

        Assert.Equal("call_001", request.ToolCallId);
        Assert.Equal("Run tests", request.Title);
        Assert.Equal(2, request.Options.Count);
        Assert.Equal("allow-once", request.Options[0].OptionId);
        Assert.Equal("allow_once", request.Options[0].Kind);
    }

    [Fact]
    public void ParsePermission_falls_back_to_the_tool_kind_for_a_title()
    {
        var request = AcpClient.ParsePermission(new JsonObject
        {
            ["toolCall"] = new JsonObject { ["toolCallId"] = "c", ["kind"] = "execute" },
        });

        Assert.Equal("execute", request.Title);
    }

    [Fact]
    public void ParsePermission_skips_an_option_with_no_id()
    {
        var request = AcpClient.ParsePermission(new JsonObject
        {
            ["options"] = new JsonArray(
                new JsonObject { ["name"] = "broken" },
                new JsonObject { ["optionId"] = "ok" }),
        });

        Assert.Equal("ok", Assert.Single(request.Options).OptionId);
    }

    [Fact]
    public void ParsePermission_survives_a_missing_payload()
    {
        var request = AcpClient.ParsePermission(null);

        Assert.Empty(request.Options);
        Assert.Equal(string.Empty, request.ToolCallId);
    }

    /// <summary>
    /// optionId strings are agent-specific; kind is protocol-defined. Auto-approval must match on
    /// the latter or it picks the wrong button on an agent that names things differently.
    /// </summary>
    [Fact]
    public void AutoAllow_prefers_allow_always_then_allow_once()
    {
        var request = new AcpPermissionRequest("c", "t",
        [
            new AcpPermissionOption("r", "Reject", "reject_once"),
            new AcpPermissionOption("a1", "Allow once", "allow_once"),
            new AcpPermissionOption("aa", "Always", "allow_always"),
        ]);

        Assert.Equal("aa", InvokeAcpCommand.AutoAllow(request));
    }

    [Fact]
    public void AutoAllow_picks_allow_once_when_there_is_no_always()
    {
        var request = new AcpPermissionRequest("c", "t",
        [
            new AcpPermissionOption("r", "Reject", "reject_once"),
            new AcpPermissionOption("a1", "Allow once", "allow_once"),
        ]);

        Assert.Equal("a1", InvokeAcpCommand.AutoAllow(request));
    }

    [Fact]
    public void AutoAllow_returns_null_when_the_agent_offered_nothing()
    {
        Assert.Null(InvokeAcpCommand.AutoAllow(new AcpPermissionRequest("c", "t", [])));
    }

    [Fact]
    public void Known_agent_names_resolve_to_their_launch_command()
    {
        var launch = AcpLaunch.Resolve("claude", null);

        Assert.Equal("npx", launch.Command);
        Assert.Contains("@zed-industries/claude-code-acp", launch.Arguments);
    }

    [Fact]
    public void Known_agent_names_are_case_insensitive()
    {
        Assert.Equal("npx", AcpLaunch.Resolve("Gemini", null).Command);
    }

    /// <summary>
    /// opencode is the odd one out: ACP is a subcommand of its own binary, not an npx package.
    /// Resolving it to a bare "opencode" would launch the interactive TUI instead of the server.
    /// </summary>
    [Fact]
    public void Opencode_resolves_to_its_acp_subcommand()
    {
        var launch = AcpLaunch.Resolve("opencode", null);

        Assert.Equal("opencode", launch.Command);
        Assert.Equal(["acp"], launch.Arguments);
    }

    [Fact]
    public void Extra_arguments_append_to_a_presets_own()
    {
        var launch = AcpLaunch.Resolve("claude", ["--verbose"]);

        Assert.Equal("--verbose", launch.Arguments[^1]);
        Assert.Contains("@zed-industries/claude-code-acp", launch.Arguments);
    }

    [Fact]
    public void An_unknown_name_is_treated_as_an_executable()
    {
        var launch = AcpLaunch.Resolve("my-acp-agent", ["--stdio"]);

        Assert.Equal("my-acp-agent", launch.Command);
        Assert.Equal(["--stdio"], launch.Arguments);
    }
}
