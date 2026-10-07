using Microsoft.Extensions.Options;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Infrastructure.Ado;
using ReviewForge.Infrastructure.Chat;
using ReviewForge.Infrastructure.Filesystem;
using ReviewForge.Infrastructure.Git;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Infrastructure.Process;
using ReviewForge.Service.Queue;

namespace ReviewForge.Service;

public static partial class ServiceCollectionExtensions
{
    private static IServiceCollection AddReviewForgeAdapters(this IServiceCollection services)
    {
        services.AddSingleton<ReviewQueue>();
        services.AddSingleton<IReviewQueue>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            return ParseDefinedEnum<QueueMode>(opts.QueueMode, "Persistence:QueueMode") switch
            {
                QueueMode.Sqlite => new SqliteReviewQueue(opts.StoreConnectionString,
                    journalMode: ParseDefinedEnum<StoreJournalMode>(opts.JournalMode, "Persistence:JournalMode")),
                QueueMode.Memory => sp.GetRequiredService<ReviewQueue>(),
                _ => throw new InvalidOperationException("Unsupported Persistence:QueueMode."),
            };
        });
        services.AddSingleton<IPullRequestSource>(sp => new InstrumentedPullRequestSource(
            new AdoPullRequestSource(sp.GetRequiredService<IOptions<AdoOptions>>().Value)));
        services.AddSingleton(sp =>
            new LlmGovernor(sp.GetRequiredService<IOptions<ChatProviderOptions>>().Value.MaxConcurrentRequests));
        services.AddSingleton<IChatClientFactory>(sp => new ChatClientFactory(
            sp.GetRequiredService<IOptions<ChatProviderOptions>>().Value,
            governor: sp.GetRequiredService<LlmGovernor>()));
        services.AddSingleton(sp => new GitOperationScheduler(
            sp.GetRequiredService<IOptions<GitOptions>>().Value.MaxConcurrency));
        services.AddSingleton<IGitOps>(sp => new LibGit2SharpGitOps(
            sp.GetRequiredService<IOptions<GitOptions>>().Value.TargetedFetchEnabled,
            sp.GetRequiredService<GitOperationScheduler>(),
            credentialHost: new Uri(sp.GetRequiredService<IOptions<AdoOptions>>().Value.OrgUrl).Host));
        services.AddSingleton(sp => (IDisposable) sp.GetRequiredService<GitOperationScheduler>());
        services.AddSingleton<IWorkspaceFs, FileSystemWorkspaceFs>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<WorkspaceOptions>>().Value;
            return new RepoCheckoutPool(sp.GetRequiredService<IGitOps>(), sp.GetRequiredService<IWorkspaceFs>(), opts.WorkDir,
                sp.GetRequiredService<IOptions<AdoOptions>>().Value.Pat,
                logger: sp.GetRequiredService<ILogger<RepoCheckoutPool>>());
        });
        services.AddSingleton<IFindingStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            return new SqliteFindingStore(opts.StoreConnectionString,
                ParseDefinedEnum<StoreJournalMode>(opts.JournalMode, "Persistence:JournalMode"));
        });
        return services;
    }
}