using PsAgent.Cmdlets.Acp;
using PsAgent.Cmdlets.Agent;
using Xunit;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// The ACP client against a real subprocess — <c>fixtures/stub-acp-agent.js</c>, a deterministic
/// agent that needs no network, no API key, and no ACP agent installed.
/// </summary>
/// <remarks>
/// The unit tests cover the pieces; this covers the wiring between them, which is where a protocol
/// client actually breaks: framing, the handshake, notifications arriving <i>while</i> a request is
/// outstanding, and the agent calling back into the client mid-turn. Skipped when node is absent
/// rather than failed — an environment without node is not a defect in this code.
/// </remarks>
public sealed class AcpClientIntegrationTests : IDisposable
{
    private readonly string _root;

    /// <summary>Create the session workspace with the file the stub agent will ask us to read.</summary>
    public AcpClientIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ps-agent-acp", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "fixture.txt"), "FIXTURE-CONTENT");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static string? NodeOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), exe))
            .FirstOrDefault(File.Exists);
    }

    private static string StubPath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "stub-acp-agent.js");

    /// <summary>Start the stub, or skip the test when node (or the fixture) is unavailable.</summary>
    private AcpClient StartStub(string root)
    {
        var node = NodeOnPath();
        Skip.If(node is null, "node is not on PATH; the stub ACP agent cannot run.");
        Skip.IfNot(File.Exists(StubPath), $"stub agent fixture missing at {StubPath}.");

        return AcpClient.Start(new AcpLaunch(node!, [StubPath]), root);
    }

    [SkippableFact]
    public async Task A_full_turn_streams_updates_and_serves_the_agents_callbacks()
    {
        await using var client = StartStub(_root);

        var transcript = new AcpTranscript();
        var sync = new object();
        client.OnSessionUpdate = p =>
        {
            lock (sync)
            {
                transcript.Apply(p);
            }
        };
        client.OnPermission = request =>
            request.Options.First(o => o.Kind == "allow_once").OptionId;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var info = await client.InitializeAsync(cts.Token);
        Assert.Equal("stub-agent", info?["agentInfo"]?["name"]?.GetValue<string>());
        Assert.Equal(1, info?["protocolVersion"]?.GetValue<int>());

        var sessionId = await client.NewSessionAsync(cts.Token);
        Assert.Equal("stub-session-1", sessionId);

        var stop = await client.PromptAsync(sessionId, "hello agent", cts.Token);
        Assert.Equal("end_turn", stop);

        List<AgentEvent> rows;
        lock (sync)
        {
            rows = [.. transcript.Rows];
        }

        // Streamed chunks coalesced into one row per kind.
        var thought = Assert.Single(rows.Where(r => r.Kind == AgentEventKind.Thought));
        Assert.Equal("considering the request", thought.Body);

        var message = Assert.Single(rows.Where(r => r.Kind == AgentEventKind.Assistant));
        Assert.Equal("you said: hello agent", message.Body);

        // The agent read the file through OUR fs callback, so its content proves the round trip.
        var read = rows.Single(r => r.ToolCallId == "call_1");
        Assert.Equal(AgentToolStatus.Completed, read.Status);
        Assert.Contains("FIXTURE-CONTENT", read.Body, StringComparison.Ordinal);

        // And the permission answer came back to the agent with the id we chose.
        var permission = rows.Single(r => r.ToolCallId == "call_2");
        Assert.Contains("selected/yes-once", permission.Title, StringComparison.Ordinal);

        var plan = Assert.Single(rows.Where(r => r.Kind == AgentEventKind.Plan));
        Assert.Equal("plan (1/2 done)", plan.Title);
    }

    /// <summary>
    /// The client's own confinement, enforced against a real agent asking for a real escape — the
    /// agent is a separate process and its idea of what it may read is not this client's.
    /// </summary>
    [SkippableFact]
    public async Task A_file_read_outside_the_session_directory_is_refused()
    {
        // Point the session at a subdirectory so `fixture.txt` sits outside it: the stub asks for
        // "fixture.txt", which now resolves above the root and must be refused.
        var deeper = Path.Combine(_root, "inner");
        Directory.CreateDirectory(deeper);

        await using var scoped = StartStub(deeper);

        var transcript = new AcpTranscript();
        scoped.OnSessionUpdate = transcript.Apply;
        scoped.OnPermission = _ => null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await scoped.InitializeAsync(cts.Token);
        var sessionId = await scoped.NewSessionAsync(cts.Token);
        await scoped.PromptAsync(sessionId, "read it", cts.Token);

        var read = transcript.Rows.Single(r => r.ToolCallId == "call_1");
        Assert.DoesNotContain("FIXTURE-CONTENT", read.Body, StringComparison.Ordinal);
        Assert.Contains("No such file", read.Body, StringComparison.Ordinal);
    }

    /// <summary>Declining a permission must answer <c>cancelled</c>, not silently allow.</summary>
    [SkippableFact]
    public async Task A_declined_permission_answers_cancelled()
    {
        await using var client = StartStub(_root);

        var transcript = new AcpTranscript();
        client.OnSessionUpdate = transcript.Apply;
        client.OnPermission = _ => null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.InitializeAsync(cts.Token);
        var sessionId = await client.NewSessionAsync(cts.Token);
        await client.PromptAsync(sessionId, "go", cts.Token);

        var permission = transcript.Rows.Single(r => r.ToolCallId == "call_2");
        Assert.Contains("cancelled", permission.Title, StringComparison.Ordinal);
        Assert.Equal(AgentToolStatus.Failed, permission.Status);
    }

    [Fact]
    public async Task Launching_a_missing_agent_fails_with_a_clear_error()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var client = AcpClient.Start(
                new AcpLaunch("definitely-not-an-agent-binary", []), _root);
            await client.InitializeAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        });
    }
}
