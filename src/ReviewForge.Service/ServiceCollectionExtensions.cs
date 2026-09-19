using Microsoft.Extensions.Options;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Ado;
using ReviewForge.Infrastructure.Chat;
using ReviewForge.Infrastructure.Git;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Service.Queue;

namespace ReviewForge.Service;

/// <summary>DI wiring for the whole host — options validation at startup, fail fast on bad config.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddReviewForge(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ReviewQueue>();
        services.AddSingleton<RunTracker>();
        services.AddSingleton<InFlightClaims>();

        var ado = configuration.GetSection(AdoOptions.SectionName).Get<AdoOptions>()
                  ?? throw new InvalidOperationException($"configuration section '{AdoOptions.SectionName}' missing");
        ado.Pat = Environment.GetEnvironmentVariable(AdoOptions.PatEnvironmentVariable);
        services.AddSingleton(ado);
        services.AddSingleton<IPullRequestSource>(_ => new AdoPullRequestSource(ado));

        var reasoning = configuration.GetSection(ReasoningOptions.SectionName).Get<ReasoningOptions>()
                        ?? throw new InvalidOperationException($"configuration section '{ReasoningOptions.SectionName}' missing");
        services.AddSingleton(reasoning);
        services.AddSingleton<IChatClientFactory>(sp => new ChatClientFactory(sp.GetRequiredService<ReasoningOptions>()));

        services.Configure<ReviewForgeServiceOptions>(configuration.GetSection(ReviewForgeServiceOptions.SectionName));
        services.Configure<ApiDocsOptions>(configuration.GetSection(ApiDocsOptions.SectionName));
        services.AddSingleton<IGitOps, LibGit2SharpGitOps>();
        services.Configure<DiscoveryOptions>(configuration.GetSection(DiscoveryOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DiscoveryOptions>>().Value);
        services.AddSingleton<DiscoveryService>();

        services.AddSingleton<IFindingStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value;
            return new SqliteFindingStore(opts.StoreConnectionString);
        });

        services.AddSingleton(sp => new ReviewPipelineFactory(
            sp.GetRequiredService<IPullRequestSource>(),
            sp.GetRequiredService<IFindingStore>(),
            sp.GetRequiredService<IGitOps>(),
            sp.GetRequiredService<IChatClientFactory>(),
            sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>(),
            sp.GetRequiredService<ILoggerFactory>(),
            enricher: null,
            adoPat: ado.Pat,
            clock: sp.GetRequiredService<TimeProvider>()));

        services.AddHostedService<ReviewWorker>();
        services.AddHostedService<DiscoverySweepWorker>();

        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(ReviewForgeTelemetry.SourceName));

        return services;
    }
}