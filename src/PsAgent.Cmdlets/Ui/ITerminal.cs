namespace PsAgent.Cmdlets.Ui;

/// <summary>
/// The terminal the transcript viewer draws on and reads keys from.
/// </summary>
/// <remarks>
/// <para>Exists so the interactive loop is not welded to <see cref="Console"/>. Everything else in
/// the viewer is a pure function and unit-tested; the loop itself — prompt entry, a turn running
/// while rows arrive, expanding a row, the permission chooser — was reachable only by a human at a
/// keyboard, which meant it was never exercised at all.</para>
/// <para>With this seam a test drives the real loop with scripted keys and captures the frames,
/// including against a live agent. Production still runs on <see cref="ConsoleTerminal"/>, so the
/// tested path and the shipped path are the same code.</para>
/// </remarks>
internal interface ITerminal
{
    /// <summary>False when stdin or stdout is redirected — the viewer then declines to run.</summary>
    bool IsInteractive { get; }

    /// <summary>Columns available for rendering.</summary>
    int Width { get; }

    /// <summary>Rows available for rendering.</summary>
    int Height { get; }

    /// <summary>Whether <see cref="ReadKey"/> would return without blocking.</summary>
    bool KeyAvailable { get; }

    /// <summary>Read one keystroke without echoing it.</summary>
    ConsoleKeyInfo ReadKey();

    /// <summary>Emit text (frames and control sequences) verbatim.</summary>
    void Write(string text);

    /// <summary>Show or hide the cursor. Best-effort; an unsupporting host is not an error.</summary>
    void SetCursorVisible(bool visible);

    /// <summary>Park briefly when there is no key to read, so the repaint loop does not spin.</summary>
    void Idle(int milliseconds);

    /// <summary>
    /// Take ownership of the terminal for the duration of the viewer — in particular, guarantee
    /// the output encoding can carry the transcript's glyphs.
    /// </summary>
    void BeginSession();

    /// <summary>Hand the terminal back in the state it was found.</summary>
    void EndSession();
}

/// <summary>The real terminal.</summary>
internal sealed class ConsoleTerminal : ITerminal
{
    /// <summary>The single shared instance; <see cref="Console"/> is process-global anyway.</summary>
    public static ConsoleTerminal Instance { get; } = new();

    /// <inheritdoc/>
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <inheritdoc/>
    public int Width => Measure(() => Console.WindowWidth, 80);

    /// <inheritdoc/>
    public int Height => Measure(() => Console.WindowHeight, 24);

    /// <inheritdoc/>
    public bool KeyAvailable => Console.KeyAvailable;

    /// <inheritdoc/>
    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    /// <inheritdoc/>
    public void Write(string text) => Console.Write(text);

    /// <inheritdoc/>
    public void SetCursorVisible(bool visible)
    {
        try
        {
            Console.CursorVisible = visible;
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException)
        {
            // Host does not support cursor control.
        }
    }

    /// <inheritdoc/>
    public void Idle(int milliseconds) => Thread.Sleep(milliseconds);

    private System.Text.Encoding? _restoreEncoding;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>Forces UTF-8 output. The transcript's row markers are non-Latin-1 — <c>●</c>, <c>⚙</c>,
    /// <c>✔</c>, <c>✘</c>, <c>↑↓</c> — and a console left on a legacy code page silently replaces
    /// every one of them with <c>?</c>. Under CP1252 the failure is especially deceptive: <c>›</c>,
    /// <c>·</c> and <c>—</c> are all in that code page and survive, so the transcript looks almost
    /// right while the two markers that matter most (tool and assistant) are the ones lost.</para>
    /// <para>PowerShell 7 usually defaults to UTF-8, so this is invisible most of the time — it is
    /// the odd host that needs it, which is exactly why it cannot be left to chance.</para>
    /// </remarks>
    public void BeginSession()
    {
        try
        {
            if (Console.OutputEncoding.CodePage != System.Text.Encoding.UTF8.CodePage)
            {
                _restoreEncoding = Console.OutputEncoding;
                Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or ArgumentException)
        {
            // A host that refuses the change still renders; the glyphs degrade rather than crash.
            _restoreEncoding = null;
        }
    }

    /// <inheritdoc/>
    public void EndSession()
    {
        if (_restoreEncoding is null)
        {
            return;
        }

        try
        {
            Console.OutputEncoding = _restoreEncoding;
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or ArgumentException)
        {
            // Nothing further to do — the process is leaving the viewer either way.
        }
        finally
        {
            _restoreEncoding = null;
        }
    }

    private static int Measure(Func<int> read, int fallback)
    {
        try
        {
            var value = read();
            return value > 0 ? value : fallback;
        }
        catch (IOException)
        {
            return fallback;   // size unknown (no console attached)
        }
    }
}
