using System.Text.Json;
using System.Text.Json.Nodes;
using PsAgent.Cmdlets;
using PsAgent.Cmdlets.Agent;
using PsAgent.Cmdlets.Agent.Models;
using Xunit;

namespace PsAgent.Cmdlets.Tests;

/// <summary>
/// The OpenAI-compatible transport, driven from captured wire shapes. Every method that decides
/// anything is pure, so the format handling is covered without a server or a key.
/// </summary>
public sealed class OpenAiConversationTests
{
    private static OpenAiConversation New() => new(
        new System.Net.Http.HttpClient(),
        "https://api.example.com/v1",
        "gpt-test",
        "you are a coding agent",
        AgentTools.Catalog,
        4096);

    // ---------------------------------------------------------------- request shape

    [Fact]
    public void The_request_carries_the_system_prompt_first()
    {
        var request = New().BuildRequest();

        var messages = request["messages"]!.AsArray();
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("you are a coding agent", messages[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Tools_are_advertised_in_the_function_shape()
    {
        var tools = OpenAiConversation.ToolsPayload(AgentTools.Catalog);

        Assert.Equal(AgentTools.Catalog.Count, tools.Count);

        var first = tools[0]!.AsObject();
        Assert.Equal("function", first["type"]!.GetValue<string>());

        var fn = first["function"]!.AsObject();
        Assert.Equal("read_file", fn["name"]!.GetValue<string>());
        Assert.Equal("object", fn["parameters"]!["type"]!.GetValue<string>());
        Assert.NotNull(fn["parameters"]!["properties"]!["path"]);
        Assert.Contains("path", fn["parameters"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void A_tool_with_no_required_parameters_still_serialises()
    {
        var listFiles = OpenAiConversation.ToolsPayload([AgentTools.Find("list_files")!])[0]!;

        Assert.Empty(listFiles["function"]!["parameters"]!["required"]!.AsArray());
    }

    [Fact]
    public void User_turns_append_in_order()
    {
        var c = New();
        c.AddUser("first");
        c.AddUser("second");

        var messages = c.BuildRequest()["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("first", messages[1]!["content"]!.GetValue<string>());
        Assert.Equal("second", messages[2]!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// A tool result is a `role: tool` message keyed by tool_call_id. Send it any other way and the
    /// API rejects the whole conversation rather than the offending message.
    /// </summary>
    [Fact]
    public void Tool_results_are_tool_role_messages_keyed_by_call_id()
    {
        var c = New();
        c.AddToolResults([new ModelToolResult("call_1", "read_file", "file body", IsError: false)]);

        var last = c.BuildRequest()["messages"]!.AsArray()[^1]!.AsObject();
        Assert.Equal("tool", last["role"]!.GetValue<string>());
        Assert.Equal("call_1", last["tool_call_id"]!.GetValue<string>());
        Assert.Equal("read_file", last["name"]!.GetValue<string>());
        Assert.Equal("file body", last["content"]!.GetValue<string>());
    }

    /// <summary>This shape has no is_error flag, so a failure has to read as one in the text.</summary>
    [Fact]
    public void A_failed_tool_result_is_marked_in_its_text()
    {
        var c = New();
        c.AddToolResults([new ModelToolResult("call_1", "run_bash", "no such file", IsError: true)]);

        var content = c.BuildRequest()["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
        Assert.StartsWith("ERROR:", content, StringComparison.Ordinal);
        Assert.Contains("no such file", content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_endpoint_is_the_base_url_plus_chat_completions()
    {
        Assert.Contains("https://api.example.com/v1/chat/completions", New().Describe, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trailing_slash_on_the_base_url_does_not_double_up()
    {
        var c = new OpenAiConversation(
            new System.Net.Http.HttpClient(), "https://api.example.com/v1/", "m", "s", [], 10);

        Assert.DoesNotContain("//chat", c.Describe, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- response parsing

    private const string PlainReply = """
        {
          "choices": [{ "message": { "role": "assistant", "content": "Hello there" }, "finish_reason": "stop" }],
          "usage": { "prompt_tokens": 12, "completion_tokens": 3 }
        }
        """;

    private const string ToolCallReply = """
        {
          "choices": [{
            "message": {
              "role": "assistant",
              "content": null,
              "tool_calls": [{
                "id": "call_abc",
                "type": "function",
                "function": { "name": "read_file", "arguments": "{\"path\":\"calc.py\"}" }
              }]
            },
            "finish_reason": "tool_calls"
          }],
          "usage": { "prompt_tokens": 40, "completion_tokens": 9 }
        }
        """;

    [Fact]
    public void A_plain_reply_yields_text_and_usage()
    {
        var (reply, _) = OpenAiConversation.ParseResponse(PlainReply);

        Assert.Equal("Hello there", Assert.Single(reply.Texts));
        Assert.Empty(reply.ToolCalls);
        Assert.False(reply.Refused);
        Assert.Equal(12, reply.InputTokens);
        Assert.Equal(3, reply.OutputTokens);
    }

    /// <summary>function.arguments is a JSON *string* containing an object, in both directions.</summary>
    [Fact]
    public void A_tool_call_reply_parses_its_arguments_out_of_the_json_string()
    {
        var (reply, _) = OpenAiConversation.ParseResponse(ToolCallReply);

        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("call_abc", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("calc.py", call.Input["path"].GetString());
    }

    /// <summary>
    /// The assistant message is echoed verbatim: tool_calls must come back exactly as issued, or
    /// the follow-up tool messages have nothing to attach to.
    /// </summary>
    [Fact]
    public void The_assistant_echo_preserves_the_tool_calls()
    {
        var (_, assistant) = OpenAiConversation.ParseResponse(ToolCallReply);

        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        Assert.Equal("call_abc", assistant["tool_calls"]!.AsArray()[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void A_null_content_does_not_become_an_empty_text_row()
    {
        var (reply, _) = OpenAiConversation.ParseResponse(ToolCallReply);

        Assert.Empty(reply.Texts);
    }

    [Theory]
    [InlineData("reasoning_content")]
    [InlineData("reasoning")]
    public void Reasoning_is_read_from_whichever_field_the_vendor_uses(string field)
    {
        var body = $$"""
            { "choices": [{ "message": { "role": "assistant", "content": "answer", "{{field}}": "thinking out loud" } }] }
            """;

        var (reply, _) = OpenAiConversation.ParseResponse(body);

        Assert.Equal("thinking out loud", Assert.Single(reply.Thoughts));
    }

    [Fact]
    public void A_response_with_no_reasoning_field_is_fine()
    {
        Assert.Empty(OpenAiConversation.ParseResponse(PlainReply).Reply.Thoughts);
    }

    [Fact]
    public void A_content_filter_finish_reads_as_a_refusal()
    {
        var body = """
            { "choices": [{ "message": { "role": "assistant", "content": null }, "finish_reason": "content_filter" }] }
            """;

        Assert.True(OpenAiConversation.ParseResponse(body).Reply.Refused);
    }

    [Fact]
    public void An_error_body_is_raised_with_its_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OpenAiConversation.ParseResponse("""{ "error": { "message": "model not found" } }"""));

        Assert.Contains("model not found", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "choices": [] }""")]
    [InlineData("""{ "choices": [{ "finish_reason": "stop" }] }""")]
    public void A_malformed_completion_is_a_clear_error(string body)
    {
        Assert.Throws<InvalidOperationException>(() => OpenAiConversation.ParseResponse(body));
    }

    [Fact]
    public void Missing_usage_counts_as_zero_rather_than_throwing()
    {
        var body = """{ "choices": [{ "message": { "role": "assistant", "content": "hi" } }] }""";

        var (reply, _) = OpenAiConversation.ParseResponse(body);

        Assert.Equal(0, reply.InputTokens);
    }

    // ---------------------------------------------------------------- argument parsing

    [Theory]
    [InlineData("""{"a":1}""", 1)]
    [InlineData("{}", 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("not json", 0)]      // the model's mistake becomes a failed tool call, not a dead turn
    [InlineData("[1,2]", 0)]         // an array is not an argument object
    public void Tool_arguments_survive_the_shapes_models_actually_emit(string raw, int expectedCount)
    {
        var parsed = OpenAiConversation.ParseArguments(JsonValue.Create(raw));

        Assert.Equal(expectedCount, parsed.Count);
    }

    [Fact]
    public void Arguments_given_as_an_object_rather_than_a_string_still_parse()
    {
        var parsed = OpenAiConversation.ParseArguments(JsonNode.Parse("""{"path":"a.txt"}"""));

        Assert.Equal("a.txt", parsed["path"].GetString());
    }

    [Fact]
    public void Null_arguments_parse_to_nothing()
    {
        Assert.Empty(OpenAiConversation.ParseArguments(null));
    }

    // ---------------------------------------------------------------- transport choice

    /// <summary>
    /// Anthropic restricts its OAuth to Claude Code, so a bearer token obtained by any other client
    /// belongs to a service speaking the OpenAI shape. `auto` therefore follows the credential.
    /// </summary>
    [Fact]
    public void Auto_picks_openai_for_a_browser_sign_in()
    {
        var credential = new AgentCredential(AgentCredentialSource.OAuth, "signed in") { AuthToken = "t" };

        Assert.Equal("openai", InvokeAgentCommand.ChooseApi("auto", credential));
    }

    [Fact]
    public void Auto_picks_anthropic_for_a_key()
    {
        var credential = new AgentCredential(AgentCredentialSource.DotEnv, "key", "sk-x");

        Assert.Equal("anthropic", InvokeAgentCommand.ChooseApi("auto", credential));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("OpenAI")]
    public void An_explicit_choice_is_honoured(string requested)
    {
        var credential = new AgentCredential(AgentCredentialSource.OAuth, "signed in") { AuthToken = "t" };

        Assert.Equal(requested.ToLowerInvariant(), InvokeAgentCommand.ChooseApi(requested, credential));
    }
}
