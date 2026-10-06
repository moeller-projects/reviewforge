using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.AutoFix.Fixers;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Reasoning;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Infrastructure.Ado;

namespace ReviewForge.Service;

public static partial class ServiceCollectionExtensions
{
    private static IServiceCollection AddReviewForgePipeline(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.GetValue<bool>(
                $"{ReviewOptions.SectionName}:Enrichment:{nameof(ReviewEnrichmentOptions.SymbolUsageEnabled)}"))
            services.AddSingleton<IContextEnricher, SymbolUsageEnricher>();

        services.AddSingleton<IFindingFixer>(_ => new HomoglyphIdentifierFixer("homoglyph/mixed-script-identifier"));
        services.AddSingleton<IFindingFixer>(_ => new HomoglyphIdentifierFixer("homoglyph/confusable-keyword"));
        services.AddSingleton<IFindingFixer, BashUnquotedVarsFixer>();
        services.AddSingleton<IFindingFixer, BashSetEMissingFixer>();
        services.AddSingleton<IFindingFixer, PythonMutableDefaultArgFixer>();
        services.AddSingleton<IFindingFixer, DockerAddToCopyFixer>();
        services.AddSingleton<IFindingFixer[]>(sp => sp.GetServices<IFindingFixer>().ToArray());

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DiscoveryOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PersistenceOptions>>().Value.Retention);
        services.AddSingleton<DiscoveryService>();
        var discovery = configuration.GetSection(DiscoveryOptions.SectionName).Get<DiscoveryOptions>() ?? new DiscoveryOptions();
        if (discovery.SweepInterval is not null && discovery.Creators.Length == 0 && !discovery.AllowAllCreators)
            throw new InvalidOperationException(
                "Discovery:Creators must be a non-empty allowlist when Discovery:SweepInterval is enabled " +
                "(or set Discovery:AllowAllCreators=true to accept PRs from any author explicitly).");

        services.AddSingleton(typeof(PushCredentials), sp =>
        {
            var ado = sp.GetRequiredService<IOptions<AdoOptions>>().Value;
            var autoFix = sp.GetRequiredService<AutoFixOptions>();
            return new PushCredentials(ado.Pat, autoFix.CommitAuthorName, autoFix.CommitAuthorEmail);
        });
        services.AddSingleton<AgentFactory>();
        services.AddSingleton<StageCatalog>(sp => new StageCatalog(
            sp.GetRequiredService<IPullRequestSource>(),
            sp.GetRequiredService<IFindingStore>(),
            sp.GetRequiredService<RepoCheckoutPool>(),
            sp.GetRequiredService<IChatClientFactory>(),
            sp.GetService<IContextEnricher>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IOptions<ReviewOptions>>(),
            sp.GetRequiredService<IOptions<ResolveOptions>>(),
            sp.GetRequiredService<IOptions<WorkspaceOptions>>(),
            sp.GetRequiredService<IOptions<AutoFixOptions>>(),
            sp.GetRequiredService<IOptions<VerifyFindingsOptions>>(),
            sp.GetServices<IFindingFixer>(),
            sp.GetRequiredService<IGitOps>(),
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<PushCredentials>()));
        services.AddSingleton<ResolveContextInitializer>();
        services.AddSingleton<IPipelineBuilder, ReviewPipelineBuilder>();

        services.AddHostedService<WorkspaceStartupTask>();
        services.AddHostedService<ReviewWorker>();
        services.AddHostedService<DiscoverySweepWorker>();
        services.AddHostedService<CheckoutEvictionWorker>();
        services.AddHostedService<TelemetryGaugeRegistration>();
        services.AddHostedService<ShellReaperService>();
        return services;
    }
}
