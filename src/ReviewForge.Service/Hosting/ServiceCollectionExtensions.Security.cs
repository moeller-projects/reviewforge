using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReviewForge.Service.Security;

namespace ReviewForge.Service;

public static partial class ServiceCollectionExtensions
{
    private static IServiceCollection AddReviewForgeSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiKeyOptions>()
            .Configure(opts =>
            {
                opts.AllowUnauthenticatedForDevelopment = configuration.GetValue<bool>($"{ApiKeyOptions.SectionName}:AllowUnauthenticatedForDevelopment");
                opts.SubmitPermitLimit = configuration.GetValue($"{ApiKeyOptions.SectionName}:SubmitPermitLimit", opts.SubmitPermitLimit);
                opts.SubmitWindowSeconds = configuration.GetValue($"{ApiKeyOptions.SectionName}:SubmitWindowSeconds", opts.SubmitWindowSeconds);
                opts.StatusPermitLimit = configuration.GetValue($"{ApiKeyOptions.SectionName}:StatusPermitLimit", opts.StatusPermitLimit);
                opts.StatusWindowSeconds = configuration.GetValue($"{ApiKeyOptions.SectionName}:StatusWindowSeconds", opts.StatusWindowSeconds);
                var fromEnv = Environment.GetEnvironmentVariable(ApiKeyOptions.KeysEnvironmentVariable);
                opts.SetEnvironmentKeys(string.IsNullOrWhiteSpace(fromEnv) ? [] :
                    fromEnv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            })
            .Validate(opts => opts.AllowUnauthenticatedForDevelopment || opts.Keys.Any(key => !string.IsNullOrWhiteSpace(key)),
                $"No API keys configured. Set {ApiKeyOptions.KeysEnvironmentVariable}, or set Api:AllowUnauthenticatedForDevelopment=true in Development.")
            .Validate(opts => opts.SubmitPermitLimit > 0, "Api:SubmitPermitLimit must be greater than 0.")
            .Validate(opts => opts.SubmitWindowSeconds > 0, "Api:SubmitWindowSeconds must be greater than 0.")
            .Validate(opts => opts.StatusPermitLimit > 0, "Api:StatusPermitLimit must be greater than 0.")
            .Validate(opts => opts.StatusWindowSeconds > 0, "Api:StatusWindowSeconds must be greater than 0.")
            .ValidateOnStart();
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(ApiKeyOptions.SubmitPolicy, httpContext =>
            {
                var api = httpContext.RequestServices.GetRequiredService<IOptions<ApiKeyOptions>>().Value;
                var partition = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = api.SubmitPermitLimit, Window = TimeSpan.FromSeconds(api.SubmitWindowSeconds), QueueLimit = 0,
                });
            });
            limiter.AddPolicy(ApiKeyOptions.StatusPolicy, httpContext =>
            {
                var api = httpContext.RequestServices.GetRequiredService<IOptions<ApiKeyOptions>>().Value;
                var partition = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = api.StatusPermitLimit, Window = TimeSpan.FromSeconds(api.StatusWindowSeconds), QueueLimit = 0,
                });
            });
        });
        return services;
    }
}
