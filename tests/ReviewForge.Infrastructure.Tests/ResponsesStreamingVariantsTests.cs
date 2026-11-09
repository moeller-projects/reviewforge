using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using ReviewForge.Infrastructure.Chat;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class ResponsesStreamingVariantsTests
{
    [Fact]
    public async Task Streaming_statuses_text_refusal_reasoning_and_terminal_usage_are_mapped()
    {
        using var environment = new EnvironmentScope();
        using var handler = new SseHandler(StatusAndContentEvents);
        using var factory = CreateFactory(handler);
        var updates = await ReadUpdates(factory);

        Assert.Contains(updates, update => update.Text == "hel");
        Assert.Contains(updates, update => update.Text == "lo");
        Assert.Contains(updates.SelectMany(update => update.Contents), content => content is TextReasoningContent reasoning && reasoning.Text == "because");
        Assert.Contains(updates.SelectMany(update => update.Contents), content => content is ErrorContent error && error.Message == "not allowed");
        Assert.Contains(updates.SelectMany(update => update.Contents),
            content => content.Annotations?.OfType<CitationAnnotation>().Any(citation => citation.Url == new Uri("https://example.test/stream")) == true);
        Assert.Contains(updates.SelectMany(update => update.Contents).OfType<TextReasoningContent>(),
            reasoning => reasoning.ProtectedData == "encrypted-summary");

        var terminal = Assert.Single(updates, update => update.FinishReason is not null);
        Assert.Equal("resp-rich", terminal.ResponseId);
        Assert.Equal("gpt-rich", terminal.ModelId);
        Assert.Equal("resp-rich", terminal.ConversationId);
        var usage = Assert.Single(terminal.Contents.OfType<UsageContent>()).Details;
        Assert.Equal(11, usage.InputTokenCount);
        Assert.Equal(9, usage.OutputTokenCount);
        Assert.Equal(20, usage.TotalTokenCount);
    }

    [Fact]
    public async Task Streaming_function_call_arguments_and_tool_events_are_exposed_as_updates()
    {
        using var environment = new EnvironmentScope();
        using var handler = new SseHandler(ToolEvents);
        using var factory = CreateFactory(handler);
        var updates = await ReadUpdates(factory);

        var call = Assert.Single(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>());
        Assert.Equal("call-7", call.CallId);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("reviewforge", call.Arguments!["query"]?.ToString());
        Assert.Contains(updates.SelectMany(update => update.Contents).OfType<WebSearchToolCallContent>(), search => search.CallId == "search-1");
        Assert.Contains(updates.SelectMany(update => update.Contents).OfType<McpServerToolCallContent>(), callContent => callContent.CallId == "mcp-1");
        Assert.Contains(updates.SelectMany(update => update.Contents).OfType<FunctionResultContent>(), result => result.CallId == "call-7" && Equals(result.Result, "answer"));
        var approval = Assert.Single(updates.SelectMany(update => update.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Equal("approval-2", approval.RequestId);
        Assert.Contains(updates.SelectMany(update => update.Contents).OfType<ToolApprovalResponseContent>(), result => result.RequestId == "approval-2" && result.Approved);
    }

    [Fact]
    public async Task Streaming_specialized_tool_lifecycle_events_preserve_partial_outputs_and_terminal_result()
    {
        using var environment = new EnvironmentScope();
        using var handler = new SseHandler(SpecializedToolEvents);
        using var factory = CreateFactory(handler);
        var updates = await ReadUpdates(factory);
        var contents = updates.SelectMany(update => update.Contents).ToArray();

        Assert.Contains(contents.OfType<CodeInterpreterToolCallContent>(), content => content.CallId == "code-1" && content.Inputs!.OfType<DataContent>().Any(input => Encoding.UTF8.GetString(input.Data.Span) == "print(1)"));
        Assert.Contains(contents.OfType<ImageGenerationToolResultContent>(), image => image.Outputs!.OfType<DataContent>().Any(data => data.Data.Span.SequenceEqual(Convert.FromBase64String("aGVsbG8="))));
        Assert.Contains(contents.OfType<ImageGenerationToolResultContent>(), image => image.Outputs!.OfType<DataContent>().Any(data => data.Data.Span.SequenceEqual(Convert.FromBase64String("aW1hZ2U="))));
        Assert.Contains(contents.OfType<CodeInterpreterToolResultContent>(), result => result.CallId == "code-1" && result.Outputs!.OfType<TextContent>().Any(text => text.Text == "done"));
        Assert.Contains(contents.OfType<ToolCallContent>(), call => call.CallId == "file-search-1");
        Assert.Contains(contents.OfType<ToolResultContent>(), result => result.CallId == "file-search-1");
        Assert.Contains(contents.OfType<ToolResultContent>(), result => result.CallId == "computer-1");
        Assert.Contains(contents.OfType<ErrorContent>(), error => error.ErrorCode == "tool_error" && error.Message == "tool unavailable");
        var terminal = Assert.Single(updates, update => update.FinishReason is not null);
        Assert.Equal(10, Assert.Single(terminal.Contents.OfType<UsageContent>()).Details.TotalTokenCount);
    }

    private static async Task<List<ChatResponseUpdate>> ReadUpdates(ChatClientFactory factory)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in factory.Create(ChatTier.Full).GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "stream")]))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static ChatClientFactory CreateFactory(HttpMessageHandler handler)
        => new(new ChatProviderOptions { Provider = ChatProviderKind.OpenAi, Model = "gpt-rich" }, handler);

    private const string StatusAndContentEvents = """
        event: response.queued
        data: {"type":"response.queued","response":{"id":"resp-rich","model":"gpt-rich","status":"queued"}}

        event: response.in_progress
        data: {"type":"response.in_progress","response":{"id":"resp-rich","model":"gpt-rich","status":"in_progress"}}

        event: response.created
        data: {"type":"response.created","response":{"id":"resp-rich","model":"gpt-rich","conversation":{"id":"conv-rich"},"status":"in_progress"}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg-rich","output_index":0,"content_index":0,"delta":"hel"}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg-rich","output_index":0,"content_index":0,"delta":"lo"}

        event: response.output_text.done
        data: {"type":"response.output_text.done","item_id":"msg-rich","output_index":0,"content_index":0,"text":"hello","annotations":[]}

        event: response.refusal.delta
        data: {"type":"response.refusal.delta","item_id":"msg-rich","output_index":0,"content_index":1,"delta":"not allowed"}

        event: response.refusal.done
        data: {"type":"response.refusal.done","item_id":"msg-rich","output_index":0,"content_index":1,"refusal":"not allowed"}

        event: response.reasoning_summary_text.delta
        data: {"type":"response.reasoning_summary_text.delta","item_id":"reason-1","output_index":1,"summary_index":0,"delta":"because"}

        event: response.reasoning_summary_text.done
        data: {"type":"response.reasoning_summary_text.done","item_id":"reason-1","output_index":1,"summary_index":0,"text":"because"}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":0,"item":{"id":"msg-rich","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"hello","annotations":[{"type":"url_citation","start_index":0,"end_index":2,"url":"https://example.test/stream","title":"Stream"}]}]}}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":1,"item":{"id":"reason-1","type":"reasoning","summary":[{"type":"summary_text","text":"because"}],"encrypted_content":"encrypted-summary"}}

        event: response.incomplete
        data: {"type":"response.incomplete","response":{"id":"resp-rich","model":"gpt-rich","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}

        event: response.failed
        data: {"type":"response.failed","response":{"id":"resp-rich","model":"gpt-rich","status":"failed","error":{"code":"temporary","message":"retry"}}}

        event: response.completed
        data: {"type":"response.completed","response":{"id":"resp-rich","model":"gpt-rich","conversation":{"id":"conv-rich"},"status":"completed","usage":{"input_tokens":11,"output_tokens":9,"total_tokens":20}}}

        """ + "\n";

    private const string ToolEvents = """
        event: response.created
        data: {"type":"response.created","response":{"id":"resp-tools","model":"gpt-rich","status":"in_progress"}}

        event: response.function_call_arguments.delta
        data: {"type":"response.function_call_arguments.delta","item_id":"call-7","output_index":0,"delta":"{\"query\":"}

        event: response.function_call_arguments.delta
        data: {"type":"response.function_call_arguments.delta","item_id":"call-7","output_index":0,"delta":"\"reviewforge\"}"}

        event: response.function_call_arguments.done
        data: {"type":"response.function_call_arguments.done","item_id":"call-7","output_index":0,"name":"lookup","call_id":"call-7","arguments":"{\"query\":\"reviewforge\"}"}

        event: response.output_item.added
        data: {"type":"response.output_item.added","output_index":0,"item":{"id":"call-7","type":"function_call","call_id":"call-7","name":"lookup","arguments":""}}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":0,"item":{"id":"call-7","type":"function_call","call_id":"call-7","name":"lookup","arguments":"{\"query\":\"reviewforge\"}"}}


        event: response.web_search_call.in_progress
        data: {"type":"response.web_search_call.in_progress","item_id":"search-1","output_index":1}

        event: response.web_search_call.searching
        data: {"type":"response.web_search_call.searching","item_id":"search-1","output_index":1}

        event: response.web_search_call.completed
        data: {"type":"response.web_search_call.completed","item_id":"search-1","output_index":1}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":1,"item":{"id":"search-1","type":"web_search_call","status":"completed","action":{"type":"search","query":"reviewforge","sources":[]}}}


        event: response.mcp_call.in_progress
        data: {"type":"response.mcp_call.in_progress","item_id":"mcp-1","output_index":2}

        event: response.mcp_call.arguments.delta
        data: {"type":"response.mcp_call.arguments.delta","item_id":"mcp-1","output_index":2,"delta":"{}"}

        event: response.mcp_call.arguments.done
        data: {"type":"response.mcp_call.arguments.done","item_id":"mcp-1","output_index":2,"arguments":"{}"}

        event: response.mcp_call.completed
        data: {"type":"response.mcp_call.completed","item_id":"mcp-1","output_index":2,"output":"ok"}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":2,"item":{"id":"mcp-1","type":"mcp_call","status":"completed","server_label":"remote","name":"lookup","arguments":"{}","output":"ok"}}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":3,"item":{"id":"output-7","type":"function_call_output","call_id":"call-7","output":"answer"}}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":4,"item":{"id":"approval-2","type":"mcp_approval_request","name":"delete_doc","arguments":"{}","server_label":"remote"}}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":5,"item":{"id":"approval-response-2","type":"mcp_approval_response","approval_request_id":"approval-2","approve":true}}


        event: response.completed
        data: {"type":"response.completed","response":{"id":"resp-tools","model":"gpt-rich","status":"completed","usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}}

        """ + "\n";

    private const string SpecializedToolEvents = """
        event: response.created
        data: {"type":"response.created","response":{"id":"resp-special","model":"gpt-rich","status":"in_progress"}}

        event: response.code_interpreter_call.in_progress
        data: {"type":"response.code_interpreter_call.in_progress","item_id":"code-1","output_index":0}

        event: response.code_interpreter_call_code.delta
        data: {"type":"response.code_interpreter_call_code.delta","item_id":"code-1","output_index":0,"delta":"print(1)"}

        event: response.code_interpreter_call_code.done
        data: {"type":"response.code_interpreter_call_code.done","item_id":"code-1","output_index":0,"code":"print(1)"}

        event: response.code_interpreter_call.completed
        data: {"type":"response.code_interpreter_call.completed","item_id":"code-1","output_index":0}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":0,"item":{"id":"code-1","type":"code_interpreter_call","status":"completed","code":"print(1)","container_id":"container-1","outputs":[{"type":"logs","logs":"done"}]}}

        event: response.image_generation_call.in_progress
        data: {"type":"response.image_generation_call.in_progress","item_id":"image-1","output_index":1}

        event: response.image_generation_call.partial_image
        data: {"type":"response.image_generation_call.partial_image","item_id":"image-1","output_index":1,"partial_image_b64":"aGVsbG8=","partial_image_index":0}

        event: response.image_generation_call.completed
        data: {"type":"response.image_generation_call.completed","item_id":"image-1","output_index":1}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":1,"item":{"id":"image-1","type":"image_generation_call","status":"completed","result":"aW1hZ2U="}}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":2,"item":{"id":"file-search-1","type":"file_search_call","status":"completed","queries":["quarterly report"],"results":[]}}

        event: response.computer_call.in_progress
        data: {"type":"response.computer_call.in_progress","item_id":"computer-1","output_index":2}

        event: response.computer_call.completed
        data: {"type":"response.computer_call.completed","item_id":"computer-1","output_index":2}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":2,"item":{"id":"computer-1","type":"computer_call","status":"completed","call_id":"computer-1","action":{"type":"screenshot","id":"screenshot-1"},"pending_safety_checks":[]}}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":3,"item":{"id":"computer-output-1","type":"computer_call_output","call_id":"computer-1","output":{"type":"computer_screenshot","image_url":"https://example.test/screenshot.png"}}}


        event: response.error
        data: {"type":"error","error":{"code":"tool_error","message":"tool unavailable","param":"input"}}

        event: response.completed
        data: {"type":"response.completed","response":{"id":"resp-special","model":"gpt-rich","status":"completed","usage":{"input_tokens":4,"output_tokens":6,"total_tokens":10}}}

        """ + "\n";

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        public EnvironmentScope() => Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        public void Dispose() => Environment.SetEnvironmentVariable("OPENAI_API_KEY", _previous);
    }

    private sealed class SseHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "text/event-stream"),
            });
    }
}
