using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Codex;

namespace ReviewForge.Infrastructure.Chat;

/// <summary>Builds the model client for the configured provider. No engine fallback — config errors throw.</summary>
public sealed class ChatClientFactory(ReasoningOptions options, HttpMessageHandler? httpHandler = null) : IChatClientFactory
{
    public const string CodexEndpoint = "https://chatgpt.com/backend-api/codex";

    public string ModelName => ResolveModelName(options.Model);

    public IChatClient Create() => options.Provider switch
    {
        "openai-codex" => CreateCodexClient(),
        "openai" => CreateOpenAiClient(),
        _ => throw new InvalidOperationException($"unknown reasoning provider '{options.Provider}'"),
    };

    /// <summary>Strips the provider routing prefix, e.g. "openai-codex:gpt-5.6-luna" → "gpt-5.6-luna".</summary>
    public static string ResolveModelName(string configured)
        => configured.Split(':', 2) is [var prefix, var rest] && prefix.StartsWith("openai", StringComparison.Ordinal)
            ? rest
            : configured;

    private IChatClient CreateCodexClient()
    {
        var credential = new CodexCredential(
            options.CredentialPath ?? ReasoningOptions.DefaultCredentialPath(), httpHandler);
        var authHandler = new CodexAuthHandler(credential)
        {
            InnerHandler = httpHandler ?? new HttpClientHandler(),
        };
        HttpMessageHandler transportHandler = authHandler;
        if (string.Equals(
                Environment.GetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            transportHandler = new CodexHttpDebugHandler {InnerHandler = authHandler};
        }

        var authHttp = new HttpClient(transportHandler);

        var client = new OpenAIClient(new ApiKeyCredential("unused"), new OpenAIClientOptions
            {
                Endpoint = new Uri(CodexEndpoint),
                Transport = new HttpClientPipelineTransport(authHttp),
            })
            .GetResponsesClient()
            .AsIChatClient();

        var configured = new ChatClientBuilder(client)
            .ConfigureOptions(options =>
                options.RawRepresentationFactory = _ => new CreateResponseOptions
                {
                    StoredOutputEnabled = false,
                    StreamingEnabled = true,
                })
            .Build();

        return new CodexStreamingChatClient(configured);
    }

    private IChatClient CreateOpenAiClient()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                     ?? throw new InvalidOperationException("OPENAI_API_KEY environment variable missing");

        var clientOptions = httpHandler is null
            ? new OpenAIClientOptions()
            : new OpenAIClientOptions {Transport = new HttpClientPipelineTransport(new HttpClient(httpHandler, disposeHandler: false))};

        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetResponsesClient()
            .AsIChatClient();
    }
}