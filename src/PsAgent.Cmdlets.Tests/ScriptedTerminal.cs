using System.Collections.Concurrent;
using System.Text;
using PsAgent.Cmdlets.Ui;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// A terminal made of a key queue and a string buffer, so the viewer's real loop can be driven
/// without a TTY.
/// </summary>
/// <remarks>
/// <para>Keys are queued ahead of time; when the queue empties the terminal reports no key
/// available, which is exactly what a real terminal does between keystrokes, so the loop keeps
/// repainting instead of blocking. A test that wants to react to something the agent produced can
/// therefore enqueue more keys from another thread mid-run.</para>
/// <para><see cref="Idle"/> counts its calls, giving a cheap watchdog: a loop that idles far more
/// than the script expects is stuck, and the test can fail rather than hang.</para>
/// </remarks>
internal sealed class ScriptedTerminal : ITerminal
{
    private readonly ConcurrentQueue<ConsoleKeyInfo> _keys = new();
    private readonly StringBuilder _output = new();
    private readonly List<string> _frames = [];
    private readonly object _sync = new();

    /// <inheritdoc/>
    public bool IsInteractive => true;

    /// <inheritdoc/>
    public int Width { get; init; } = 100;

    /// <inheritdoc/>
    public int Height { get; init; } = 30;

    /// <summary>How many times the loop parked with no key to read.</summary>
    public int IdleCount { get; private set; }

    /// <summary>Idle turns to allow before <see cref="ReadKey"/> gives up, so a test cannot hang.</summary>
    public int IdleBudget { get; init; } = 4000;

    /// <summary>Everything written to the terminal, in order.</summary>
    public string Output
    {
        get { lock (_sync) { return _output.ToString(); } }
    }

    /// <summary>Each individual write — one per repaint, so a test can inspect the last frame.</summary>
    public IReadOnlyList<string> Frames
    {
        get { lock (_sync) { return [.. _frames]; } }
    }

    /// <summary>The most recent frame, with ANSI sequences stripped.</summary>
    /// <remarks>
    /// After the loop exits this is the alt-screen teardown write, which strips to nothing — use
    /// <see cref="LastContentFrame"/> to see what the user actually last looked at.
    /// </remarks>
    public string LastPlainFrame
    {
        get
        {
            lock (_sync)
            {
                return _frames.Count == 0 ? string.Empty : StripAnsi(_frames[^1]);
            }
        }
    }

    /// <summary>
    /// The last frame that actually carried transcript text, ANSI stripped — i.e. the final
    /// painted screen, skipping the control-sequence-only writes that enter and leave the
    /// alternate screen.
    /// </summary>
    public string LastContentFrame
    {
        get
        {
            lock (_sync)
            {
                for (var i = _frames.Count - 1; i >= 0; i--)
                {
                    var plain = StripAnsi(_frames[i]).Trim();
                    if (plain.Length > 0)
                    {
                        return plain;
                    }
                }

                return string.Empty;
            }
        }
    }

    /// <summary>Queue printable characters, one key each.</summary>
    public ScriptedTerminal Type(string text)
    {
        foreach (var ch in text)
        {
            _keys.Enqueue(new ConsoleKeyInfo(ch, ConsoleKey.NoName, false, false, false));
        }

        return this;
    }

    /// <summary>Queue a named key.</summary>
    public ScriptedTerminal Press(ConsoleKey key, char ch = '\0')
    {
        _keys.Enqueue(new ConsoleKeyInfo(ch, key, false, false, false));
        return this;
    }

    /// <inheritdoc/>
    public bool KeyAvailable => !_keys.IsEmpty;

    /// <inheritdoc/>
    public ConsoleKeyInfo ReadKey() =>
        _keys.TryDequeue(out var key)
            ? key
            : throw new InvalidOperationException("ScriptedTerminal.ReadKey with an empty queue.");

    /// <inheritdoc/>
    public void Write(string text)
    {
        lock (_sync)
        {
            _output.Append(text);
            _frames.Add(text);
        }
    }

    /// <inheritdoc/>
    public void SetCursorVisible(bool visible)
    {
        // Nothing to do; recorded only by its absence of failure.
    }

    /// <summary>True between <see cref="BeginSession"/> and <see cref="EndSession"/>.</summary>
    public bool SessionOpen { get; private set; }

    /// <summary>How many times a session has been opened — a viewer must open exactly one.</summary>
    public int SessionOpenCount { get; private set; }

    /// <inheritdoc/>
    public void BeginSession()
    {
        SessionOpen = true;
        SessionOpenCount++;
    }

    /// <inheritdoc/>
    public void EndSession() => SessionOpen = false;

    /// <inheritdoc/>
    public void Idle(int milliseconds)
    {
        IdleCount++;
        if (IdleCount > IdleBudget)
        {
            throw new TimeoutException(
                $"the viewer idled {IdleCount} times without consuming the script — it is stuck.");
        }

        Thread.Sleep(Math.Min(milliseconds, 5));
    }

    /// <summary>True once any written frame contains <paramref name="text"/> (ANSI stripped).</summary>
    public bool Saw(string text)
    {
        lock (_sync)
        {
            return StripAnsi(_output.ToString()).Contains(text, StringComparison.Ordinal);
        }
    }

    /// <summary>Block until <see cref="Saw"/> is true, or the timeout elapses.</summary>
    public async Task<bool> WaitForAsync(string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Saw(text))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    /// <summary>Drop ANSI escape sequences so plain text can be matched.</summary>
    public static string StripAnsi(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[[0-9;?]*[a-zA-Z]", string.Empty);
}
