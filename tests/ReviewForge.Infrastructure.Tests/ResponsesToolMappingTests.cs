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
public sealed class ResponsesToolMappingTests
{
    [Fact]
    public async Task Hosted_tools_map_to_responses_tool_shapes_and_namespace_deferred_tools()
    {
        using var environment = new EnvironmentScope();
        using var handler = new RequestHandler(CompletedResponse);
        using var factory = new ChatClientFactory(new ChatProviderOptions
        {
            Provider = ChatProviderKind.OpenAi,
            Model = "gpt-test",
        }, handler);

        var search = new HostedToolSearchTool
        {
            DeferredTools = ["lookup", "docs", "not-registered"],
            Namespace = "research",
            NamespaceDescription = "Research tools",
        };
        var lookup = AIFunctionFactory.Create((string query) => query, "lookup", "Find information");
        var docs = new HostedMcpServerTool("docs", new Uri("https://example.test/mcp"))
        {
            ServerDescription = "Document search",
            Headers = new Dictionary<string, string> { ["X-Api"] = "public" },
            AllowedTools = ["search", "fetch"],
            ApprovalMode = HostedMcpServerToolApprovalMode.RequireSpecific(["delete"], ["search"]),
        };
        var connector = new HostedMcpServerTool("connector", "connector-id")
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "  bearer   token-value" },
            ApprovalMode = HostedMcpServerToolApprovalMode.NeverRequire,
        };
        var fileSearch = new HostedFileSearchTool
        {
            Inputs = [new HostedVectorStoreContent("vs-1"), new HostedFileContent("file-1")],
            MaximumResultCount = 7,
        };
        var code = new HostedCodeInterpreterTool
        {
            Inputs = [new HostedFileContent("code-file")],
        };
        var image = new HostedImageGenerationTool
        {
            Options = new ImageGenerationOptions { ModelId = "image-model", MediaType = "image/jpeg", StreamingCount = 2 },
        };
        var noDeferred = AIFunctionFactory.Create((string id) => id, "immediate", "Immediate function");
        var unrecognized = new UnsupportedTestTool();

        await factory.Create(ChatTier.Full).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "search")],
            new ChatOptions
            {
                Tools = [search, lookup, docs, connector, fileSearch, code, image, noDeferred, unrecognized],
                AllowMultipleToolCalls = false,
            });

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var tools = request.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Contains(tools, tool => tool.GetProperty("type").GetString() == "tool_search");
        var nameSpace = Assert.Single(tools, tool => tool.GetProperty("type").GetString() == "namespace");
        Assert.Equal("research", nameSpace.GetProperty("name").GetString());
        Assert.Equal("Research tools", nameSpace.GetProperty("description").GetString());
        var nested = nameSpace.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Contains(nested, tool => tool.GetProperty("type").GetString() == "function" && tool.GetProperty("name").GetString() == "lookup" && tool.GetProperty("defer_loading").GetBoolean());
        Assert.DoesNotContain(nested, tool => tool.TryGetProperty("name", out var name) && name.GetString() == "not-registered");
        Assert.Contains(nested, tool => tool.GetProperty("type").GetString() == "mcp" && tool.GetProperty("server_label").GetString() == "docs");
        Assert.Contains(tools, tool => tool.GetProperty("type").GetString() == "mcp" && tool.GetProperty("server_label").GetString() == "connector");
        Assert.Contains(tools, tool => tool.GetProperty("type").GetString() == "file_search");
        Assert.Contains(tools, tool => tool.GetProperty("type").GetString() == "code_interpreter");
        Assert.Contains(tools, tool => tool.GetProperty("type").GetString() == "image_generation");
        Assert.Contains(tools, tool => tool.GetProperty("type").GetString() == "function" && tool.GetProperty("name").GetString() == "immediate");
        Assert.DoesNotContain(tools, tool => tool.TryGetProperty("name", out var name) && name.GetString() == "unsupported");
        Assert.False(request.RootElement.GetProperty("parallel_tool_calls").GetBoolean());

    }
    [Fact]
    public void Tool_search_lookup_tracks_deferred_namespaces_and_response_tool_services()
    {
        var function = AIFunctionFactory.Create((string query) => query, "lookup", "Find records");
        var mcp = new HostedMcpServerTool("docs", "connector");
        var firstSearch = new HostedToolSearchTool { DeferredTools = ["lookup"], Namespace = "research" };
        var secondSearch = new HostedToolSearchTool { DeferredTools = ["docs"], Namespace = "research", NamespaceDescription = "Deferred tools" };
        var thirdSearch = new HostedToolSearchTool { DeferredTools = ["docs"], Namespace = "research", NamespaceDescription = "Ignore later description" };

        var lookup = ResponsesChatClientAdapter.ToolSearchLookup.Create([firstSearch, secondSearch, thirdSearch, function, mcp]);
        Assert.True(lookup.IsDeferred("lookup"));
        Assert.True(lookup.IsDeferred("docs"));
        Assert.False(lookup.IsDeferred("unknown"));
        Assert.Equal("research", lookup.GetNamespace("lookup")!.Name);
        Assert.Same(lookup.GetNamespace("lookup"), lookup.GetNamespace("docs"));
        Assert.Equal("Deferred tools", lookup.GetNamespace("docs")!.Description);
        Assert.Null(lookup.GetNamespace("unknown"));

        var empty = ResponsesChatClientAdapter.ToolSearchLookup.Create([new HostedWebSearchTool()]);
        Assert.False(empty.IsDeferred("lookup"));
        Assert.Null(empty.GetNamespace("lookup"));

        var noTools = ResponsesChatClientAdapter.ToolSearchLookup.Create(null);
        Assert.False(noTools.IsDeferred("lookup"));
        var deferAll = ResponsesChatClientAdapter.ToolSearchLookup.Create(
            [new HostedToolSearchTool { Namespace = "all-tools", NamespaceDescription = "All deferred" }, function, mcp]);
        Assert.True(deferAll.IsDeferred("lookup"));
        Assert.True(deferAll.IsDeferred("docs"));
        Assert.Equal("all-tools", deferAll.GetNamespace("lookup")!.Name);
        Assert.Same(deferAll.GetNamespace("lookup"), deferAll.GetNamespace("docs"));
        Assert.Equal("All deferred", deferAll.GetNamespace("docs")!.Description);

        var responseTool = new WebSearchTool();
        var wrapper = new ResponsesChatClientAdapter.ResponseToolAITool(responseTool);
        Assert.Equal(nameof(WebSearchTool), wrapper.Name);
        Assert.Same(responseTool, wrapper.GetService(typeof(WebSearchTool)));
        Assert.Null(wrapper.GetService(typeof(WebSearchTool), "different"));
        Assert.Throws<ArgumentNullException>(() => wrapper.GetService(null!));
    }


    [Fact]
    public async Task Tool_mode_none_and_auto_are_mapped_to_responses_choices()
    {
        using var environment = new EnvironmentScope();
        var function = AIFunctionFactory.Create((string query) => query, "lookup", "Find records");

        foreach (var (mode, expectedChoice) in new (ChatToolMode Mode, string ExpectedChoice)[]
        {
            (ChatToolMode.None, "none"),
            (ChatToolMode.Auto, "auto"),
        })
        {
            using var handler = new RequestHandler(CompletedResponse);
            using var factory = new ChatClientFactory(new ChatProviderOptions { Provider = ChatProviderKind.OpenAi, Model = "gpt-test" }, handler);
            await factory.Create(ChatTier.Full).GetResponseAsync(
                [new ChatMessage(ChatRole.User, "find")],
                new ChatOptions { Tools = [function], ToolMode = mode });

            using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
            Assert.Equal(expectedChoice, request.RootElement.GetProperty("tool_choice").GetString());
        }
    }

    [Fact]
    public void Response_usage_mapping_preserves_cache_and_reasoning_subcounts()
    {
        Assert.Null(ResponsesChatClientAdapter.ToResponseTokenUsage(null));

        var usage = new UsageDetails
        {
            InputTokenCount = 12,
            OutputTokenCount = 8,
            TotalTokenCount = 20,
            AdditionalCounts = new()
            {
                ["InputTokenDetails.CachedTokenCount"] = 3,
                ["OutputTokenDetails.ReasoningTokenCount"] = 5,
            },
        };

        var mapped = ResponsesChatClientAdapter.ToResponseTokenUsage(usage)!;

        Assert.Equal(12, mapped.InputTokenCount);
        Assert.Equal(8, mapped.OutputTokenCount);
        Assert.Equal(20, mapped.TotalTokenCount);
        Assert.Equal(3, mapped.InputTokenDetails.CachedTokenCount);
        Assert.Equal(5, mapped.OutputTokenDetails.ReasoningTokenCount);
    }

    private const string CompletedResponse = """
        {"id":"resp-tools","object":"response","created_at":0,"status":"completed","model":"gpt-test","output":[{"id":"msg-tools","type":"message","role":"assistant","content":[{"type":"output_text","text":"ok","annotations":[]}]}]}
        """;

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        public EnvironmentScope() => Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        public void Dispose() => Environment.SetEnvironmentVariable("OPENAI_API_KEY", _previous);
    }

    private sealed class RequestHandler(string response) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class UnsupportedTestTool : AITool
    {
        public override string Name => "unsupported";
    }
}
