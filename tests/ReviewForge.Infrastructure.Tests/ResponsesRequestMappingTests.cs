using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Chat;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class ResponsesRequestMappingTests
{
    [Fact]
    public async Task History_and_multimodal_content_are_encoded_as_responses_input_items()
    {
        var transport = new QueueingResponsesHandler(CompletedResponse("visible"));
        var response = await SendAsync(
            transport,
            new[]
            {
                new ChatMessage(ChatRole.System, "system rule"),
                new ChatMessage(ChatRole.User, new List<AIContent>
                {
                    new TextContent("look at this"),
                    new UriContent("https://example.test/image.png", "image/png"),
                }),
            },
            new ChatOptions {ModelId = "gpt-test"});

        Assert.Equal("visible", response.Text);
        using var document = JsonDocument.Parse(Assert.Single(transport.RequestBodies));
        var input = document.RootElement.GetProperty("input");
        Assert.Equal(JsonValueKind.Array, input.ValueKind);
        Assert.Equal("system", input[0].GetProperty("role").GetString());
        Assert.Equal("system rule", input[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("user", input[1].GetProperty("role").GetString());
        var content = input[1].GetProperty("content");
        Assert.Contains(content.EnumerateArray(), item => item.GetProperty("type").GetString() == "input_text");
        Assert.Contains(content.EnumerateArray(), item => item.GetProperty("type").GetString() == "input_image");
    }

    [Fact]
    public async Task Conversation_id_is_mapped_as_conversation_or_previous_response_id()
    {
        var conversationTransport = new QueueingResponsesHandler(CompletedResponse("conversation"));
        await SendAsync(
            conversationTransport,
            [new ChatMessage(ChatRole.User, "continue")],
            new ChatOptions {ConversationId = "conv_session"});

        using var conversationRequest = JsonDocument.Parse(Assert.Single(conversationTransport.RequestBodies));
        Assert.Equal("conv_session", conversationRequest.RootElement.GetProperty("conversation").GetProperty("id").GetString());

        var responseTransport = new QueueingResponsesHandler(CompletedResponse("response"));
        await SendAsync(
            responseTransport,
            [new ChatMessage(ChatRole.User, "continue")],
            new ChatOptions {ConversationId = "resp_previous"});

        using var responseRequest = JsonDocument.Parse(Assert.Single(responseTransport.RequestBodies));
        Assert.Equal("resp_previous", responseRequest.RootElement.GetProperty("previous_response_id").GetString());
    }

    [Fact]
    public async Task Tools_json_schema_and_background_options_are_visible_on_wire()
    {
        var transport = new QueueingResponsesHandler(CompletedResponse("done"));
        var tool = AIFunctionFactory.Create(
            (string query, int limit) => $"{query}:{limit}",
            "search_records",
            "Search records by query.");
        var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"]}").RootElement;

        await SendAsync(
            transport,
            new[] {new ChatMessage(ChatRole.User, "find records")},
            new ChatOptions
            {
                ModelId = "gpt-test",
                Tools = new List<AITool> {tool},
                ToolMode = ChatToolMode.RequireAny,
                ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "answer", "Structured answer"),
                AllowBackgroundResponses = true,
            });

        using var document = JsonDocument.Parse(Assert.Single(transport.RequestBodies));
        var root = document.RootElement;
        Assert.Equal("gpt-test", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("background").GetBoolean());
        var function = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", function.GetProperty("type").GetString());
        Assert.Equal("search_records", function.GetProperty("name").GetString());
        Assert.Equal("Search records by query.", function.GetProperty("description").GetString());
        Assert.Equal("object", function.GetProperty("parameters").GetProperty("type").GetString());
        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("answer", format.GetProperty("name").GetString());
        Assert.Equal("Structured answer", format.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Continuation_token_fetches_the_background_response_by_id()
    {
        var transport = new QueueingResponsesHandler(CompletedResponse("resumed", "resp_resume"));
        var client = CreateClient(transport);

        var response = await client.GetResponseAsync(
            Array.Empty<ChatMessage>(),
            new ChatOptions
            {
                ContinuationToken = new ResponsesClientContinuationToken("resp_resume"),
            });

        Assert.Equal("resp_resume", response.ResponseId);
        Assert.EndsWith("/responses/resp_resume", Assert.Single(transport.RequestUris).AbsolutePath);
        Assert.Equal(string.Empty, Assert.Single(transport.RequestBodies));
    }

    [Fact]
    public async Task Function_call_and_function_result_history_are_preserved_as_supported_items()
    {
        var transport = new QueueingResponsesHandler(CompletedResponse("ok"));
        await SendAsync(
            transport,
            new[]
            {
                new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> {{"id", 7}}),
                }),
                new ChatMessage(ChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent("call-1", new Dictionary<string, object?> {{"name", "Ada"}}),
                }),
            },
            new ChatOptions {ModelId = "gpt-test"});

        using var document = JsonDocument.Parse(Assert.Single(transport.RequestBodies));
        var items = document.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Contains(items, item => item.GetProperty("type").GetString() == "function_call" && item.GetProperty("call_id").GetString() == "call-1");
        Assert.Contains(items, item => item.GetProperty("type").GetString() == "function_call_output" && item.GetProperty("call_id").GetString() == "call-1");
    }

    private static async Task<ChatResponse> SendAsync(
        QueueingResponsesHandler transport,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null)
        => await CreateClient(transport).GetResponseAsync(messages, options);

    private static IChatClient CreateClient(QueueingResponsesHandler transport)
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            return new ChatClientFactory(
                new ChatProviderOptions {Provider = ChatProviderKind.OpenAi, Model = "gpt-test"}, transport).Create(ChatTier.Full);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    private static string CompletedResponse(string text, string id = "resp_test")
        => $"{{\"id\":\"{id}\",\"object\":\"response\",\"created_at\":0,\"status\":\"completed\",\"model\":\"gpt-test\",\"output\":[{{\"id\":\"msg_test\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{{\"type\":\"output_text\",\"text\":{JsonSerializer.Serialize(text)},\"annotations\":[]}}]}}],\"output_text\":{JsonSerializer.Serialize(text)}}}";

    private sealed class QueueingResponsesHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<string> RequestBodies { get; } = new();
        public List<Uri> RequestUris { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
