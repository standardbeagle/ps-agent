using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsAgent.Cmdlets.Acp;

/// <summary>
/// A JSON-RPC 2.0 peer over a line-delimited stream — the transport the Agent Client Protocol
/// speaks on an agent subprocess's stdin/stdout.
/// </summary>
/// <remarks>
/// <para>ACP frames one JSON value per line (JSON Lines), <b>not</b> LSP's
/// <c>Content-Length</c> headers. Getting that wrong produces a connection that hangs on
/// <c>initialize</c> with no error, so it is stated here rather than left to the reader.</para>
/// <para>Constructed over <see cref="TextReader"/>/<see cref="TextWriter"/> rather than a
/// <see cref="System.Diagnostics.Process"/> so the whole protocol layer can be driven in a test
/// from a pair of in-memory streams, with no subprocess and no ACP agent installed.</para>
/// <para>The peer is bidirectional: while we wait on <c>session/prompt</c>, the agent calls
/// <i>us</i> back for file reads and permission decisions. Those arrive on the same read loop, so
/// the loop must never block on a pending response — it dispatches inbound requests to
/// <see cref="OnRequest"/> on a separate task and keeps reading.</para>
/// </remarks>
public sealed class JsonRpcConnection : IDisposable
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonNode?>> _pending = [];
    private readonly object _pendingLock = new();
    private long _nextId;
    private bool _disposed;

    /// <summary>Create a peer over an already-open pair of streams.</summary>
    public JsonRpcConnection(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Handles a request the remote peer made of us. Return the <c>result</c> payload, or throw
    /// <see cref="JsonRpcException"/> to answer with an error.
    /// </summary>
    public Func<string, JsonNode?, CancellationToken, Task<JsonNode?>>? OnRequest { get; set; }

    /// <summary>Handles a notification from the remote peer (no reply). Must not throw.</summary>
    public Action<string, JsonNode?>? OnNotification { get; set; }

    /// <summary>Raised when the read loop ends — the agent exited or closed its stdout.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>Send a request and await its response.</summary>
    public async Task<JsonNode?> InvokeAsync(string method, JsonNode? parameters, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[id] = tcs;
        }

        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            envelope["params"] = parameters;
        }

        await WriteAsync(envelope, ct).ConfigureAwait(false);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }
        }
    }

    /// <summary>Send a notification (fire-and-forget; no id, no response).</summary>
    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken ct = default)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (parameters is not null)
        {
            envelope["params"] = parameters;
        }

        return WriteAsync(envelope, ct);
    }

    /// <summary>
    /// Pump the input stream until it closes or <paramref name="ct"/> fires. Every pending request
    /// is failed on exit so no caller is left awaiting a response that can never arrive.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        Exception? fault = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _input.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;   // stdout closed: the agent exited
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    // Agents sometimes print a banner or a warning to stdout before framing
                    // starts. Skipping unparseable lines is what keeps that from killing the
                    // session.
                    continue;
                }

                if (message is JsonObject obj)
                {
                    Dispatch(obj, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception e)
        {
            fault = e;
        }
        finally
        {
            FailAllPending(fault);
            Closed?.Invoke(fault);
        }
    }

    private void Dispatch(JsonObject obj, CancellationToken ct)
    {
        var hasId = obj.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        var method = obj["method"]?.GetValue<string>();

        if (method is null && hasId)
        {
            CompleteResponse(obj, idNode!);
            return;
        }

        if (method is null)
        {
            return;   // neither a call nor a reply
        }

        var parameters = obj["params"];

        if (!hasId)
        {
            try
            {
                OnNotification?.Invoke(method, parameters);
            }
            catch
            {
                // A notification handler must never take down the read loop.
            }

            return;
        }

        // An inbound request. Answer it on its own task: the handler may itself await, and the
        // read loop has to stay free to receive the next frame.
        var id = idNode!.DeepClone();
        _ = Task.Run(async () =>
        {
            JsonObject reply = new() { ["jsonrpc"] = "2.0", ["id"] = id };
            try
            {
                var handler = OnRequest;
                if (handler is null)
                {
                    reply["error"] = Error(-32601, $"No handler for '{method}'.");
                }
                else
                {
                    reply["result"] = await handler(method, parameters, ct).ConfigureAwait(false) ?? new JsonObject();
                }
            }
            catch (JsonRpcException e)
            {
                reply["error"] = Error(e.Code, e.Message);
            }
            catch (Exception e)
            {
                reply["error"] = Error(-32603, e.Message);
            }

            try
            {
                await WriteAsync(reply, ct).ConfigureAwait(false);
            }
            catch
            {
                // The peer is gone; the read loop will notice.
            }
        }, ct);
    }

    private void CompleteResponse(JsonObject obj, JsonNode idNode)
    {
        long id;
        try
        {
            id = idNode.GetValue<long>();
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            return;   // we only ever issue numeric ids, so this reply is not ours
        }

        TaskCompletionSource<JsonNode?>? tcs;
        lock (_pendingLock)
        {
            _pending.Remove(id, out tcs);
        }

        if (tcs is null)
        {
            return;
        }

        if (obj["error"] is JsonObject err)
        {
            var code = err["code"]?.GetValue<int>() ?? -32603;
            var msg = err["message"]?.GetValue<string>() ?? "unknown error";
            tcs.TrySetException(new JsonRpcException(code, msg));
        }
        else
        {
            tcs.TrySetResult(obj["result"]);
        }
    }

    private async Task WriteAsync(JsonObject envelope, CancellationToken ct)
    {
        // One JSON value per line; a serialized writer because responses to inbound requests are
        // produced off the read loop and would otherwise interleave mid-frame.
        var text = envelope.ToJsonString();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
            await _output.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FailAllPending(Exception? fault)
    {
        List<TaskCompletionSource<JsonNode?>> waiters;
        lock (_pendingLock)
        {
            waiters = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var w in waiters)
        {
            w.TrySetException(fault ?? new IOException("The agent connection closed."));
        }
    }

    private static JsonObject Error(int code, string message) =>
        new() { ["code"] = code, ["message"] = message };

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FailAllPending(null);
        _writeLock.Dispose();
    }
}

/// <summary>A JSON-RPC error, in either direction.</summary>
public sealed class JsonRpcException : Exception
{
    /// <summary>Create an error with a JSON-RPC code.</summary>
    public JsonRpcException(int code, string message)
        : base(message) => Code = code;

    /// <summary>The JSON-RPC error code.</summary>
    public int Code { get; }
}
