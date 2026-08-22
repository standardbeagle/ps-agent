using System.Collections.Concurrent;
using System.Management.Automation;
using PsAgent.Cmdlets.Acp;
using PsAgent.Cmdlets.Agent;
using PsAgent.Cmdlets.Ui;

namespace PsAgent.Cmdlets;

/// <summary>
/// Drive any Agent Client Protocol agent — Claude Code, Gemini, Codex — from the terminal, with
/// the same transcript UI <c>Invoke-Agent</c> uses.
/// </summary>
/// <remarks>
/// <para>ACP is the editor-to-agent protocol Zed defined: JSON-RPC over the agent's stdio. This
/// cmdlet is the <b>client</b> half. It launches the agent, negotiates capabilities, opens a
/// session rooted at the working directory, and then both streams the agent's
/// <c>session/update</c> notifications into the viewer and answers the callbacks the agent makes
/// back — file reads and writes, and permission requests, which surface as a chooser.</para>
/// <para>The agent runs as a separate process with its own model, its own key, and its own tools;
/// nothing here talks to an LLM. That is the point: one front end, any agent.</para>
/// </remarks>
/// <example>
///   <code>Invoke-Acp -Agent claude "add a regression test for the trailing-newline bug"</code>
/// </example>
[Cmdlet(VerbsLifecycle.Invoke, "Acp", DefaultParameterSetName = "Prompt")]
[Alias("acp")]
[OutputType(typeof(PSObject))]
public sealed class InvokeAcpCommand : PSCmdlet
{
    /// <summary>What to ask. Omitted on a terminal, the viewer opens empty and waits for <c>i</c>.</summary>
    [Parameter(Position = 0, ValueFromPipeline = true)]
    [AllowEmptyString]
    public string? Prompt { get; set; }

    /// <summary>
    /// Which agent: a known name (<c>claude</c>, <c>gemini</c>, <c>codex</c>) or an executable on
    /// PATH that speaks ACP on stdio.
    /// </summary>
    [Parameter]
    public string Agent { get; set; } = "claude";

    /// <summary>Extra arguments appended to the agent's command line.</summary>
    [Parameter]
    [Alias("Args")]
    public string[]? ArgumentList { get; set; }

    /// <summary>Directory the session is rooted at. Defaults to the current location.</summary>
    [Parameter]
    [Alias("Root", "Path")]
    public string? WorkspaceRoot { get; set; }

    /// <summary>Answer every permission request with the agent's first allow-shaped option.</summary>
    [Parameter]
    public SwitchParameter AutoApprove { get; set; }

    /// <summary>Skip the terminal viewer and write transcript objects to the pipeline.</summary>
    [Parameter]
    public SwitchParameter NoUi { get; set; }

    /// <summary>Show the agent's stderr as transcript rows. Useful when a launch fails.</summary>
    [Parameter]
    public SwitchParameter ShowAgentLog { get; set; }

    /// <summary>Seconds to wait for <c>initialize</c> before giving up on the agent.</summary>
    [Parameter]
    [ValidateRange(1, 600)]
    public int ConnectTimeoutSeconds { get; set; } = 60;

    /// <summary>Stylesheet name, inline CSS, or a .pcss path. Defaults to the built-in <c>agent</c> sheet.</summary>
    [Parameter]
    [Alias("Style", "Stylesheet")]
    public string? Css { get; set; }

    private readonly AcpTranscript _transcript = new();
    private readonly ConcurrentQueue<AgentEvent> _headless = new();
    private readonly object _sync = new();
    private TranscriptView? _view;

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        var root = ResolveRoot();
        var launch = AcpLaunch.Resolve(Agent, ArgumentList);
        var interactive = !NoUi && TranscriptView.IsInteractive;

        if (!interactive && string.IsNullOrWhiteSpace(Prompt))
        {
            WriteError(new ErrorRecord(
                new ArgumentException("No prompt, and no terminal to ask for one."),
                "NoPrompt", ErrorCategory.InvalidArgument, null));
            return;
        }

        AcpClient client;
        try
        {
            client = AcpClient.Start(launch, root);
        }
        catch (Exception e)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    $"Could not launch ACP agent '{launch.Command}'. Is it installed? ({e.Message})", e),
                "AcpLaunchFailed", ErrorCategory.ResourceUnavailable, launch.Command));
            return;
        }

        try
        {
            RunSession(client, root, launch, interactive);
        }
        finally
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        foreach (var row in _transcript.Rows)
        {
            WriteObject(row.ToPSObject());
        }
    }

    private void RunSession(AcpClient client, string root, AcpLaunch launch, bool interactive)
    {
        client.OnSessionUpdate = parameters =>
        {
            lock (_sync)
            {
                _transcript.Apply(parameters);
                Publish();
            }
        };

        if (ShowAgentLog)
        {
            client.OnAgentDiagnostic = line => Note(AgentEventKind.Status, $"[agent] {line}");
        }

        client.OnPermission = AutoApprove ? AutoAllow : AskForPermission;

        string sessionId;
        try
        {
            using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
            var info = client.InitializeAsync(connect.Token).GetAwaiter().GetResult();
            var name = info?["agentInfo"]?["name"]?.ToString() ?? launch.Command;
            var version = info?["agentInfo"]?["version"]?.ToString() ?? "?";
            Note(AgentEventKind.Status, $"connected to {name} {version} (ACP v{AcpClient.ProtocolVersion})");

            sessionId = client.NewSessionAsync(connect.Token).GetAwaiter().GetResult();
            Note(AgentEventKind.Status, $"session {sessionId} in {root}");
        }
        catch (Exception e)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    $"ACP handshake with '{launch.Command}' failed: {e.Message}", e),
                "AcpHandshakeFailed", ErrorCategory.ProtocolError, launch.Command));
            return;
        }

        if (!interactive)
        {
            RunHeadless(client, sessionId);
            return;
        }

        _view = new TranscriptView(AgentStyles.Resolve(Css), $"ps-acp · {Agent} · {root}")
        {
            Status = "ready",
        };
        Publish();

        _view.OnSubmit = async (text, ct) =>
        {
            lock (_sync)
            {
                _transcript.Add(new AgentEvent
                {
                    Kind = AgentEventKind.User,
                    Title = text,
                    Body = text,
                });
                Publish();
            }

            // Esc cancels the turn: ACP wants an explicit session/cancel notification, and then
            // session/prompt returns "cancelled" of its own accord.
            using var reg = ct.Register(() =>
                client.CancelAsync(sessionId, CancellationToken.None).GetAwaiter().GetResult());

            var stop = await client.PromptAsync(sessionId, text, CancellationToken.None).ConfigureAwait(false);
            Note(AgentEventKind.Status, $"stop reason: {stop}");
        };

        _view.Run(Prompt, CancellationToken.None);
    }

    private void RunHeadless(AcpClient client, string sessionId)
    {
        lock (_sync)
        {
            _transcript.Add(new AgentEvent
            {
                Kind = AgentEventKind.User,
                Title = Prompt!,
                Body = Prompt!,
            });
        }

        var turn = client.PromptAsync(sessionId, Prompt!, CancellationToken.None);

        // Drain to the pipeline as updates arrive rather than after the turn, so a long run shows
        // progress under `| Tee-Object` or a redirect.
        while (!turn.IsCompleted)
        {
            DrainHeadless();
            Thread.Sleep(25);
        }

        DrainHeadless();

        if (turn.IsFaulted)
        {
            var inner = turn.Exception!.GetBaseException();
            WriteError(new ErrorRecord(inner, "AcpPromptFailed", ErrorCategory.NotSpecified, null));
            return;
        }

        Note(AgentEventKind.Status, $"stop reason: {turn.Result}");
    }

    private void DrainHeadless()
    {
        while (_headless.TryDequeue(out _))
        {
            // The transcript is the source of truth in the ACP path (rows mutate in place as
            // chunks arrive), so rows are emitted once at the end. This queue only paces the loop.
        }
    }

    /// <summary>Push the current transcript into the viewer, if one is up.</summary>
    private void Publish() => _view?.ReplaceAll(_transcript.Rows);

    private void Note(AgentEventKind kind, string text)
    {
        lock (_sync)
        {
            _transcript.Add(new AgentEvent { Kind = kind, Title = text, Body = text });
            Publish();
        }
    }

    /// <summary>Render the agent's own options as a chooser; Esc cancels the request.</summary>
    private string? AskForPermission(AcpPermissionRequest request)
    {
        var view = _view;
        if (view is null || request.Options.Count == 0)
        {
            return null;
        }

        var labels = request.Options
            .Select(o => string.IsNullOrEmpty(o.Kind) ? o.Name : $"{o.Name}  ({o.Kind})")
            .ToArray();

        var choice = view.Choose(request.Title, labels);
        return choice is null ? null : request.Options[choice.Value].OptionId;
    }

    /// <summary>
    /// Pick the agent's own allow option rather than assuming an id: <c>optionId</c> strings are
    /// agent-specific, but <c>kind</c> is protocol-defined, so that is what is matched on.
    /// </summary>
    internal static string? AutoAllow(AcpPermissionRequest request)
    {
        foreach (var wanted in (string[])["allow_always", "allow_once"])
        {
            var match = request.Options.FirstOrDefault(o =>
                string.Equals(o.Kind, wanted, StringComparison.Ordinal));
            if (match is not null)
            {
                return match.OptionId;
            }
        }

        return request.Options.Count > 0 ? request.Options[0].OptionId : null;
    }

    private string ResolveRoot()
    {
        var raw = string.IsNullOrWhiteSpace(WorkspaceRoot)
            ? SessionState.Path.CurrentFileSystemLocation.Path
            : GetUnresolvedProviderPathFromPSPath(WorkspaceRoot);

        return Path.GetFullPath(raw);
    }
}
