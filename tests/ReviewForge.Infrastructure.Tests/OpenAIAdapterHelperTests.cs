using System.Net;
using System.ComponentModel.DataAnnotations;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Chat;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class OpenAIAdapterHelperTests
{
    [Fact]
    public void Responses_client_adapter_exposes_services_and_default_model()
    {
        var responses = new OpenAIClient(new ApiKeyCredential("key"), new OpenAIClientOptions()).GetResponsesClient();

        var client = responses.AsIChatClient("model-from-test");

        Assert.NotNull(client);
        Assert.NotNull(client.GetService(typeof(OpenAIRequestPolicies)));
        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Null(client.GetService(typeof(string)));
    }

    [Fact]
    public void AsIChatClient_rejects_null_response_client()
        => Assert.Throws<ArgumentNullException>(() => ((ResponsesClient)null!).AsIChatClient());

    [Theory]
    [InlineData("conv_abc", true)]
    [InlineData("resp_abc", false)]
    [InlineData("", false)]
    public void Conversation_id_detection_uses_openai_prefix(string value, bool expected)
        => Assert.Equal(expected, OpenAIClientExtensions.IsConversationId(value));

    [Theory]
    [InlineData("https://example.test/a.png", "image/png")]
    [InlineData("https://example.test/a.JPEG", "image/jpeg")]
    [InlineData("https://example.test/a.webp", "image/webp")]
    [InlineData("https://example.test/a.gif", "image/gif")]
    [InlineData("https://example.test/a.bmp", "image/bmp")]
    [InlineData("https://example.test/a.tif", "image/tiff")]
    [InlineData("https://example.test/a.svg", "image/svg+xml")]
    [InlineData("https://example.test/a.avif", "image/avif")]
    [InlineData("https://example.test/a.unknown", "image/*")]
    public void Image_uri_media_type_is_caller_observable(string uri, string expected)
        => Assert.Equal(expected, OpenAIClientExtensions.ImageUriToMediaType(new Uri(uri)));

    [Fact]
    public void ParseCallContent_preserves_valid_arguments()
    {
        var valid = OpenAIClientExtensions.ParseCallContent("{\"value\":42}", "call-1", "tool");

        Assert.Equal("call-1", valid.CallId);
        Assert.Equal("tool", valid.Name);
        Assert.Equal(42, Assert.IsType<JsonElement>(valid.Arguments!["value"]).GetInt32());
    }

    [Fact]
    public void ParseCallContent_validates_identity_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => OpenAIClientExtensions.ParseCallContent("{}", null!, "tool"));
        Assert.Throws<ArgumentNullException>(() => OpenAIClientExtensions.ParseCallContent("{}", "id", null!));
    }

    [Fact]
    public void Strict_metadata_controls_function_parameters()
    {
        var function = AIFunctionFactory.Create((string input) => input, name: "lookup", description: "A lookup tool");
        var parameters = OpenAIClientExtensions.ToOpenAIFunctionParameters(function, strict: true);
        Assert.True(OpenAIClientExtensions.HasStrict(new Dictionary<string, object?> { ["strict"] = true }));
        Assert.False(OpenAIClientExtensions.HasStrict(new Dictionary<string, object?> { ["strict"] = false }));
        Assert.Null(OpenAIClientExtensions.HasStrict(null));
        using var schema = JsonDocument.Parse(parameters.ToString());

        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("input", schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()));
    }

    [Fact]
    public void Strict_schema_moves_unsupported_parameter_constraints_into_description()
    {
        static string Search([StringLength(12, MinimumLength = 2)] string query) => query;
        var function = AIFunctionFactory.Create((Func<string, string>)Search, "search", "Search records");

        using var schema = JsonDocument.Parse(OpenAIClientExtensions.ToOpenAIFunctionParameters(function, strict: true).ToString());
        var parameter = schema.RootElement.GetProperty("properties").GetProperty("query");

        Assert.False(parameter.TryGetProperty("minLength", out _));
        Assert.False(parameter.TryGetProperty("maxLength", out _));
        Assert.Contains("minLength", parameter.GetProperty("description").GetString());
        Assert.Contains("maxLength", parameter.GetProperty("description").GetString());
    }

#pragma warning disable SCME0001 // JsonPatch is experimental.
    [Fact]
    public void PatchModelIfNotSet_does_not_override_explicit_model()
    {
        var patch = new JsonPatch(Encoding.UTF8.GetBytes("{\"model\":\"caller-model\"}"));
        OpenAIClientExtensions.PatchModelIfNotSet(ref patch, "default-model");

        Assert.Equal("caller-model", patch.GetString("$.model"u8));
    }

    [Fact]
    public void PatchModelIfNotSet_adds_default_model_when_missing()
    {
        var patch = new JsonPatch(Encoding.UTF8.GetBytes("{}"));
        OpenAIClientExtensions.PatchModelIfNotSet(ref patch, "default-model");

        Assert.Equal("default-model", patch.GetString("$.model"u8));
    }
#pragma warning restore SCME0001

    [Fact]
    public async Task Request_policies_run_in_registration_order_on_real_requests()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var order = new List<int>();
            using var handler = new PolicyResponseHandler();
            using var factory = new ChatClientFactory(
                new ChatProviderOptions { Provider = ChatProviderKind.OpenAi, Model = "gpt-test" },
                handler);
            var client = factory.Create(ChatTier.Full);
            var policies = Assert.IsType<OpenAIRequestPolicies>(
                client.GetService(typeof(OpenAIRequestPolicies)));

            policies.AddPolicy(new RecordingPolicy(1, order));
            policies.AddPolicy(new RecordingPolicy(2, order));
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "request")]);

            Assert.Equal(new[] { 1, 2 }, order);
            Assert.Contains("MEAI/", handler.UserAgent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public void Request_policies_reject_null()
    {
        var policies = new OpenAIRequestPolicies();
        Assert.Throws<ArgumentNullException>(() => policies.AddPolicy(null!));
    }

    [Fact]
    public void Request_options_preserve_cancellation_and_buffering_mode()
    {
        using var cancellationSource = new CancellationTokenSource();
        var streaming = cancellationSource.Token.ToRequestOptions(streaming: true);
        var buffered = CancellationToken.None.ToRequestOptions(streaming: false);

        Assert.Equal(cancellationSource.Token, streaming.CancellationToken);
        Assert.False(streaming.BufferResponse);
        Assert.True(buffered.BufferResponse);
    }


    [Fact]
    public void Activity_helpers_only_tag_chat_activity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test");
        using var activity = source.StartActivity("chat test")!;
        OpenAIClientExtensions.AddOpenAIApiType("responses");
        OpenAIClientExtensions.AddOpenAIResponseAttributes("tier", "fingerprint");
        Assert.Equal("responses", activity.GetTagItem("openai.api.type"));
        Assert.Equal("tier", activity.GetTagItem("openai.response.service_tier"));
        Assert.Equal("fingerprint", activity.GetTagItem("openai.response.system_fingerprint"));

        string? capturedTier = null;
        string? capturedFingerprint = null;
        OpenAIClientExtensions.AddOpenAIResponseAttributes("stream-tier", null, ref capturedTier, ref capturedFingerprint);
        OpenAIClientExtensions.AddOpenAIResponseAttributes("ignored-tier", "stream-fingerprint", ref capturedTier, ref capturedFingerprint);
        OpenAIClientExtensions.AddOpenAIResponseAttributes("later-tier", "later-fingerprint", ref capturedTier, ref capturedFingerprint);

        Assert.Equal("stream-tier", capturedTier);
        Assert.Equal("stream-fingerprint", capturedFingerprint);
        Assert.Equal("stream-tier", activity.GetTagItem("openai.response.service_tier"));
        Assert.Equal("stream-fingerprint", activity.GetTagItem("openai.response.system_fingerprint"));

        using var nonChat = source.StartActivity("request")!;
        OpenAIClientExtensions.AddOpenAIApiType("ignored");
        string? uncapturedTier = null;
        string? uncapturedFingerprint = null;
        OpenAIClientExtensions.AddOpenAIResponseAttributes("ignored-tier", "ignored-fingerprint", ref uncapturedTier, ref uncapturedFingerprint);
        Assert.Null(uncapturedTier);
        Assert.Null(uncapturedFingerprint);
        Assert.Null(nonChat.GetTagItem("openai.api.type"));
        Assert.Null(nonChat.GetTagItem("openai.response.service_tier"));
    }

    private sealed class RecordingPolicy(int id, IList<int> order) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
        {
            order.Add(id);
            ProcessNext(message, pipeline, index);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
        {
            order.Add(id);
            return ProcessNextAsync(message, pipeline, index);
        }
    }
    private sealed class PolicyResponseHandler : HttpMessageHandler
    {
        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"resp-policy","object":"response","created_at":0,"status":"completed","model":"gpt-test","output":[]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
