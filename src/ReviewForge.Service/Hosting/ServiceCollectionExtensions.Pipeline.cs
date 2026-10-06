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

        services.AddSingleton(sp => new ReviewPipelineFactory(
            sp.GetRequiredService<IPullRequestSource>(), sp.GetRequiredService<IFindingStore>(),
            sp.GetRequiredService<RepoCheckoutPool>(), sp.GetRequiredService<IChatClientFactory>(),
            sp.GetRequiredService<IOptions<ReviewOptions>>(), sp.GetRequiredService<IOptions<WorkspaceOptions>>(),
            sp.GetRequiredService<ILoggerFactory>(),
            enricher: sp.GetService<IContextEnricher>(), clock: sp.GetRequiredService<TimeProvider>(),
            findingFixers: sp.GetRequiredService<IFindingFixer[]>(),
            autoFixOptions: sp.GetRequiredService<IOptions<AutoFixOptions>>(),
            verifyFindingsOptions: sp.GetRequiredService<IOptions<VerifyFindingsOptions>>(),
            gitOps: sp.GetRequiredService<IGitOps>(), pushPat: sp.GetRequiredService<IOptions<AdoOptions>>().Value.Pat));
        if (configuration.GetValue<bool>($"{ResolveOptions.SectionName}:{nameof(ResolveOptions.Enabled)}"))
            services.AddSingleton<IResolveRunService>(sp => new ResolveRunService(
                sp.GetRequiredService<IPullRequestSource>(), sp.GetRequiredService<IFindingStore>(),
                sp.GetRequiredService<RepoCheckoutPool>(), sp.GetRequiredService<IChatClientFactory>(),
                sp.GetRequiredService<IGitOps>(), sp.GetRequiredService<IProcessRunner>(),
                sp.GetRequiredService<AutoFixOptions>(), sp.GetRequiredService<IOptions<ResolveOptions>>(),
                sp.GetRequiredService<IOptions<ReviewOptions>>(),
                sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<AdoOptions>>().Value.Pat));

        services.AddHostedService<WorkspaceStartupTask>();
        services.AddHostedService<ReviewWorker>();
        services.AddHostedService<DiscoverySweepWorker>();
        services.AddHostedService<CheckoutEvictionWorker>();
        services.AddHostedService<TelemetryGaugeRegistration>();
        services.AddHostedService<ShellReaperService>();
        return services;
    }
}
