using System.Text;
using PsAgent.Cmdlets.Agent;
using Spectre.Console;
using Spectre.Console.Rendering;
using Strata;
using Strata.Core;
using Strata.Css;
using Strata.Interaction;
using Strata.Properties.Styling;
using Strata.Render.Spectre;

namespace PsAgent.Cmdlets.Ui;

/// <summary>What a keystroke means in the transcript viewer.</summary>
internal enum ViewAction
{
    /// <summary>Ignore it.</summary>
    None,

    /// <summary>Move the cursor down one row.</summary>
    Down,

    /// <summary>Move the cursor up one row.</summary>
    Up,

    /// <summary>Jump to the newest row and resume following.</summary>
    End,

    /// <summary>Jump to the oldest row.</summary>
    Home,

    /// <summary>Expand or collapse the focused row.</summary>
    ToggleExpand,

    /// <summary>Start typing a prompt.</summary>
    Prompt,

    /// <summary>Cancel the turn in flight.</summary>
    Interrupt,

    /// <summary>Leave the viewer.</summary>
    Quit,
}

/// <summary>
/// The shared terminal UI for both cmdlets: a live, navigable agent transcript rendered through
/// the same Strata cascade + Spectre projection that <c>Show-Styled</c> drives, with a prompt line
/// underneath.
/// </summary>
/// <remarks>
/// <para>The render pattern is deliberately <c>Show-Styled</c>'s and not Terminal.Gui's: a
/// <see cref="Console.ReadKey(bool)"/> loop over a Spectre frame shares the exact terminal path a
/// PowerShell host's line editor uses, so the viewer exits cleanly and leaves stdin usable. This
/// adds the two things a transcript needs and a static list does not — rows arriving <b>while</b>
/// the view is up, and a modal prompt/permission input — which is why it is a loop of its own
/// rather than a call to <c>StyledInteractiveSession.RunInteractive</c>.</para>
/// <para>Thread safety: agent events arrive on worker tasks while the key loop runs on the caller's
/// thread. Every mutation of the row list goes through <see cref="_sync"/>, and only the key loop
/// ever writes to the console — except while it is parked inside <see cref="Choose"/>, which is
/// exactly when nothing else is drawing.</para>
/// </remarks>
internal sealed class TranscriptView
{
    private readonly List<AgentEvent> _rows = [];
    private readonly HashSet<int> _expanded = [];
    private readonly object _sync = new();
    private readonly IStylesheet _stylesheet;
    private readonly Cascade _cascade;
    private readonly SpectreProjection _projection;
    private readonly string _header;
    private readonly ITerminal _terminal;

    private int _focus;
    private bool _follow = true;
    private bool _dirty = true;
    private string _status = string.Empty;

    /// <summary>Build a viewer over a stylesheet's CSS text.</summary>
    /// <param name="css">The stylesheet text.</param>
    /// <param name="header">The title line above the transcript.</param>
    /// <param name="terminal">
    /// Where to draw and read keys. Defaults to the real console; a test supplies a scripted one to
    /// drive the loop without a TTY.
    /// </param>
    public TranscriptView(string css, string header, ITerminal? terminal = null)
    {
        _header = header;
        _terminal = terminal ?? ConsoleTerminal.Instance;

        var registry = StylingProperties.CreateRegistry();
        LayoutProperties.RegisterAll(registry);
        InteractionProperties.RegisterAll(registry);
        _stylesheet = new CssStylesheetParser(new CssSelectorLanguage(), registry).Parse(css);
        _cascade = new Cascade(registry);
        _projection = new SpectreProjection { TextSelector = NodeText };
    }

    /// <summary>Runs one user prompt to completion. Cancellation is wired to the Esc key.</summary>
    public Func<string, CancellationToken, Task>? OnSubmit { get; set; }

    /// <summary>True while a turn is in flight — the footer says so and Esc becomes meaningful.</summary>
    public bool Busy { get; private set; }

    /// <summary>A real terminal is required; a redirected stream gets the plain pipeline instead.</summary>
    public static bool IsInteractive => ConsoleTerminal.Instance.IsInteractive;

    /// <summary>The one-line status shown above the key legend.</summary>
    public string Status
    {
        get { lock (_sync) { return _status; } }
        set { lock (_sync) { _status = value; _dirty = true; } }
    }

    /// <summary>Append a transcript row. Safe to call from any thread.</summary>
    public void Append(AgentEvent row)
    {
        lock (_sync)
        {
            _rows.Add(row);
            if (_follow)
            {
                _focus = _rows.Count - 1;
            }

            _dirty = true;
        }
    }

    /// <summary>
    /// Replace the whole transcript — what the ACP path uses, because <see cref="Acp.AcpTranscript"/>
    /// rewrites rows in place as chunks and tool updates arrive rather than only appending.
    /// </summary>
    public void ReplaceAll(IReadOnlyList<AgentEvent> rows)
    {
        lock (_sync)
        {
            _rows.Clear();
            _rows.AddRange(rows);
            if (_follow && _rows.Count > 0)
            {
                _focus = _rows.Count - 1;
            }

            _focus = Math.Clamp(_focus, 0, Math.Max(0, _rows.Count - 1));
            _dirty = true;
        }
    }

    /// <summary>A snapshot of the transcript, for emitting to the pipeline once the view exits.</summary>
    public IReadOnlyList<AgentEvent> Snapshot()
    {
        lock (_sync)
        {
            return [.. _rows];
        }
    }

    /// <summary>Translate a keystroke into an action. Pure, so the key map is unit-testable.</summary>
    internal static ViewAction Decide(ConsoleKey key, char ch, bool busy) => key switch
    {
        ConsoleKey.DownArrow => ViewAction.Down,
        ConsoleKey.UpArrow => ViewAction.Up,
        ConsoleKey.PageDown or ConsoleKey.End => ViewAction.End,
        ConsoleKey.PageUp or ConsoleKey.Home => ViewAction.Home,
        ConsoleKey.Enter or ConsoleKey.Spacebar => ViewAction.ToggleExpand,

        // Esc means "stop what you are doing" while a turn runs, and "leave" when idle — the same
        // key can be both because those never overlap.
        ConsoleKey.Escape => busy ? ViewAction.Interrupt : ViewAction.Quit,
        _ => ch switch
        {
            'j' => ViewAction.Down,
            'k' => ViewAction.Up,
            'G' => ViewAction.End,
            'g' => ViewAction.Home,
            'i' or '/' => ViewAction.Prompt,
            'q' => ViewAction.Quit,
            _ => ViewAction.None,
        },
    };

    /// <summary>
    /// Run the viewer until the user quits. Returns 0, or -1 when there is no usable terminal —
    /// in which case the caller falls back to plain pipeline output.
    /// </summary>
    public int Run(string? initialPrompt, CancellationToken ct)
    {
        if (!_terminal.IsInteractive)
        {
            return -1;
        }

        Task? turn = null;
        CancellationTokenSource? turnCts = null;

        try
        {
            _terminal.Write("\x1b[?1049h");
            _terminal.SetCursorVisible(false);

            if (!string.IsNullOrWhiteSpace(initialPrompt))
            {
                (turn, turnCts) = StartTurn(initialPrompt!, ct);
            }

            while (!ct.IsCancellationRequested)
            {
                if (turn is { IsCompleted: true })
                {
                    ObserveTurn(turn);
                    turn = null;
                    turnCts?.Dispose();
                    turnCts = null;
                    Busy = false;
                    lock (_sync) { _dirty = true; }
                }

                Repaint();

                if (!_terminal.KeyAvailable)
                {
                    // A short park rather than a blocking ReadKey: the loop must keep repainting
                    // while a turn streams rows in from a worker task.
                    _terminal.Idle(30);
                    continue;
                }

                var key = _terminal.ReadKey();
                switch (Decide(key.Key, key.KeyChar, Busy))
                {
                    case ViewAction.Quit:
                        turnCts?.Cancel();
                        return 0;

                    case ViewAction.Interrupt:
                        turnCts?.Cancel();
                        Status = "interrupting…";
                        break;

                    case ViewAction.Down:
                        MoveFocus(+1);
                        break;

                    case ViewAction.Up:
                        MoveFocus(-1);
                        break;

                    case ViewAction.End:
                        lock (_sync) { _focus = Math.Max(0, _rows.Count - 1); _follow = true; _dirty = true; }
                        break;

                    case ViewAction.Home:
                        lock (_sync) { _focus = 0; _follow = false; _dirty = true; }
                        break;

                    case ViewAction.ToggleExpand:
                        lock (_sync)
                        {
                            if (!_expanded.Remove(_focus))
                            {
                                _expanded.Add(_focus);
                            }

                            _dirty = true;
                        }

                        break;

                    case ViewAction.Prompt when turn is null:
                    {
                        var text = ReadPromptLine();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            (turn, turnCts) = StartTurn(text, ct);
                        }

                        lock (_sync) { _dirty = true; }
                        break;
                    }

                    case ViewAction.Prompt:
                        Status = "still working — Esc to interrupt first";
                        break;
                }
            }

            return 0;
        }
        finally
        {
            turnCts?.Cancel();
            try
            {
                turn?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The turn faulted or was cancelled on the way out; the transcript already says so.
            }

            turnCts?.Dispose();
            _terminal.Write("\x1b[2J\x1b[H\x1b[?1049l");
            _terminal.SetCursorVisible(true);
        }
    }

    private (Task Turn, CancellationTokenSource Cts) StartTurn(string text, CancellationToken ct)
    {
        Busy = true;
        Status = "working…";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var submit = OnSubmit;
        var task = submit is null
            ? Task.CompletedTask
            : Task.Run(() => submit(text, cts.Token), cts.Token);
        return (task, cts);
    }

    private void ObserveTurn(Task turn)
    {
        if (turn.IsCanceled)
        {
            Status = "interrupted";
            return;
        }

        if (turn.Exception is { } ex)
        {
            var inner = ex.GetBaseException();
            Append(new AgentEvent
            {
                Kind = AgentEventKind.Error,
                Title = inner.Message,
                Body = inner.ToString(),
            });
            Status = "failed";
            return;
        }

        Status = "ready";
    }

    private void MoveFocus(int delta)
    {
        lock (_sync)
        {
            if (_rows.Count == 0)
            {
                return;
            }

            _focus = Math.Clamp(_focus + delta, 0, _rows.Count - 1);

            // Moving off the newest row stops the view chasing new output, so a reader can look at
            // an earlier tool result without being yanked back down.
            _follow = _focus == _rows.Count - 1;
            _dirty = true;
        }
    }

    /// <summary>
    /// Choose one of <paramref name="options"/>. Blocks the calling thread — used to answer an ACP
    /// permission request from the RPC handler, which is precisely when the agent is waiting and
    /// nothing else is drawing.
    /// </summary>
    public int? Choose(string title, IReadOnlyList<string> options)
    {
        if (!_terminal.IsInteractive || options.Count == 0)
        {
            return null;
        }

        var selected = 0;
        while (true)
        {
            var sb = new StringBuilder();
            sb.Append("\x1b[2J\x1b[H");
            sb.Append("  ").Append(title).Append("\n\n");
            for (var i = 0; i < options.Count; i++)
            {
                sb.Append(i == selected ? "  \x1b[7m> " : "    ")
                  .Append(options[i])
                  .Append(i == selected ? "\x1b[0m" : string.Empty)
                  .Append('\n');
            }

            sb.Append("\n  ↑↓ choose · Enter confirm · Esc cancel");
            _terminal.Write(sb.ToString());

            var key = _terminal.ReadKey();
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    selected = (selected - 1 + options.Count) % options.Count;
                    break;
                case ConsoleKey.DownArrow:
                    selected = (selected + 1) % options.Count;
                    break;
                case ConsoleKey.Enter:
                    lock (_sync) { _dirty = true; }
                    return selected;
                case ConsoleKey.Escape:
                    lock (_sync) { _dirty = true; }
                    return null;
                default:
                    if (key.KeyChar is >= '1' and <= '9')
                    {
                        var index = key.KeyChar - '1';
                        if (index < options.Count)
                        {
                            lock (_sync) { _dirty = true; }
                            return index;
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>Read a line at the bottom of the screen. Esc abandons it.</summary>
    private string? ReadPromptLine()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            _terminal.Write("\x1b[2J\x1b[H" + RenderFrame(promptText: buffer.ToString()));
            var key = _terminal.ReadKey();
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    return buffer.ToString();
                case ConsoleKey.Escape:
                    return null;
                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                    }

                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }

    private void Repaint()
    {
        lock (_sync)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
        }

        _terminal.Write("\x1b[2J\x1b[H" + RenderFrame(promptText: null));
    }

    /// <summary>
    /// Render one frame to a string with the terminal's size supplied rather than read from the
    /// console — the seam that lets a test exercise the real stylesheet cascade and projection with
    /// no TTY. Without it the entire visual layer is only reachable by eye.
    /// </summary>
    internal string RenderSnapshot(int width, int height, string? promptText = null) =>
        RenderFrame(promptText, width, height);

    /// <summary>Move the cursor and expand a row from outside the key loop (rendering tests).</summary>
    internal void SetView(int focus, params int[] expanded)
    {
        lock (_sync)
        {
            _focus = focus;
            _expanded.Clear();
            foreach (var i in expanded)
            {
                _expanded.Add(i);
            }

            _follow = false;
            _dirty = true;
        }
    }

    private string RenderFrame(string? promptText, int? widthOverride = null, int? heightOverride = null)
    {
        AgentEvent[] rows;
        int focus, total;
        HashSet<int> expanded;
        string status;
        lock (_sync)
        {
            rows = [.. _rows];
            focus = _focus;
            expanded = [.. _expanded];
            status = _status;
        }

        total = rows.Length;

        // Reserve the chrome (header + status + legend + prompt) so the transcript window never
        // scrolls the footer off the screen.
        var height = heightOverride ?? _terminal.Height;
        var window = Math.Max(3, height - 6);
        var (start, end) = Window(total, focus, window, expanded);

        var surface = new AgentNode("Surface");
        List<AgentNode> children = [];
        for (var i = start; i < end; i++)
        {
            var row = rows[i];
            var node = new AgentNode("Row", classes: SplitClasses(row.Class));
            node.SetAttribute("Name", row.Display);
            if (i == focus)
            {
                node.AddPseudoState("focused");
            }

            if (expanded.Contains(i))
            {
                node.AddPseudoState("expanded");
            }

            children.Add(node);

            if (expanded.Contains(i) && row.Body.Length > 0)
            {
                children.Add(BuildDetail(row));
            }
        }

        surface.SetChildren(children);

        var result = _cascade.Compute(surface, _stylesheet);
        var frame = RenderToAnsi(_projection.Project(surface, result), widthOverride ?? _terminal.Width);

        var sb = new StringBuilder();
        sb.Append("\x1b[1m").Append(_header).Append("\x1b[0m\n");
        sb.Append(frame).Append('\n');

        var position = total == 0 ? "empty" : $"[{focus + 1}/{total}]";
        var busy = Busy ? " ⋯" : string.Empty;
        sb.Append("\x1b[90m").Append(position).Append(' ').Append(status).Append(busy).Append("\x1b[0m\n");

        if (promptText is not null)
        {
            sb.Append("\x1b[1m› \x1b[0m").Append(promptText).Append('▏');
        }
        else
        {
            sb.Append("\x1b[90m↑↓/jk move · Enter expand · i prompt · ")
              .Append(Busy ? "Esc interrupt" : "Esc/q quit")
              .Append("\x1b[0m");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The slice of rows to draw: keep the focused row visible, and prefer showing the newest rows
    /// when there is slack. Pure, so the scroll maths is unit-testable.
    /// </summary>
    internal static (int Start, int End) Window(int total, int focus, int window, IReadOnlySet<int>? expanded = null)
    {
        if (total <= 0 || window <= 0)
        {
            return (0, 0);
        }

        if (total <= window)
        {
            return (0, total);
        }

        // Centre on the focus, then clamp so the window never runs past either end.
        var start = Math.Clamp(focus - (window / 2), 0, total - window);
        return (start, start + window);
    }

    private static AgentNode BuildDetail(AgentEvent row)
    {
        var detail = new AgentNode("Detail");
        foreach (var line in row.Body.ReplaceLineEndings("\n").Split('\n'))
        {
            var node = new AgentNode("DetailLine", classes: ["key"]);
            node.SetAttribute("Name", "  " + line);
            detail.Add(node);
        }

        return detail;
    }

    private static IEnumerable<string> SplitClasses(string classes) =>
        classes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Text for a node, by kind. Containers render nothing of their own.</summary>
    private static string NodeText(ITreeNode node)
    {
        if (node.Kind is "Surface" or "Detail")
        {
            return string.Empty;
        }

        return node.TryGetAttribute("Name", out var v) ? v?.ToString() ?? string.Empty : string.Empty;
    }

    /// <summary>Render a Spectre renderable to an ANSI frame at the current width (honours NO_COLOR).</summary>
    private static string RenderToAnsi(IRenderable renderable, int? widthOverride = null)
    {
        var writer = new StringWriter();
        var noColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = noColor ? AnsiSupport.No : AnsiSupport.Yes,
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Standard,
            Out = new AnsiConsoleOutput(writer),
        });

        try
        {
            var width = widthOverride ?? 0;
            if (width > 0)
            {
                console.Profile.Width = width;
            }
        }
        catch (IOException)
        {
            // Width unknown — let Spectre pick a default.
        }

        console.Write(renderable);
        return writer.ToString().TrimEnd('\r', '\n');
    }

}
