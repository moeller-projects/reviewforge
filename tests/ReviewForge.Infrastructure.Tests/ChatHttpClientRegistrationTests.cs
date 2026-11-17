using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using ReviewForge.Infrastructure.Chat;
using ReviewForge.Infrastructure.Codex;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public class ChatHttpClientRegistrationTests
{
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 2)]
    [InlineData(HttpStatusCode.BadRequest, 1)]
    public async Task Named_provider_client_uses_standard_resilience_policy(
        HttpStatusCode firstStatus,
        int expectedRequests)
    {
        var handler = new StatusSequenceHandler(firstStatus);
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<ChatProviderOptions>>(Options.Create(new ChatProviderOptions
        {
            Provider = ChatProviderKind.OpenAi,
            Model = "gpt-test",
            MaxConcurrentRequests = 3,
            MaxQueuedRequests = 4,
        }));
        services.AddReviewForgeChatHttpClients();
        services.AddHttpClient(ChatClientFactory.OpenAiHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.Configure<HttpStandardResilienceOptions>(ChatClientFactory.OpenAiHttpClientName, options =>
        {
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.UseJitter = false;
        });

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(ChatClientFactory.OpenAiHttpClientName);
        Assert.Equal(3, policy.RateLimiter.DefaultRateLimiterOptions.PermitLimit);
        Assert.Equal(4, policy.RateLimiter.DefaultRateLimiterOptions.QueueLimit);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.CircuitBreaker.SamplingDuration);
        Assert.Equal(TimeSpan.FromSeconds(100), policy.AttemptTimeout.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(10), policy.TotalRequestTimeout.Timeout);

        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ChatClientFactory.OpenAiHttpClientName);
        using var response = await client.GetAsync("https://inference.test/");

        Assert.Equal(expectedRequests, handler.RequestCount);
        Assert.Equal(firstStatus == HttpStatusCode.ServiceUnavailable ? HttpStatusCode.OK : firstStatus,
            response.StatusCode);
    }

    [Fact]
    public void Codex_debug_handler_is_registered_when_enabled()
    {
        var previous = Environment.GetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable);
        Environment.SetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable, "1");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IOptions<ChatProviderOptions>>(Options.Create(new ChatProviderOptions
            {
                Provider = ChatProviderKind.OpenAiCodex,
                Model = "openai-codex:test",
                MaxConcurrentRequests = 3,
            }));
            services.AddReviewForgeChatHttpClients();

            using var provider = services.BuildServiceProvider();
            using var client = provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(ChatClientFactory.CodexHttpClientName);
            Assert.NotNull(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable, previous);
        }
    }


    private sealed class StatusSequenceHandler(HttpStatusCode firstStatus) : HttpMessageHandler
    {
        private int _RequestCount;
        public int RequestCount => Volatile.Read(ref _RequestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _RequestCount);
            return Task.FromResult(new HttpResponseMessage(
                requestNumber == 1 ? firstStatus : HttpStatusCode.OK));
        }
    }
}
