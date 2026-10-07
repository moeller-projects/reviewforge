namespace ReviewForge.Service;

/// <summary>DI wiring for the whole host — options validation at startup, fail fast on bad config.</summary>
public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddReviewForge(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddReviewForgeOptions(configuration);
        services.AddReviewForgeAdapters();
        services.AddReviewForgePipeline(configuration);
        services.AddReviewForgeSecurity(configuration);
        services.AddReviewForgeMcp();
        services.AddReviewForgeTelemetry(configuration);
        return services;
    }
}