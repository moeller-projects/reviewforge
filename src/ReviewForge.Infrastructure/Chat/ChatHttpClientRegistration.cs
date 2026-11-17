using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using ReviewForge.Infrastructure.Codex;

namespace ReviewForge.Infrastructure.Chat;

/// <summary>Registers the standard HTTP resilience pipeline for inference providers.</summary>
public static class ChatHttpClientRegistration
{
    public static IServiceCollection AddReviewForgeChatHttpClients(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ChatProviderOptions>>().Value;
            return new CodexCredential(options.CredentialPath ?? ChatProviderOptions.DefaultCredentialPath());
        });

        var codex = services.AddHttpClient(ChatClientFactory.CodexHttpClientName);
        if (string.Equals(
                Environment.GetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            codex.AddHttpMessageHandler(() => new CodexHttpDebugHandler());
        }

        codex
            .AddHttpMessageHandler(sp => new CodexAuthHandler(sp.GetRequiredService<CodexCredential>()))
            .AddStandardResilienceHandler();

        services.AddHttpClient(ChatClientFactory.OpenAiHttpClientName)
            .AddStandardResilienceHandler();

        ConfigureProviderResilienceOptions(services, ChatClientFactory.CodexHttpClientName);
        ConfigureProviderResilienceOptions(services, ChatClientFactory.OpenAiHttpClientName);
        return services;
    }

    private static void ConfigureProviderResilienceOptions(IServiceCollection services, string clientName)
        => services.AddOptions<HttpStandardResilienceOptions>(clientName)
            .Configure<IOptions<ChatProviderOptions>>((resilience, chat) =>
            {
                var options = chat.Value;
                resilience.RateLimiter.DefaultRateLimiterOptions.PermitLimit = options.MaxConcurrentRequests;
                resilience.RateLimiter.DefaultRateLimiterOptions.QueueLimit = options.MaxQueuedRequests;
                // Match HttpClient's former 100-second request timeout and allow the
                // former five-minute admission wait plus provider retries to complete.
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(5);
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(100);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
            });
}
