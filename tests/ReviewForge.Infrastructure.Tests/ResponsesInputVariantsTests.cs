using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Chat;
using OpenAI.Responses;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class ResponsesInputVariantsTests
{
    [Fact]
    public async Task Rich_user_and_tool_content_is_encoded_as_responses_items()
    {
        var handler = new InputVariantsHandler(CompletedResponse("accepted"));
        var mcpCall = new McpServerToolCallContent("mcp-call", "lookup", "remote")
        {
            Arguments = new Dictionary<string, object?> { ["key"] = "value" },
        };
        var mcpResult = new McpServerToolResultContent("mcp-call")
        {
            Outputs = new List<AIContent> { new TextContent("mcp output") },
        };
        var imageWithDetail = new UriContent("https://example.test/high.png", "image/png");
        (imageWithDetail.AdditionalProperties ??= new())["detail"] = "high";
        var errorMcpCall = new McpServerToolCallContent("mcp-error", "mutate", "remote")
        {
            Arguments = new Dictionary<string, object?>(),
        };
        var errorMcpResult = new McpServerToolResultContent("mcp-error")
        {
            Outputs = new List<AIContent> { new ErrorContent("{\"error\":\"tool failed\"}") },
        };
        var reasoningWithJsonId = new TextReasoningContent("serialized reasoning")
        {
            ProtectedData = "encrypted reasoning",
            AdditionalProperties = new() { ["reasoningItemId"] = JsonDocument.Parse("\"reason-json\"").RootElement.Clone() },
        };
        var unsupportedDetailImage = new UriContent("https://example.test/unknown-detail.png", "image/png");
        (unsupportedDetailImage.AdditionalProperties ??= new())["detail"] = 17;
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, new List<AIContent>
            {
                new TextContent("prompt"),
                new DataContent(Convert.FromBase64String("AQID"), "image/png"),
                new DataContent(Convert.FromBase64String("BAUG"), "application/pdf"),
                new UriContent("https://example.test/photo.jpg", "image/jpeg"),
                new UriContent("https://example.test/document.pdf", "application/pdf"),
                new HostedFileContent("file-hosted"),
                new HostedFileContent("hosted-image") { MediaType = "image/png" },
                new AIContent { RawRepresentation = ResponseContentPart.CreateInputTextPart("already mapped part") },
                imageWithDetail,
                new ErrorContent("refused") { ErrorCode = "Refusal" },
                unsupportedDetailImage,
                new AIContent { RawRepresentation = ResponseItem.CreateAssistantMessageItem("already mapped") },
                new ToolApprovalResponseContent("approval-user", true, mcpCall),
            }),
            new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("think"),
                new TextContent("assistant text"),
                new AIContent { RawRepresentation = ResponseItem.CreateAssistantMessageItem("raw assistant response") },
                reasoningWithJsonId,
                new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["id"] = 7 }),
                mcpCall,
                mcpResult,
                errorMcpCall,
                errorMcpResult,
                new ToolApprovalRequestContent("approval-assistant", mcpCall),
            }),
            new ChatMessage(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call-1", "plain"),
                new FunctionResultContent("call-2", new Dictionary<string, object?> { ["answer"] = 42 }),
                new FunctionResultContent("call-3", new TextContent("content")),
                new FunctionResultContent("call-4", new AIContent[]
                {
                    new TextContent("many"),
                    new DataContent(Convert.FromBase64String("AQI="), "image/png"),
                    new DataContent(Convert.FromBase64String("AwQ="), "application/pdf"),
                    new UriContent("https://example.test/result.png", "image/png"),
                    new UriContent("https://example.test/result.pdf", "application/pdf"),
                    new HostedFileContent("file-image") { MediaType = "image/png" },
                    new HostedFileContent("file-output"),
                }),
                new FunctionResultContent("call-5", typeof(string)),
                new ToolApprovalResponseContent("approval-tool", false, mcpCall),
                new AIContent { RawRepresentation = ResponseItem.CreateFunctionCallOutputItem("raw-tool", "raw result") },
            }),
            new ChatMessage(ChatRole.User, Array.Empty<AIContent>()),
        };

        var response = await CreateClient(handler).GetResponseAsync(messages,
            new ChatOptions { ModelId = "gpt-test", Instructions = "be concise", MaxOutputTokens = 64, Temperature = 0.2f, TopP = 0.8f });

        Assert.Equal("accepted", response.Text);
        using var json = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var items = json.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Contains(items, x => x.GetProperty("type").GetString() == "message" && x.GetProperty("role").GetString() == "user");
        Assert.Contains(items, x => x.GetProperty("type").GetString() == "function_call" && x.GetProperty("call_id").GetString() == "call-1");
        Assert.Contains(items, x => x.GetProperty("type").GetString() == "function_call_output" && x.GetProperty("call_id").GetString() == "call-1");
        var mcpItem = Assert.Single(items, x => x.GetProperty("type").GetString() == "mcp_call" && x.TryGetProperty("output", out var output) && output.GetString() == "mcp output");
        Assert.Equal("mcp output", mcpItem.GetProperty("output").GetString());
        Assert.Contains(items, x => x.GetProperty("type").GetString() == "reasoning");
        var root = json.RootElement;
        Assert.Equal("gpt-test", root.GetProperty("model").GetString());
        Assert.Equal("be concise", root.GetProperty("instructions").GetString());
        Assert.Equal(64, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, root.GetProperty("top_p").GetDouble());

        var functionOutputs = items.Where(x => x.GetProperty("type").GetString() == "function_call_output" && x.GetProperty("call_id").GetString() == "call-4").ToArray();
        Assert.Contains(functionOutputs, output => output.GetProperty("output").EnumerateArray().Any(part => part.GetProperty("type").GetString() == "input_image"));
        Assert.Contains(functionOutputs, output => output.GetProperty("output").EnumerateArray().Any(part => part.GetProperty("type").GetString() == "input_file"));
    }

    [Fact]
    public async Task Options_metadata_formats_and_function_tool_schema_are_sent()
    {
        var handler = new InputVariantsHandler(CompletedResponse("ok"));
        var function = AIFunctionFactory.Create((string query) => query, "lookup", "Find records");
        var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"]}").RootElement;
        await CreateClient(handler).GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "find") }, new ChatOptions
        {
            ModelId = "gpt-options",
            Tools = new List<AITool> { function },
            ToolMode = ChatToolMode.RequireAny,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "answer", "Structured"),
            AllowBackgroundResponses = true,
            AdditionalProperties = new() { ["strict"] = true },
        });

        using var json = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = json.RootElement;
        Assert.Equal("gpt-options", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("background").GetBoolean());
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.True(tool.GetProperty("strict").GetBoolean());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("lookup", tool.GetProperty("name").GetString());
        Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
    }

    private static IChatClient CreateClient(HttpMessageHandler handler)
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            return new ChatClientFactory(new ChatProviderOptions { Provider = ChatProviderKind.OpenAi, Model = "gpt-test" }, handler).Create(ChatTier.Full);
        }
        finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous); }
    }

    private static string CompletedResponse(string text) => $"{{\"id\":\"resp_variants\",\"object\":\"response\",\"created_at\":0,\"status\":\"completed\",\"model\":\"gpt-test\",\"output\":[{{\"id\":\"msg_variants\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{{\"type\":\"output_text\",\"text\":{JsonSerializer.Serialize(text)},\"annotations\":[]}}]}}],\"output_text\":{JsonSerializer.Serialize(text)}}}";

    private sealed class InputVariantsHandler(string response) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
