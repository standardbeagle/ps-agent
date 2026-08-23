using PsAgent.Cmdlets.Acp;
using PsAgent.Cmdlets.Agent;
using PsAgent.Cmdlets.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// The viewer's real key loop, driven by a scripted terminal — first against a fake turn, then
/// against a live <c>opencode acp</c> session.
/// </summary>
/// <remarks>
/// This is the layer that was previously reachable only by a human at a keyboard: prompt entry, a
/// turn running while rows stream in, expanding a row, interrupting, and quitting. It is the same
/// <see cref="TranscriptView.Run"/> that ships; only the terminal differs.
/// </remarks>
public sealed class InteractiveLoopTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>xunit output sink, so a failed frame can be read.</summary>
    public InteractiveLoopTests(ITestOutputHelper output) => _output = output;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static TranscriptView View(ScriptedTerminal terminal, string header = "ps-agent · test") =>
        new(AgentStyles.Resolve(null), header, terminal);

    [Fact]
    public void The_loop_enters_the_alternate_screen_and_restores_it_on_quit()
    {
        var terminal = new ScriptedTerminal();
        terminal.Type("q");

        View(terminal).Run(initialPrompt: null, CancellationToken.None);

        Assert.StartsWith("\x1b[?1049h", terminal.Output, StringComparison.Ordinal);
        Assert.EndsWith("\x1b[?1049l", terminal.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The viewer must claim the terminal (and hand it back), because that is where the output
    /// encoding is forced to UTF-8. Skipping it is silent: on a console left at a legacy code page
    /// the transcript still draws, but every non-Latin-1 row marker becomes `?`.
    /// </summary>
    [Fact]
    public void The_loop_opens_exactly_one_terminal_session_and_closes_it()
    {
        var terminal = new ScriptedTerminal();
        terminal.Type("q");

        View(terminal).Run(initialPrompt: null, CancellationToken.None);

        Assert.Equal(1, terminal.SessionOpenCount);
        Assert.False(terminal.SessionOpen);
    }

    /// <summary>
    /// Why <see cref="ITerminal.BeginSession"/> forces UTF-8: these row markers are outside every
    /// single-byte code page, so a console left on a legacy one replaces them with `?`.
    /// </summary>
    /// <remarks>
    /// The check is "beyond Latin-1" rather than a CP1252 round-trip, because
    /// <c>Encoding.GetEncoding(1252)</c> needs <c>CodePagesEncodingProvider</c> registered and
    /// silently behaves differently where it is not — an environment-dependent test would assert
    /// nothing on the machines that matter. Observed live: under CP1252 `›`, `·` and `—` survive
    /// while `●`, `⚙`, `✔`, `✘` and `↑↓` all become `?`, so the transcript reads almost right with
    /// exactly the tool and assistant markers missing.
    /// </remarks>
    [Theory]
    [InlineData(AgentEventKind.Assistant, AgentToolStatus.None)]
    [InlineData(AgentEventKind.ToolCall, AgentToolStatus.InProgress)]
    [InlineData(AgentEventKind.ToolResult, AgentToolStatus.Completed)]
    [InlineData(AgentEventKind.ToolResult, AgentToolStatus.Failed)]
    [InlineData(AgentEventKind.Error, AgentToolStatus.None)]
    public void Row_markers_need_utf8_output(AgentEventKind kind, AgentToolStatus status)
    {
        var glyph = new AgentEvent { Kind = kind, Title = "t", Status = status }.Glyph;

        Assert.All(glyph, ch => Assert.True(ch > 0xFF, $"U+{(int)ch:X4} would survive a single-byte code page"));
    }

    // Quitting cancels an in-flight turn — by design. So `q` must not be queued behind Enter:
    // the loop would consume it before the submit task ran and cancel the turn before it started.
    // Wait for the turn to be observed, then quit.
    [Fact]
    public void Typing_a_prompt_submits_it_to_the_agent()
    {
        var terminal = new ScriptedTerminal();
        terminal.Type("i")                       // open the prompt line
                .Type("hello there")
                .Press(ConsoleKey.Enter);        // submit

        var submitted = new List<string>();
        var view = View(terminal);
        view.OnSubmit = (text, _) =>
        {
            lock (submitted)
            {
                submitted.Add(text);
            }

            view.Append(new AgentEvent { Kind = AgentEventKind.Assistant, Title = "acknowledged" });
            return Task.CompletedTask;
        };

        var runner = Task.Run(() => view.Run(initialPrompt: null, CancellationToken.None));

        Assert.True(SpinUntil(() => terminal.Saw("acknowledged"), Budget), "the turn never ran");

        terminal.Type("q");
        Assert.True(runner.Wait(Budget));

        lock (submitted)
        {
            Assert.Equal(["hello there"], submitted);
        }
    }

    [Fact]
    public void Escape_abandons_the_prompt_line_without_submitting()
    {
        var terminal = new ScriptedTerminal();
        terminal.Type("i").Type("never mind").Press(ConsoleKey.Escape).Type("q");

        var submitted = new List<string>();
        var view = View(terminal);
        view.OnSubmit = (t, _) => { submitted.Add(t); return Task.CompletedTask; };

        view.Run(initialPrompt: null, CancellationToken.None);

        Assert.Empty(submitted);
    }

    [Fact]
    public void Backspace_edits_the_prompt_before_submission()
    {
        var terminal = new ScriptedTerminal();
        terminal.Type("i").Type("abcXY")
                .Press(ConsoleKey.Backspace).Press(ConsoleKey.Backspace)
                .Press(ConsoleKey.Enter);

        var submitted = new List<string>();
        var view = View(terminal);
        view.OnSubmit = (t, _) =>
        {
            lock (submitted)
            {
                submitted.Add(t);
            }

            return Task.CompletedTask;
        };

        var runner = Task.Run(() => view.Run(initialPrompt: null, CancellationToken.None));

        Assert.True(SpinUntil(() => { lock (submitted) { return submitted.Count == 1; } }, Budget));

        terminal.Type("q");
        Assert.True(runner.Wait(Budget));

        lock (submitted)
        {
            Assert.Equal(["abc"], submitted);
        }
    }

    /// <summary>
    /// The reason the loop polls instead of blocking on ReadKey: rows appended by a worker task
    /// mid-turn have to reach the screen before the turn ends.
    /// </summary>
    [Fact]
    public void Rows_that_arrive_during_a_turn_are_painted_before_it_finishes()
    {
        var terminal = new ScriptedTerminal();
        var release = new ManualResetEventSlim(false);
        var painted = new ManualResetEventSlim(false);

        var view = View(terminal);
        view.OnSubmit = async (_, _) =>
        {
            view.Append(new AgentEvent { Kind = AgentEventKind.ToolCall, Title = "MID-TURN-ROW", ToolCallId = "c" });
            painted.Set();
            await Task.Run(() => release.Wait(Budget));
        };

        var runner = Task.Run(() => view.Run("go", CancellationToken.None));

        Assert.True(painted.Wait(Budget));
        Assert.True(SpinUntil(() => terminal.Saw("MID-TURN-ROW"), Budget),
            "the row appended mid-turn never reached the screen");

        release.Set();
        terminal.Type("q");
        Assert.True(runner.Wait(Budget));
    }

    [Fact]
    public void Escape_during_a_turn_interrupts_it_rather_than_quitting()
    {
        var terminal = new ScriptedTerminal();
        var started = new ManualResetEventSlim(false);
        var cancelled = false;

        var view = View(terminal);
        view.OnSubmit = async (_, ct) =>
        {
            started.Set();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
        };

        var runner = Task.Run(() => view.Run("go", CancellationToken.None));
        Assert.True(started.Wait(Budget));

        terminal.Press(ConsoleKey.Escape);          // interrupt, not quit
        Assert.True(SpinUntil(() => cancelled, Budget), "the turn was never cancelled");

        terminal.Type("q");                          // now idle, so this quits
        Assert.True(runner.Wait(Budget));
    }

    [Fact]
    public void Enter_expands_the_focused_row_to_show_its_body()
    {
        var terminal = new ScriptedTerminal();
        var view = View(terminal);
        view.ReplaceAll(
        [
            new AgentEvent { Kind = AgentEventKind.Assistant, Title = "summary line", Body = "HIDDEN-DETAIL-TEXT" },
        ]);

        terminal.Press(ConsoleKey.Enter).Type("q");
        view.Run(initialPrompt: null, CancellationToken.None);

        Assert.True(terminal.Saw("HIDDEN-DETAIL-TEXT"), "Enter did not expand the focused row");
    }

    /// <summary>The permission chooser, which the ACP client blocks on from its RPC handler.</summary>
    [Fact]
    public void The_chooser_returns_the_selected_option()
    {
        var terminal = new ScriptedTerminal();
        terminal.Press(ConsoleKey.DownArrow).Press(ConsoleKey.Enter);

        var choice = View(terminal).Choose("Allow this?", ["Allow once", "Always", "Reject"]);

        Assert.Equal(1, choice);
        Assert.True(terminal.Saw("Allow this?"));
    }

    [Fact]
    public void The_chooser_cancels_on_escape()
    {
        var terminal = new ScriptedTerminal();
        terminal.Press(ConsoleKey.Escape);

        Assert.Null(View(terminal).Choose("Allow this?", ["Allow once", "Reject"]));
    }

    [Fact]
    public void The_chooser_accepts_a_number_key()
    {
        var terminal = new ScriptedTerminal();
        terminal.Type("3");

        Assert.Equal(2, View(terminal).Choose("Pick", ["a", "b", "c"]));
    }

    // ---------------------------------------------------------------- live agent

    private static string? NodeFreeAgentOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exe = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), exe))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The whole stack at once: the shipping key loop, driven by a script, against a real
    /// <c>opencode acp</c> subprocess — prompt typed, turn run, answer rendered, row expanded, quit.
    /// </summary>
    [SkippableFact]
    public async Task The_viewer_drives_a_live_opencode_session()
    {
        var opencode = NodeFreeAgentOnPath();
        Skip.If(opencode is null, "opencode is not on PATH.");

        var root = Path.Combine(Path.GetTempPath(), "ps-agent-tui", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "hello.txt"), "The magic word is BEAGLE.");

        try
        {
            await using var client = AcpClient.Start(AcpLaunch.Resolve("opencode", null), root);

            var transcript = new AcpTranscript();
            var terminal = new ScriptedTerminal { Width = 100, Height = 30 };
            var view = View(terminal, $"ps-agent · opencode · {root}");

            var sync = new object();
            client.OnSessionUpdate = p =>
            {
                lock (sync)
                {
                    transcript.Apply(p);
                    view.ReplaceAll(transcript.Rows);
                }
            };
            client.OnPermission = r => r.Options.FirstOrDefault(o => o.Kind == "allow_once")?.OptionId;

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await client.InitializeAsync(cts.Token);
            var sessionId = await client.NewSessionAsync(cts.Token);

            view.OnSubmit = async (text, _) =>
            {
                lock (sync)
                {
                    transcript.Add(new AgentEvent { Kind = AgentEventKind.User, Title = text, Body = text });
                    view.ReplaceAll(transcript.Rows);
                }

                var stop = await client.PromptAsync(sessionId, text, cts.Token);
                lock (sync)
                {
                    transcript.Add(new AgentEvent { Kind = AgentEventKind.Status, Title = $"stop reason: {stop}" });
                    view.ReplaceAll(transcript.Rows);
                }
            };

            // Type the prompt through the real prompt line, exactly as a user would.
            terminal.Type("i").Type("Read hello.txt and reply with only the magic word.")
                    .Press(ConsoleKey.Enter);

            var runner = Task.Run(() => view.Run(initialPrompt: null, CancellationToken.None));

            Assert.True(
                await terminal.WaitForAsync("BEAGLE", TimeSpan.FromMinutes(2)),
                "the agent's answer never appeared on screen");

            // Expand the focused row, then leave.
            terminal.Press(ConsoleKey.Enter).Type("q");
            Assert.True(runner.Wait(TimeSpan.FromSeconds(30)), "the viewer did not exit");

            _output.WriteLine("===== final painted frame =====");
            _output.WriteLine(terminal.LastContentFrame);

            // The frame really is the transcript: header, the typed prompt, and the answer.
            var final = ScriptedTerminal.StripAnsi(terminal.Output);
            Assert.Contains("ps-agent · opencode", final, StringComparison.Ordinal);
            Assert.Contains("Read hello.txt", final, StringComparison.Ordinal);
            Assert.Contains("BEAGLE", final, StringComparison.Ordinal);

            // And it exited cleanly rather than leaving the terminal on the alternate screen.
            Assert.EndsWith("\x1b[?1049l", terminal.Output, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }
}
