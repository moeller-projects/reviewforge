using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using ReviewForge.Core.Ports;
using OpenAI.Responses;
using ReviewForge.Infrastructure.Chat;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class ResponsesOutputVariantsTests
{
    [Fact]
    public async Task Non_streaming_response_maps_rich_tool_and_reasoning_output_items()
    {
        using var environment = new EnvironmentScope();
        using var handler = new FixtureHandler(RichResponseJson);
        using var factory = CreateFactory(handler);

        var response = await factory.Create(ChatTier.Full).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "inspect the report")]);
        var contents = response.Messages.SelectMany(message => message.Contents).ToArray();

        Assert.Contains(contents, content => content is TextContent text && text.Text == "Here is the report.");
        Assert.Contains(contents, content => content is ErrorContent error && error.Message == "No secrets.");
        var outputText = Assert.Single(contents.OfType<TextContent>(), text => text.Text == "Here is the report.");
        var citation = Assert.Single(outputText.Annotations!.OfType<CitationAnnotation>(), annotation => annotation.Url is not null);
        Assert.Equal(new Uri("https://example.test/report"), citation.Url);
        Assert.Equal("Report", citation.Title);
        Assert.Contains(contents, content => content is HostedFileContent file && file.FileId == "image-file");
        Assert.Contains(contents, content => content is DataContent data && data.MediaType == "image/png" && data.Data.Span.SequenceEqual(Convert.FromBase64String("AQID")));
        Assert.Contains(contents, content => content is UriContent uri && uri.Uri.ToString() == "https://example.test/from-response.png");
        Assert.Contains(contents, content => content is HostedFileContent file && file.FileId == "document-file" && file.Name == "report.pdf");
        Assert.Contains(contents, content => content is DataContent data && data.MediaType == "application/pdf" && data.Name == "raw.pdf");
        Assert.Contains(contents, content => content.RawRepresentation is ResponseContentPart { Kind: ResponseContentPartKind.InputFile });
        Assert.Contains(contents, content => content is TextReasoningContent reasoning && reasoning.Text == "I checked the report.");

        var function = Assert.Single(contents.OfType<FunctionCallContent>());
        Assert.Equal("call-42", function.CallId);
        Assert.Equal("lookup_report", function.Name);
        Assert.Equal("quarterly", function.Arguments!["query"]?.ToString());
        Assert.Contains(contents.OfType<FunctionResultContent>(), result => result.CallId == "call-42" && Equals(result.Result, "cached function result"));
        Assert.Contains(contents.OfType<ToolCallContent>(), call => call.CallId == "file-search-1");
        Assert.Contains(contents.OfType<ToolResultContent>(), result => result.CallId == "file-search-1");
        Assert.Contains(contents.OfType<ToolResultContent>(), result => result.CallId == "computer-call-1");
        Assert.Equal(2, response.Messages.Count);

        Assert.Equal("end-user-7", response.AdditionalProperties!["EndUserId"]);
        Assert.Equal("safety-7", response.AdditionalProperties["SafetyIdentifier"]);
        Assert.Equal("provider failure", Assert.Single(response.Messages[^1].Contents.OfType<ErrorContent>()).Message);
        var webSearch = Assert.Single(contents.OfType<WebSearchToolCallContent>());
        Assert.Equal("web-1", webSearch.CallId);
        Assert.Contains("reviewforge quarterly", webSearch.Queries!);
        var webResult = Assert.Single(contents.OfType<WebSearchToolResultContent>());
        Assert.Contains(webResult.Outputs!, output => output is UriContent uri && uri.Uri.ToString() == "https://example.test/source");

        var codeCall = Assert.Single(contents.OfType<CodeInterpreterToolCallContent>());
        Assert.Equal("code-1", codeCall.CallId);
        var codeResult = Assert.Single(contents.OfType<CodeInterpreterToolResultContent>());
        Assert.Contains(codeResult.Outputs!, output => output is TextContent text && text.Text.Contains("42", StringComparison.Ordinal));
        Assert.Contains(codeResult.Outputs!, output => output is HostedFileContent file && file.FileId == "file-output");

        var imageCall = Assert.Single(contents.OfType<ImageGenerationToolCallContent>());
        Assert.Equal("image-1", imageCall.CallId);
        var imageResult = Assert.Single(contents.OfType<ImageGenerationToolResultContent>());
        Assert.Contains(imageResult.Outputs!, output => output is DataContent data && data.MediaType == "image/png");

        var reasoning = Assert.Single(contents.OfType<TextReasoningContent>());
        Assert.Equal("protected-trace", reasoning.ProtectedData);
        Assert.Contains(reasoning.AdditionalProperties!.Values, value => Equals(value, "reasoning-1"));
    }

    [Fact]
    public async Task Non_streaming_response_maps_mcp_approval_and_computer_output_items()
    {
        using var environment = new EnvironmentScope();
        using var handler = new FixtureHandler(McpAndComputerResponseJson);
        using var factory = CreateFactory(handler);

        var response = await factory.Create(ChatTier.Full).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "run the approved action")]);
        var contents = response.Messages.SelectMany(message => message.Contents).ToArray();

        Assert.Contains(contents.OfType<McpServerToolCallContent>(), call => call.CallId == "mcp-call-1" && call.Name == "search_docs");
        var mcpCall = contents.OfType<McpServerToolCallContent>().First(call => call.CallId == "mcp-call-1" && call.Name == "search_docs");
        Assert.Equal("mcp-call-1", mcpCall.CallId);
        Assert.Equal("search_docs", mcpCall.Name);
        var mcpResult = Assert.Single(contents.OfType<McpServerToolResultContent>());
        Assert.Equal("mcp-call-1", mcpResult.CallId);
        Assert.Contains(mcpResult.Outputs!, output => output is TextContent text && text.Text.Contains("found document", StringComparison.Ordinal));

        var approval = Assert.Single(contents.OfType<ToolApprovalRequestContent>());
        var approvalCall = Assert.IsType<McpServerToolCallContent>(approval.ToolCall);
        Assert.Equal("delete_document", approvalCall.Name);
        Assert.Equal("approval-1", approvalCall.CallId);
        Assert.Contains(contents.OfType<ToolApprovalResponseContent>(), result => result.RequestId == "approval-1" && result.Approved);

        Assert.Contains(contents.OfType<ToolCallContent>(), call => call.CallId == "computer-1");
    }

    private static ChatClientFactory CreateFactory(HttpMessageHandler handler)
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        return new ChatClientFactory(new ChatProviderOptions
        {
            Provider = ChatProviderKind.OpenAi,
            Model = "gpt-test",
        }, handler);
    }

    private const string RichResponseJson = """
        {
          "id":"resp-rich","object":"response","created_at":1700000000,"status":"completed","model":"gpt-test","user":"end-user-7","safety_identifier":"safety-7","error":{"code":"server_error","message":"provider failure"},
          "output":[
            {"id":"msg-rich","type":"message","status":"completed","role":"assistant","content":[
              {"type":"output_text","text":"Here is the report.","annotations":[
                {"type":"url_citation","start_index":0,"end_index":4,"url":"https://example.test/report","title":"Report"},
                {"type":"file_citation","file_id":"file-report","index":0},
                {"type":"container_file_citation","container_id":"container-1","file_id":"file-container","filename":"container.txt","start_index":1,"end_index":4},
                {"type":"file_path","file_id":"file-path","index":1}
              ]},
              {"type":"refusal","refusal":"No secrets."},
              {"type":"input_image","file_id":"image-file"},
              {"type":"input_image","image_url":"data:image/png;base64,AQID"},
              {"type":"input_image","image_url":"not-an-absolute-uri"},
              {"type":"input_image","image_url":"https://example.test/from-response.png"},
              {"type":"input_file","file_id":"document-file","filename":"report.pdf"},
              {"type":"input_file","file_data":"data:application/pdf;base64,BAUG","filename":"raw.pdf"},
              {"type":"input_file","file_url":"https://example.test/from-response.pdf"}
            ]},
            {"id":"reasoning-1","type":"reasoning","summary":[{"type":"summary_text","text":"I checked the report."}],"encrypted_content":"protected-trace"},
            {"id":"call-42","type":"function_call","status":"completed","call_id":"call-42","name":"lookup_report","arguments":"{\"query\":\"quarterly\"}"},
            {"id":"web-1","type":"web_search_call","status":"completed","action":{"type":"search","queries":["reviewforge quarterly"],"sources":[{"type":"url","url":"https://example.test/source","title":"Source"}]}},
            {"id":"code-1","type":"code_interpreter_call","status":"completed","code":"print(42)","container_id":"container-1","outputs":[{"type":"logs","logs":"42\\n"},{"type":"files","files":[{"file_id":"file-output","mime_type":"text/plain","filename":"result.txt"}]}]},
            {"id":"image-1","type":"image_generation_call","status":"completed","result":"aW1hZ2U=","revised_prompt":"a blue square"},
            {"id":"file-search-1","type":"file_search_call","status":"completed","queries":["quarterly"],"results":[]},
            {"id":"function-output-1","type":"function_call_output","call_id":"call-42","output":"cached function result"},
            {"id":"computer-call-1","type":"computer_call","status":"completed","call_id":"computer-call-1","action":{"type":"screenshot","id":"screenshot-1"},"pending_safety_checks":[]},
            {"id":"computer-output-1","type":"computer_call_output","call_id":"computer-call-1","output":{"type":"computer_screenshot","image_url":"https://example.test/screen.png"}},
            {"id":"msg-next","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Second message.","annotations":[]}]}
          ],
          "usage":{"input_tokens":4,"output_tokens":12,"total_tokens":16}
        }
        """;

    private const string McpAndComputerResponseJson = """
        {
          "id":"resp-mcp","object":"response","created_at":1700000000,"status":"completed","model":"gpt-test",
          "output":[
            {"id":"mcp-call-1","type":"mcp_call","status":"completed","approval_request_id":null,"arguments":"{\"query\":\"docs\"}","name":"search_docs","server_label":"docs","output":"found document doc-7"},
            {"id":"approval-1","type":"mcp_approval_request","name":"delete_document","arguments":"{\"id\":\"doc-7\"}","server_label":"docs"},
            {"id":"approval-response-1","type":"mcp_approval_response","approval_request_id":"approval-1","approve":true},
            {"id":"computer-1","type":"computer_call","status":"completed","call_id":"computer-1","action":{"type":"screenshot","id":"screenshot-1"},"pending_safety_checks":[]}
          ]
        }
        """;

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        public EnvironmentScope() => Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        public void Dispose() => Environment.SetEnvironmentVariable("OPENAI_API_KEY", _previous);
    }

    private sealed class FixtureHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
    }
}
