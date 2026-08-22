using System.Text.Json.Nodes;
using PsAgent.Cmdlets.Acp;
using Xunit;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// The JSON-RPC transport, driven over in-memory streams — no subprocess and no ACP agent
/// installed. That is the whole reason the connection takes a TextReader/TextWriter pair.
/// </summary>
public sealed class JsonRpcConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_request_is_framed_as_one_json_line_and_its_response_resolves()
    {
        var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"sessionId\":\"s1\"}}\n");
        var output = new StringWriter();
        using var conn = new JsonRpcConnection(input, output);

        // Started before the pump so the pending id is registered when the reply is read.
        var call = conn.InvokeAsync("session/new", new JsonObject { ["cwd"] = "/tmp" });
        _ = conn.RunAsync();

        var result = await call.WaitAsync(Timeout);

        Assert.Equal("s1", result?["sessionId"]?.GetValue<string>());

        var written = output.ToString();
        Assert.EndsWith("\n", written, StringComparison.Ordinal);
        Assert.Single(written.TrimEnd('\n').Split('\n'));

        var sent = JsonNode.Parse(written)!;
        Assert.Equal("2.0", sent["jsonrpc"]!.GetValue<string>());
        Assert.Equal("session/new", sent["method"]!.GetValue<string>());
        Assert.Equal(1, sent["id"]!.GetValue<long>());
        Assert.Equal("/tmp", sent["params"]!["cwd"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_error_response_surfaces_as_a_JsonRpcException()
    {
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32601,\"message\":\"no such method\"}}\n");
        using var conn = new JsonRpcConnection(input, new StringWriter());

        var call = conn.InvokeAsync("nope", null);
        _ = conn.RunAsync();

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() => call.WaitAsync(Timeout));
        Assert.Equal(-32601, ex.Code);
        Assert.Equal("no such method", ex.Message);
    }

    [Fact]
    public async Task An_inbound_request_is_answered_with_its_id()
    {
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"fs/read_text_file\",\"params\":{\"path\":\"a.txt\"}}\n");
        var output = new StringWriter();
        using var conn = new JsonRpcConnection(input, output)
        {
            OnRequest = (method, p, _) => Task.FromResult<JsonNode?>(
                new JsonObject { ["content"] = $"{method}:{p?["path"]?.GetValue<string>()}" }),
        };

        await conn.RunAsync().WaitAsync(Timeout);
        await WaitForOutput(output);

        var reply = JsonNode.Parse(output.ToString())!;
        Assert.Equal(7, reply["id"]!.GetValue<long>());
        Assert.Equal("fs/read_text_file:a.txt", reply["result"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_handler_that_throws_JsonRpcException_answers_with_that_error_code()
    {
        var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"fs/read_text_file\"}\n");
        var output = new StringWriter();
        using var conn = new JsonRpcConnection(input, output)
        {
            OnRequest = (_, _, _) => throw new JsonRpcException(-32602, "refused"),
        };

        await conn.RunAsync().WaitAsync(Timeout);
        await WaitForOutput(output);

        var reply = JsonNode.Parse(output.ToString())!;
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        Assert.Equal("refused", reply["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_inbound_request_with_no_handler_answers_method_not_found()
    {
        var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"terminal/create\"}\n");
        var output = new StringWriter();
        using var conn = new JsonRpcConnection(input, output);

        await conn.RunAsync().WaitAsync(Timeout);
        await WaitForOutput(output);

        Assert.Equal(-32601, JsonNode.Parse(output.ToString())!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_notification_reaches_the_handler_and_is_not_answered()
    {
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\"}}\n");
        var output = new StringWriter();
        string? seen = null;
        using var conn = new JsonRpcConnection(input, output)
        {
            OnNotification = (method, p) => seen = $"{method}:{p?["sessionId"]?.GetValue<string>()}",
        };

        await conn.RunAsync().WaitAsync(Timeout);

        Assert.Equal("session/update:s1", seen);
        Assert.Equal(string.Empty, output.ToString());
    }

    /// <summary>
    /// Agents print banners and warnings to stdout before framing settles. Killing the session on
    /// the first unparseable line would make those agents unusable.
    /// </summary>
    [Fact]
    public async Task Unparseable_lines_are_skipped_rather_than_fatal()
    {
        var input = new StringReader(
            "npm warn exec\n\n{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{}}\n");
        var count = 0;
        using var conn = new JsonRpcConnection(input, new StringWriter())
        {
            OnNotification = (_, _) => count++,
        };

        await conn.RunAsync().WaitAsync(Timeout);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task A_notification_handler_that_throws_does_not_stop_the_pump()
    {
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"method\":\"a\"}\n{\"jsonrpc\":\"2.0\",\"method\":\"b\"}\n");
        var seen = new List<string>();
        using var conn = new JsonRpcConnection(input, new StringWriter())
        {
            OnNotification = (m, _) =>
            {
                seen.Add(m);
                if (m == "a")
                {
                    throw new InvalidOperationException("handler bug");
                }
            },
        };

        await conn.RunAsync().WaitAsync(Timeout);

        Assert.Equal(["a", "b"], seen);
    }

    /// <summary>A caller must never be left awaiting a response the dead agent can no longer send.</summary>
    [Fact]
    public async Task Pending_requests_fail_when_the_stream_closes()
    {
        using var conn = new JsonRpcConnection(new StringReader(string.Empty), new StringWriter());

        var call = conn.InvokeAsync("initialize", null);
        await conn.RunAsync().WaitAsync(Timeout);

        await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(Timeout));
    }

    [Fact]
    public async Task A_notification_carries_no_id()
    {
        var output = new StringWriter();
        using var conn = new JsonRpcConnection(new StringReader(string.Empty), output);

        await conn.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = "s1" });

        var sent = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.False(sent.ContainsKey("id"));
        Assert.Equal("session/cancel", sent["method"]!.GetValue<string>());
    }

    /// <summary>Inbound requests are answered on their own task, so the reply may land after RunAsync returns.</summary>
    private static async Task WaitForOutput(StringWriter output)
    {
        for (var i = 0; i < 200 && output.ToString().Length == 0; i++)
        {
            await Task.Delay(10);
        }
    }
}
