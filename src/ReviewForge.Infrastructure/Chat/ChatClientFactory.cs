using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Codex;

namespace ReviewForge.Infrastructure.Chat;

/// <summary>
/// Builds the model client for the configured provider. No engine fallback — config errors throw.
/// Clients are cached per <see cref="ChatTier"/> process-wide: each run sharing this factory must
/// not open a fresh transport (socket exhaustion), and the Codex credential is read from disk
/// once per created client. When <c>FollowUpModel</c> is unset the Fast tier aliases the Full
/// tier — one client, identical behavior.
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory, IDisposable
{
    public const string CodexEndpoint = "https://chatgpt.com/backend-api/codex";
    private readonly ConcurrentDictionary<ChatTier, Lazy<IChatClient>> _Clients = new();
    private readonly HttpMessageHandler? _HttpHandler;

    private readonly ChatProviderOptions _Options;

    public ChatClientFactory(
        ChatProviderOptions options,
        HttpMessageHandler? httpHandler = null,
        LlmGovernor? governor = null,
        TimeProvider? clock = null)
    {
        _Options = options;
        _HttpHandler = httpHandler;
        _Governor = governor;
        _Clock = clock ?? TimeProvider.System;
    }

    private readonly LlmGovernor? _Governor;
    private readonly TimeProvider _Clock;

    public string ModelName => ModelName(ChatTier.Full);

    public string ModelName(ChatTier tier)
        => ResolveModelName(tier == ChatTier.Fast && _Options.FollowUpModel is { } fast ? fast : _Options.Model);

    public IChatClient Create() => Create(ChatTier.Full);

    public IChatClient Create(ChatTier tier)
    {
        if (tier == ChatTier.Fast && string.IsNullOrWhiteSpace(_Options.FollowUpModel))
        {
            return Create(ChatTier.Full); // Fast aliases Full: identical behavior, zero config
        }

        return _Clients.GetOrAdd(tier, static (_, self) => new Lazy<IChatClient>(self.BuildClient, true), this).Value;
    }

    /// <summary>Releases every created client's transport (if any) on host shutdown.</summary>
    public void Dispose()
    {
        foreach (var lazy in _Clients.Values)
        {
            if (lazy.IsValueCreated && lazy.Value is IDisposable d)
            {
                d.Dispose();
            }
        }
    }

    private IChatClient BuildClient()
    {
        IChatClient raw = _Options.Provider switch
        {
            "openai-codex" => CreateCodexClient(),
            "openai" => CreateOpenAiClient(),
            _ => throw new InvalidOperationException($"unknown reasoning provider '{_Options.Provider}'"),
        };

        // Between usage tracking and the transport: tracking measures real provider
        // traffic, the governor shapes it. Both tiers share one process-wide governor.
        return _Governor is { } governor
            ? new GovernedChatClient(raw, governor, _Options.GovernorAcquireTimeoutSeconds, _Clock)
            : raw;
    }

    /// <summary>Strips the provider routing prefix, e.g. "openai-codex:gpt-5.6-luna" → "gpt-5.6-luna".</summary>
    public static string ResolveModelName(string configured)
        => configured.Split(':', 2) is [var prefix, var rest] && prefix.StartsWith("openai", StringComparison.Ordinal)
            ? rest
            : configured;

    private IChatClient CreateCodexClient()
    {
        var credential = new CodexCredential(
            _Options.CredentialPath ?? ChatProviderOptions.DefaultCredentialPath(), _HttpHandler);
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
            var aspnetEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var dotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            if (string.Equals(aspnetEnvironment, "Production", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dotnetEnvironment, "Production", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{CodexHttpDebugHandler.EnvironmentVariable}=1 is refused in Production: " +
                    "the debug handler logs truncated request/response bodies containing source code.");
            }

            Console.Error.WriteLine(
                $"[codex-http] WARNING: wire debug logging enabled ({CodexHttpDebugHandler.EnvironmentVariable}=1); " +
                "truncated request/response bodies (may contain source code) are written to stderr.");
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