using System.ClientModel;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using ReviewForge.Infrastructure.Chat;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

/// <summary>Consumer-visible contract tests for the OpenAI Responses IChatClient adapter.</summary>
[Collection(EnvironmentVariableCollection.Name)]
public sealed class ResponsesOutputMappingTests
{
    [Fact]
    public async Task Non_streaming_response_maps_text_metadata_usage_and_annotations()
    {
        using var environment = new ResponsesEnvironmentScope();
        using var handler = new ResponsesOutputFixtureHandler(CompletedResponseJson);
        using var factory = CreateFactory(handler);

        var response = await factory.Create(ChatTier.Full).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "summarize")],
            new ChatOptions { ModelId = "gpt-test" });

        Assert.Equal("A result", response.Text);
        Assert.Equal("resp-42", response.ResponseId);
        Assert.Equal("gpt-test", response.ModelId);
        Assert.Equal(12, response.Usage!.InputTokenCount);
        Assert.Equal(7, response.Usage.OutputTokenCount);
        Assert.Equal(19, response.Usage.TotalTokenCount);
        Assert.Equal("resp-42", response.ConversationId);
        var text = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<TextContent>());
        var annotation = Assert.Single(text.Annotations!.OfType<CitationAnnotation>());
        Assert.Equal(new Uri("https://example.test/source"), annotation.Url);
        Assert.Equal("source", annotation.Title);
    }

    [Fact]
    public async Task Non_streaming_response_maps_refusal_and_function_call_contents()
    {
        using var environment = new ResponsesEnvironmentScope();
        using var handler = new ResponsesOutputFixtureHandler(FunctionCallResponseJson);
        using var factory = CreateFactory(handler);

        var response = await factory.Create(ChatTier.Full).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "use the tool")]);

        Assert.Contains(response.Messages.SelectMany(message => message.Contents),
            content => content is ErrorContent error && error.Message.Contains("cannot", StringComparison.Ordinal));
        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("lookup", call.Name);
        Assert.Contains("reviewforge", call.Arguments!.Values.Select(value => value?.ToString()));
    }

    [Fact]
    public async Task Streaming_response_converts_deltas_and_final_metadata()
    {
        using var environment = new ResponsesEnvironmentScope();
        using var handler = new ResponsesOutputFixtureHandler(StreamingResponseSse);
        using var factory = CreateFactory(handler);
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in factory.Create(ChatTier.Full).GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "stream")]))
        {
            updates.Add(update);
        }

        Assert.Contains(updates, update => update.Text == "hel");
        Assert.Contains(updates, update => update.Text == "lo");
        var final = Assert.Single(updates, update => update.FinishReason is not null);
        Assert.Equal("resp-stream", final.ResponseId);
        Assert.Equal("gpt-stream", final.ModelId);
        Assert.Equal("resp-stream", final.ConversationId);
        var usage = Assert.Single(final.Contents.OfType<UsageContent>()).Details;
        Assert.Equal(5, usage.InputTokenCount);
        Assert.Equal(3, usage.OutputTokenCount);
        Assert.Equal(8, usage.TotalTokenCount);
    }

    [Fact]
    public async Task Error_response_surfaces_provider_failure_without_partial_output()
    {
        using var environment = new ResponsesEnvironmentScope();
        using var handler = new ResponsesOutputFixtureHandler("{\"error\":{\"message\":\"bad request\"}}", HttpStatusCode.BadRequest);
        using var factory = CreateFactory(handler);

        var exception = await Assert.ThrowsAsync<ClientResultException>(() =>
            factory.Create(ChatTier.Full).GetResponseAsync([new ChatMessage(ChatRole.User, "fail")]));

        Assert.Equal((int)HttpStatusCode.BadRequest, exception.Status);
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

    private const string CompletedResponseJson = """
        {
          "id":"resp-42","object":"response","created_at":1700000000,"status":"completed",
          "model":"gpt-test","conversation":{"id":"conv-9"},
          "output":[{"id":"msg-1","type":"message","status":"completed","role":"assistant",
            "content":[{"type":"output_text","text":"A result","annotations":[{"type":"url_citation","url":"https://example.test/source","title":"source"}]}]}],
          "usage":{"input_tokens":12,"output_tokens":7,"total_tokens":19}
        }
        """;

    private const string FunctionCallResponseJson = """
        {
          "id":"resp-tool","object":"response","created_at":1700000000,"status":"completed","model":"gpt-test",
          "output":[
            {"id":"msg-refusal","type":"message","status":"completed","role":"assistant","content":[{"type":"refusal","refusal":"cannot comply"}]},
            {"id":"call-1","type":"function_call","status":"completed","call_id":"call-1","name":"lookup","arguments":"{\"query\":\"reviewforge\"}"}
          ],
          "usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}
        }
        """;

    private const string StreamingResponseSse = """
        event: response.created
        data: {"type":"response.created","sequence_number":0,"response":{"id":"resp-stream","object":"response","created_at":1700000000,"model":"gpt-stream","status":"in_progress","output":[]}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"msg-stream","type":"message","status":"in_progress","role":"assistant","content":[]}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","sequence_number":2,"item_id":"msg-stream","output_index":0,"content_index":0,"delta":"hel"}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","sequence_number":3,"item_id":"msg-stream","output_index":0,"content_index":0,"delta":"lo"}

        event: response.output_item.done
        data: {"type":"response.output_item.done","sequence_number":4,"output_index":0,"item":{"id":"msg-stream","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"hello","annotations":[]}]}}

        event: response.completed
        data: {"type":"response.completed","sequence_number":5,"response":{"id":"resp-stream","object":"response","created_at":1700000000,"model":"gpt-stream","status":"completed","output":[{"id":"msg-stream","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"hello","annotations":[]}]}],"usage":{"input_tokens":5,"output_tokens":3,"total_tokens":8}}}

        """ + "\n";

    private sealed class ResponsesEnvironmentScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        public ResponsesEnvironmentScope() => Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");

        public void Dispose() => Environment.SetEnvironmentVariable("OPENAI_API_KEY", _previous);
    }

    private sealed class ResponsesOutputFixtureHandler(string payload, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(payload, Encoding.UTF8,
                    payload.StartsWith("event:", StringComparison.Ordinal) ? "text/event-stream" : "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
