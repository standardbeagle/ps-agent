using System.Collections.Concurrent;
using System.Management.Automation;
using Anthropic;
using Anthropic.Models.Messages;
using PsAgent.Cmdlets.Agent;
using PsAgent.Cmdlets.Agent.Models;
using PsAgent.Cmdlets.Agent.OAuth;
using PsAgent.Cmdlets.Ui;

namespace PsAgent.Cmdlets;

/// <summary>
/// A minimal coding agent, in the shape <c>pi</c> made popular: a model, four tools, and a loop.
/// </summary>
/// <remarks>
/// <para>Interactive on a real terminal — a live transcript rendered through the same Strata
/// stylesheet cascade <c>Show-Styled</c> uses, with ↑↓ to browse, Enter to expand a tool result,
/// and <c>i</c> to type the next prompt. Redirected or under <c>-NoUi</c> it degrades to a plain
/// pipeline of <c>PsAgent.AgentEvent</c> objects, so it composes:
/// <c>Invoke-Agent "..." -NoUi | Where-Object Kind -eq ToolCall</c>.</para>
/// <para>Mutating tools ask before they run. <c>-AutoApprove</c> turns that off; without a
/// terminal and without that switch they are refused, because a silent yes is the one default a
/// tool that edits files and runs commands must not have.</para>
/// </remarks>
/// <example>
///   <code>Invoke-Agent "why does the parser drop trailing newlines?"</code>
/// </example>
[Cmdlet(VerbsLifecycle.Invoke, "Agent", DefaultParameterSetName = "Prompt")]
[Alias("agent", "pia")]
[OutputType(typeof(PSObject))]
public sealed class InvokeAgentCommand : PSCmdlet
{
    /// <summary>What to ask. Omitted on a terminal, the viewer opens empty and waits for <c>i</c>.</summary>
    [Parameter(Position = 0, ValueFromPipeline = true)]
    [AllowEmptyString]
    public string? Prompt { get; set; }

    /// <summary>Directory the agent may read, write and run commands in. Defaults to the current location.</summary>
    [Parameter]
    [Alias("Root", "Path")]
    public string? WorkspaceRoot { get; set; }

    /// <summary>Model id.</summary>
    [Parameter]
    public string Model { get; set; } = "claude-opus-5";

    /// <summary>Per-response output ceiling.</summary>
    [Parameter]
    [ValidateRange(256, 128_000)]
    public int MaxTokens { get; set; } = 16_000;

    /// <summary>Reasoning effort. Lower is cheaper and terser.</summary>
    [Parameter]
    [ValidateSet("low", "medium", "high", "max", IgnoreCase = true)]
    public string EffortLevel { get; set; } = "high";

    /// <summary>Hard ceiling on model round-trips in one turn.</summary>
    [Parameter]
    [ValidateRange(1, 500)]
    public int MaxTurns { get; set; } = 40;

    /// <summary>Seconds a single <c>run_bash</c> call may take before its process tree is killed.</summary>
    [Parameter]
    [ValidateRange(1, 3600)]
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>Run edits and commands without asking.</summary>
    [Parameter]
    public SwitchParameter AutoApprove { get; set; }

    /// <summary>Skip the terminal viewer and write transcript objects to the pipeline.</summary>
    [Parameter]
    public SwitchParameter NoUi { get; set; }

    /// <summary>Do not show the model's reasoning rows.</summary>
    [Parameter]
    public SwitchParameter NoThinking { get; set; }

    /// <summary>Stylesheet name, inline CSS, or a .pcss path. Defaults to the built-in <c>agent</c> sheet.</summary>
    [Parameter]
    [Alias("Style", "Stylesheet")]
    public string? Css { get; set; }

    /// <summary>Replace the built-in system prompt.</summary>
    [Parameter]
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// The API key to authenticate with. Omitted, it is resolved from the environment, the
    /// workspace's <c>.env.local</c>, a CLI credential store, then the SDK's own lookup.
    /// </summary>
    [Parameter]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Endpoint to call instead of Anthropic's. Any gateway serving the Anthropic Messages API
    /// works; <c>https://openrouter.ai/api</c> is verified. Give the base <b>without</b> the
    /// version segment — the SDK appends <c>/v1/messages</c> itself.
    /// </summary>
    [Parameter]
    [Alias("Endpoint")]
    public string? BaseUrl { get; set; }

    /// <summary>Do not read credentials out of other tools' stores or the workspace's .env.local.</summary>
    [Parameter]
    public SwitchParameter NoCredentialDiscovery { get; set; }

    /// <summary>
    /// Authenticate with a browser sign-in stored by <c>Connect-Agent</c>. Omitted, a single stored
    /// sign-in is used automatically; with several, name one.
    /// </summary>
    [Parameter]
    [Alias("OAuthProfile")]
    public string? OAuthProvider { get; set; }

    /// <summary>
    /// Which wire format the endpoint speaks. <c>auto</c> picks <c>openai</c> whenever a browser
    /// sign-in is in play, because Anthropic's OAuth is restricted to its own client and a bearer
    /// token from anywhere else is going to an OpenAI-compatible service.
    /// </summary>
    [Parameter]
    [ValidateSet("auto", "anthropic", "openai", IgnoreCase = true)]
    public string Api { get; set; } = "auto";

    private readonly ConcurrentQueue<AgentEvent> _pending = new();

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        var root = ResolveRoot();
        var options = new CodingAgentOptions
        {
            Model = Model,
            MaxTokens = MaxTokens,
            Effort = ParseEffort(EffortLevel),
            MaxTurns = MaxTurns,
            ShowThinking = !NoThinking,
            SystemPrompt = SystemPrompt,
        };

        var discovered = NoCredentialDiscovery
            ? []
            : CliCredentialStore.DiscoverAll();
        var dotEnv = NoCredentialDiscovery
            ? null
            : DotEnvFile.Read(root);

        // Loading the sign-in may refresh it, which is a network call, so it happens here rather
        // than inside resolution — the resolver stays a pure decision over what is available.
        ResolvedOAuth? oauth = null;
        var authNotes = new List<string>();
        if (!NoCredentialDiscovery || !string.IsNullOrWhiteSpace(OAuthProvider))
        {
            using var http = new HttpClient();
            oauth = OAuthCredentialProvider
                .TryResolveAsync(OAuthProvider, http, DateTimeOffset.UtcNow, authNotes.Add)
                .GetAwaiter().GetResult();
        }

        var credential = AgentCredentialResolver.Resolve(ApiKey, BaseUrl, env: null, discovered, dotEnv, oauth);

        AnthropicClient client;
        try
        {
            // A null ApiKey deliberately leaves the SDK to resolve: it reads ANTHROPIC_API_KEY,
            // then ANTHROPIC_AUTH_TOKEN, then an `ant auth login` profile. An unset env var does
            // not mean unauthenticated.
            // ApiKey and BaseUrl are init-only, so the client is built in one initializer. Leaving
            // ApiKey null is meaningful, not a gap: it is what defers to the SDK's own resolution.
            client = new AnthropicClient
            {
                ApiKey = credential.ApiKey is { Length: > 0 } ? credential.ApiKey : null,
                // An OAuth sign-in authenticates as a bearer token, not a key, and the provider
                // may require extra headers alongside it.
                AuthToken = credential.AuthToken is { Length: > 0 } ? credential.AuthToken : null,
                BaseUrl = credential.BaseUrl is { Length: > 0 } ? credential.BaseUrl : null,
                // Provider-required headers ride on a handler rather than the SDK's ExtraHeaders,
                // which is documented but not publicly settable.
                HttpClient = credential.ExtraHeaders.Count > 0
                    ? new HttpClient(new HeaderInjectingHandler(credential.ExtraHeaders))
                    : null,
            };
        }
        catch (Exception e)
        {
            ThrowTerminatingError(new ErrorRecord(
                e, "AnthropicClientInit", ErrorCategory.AuthenticationError, null));
            return;
        }

        // Which credential is in play is the first question any auth failure raises, so the
        // transcript answers it up front rather than leaving it to be inferred.
        var endpoint = credential.BaseUrl is { Length: > 0 } ? $" -> {credential.BaseUrl}" : string.Empty;
        var authRow = new AgentEvent
        {
            Kind = AgentEventKind.Status,
            Title = $"auth: {credential.Detail}{endpoint}",
            Body = string.Join(
                Environment.NewLine,
                new[] { AgentCredentialResolver.DescribeUnused(discovered) }
                    .Concat(authNotes)
                    .Where(l => !string.IsNullOrWhiteSpace(l))),
        };

        var interactive = !NoUi && TranscriptView.IsInteractive;
        TranscriptView? view = null;

        AgentTools.ApprovalGate gate = AutoApprove
            ? AgentTools.AllowAll
            : interactive
                ? (tool, detail) => AskInView(view!, tool, detail)
                : AskAtConsole;

        var tools = new AgentTools(
            root,
            gate,
            TimeSpan.FromSeconds(CommandTimeoutSeconds),
            maxOutputChars: 20_000);

        var api = ChooseApi(Api, credential, oauth?.Api);
        IModelConversation conversation;
        if (api == "openai")
        {
            var apiEndpoint = credential.BaseUrl;
            if (string.IsNullOrWhiteSpace(apiEndpoint))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException(
                        "The openai transport needs an endpoint. Give -BaseUrl, or a baseUrl in the OAuth profile."),
                    "NoBaseUrl", ErrorCategory.InvalidArgument, null));
                return;
            }

            conversation = new OpenAiConversation(
                OpenAiHttpClient(credential),
                apiEndpoint!,
                options.Model,
                options.SystemPrompt ?? CodingAgent.DefaultSystemPrompt(root),
                AgentTools.Catalog,
                options.MaxTokens);
        }
        else
        {
            conversation = new AnthropicConversation(
                client,
                options.Model,
                options.SystemPrompt ?? CodingAgent.DefaultSystemPrompt(root),
                AgentTools.Catalog,
                options.MaxTokens,
                options.Effort);
        }

        var agent = new CodingAgent(conversation, tools, options);
        authRow = authRow with { Title = authRow.Title + $" [{conversation.Describe}]" };

        if (!interactive)
        {
            _pending.Enqueue(authRow);
            RunHeadless(agent);
            return;
        }

        view = new TranscriptView(AgentStyles.Resolve(Css), $"ps-agent · {options.Model} · {root}")
        {
            Status = "ready",
        };
        view.Append(authRow);
        view.OnSubmit = (text, ct) => agent.RunTurnAsync(text, view!.Append, ct);

        var code = view.Run(Prompt, CancellationToken.None);
        if (code < 0)
        {
            // No usable terminal after all — never lose the transcript to a failed viewer.
            RunHeadless(agent);
            return;
        }

        foreach (var row in view.Snapshot())
        {
            WriteObject(row.ToPSObject());
        }
    }

    /// <summary>
    /// Run one turn with no viewer, draining transcript rows to the pipeline as they happen.
    /// </summary>
    /// <remarks>
    /// The agent runs on a worker task but <see cref="Cmdlet.WriteObject(object)"/> is only legal
    /// on the pipeline thread, so rows are queued by the callback and written here.
    /// </remarks>
    private void RunHeadless(CodingAgent agent)
    {
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            WriteError(new ErrorRecord(
                new ArgumentException("No prompt, and no terminal to ask for one."),
                "NoPrompt", ErrorCategory.InvalidArgument, null));
            return;
        }

        var turn = Task.Run(() => agent.RunTurnAsync(Prompt!, _pending.Enqueue, CancellationToken.None));

        while (!turn.IsCompleted)
        {
            Drain();
            Thread.Sleep(25);
        }

        Drain();

        if (turn.Exception is { } ex)
        {
            var inner = ex.GetBaseException();
            WriteError(new ErrorRecord(inner, "AgentTurnFailed", ErrorCategory.NotSpecified, null));
        }
    }

    private void Drain()
    {
        while (_pending.TryDequeue(out var row))
        {
            WriteObject(row.ToPSObject());
        }
    }

    /// <summary>Ask inside the viewer, so the approval renders on the alt screen rather than behind it.</summary>
    private static bool AskInView(TranscriptView view, string tool, string detail)
    {
        var choice = view.Choose(
            $"{tool}: {detail}",
            ["Allow once", "Allow everything this session", "Refuse"]);

        if (choice == 1)
        {
            // Session-wide yes is intentionally NOT sticky here: the gate is a delegate captured
            // once, so a durable "always" would need to mutate shared state from a worker thread.
            // Treated as a plain allow; -AutoApprove is the durable form.
            return true;
        }

        return choice == 0;
    }

    /// <summary>
    /// The non-terminal path: refuse. A prompt that cannot be shown cannot be answered, and
    /// proceeding would silently grant the permission it was meant to check.
    /// </summary>
    private static bool AskAtConsole(string tool, string detail) => false;

    private string ResolveRoot()
    {
        var raw = string.IsNullOrWhiteSpace(WorkspaceRoot)
            ? SessionState.Path.CurrentFileSystemLocation.Path
            : GetUnresolvedProviderPathFromPSPath(WorkspaceRoot);

        return Path.GetFullPath(raw);
    }

    /// <summary>
    /// Decide the wire format. A browser sign-in implies OpenAI-compatible: Anthropic restricts its
    /// OAuth to Claude Code, so a bearer token obtained by any other client belongs to a service
    /// speaking the OpenAI shape.
    /// </summary>
    internal static string ChooseApi(string requested, AgentCredential credential, string? profileApi = null)
    {
        if (!string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return requested.ToLowerInvariant();
        }

        if (credential.Source != AgentCredentialSource.OAuth)
        {
            return "anthropic";
        }

        // The profile knows what its credential is good for; openai is the fallback because that
        // is what a browser sign-in obtained by anything other than Claude Code can be spent on.
        return string.IsNullOrWhiteSpace(profileApi) ? "openai" : profileApi.ToLowerInvariant();
    }

    /// <summary>An HTTP client carrying the bearer token and whatever headers the provider needs.</summary>
    private static HttpClient OpenAiHttpClient(AgentCredential credential)
    {
        var http = credential.ExtraHeaders.Count > 0
            ? new HttpClient(new HeaderInjectingHandler(credential.ExtraHeaders))
            : new HttpClient();

        var token = credential.AuthToken ?? credential.ApiKey;
        if (!string.IsNullOrEmpty(token))
        {
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        http.Timeout = TimeSpan.FromMinutes(10);
        return http;
    }

    /// <summary>Map the cmdlet's string level onto the SDK enum.</summary>
    internal static Effort ParseEffort(string level) => level.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "max" => Effort.Max,
        _ => Effort.High,
    };
}
