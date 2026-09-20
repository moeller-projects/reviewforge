using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Codex;

namespace ReviewForge.Infrastructure.Chat;

/// <summary>
/// Builds the model client for the configured provider. No engine fallback — config errors throw.
/// The client is built once and cached process-wide: each run sharing this factory must not open
/// a fresh transport (socket exhaustion), and the Codex credential is read from disk exactly once.
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory, IDisposable
{
    public const string CodexEndpoint = "https://chatgpt.com/backend-api/codex";
    private readonly Lazy<IChatClient> _Client;
    private readonly HttpMessageHandler? _HttpHandler;

    private readonly ReasoningOptions _Options;

    public ChatClientFactory(ReasoningOptions options, HttpMessageHandler? httpHandler = null)
    {
        _Options = options;
        _HttpHandler = httpHandler;
        _Client = new Lazy<IChatClient>(BuildClient, true);
    }

    public string ModelName => ResolveModelName(_Options.Model);

    public IChatClient Create() => _Client.Value;

    /// <summary>Releases the cached client's transport (if any) on host shutdown.</summary>
    public void Dispose()
    {
        if (_Client.IsValueCreated && _Client.Value is IDisposable d) d.Dispose();
    }

    private IChatClient BuildClient() => _Options.Provider switch
    {
        "openai-codex" => CreateCodexClient(),
        "openai" => CreateOpenAiClient(),
        _ => throw new InvalidOperationException($"unknown reasoning provider '{_Options.Provider}'"),
    };

    /// <summary>Strips the provider routing prefix, e.g. "openai-codex:gpt-5.6-luna" → "gpt-5.6-luna".</summary>
    public static string ResolveModelName(string configured)
        => configured.Split(':', 2) is [var prefix, var rest] && prefix.StartsWith("openai", StringComparison.Ordinal)
            ? rest
            : configured;

    private IChatClient CreateCodexClient()
    {
        var credential = new CodexCredential(
            _Options.CredentialPath ?? ReasoningOptions.DefaultCredentialPath(), _HttpHandler);
        var authHandler = new CodexAuthHandler(credential)
        {
            InnerHandler = _HttpHandler ?? new HttpClientHandler(),
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

        var clientOptions = _HttpHandler is null
            ? new OpenAIClientOptions()
            : new OpenAIClientOptions {Transport = new HttpClientPipelineTransport(new HttpClient(_HttpHandler, disposeHandler: false))};

        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetResponsesClient()
            .AsIChatClient();
    }
}