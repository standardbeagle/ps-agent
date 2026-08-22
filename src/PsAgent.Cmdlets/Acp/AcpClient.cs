using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PsAgent.Cmdlets.Agent;

namespace PsAgent.Cmdlets.Acp;

/// <summary>How to launch an ACP agent subprocess.</summary>
/// <param name="Command">Executable to run.</param>
/// <param name="Arguments">Its arguments.</param>
public sealed record AcpLaunch(string Command, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// Presets for the agents people actually have installed, so <c>-Agent claude</c> beats
    /// remembering an npx incantation. An unknown name falls through to being treated as an
    /// executable on PATH.
    /// </summary>
    public static IReadOnlyDictionary<string, AcpLaunch> Known { get; } =
        new Dictionary<string, AcpLaunch>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new("npx", ["-y", "@zed-industries/claude-code-acp"]),
            ["claude-code-acp"] = new("npx", ["-y", "@zed-industries/claude-code-acp"]),
            ["gemini"] = new("npx", ["-y", "@google/gemini-cli", "--experimental-acp"]),
            ["codex"] = new("npx", ["-y", "@zed-industries/codex-acp"]),

            // opencode ships ACP as a subcommand of its own binary rather than a separate package,
            // so the preset is the binary plus `acp` — verified against opencode 1.18.18.
            ["opencode"] = new("opencode", ["acp"]),
        };

    /// <summary>Resolve a friendly name, or an explicit command line, into a launch.</summary>
    public static AcpLaunch Resolve(string agent, IReadOnlyList<string>? extraArgs)
    {
        if (Known.TryGetValue(agent, out var preset))
        {
            return extraArgs is { Count: > 0 }
                ? preset with { Arguments = [.. preset.Arguments, .. extraArgs] }
                : preset;
        }

        return new AcpLaunch(agent, extraArgs ?? []);
    }
}

/// <summary>A permission decision the agent is waiting on.</summary>
/// <param name="ToolCallId">The call being gated.</param>
/// <param name="Title">What the agent says it wants to do.</param>
/// <param name="Options">The choices the agent offered, in its own order.</param>
public sealed record AcpPermissionRequest(string ToolCallId, string Title, IReadOnlyList<AcpPermissionOption> Options);

/// <summary>One choice in a permission request.</summary>
/// <param name="OptionId">The id to send back.</param>
/// <param name="Name">Human label.</param>
/// <param name="Kind">One of <c>allow_once</c>, <c>allow_always</c>, <c>reject_once</c>, <c>reject_always</c>.</param>
public sealed record AcpPermissionOption(string OptionId, string Name, string Kind);

/// <summary>
/// An Agent Client Protocol <b>client</b>: it launches an agent (Claude Code, Gemini, Codex …)
/// as a subprocess and drives it over stdio, so any ACP agent gets the same terminal front end.
/// </summary>
/// <remarks>
/// <para>ACP is bidirectional. We call <c>initialize</c>, <c>session/new</c> and
/// <c>session/prompt</c>; while a prompt is running the agent calls back into <i>us</i> for file
/// reads and writes (<c>fs/*</c>) and for permission to act
/// (<c>session/request_permission</c>), and streams progress as <c>session/update</c>
/// notifications. Serving those callbacks is not optional — an agent whose <c>fs/read_text_file</c>
/// goes unanswered simply stalls.</para>
/// <para>Filesystem callbacks are confined to the session's working directory by
/// <see cref="WorkspacePath.TryResolve"/>. The agent is a separate process with its own idea of
/// what it may touch; this client enforces its own boundary rather than trusting that one.</para>
/// </remarks>
public sealed class AcpClient : IAsyncDisposable
{
    /// <summary>The ACP major version this client speaks.</summary>
    public const int ProtocolVersion = 1;

    private readonly Process _process;
    private readonly JsonRpcConnection _rpc;
    private readonly string _cwd;
    private readonly CancellationTokenSource _pump = new();
    private Task? _pumpTask;
    private Task? _stderrTask;

    private AcpClient(Process process, JsonRpcConnection rpc, string cwd)
    {
        _process = process;
        _rpc = rpc;
        _cwd = cwd;
    }

    /// <summary>Called for every <c>session/update</c> notification, with its raw params.</summary>
    public Action<JsonNode?>? OnSessionUpdate { get; set; }

    /// <summary>
    /// Asked when the agent requests permission. Return the chosen <c>optionId</c>, or
    /// <see langword="null"/> to answer <c>cancelled</c>. Unset means every request is cancelled,
    /// which is the safe default for a non-interactive run.
    /// </summary>
    public Func<AcpPermissionRequest, string?>? OnPermission { get; set; }

    /// <summary>Called with anything the agent writes to stderr — usually its own logging.</summary>
    public Action<string>? OnAgentDiagnostic { get; set; }

    /// <summary>The agent's <c>initialize</c> response, once <see cref="InitializeAsync"/> has run.</summary>
    public JsonNode? AgentInfo { get; private set; }

    /// <summary>Launch <paramref name="launch"/> with its stdio wired up and start the read loop.</summary>
    public static AcpClient Start(AcpLaunch launch, string workingDirectory)
    {
        var psi = new ProcessStartInfo(launch.Command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in launch.Arguments)
        {
            psi.ArgumentList.Add(a);
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start ACP agent '{launch.Command}'.");

        var rpc = new JsonRpcConnection(process.StandardOutput, process.StandardInput);
        var client = new AcpClient(process, rpc, Path.GetFullPath(workingDirectory));

        rpc.OnNotification = client.HandleNotification;
        rpc.OnRequest = client.HandleRequestAsync;

        client._pumpTask = rpc.RunAsync(client._pump.Token);
        client._stderrTask = client.DrainStderrAsync(client._pump.Token);
        return client;
    }

    /// <summary>Negotiate the protocol version and exchange capabilities.</summary>
    public async Task<JsonNode?> InitializeAsync(CancellationToken ct = default)
    {
        var parameters = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["clientCapabilities"] = new JsonObject
            {
                // We serve both file callbacks. `terminal` is false: this client has no terminal
                // service, and claiming one we do not implement makes the agent hang on a call
                // that never returns.
                ["fs"] = new JsonObject
                {
                    ["readTextFile"] = true,
                    ["writeTextFile"] = true,
                },
                ["terminal"] = false,
            },
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "ps-agent",
                ["title"] = "PsAgent (Invoke-Acp)",
                ["version"] = typeof(AcpClient).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            },
        };

        AgentInfo = await _rpc.InvokeAsync("initialize", parameters, ct).ConfigureAwait(false);
        return AgentInfo;
    }

    /// <summary>Open a session rooted at the working directory and return its id.</summary>
    public async Task<string> NewSessionAsync(CancellationToken ct = default)
    {
        var parameters = new JsonObject
        {
            ["cwd"] = _cwd,
            ["mcpServers"] = new JsonArray(),
        };

        var result = await _rpc.InvokeAsync("session/new", parameters, ct).ConfigureAwait(false);
        return result?["sessionId"]?.GetValue<string>()
            ?? throw new JsonRpcException(-32603, "session/new returned no sessionId.");
    }

    /// <summary>
    /// Send a prompt and await the end of the turn. Returns ACP's <c>stopReason</c>
    /// (<c>end_turn</c>, <c>max_tokens</c>, <c>refusal</c>, <c>cancelled</c>, …). Progress arrives
    /// meanwhile on <see cref="OnSessionUpdate"/>.
    /// </summary>
    public async Task<string> PromptAsync(string sessionId, string text, CancellationToken ct = default)
    {
        var parameters = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };

        var result = await _rpc.InvokeAsync("session/prompt", parameters, ct).ConfigureAwait(false);
        return result?["stopReason"]?.GetValue<string>() ?? "end_turn";
    }

    /// <summary>
    /// Ask the agent to abandon the current turn. A notification, not a request: the turn still
    /// ends through <c>session/prompt</c> returning <c>cancelled</c>.
    /// </summary>
    public Task CancelAsync(string sessionId, CancellationToken ct = default) =>
        _rpc.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = sessionId }, ct);

    private void HandleNotification(string method, JsonNode? parameters)
    {
        if (method == "session/update")
        {
            OnSessionUpdate?.Invoke(parameters);
        }
    }

    private Task<JsonNode?> HandleRequestAsync(string method, JsonNode? parameters, CancellationToken ct) =>
        Task.FromResult(method switch
        {
            "fs/read_text_file" => ReadTextFile(parameters),
            "fs/write_text_file" => WriteTextFile(parameters),
            "session/request_permission" => RequestPermission(parameters),
            _ => throw new JsonRpcException(-32601, $"ps-agent does not implement '{method}'."),
        });

    private JsonNode? ReadTextFile(JsonNode? parameters)
    {
        var path = parameters?["path"]?.GetValue<string>() ?? string.Empty;
        var full = WorkspacePath.TryResolve(_cwd, path)
            ?? throw new JsonRpcException(-32602, $"Refused: '{path}' is outside the session directory.");

        if (!File.Exists(full))
        {
            throw new JsonRpcException(-32602, $"No such file: {path}");
        }

        var content = File.ReadAllText(full);

        // Optional windowing: `line` is 1-based, `limit` is a line count.
        var line = parameters?["line"]?.GetValue<int?>();
        var limit = parameters?["limit"]?.GetValue<int?>();
        if (line is not null || limit is not null)
        {
            var lines = content.ReplaceLineEndings("\n").Split('\n');
            var start = Math.Clamp((line ?? 1) - 1, 0, lines.Length);
            var take = Math.Clamp(limit ?? lines.Length - start, 0, lines.Length - start);
            content = string.Join('\n', lines.Skip(start).Take(take));
        }

        return new JsonObject { ["content"] = content };
    }

    private JsonNode? WriteTextFile(JsonNode? parameters)
    {
        var path = parameters?["path"]?.GetValue<string>() ?? string.Empty;
        var content = parameters?["content"]?.GetValue<string>() ?? string.Empty;
        var full = WorkspacePath.TryResolve(_cwd, path)
            ?? throw new JsonRpcException(-32602, $"Refused: '{path}' is outside the session directory.");

        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(full, content);
        return new JsonObject();
    }

    private JsonNode? RequestPermission(JsonNode? parameters)
    {
        var request = ParsePermission(parameters);
        var chosen = OnPermission?.Invoke(request);

        // The outcome is a nested object, not a bare string: {"outcome":{"outcome":"selected",
        // "optionId":"..."}}. Flattening it is silently treated as a malformed reply.
        return new JsonObject
        {
            ["outcome"] = chosen is null
                ? new JsonObject { ["outcome"] = "cancelled" }
                : new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen },
        };
    }

    /// <summary>Parse a <c>session/request_permission</c> payload. Pure, so the shape is testable.</summary>
    internal static AcpPermissionRequest ParsePermission(JsonNode? parameters)
    {
        var call = parameters?["toolCall"];
        var id = call?["toolCallId"]?.GetValue<string>() ?? string.Empty;
        var title = call?["title"]?.GetValue<string>()
            ?? call?["kind"]?.GetValue<string>()
            ?? "the agent is requesting permission";

        List<AcpPermissionOption> options = [];
        if (parameters?["options"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject o)
                {
                    continue;
                }

                var optionId = o["optionId"]?.GetValue<string>();
                if (optionId is null)
                {
                    continue;
                }

                options.Add(new AcpPermissionOption(
                    optionId,
                    o["name"]?.GetValue<string>() ?? optionId,
                    o["kind"]?.GetValue<string>() ?? string.Empty));
            }
        }

        return new AcpPermissionRequest(id, title, options);
    }

    private async Task DrainStderrAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                OnAgentDiagnostic?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (IOException)
        {
            // The pipe closed with the process.
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _pump.CancelAsync().ConfigureAwait(false);

        try
        {
            if (!_process.HasExited)
            {
                // Closing stdin is the graceful ACP shutdown — an agent that honours it flushes
                // and exits. The kill below is the backstop for one that does not.
                _process.StandardInput.Close();
                if (!_process.WaitForExit(2000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or NotSupportedException)
        {
            // Already gone.
        }

        foreach (var task in new[] { _pumpTask, _stderrTask })
        {
            if (task is not null)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException)
                {
                    // The loop is wedged on a dead pipe; the process is already killed.
                }
            }
        }

        _rpc.Dispose();
        _pump.Dispose();
        _process.Dispose();
    }
}
