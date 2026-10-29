using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Stockhog.Application.Assistant;
using Stockhog.Infrastructure.Anthropic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Stockhog.Infrastructure.Tests.Assistant;

/// <summary>
/// The conversation loop against a stub of the Messages API that streams prepared answers: no network, no key.
/// </summary>
public class ClaudeConversationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tool_call_is_executed_and_the_answer_continues_with_its_result()
    {
        var api = new StubMessagesApi(ToolUseResponse("toolu_1", "get_risk_limits", "{}"), TextResponse("Limits are fine."));
        var toolbox = new RecordingToolbox();
        var conversation = CreateConversation(api, toolbox);

        var updates = await CollectAsync(conversation.SendAsync("Can I buy?", Ct));

        Assert.Contains(new AssistantToolUse("get_risk_limits"), updates);
        Assert.Equal("Limits are fine.", string.Concat(updates.OfType<AssistantTextDelta>().Select(d => d.Text)));
        Assert.IsType<AssistantCompleted>(updates[^1]);
        Assert.Equal(["get_risk_limits"], toolbox.Calls);

        // The second request carries the first answer unchanged (thinking with its signature, the tool call)
        // and the tool's result, as the API requires.
        var second = api.Requests[1];
        var messages = second["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        var echoed = messages[1]!["content"]!.AsArray();
        Assert.Equal(("thinking", "sig-1"), (echoed[0]!["type"]!.GetValue<string>(), echoed[0]!["signature"]!.GetValue<string>()));
        Assert.Equal(("tool_use", "toolu_1"), (echoed[1]!["type"]!.GetValue<string>(), echoed[1]!["id"]!.GetValue<string>()));
        var result = messages[2]!["content"]!.AsArray()[0]!;
        Assert.Equal(("tool_result", "toolu_1"), (result["type"]!.GetValue<string>(), result["tool_use_id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Request_uses_the_configured_model_streaming_caching_and_fallback()
    {
        var api = new StubMessagesApi(TextResponse("Hi."));
        await CollectAsync(CreateConversation(api, new RecordingToolbox()).SendAsync("Hello", Ct));

        var request = api.Requests[0];
        Assert.Equal("claude-opus-5-5", request["model"]!.GetValue<string>());
        Assert.True(request["stream"]!.GetValue<bool>());
        Assert.Equal("medium", request["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("ephemeral", request["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("default", request["fallbacks"]!.GetValue<string>());
        Assert.Contains("server-side-fallback-2026-07-01", api.BetaHeaders[0]);
        var tool = request["tools"]!.AsArray().First(t => t!["name"]!.GetValue<string>() == "propose_order_ticket")!;
        Assert.True(tool["eager_input_streaming"]!.GetValue<bool>());
        Assert.Contains("symbol", tool["input_schema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public async Task Failed_question_is_removed_so_the_conversation_can_continue()
    {
        var api = new StubMessagesApi(HttpStatusCode.BadRequest, TextResponse("Second answer."));
        var conversation = CreateConversation(api, new RecordingToolbox());

        await Assert.ThrowsAsync<AssistantException>(async () => await CollectAsync(conversation.SendAsync("First", Ct)));
        await CollectAsync(conversation.SendAsync("Second", Ct));

        var messages = api.Requests[1]["messages"]!.AsArray();
        Assert.Equal("Second", Assert.Single(messages)!["content"]!.GetValue<string>());
    }

    private static ClaudeConversation CreateConversation(StubMessagesApi api, IAssistantToolbox toolbox)
    {
        var services = new ServiceCollection().AddSingleton(toolbox).BuildServiceProvider();
        return new ClaudeConversation(
            new FixedCredentials(),
            new AnthropicOptions(),
            services.GetRequiredService<IServiceScopeFactory>(),
            key => new AnthropicClient { ApiKey = key, HttpClient = new HttpClient(api), BaseUrl = "https://stub.example", MaxRetries = 0 },
            NullLogger<ClaudeConversation>.Instance);
    }

    private static async Task<List<AssistantUpdate>> CollectAsync(IAsyncEnumerable<AssistantUpdate> updates)
    {
        var list = new List<AssistantUpdate>();
        await foreach (var update in updates)
            list.Add(update);
        return list;
    }

    private static string ToolUseResponse(string id, string name, string input) => Sse(
        """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-1"}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"TOOL_ID","name":"TOOL_NAME","input":{}}}""".Replace("TOOL_ID", id).Replace("TOOL_NAME", name),
        $$$"""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":{{{JsonSerializer.Serialize(input)}}}}}""",
        """{"type":"content_block_stop","index":1}""",
        """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":20}}""",
        """{"type":"message_stop"}""");

    private static string TextResponse(string text) => Sse(
        """{"type":"message_start","message":{"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":30,"output_tokens":1}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
        $$$"""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":{{{JsonSerializer.Serialize(text[..(text.Length / 2)])}}}}}""",
        $$$"""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":{{{JsonSerializer.Serialize(text[(text.Length / 2)..])}}}}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":8}}""",
        """{"type":"message_stop"}""");

    private static string Sse(params string[] events) =>
        string.Concat(events.Select(e => $"event: {JsonNode.Parse(e)!["type"]!.GetValue<string>()}\ndata: {e}\n\n"));

    /// <summary>Answers each request with the next prepared stream (or status code) and records the request bodies.</summary>
    private sealed class StubMessagesApi(params object[] responses) : HttpMessageHandler
    {
        public List<JsonNode> Requests { get; } = [];

        public List<string> BetaHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            BetaHeaders.Add(request.Headers.TryGetValues("anthropic-beta", out var betas) ? string.Join(",", betas) : string.Empty);

            return responses[Requests.Count - 1] switch
            {
                HttpStatusCode status => new HttpResponseMessage(status)
                {
                    Content = new StringContent("""{"type":"error","error":{"type":"invalid_request_error","message":"bad request"}}""", Encoding.UTF8, "application/json")
                },
                string stream => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") },
                var other => throw new InvalidOperationException($"Unexpected response {other}")
            };
        }
    }

    private sealed class RecordingToolbox : IAssistantToolbox
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<AssistantTool> Tools { get; } =
        [
            new("get_risk_limits", "Risk limits.", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray() }),
            new("propose_order_ticket", "Fills in the ticket.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["symbol"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("symbol")
            })
        ];

        public Task<AssistantToolResult> ExecuteAsync(string toolName, JsonElement input, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(toolName);
            return Task.FromResult(new AssistantToolResult("""{"tradingEnabled":true}"""));
        }
    }

    private sealed class FixedCredentials : IAssistantCredentials
    {
        public bool HasApiKey => true;

        public string? GetApiKey() => "sk-ant-test";

        public void SetApiKey(string? apiKey)
        {
        }
    }
}
