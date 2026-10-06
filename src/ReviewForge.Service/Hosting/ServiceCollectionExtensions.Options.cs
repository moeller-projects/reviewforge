using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
using ReviewForge.Infrastructure.Chat;
using ReviewForge.Service.Queue;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Reasoning;
using ReviewForge.Core.Workspaces;
using ReviewForge.Infrastructure.Ado;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Service.Security;

namespace ReviewForge.Service;

public static partial class ServiceCollectionExtensions
{
    private static IServiceCollection AddReviewForgeOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RunTracker>();
        services.AddSingleton<InFlightClaims>();

        services.AddOptions<AdoOptions>()
            .Bind(configuration.GetSection(AdoOptions.SectionName))
            .PostConfigure(o => o.Pat = Environment.GetEnvironmentVariable(AdoOptions.PatEnvironmentVariable))
            .ValidateDataAnnotations()
            .Validate(o => o.OrgUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true,
                "Ado:OrgUrl must be an https:// URL — the PAT is sent to this endpoint.")
            .ValidateOnStart();

        AddValidatedOptions<ChatProviderOptions>(services, configuration, ChatProviderOptions.SectionName);
        AddValidatedOptions<WorkspaceOptions>(services, configuration, WorkspaceOptions.SectionName);
        services.AddOptions<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => Enum.TryParse<StoreJournalMode>(o.JournalMode, true, out var jm) && Enum.IsDefined(jm),
                "Persistence:JournalMode must be Wal | Delete")
            .Validate(o => Enum.TryParse<QueueMode>(o.QueueMode, true, out var qm) && Enum.IsDefined(qm),
                "Persistence:QueueMode must be Memory | Sqlite")
            .Validate(o => o.Retention.Days >= 1, "Persistence:Retention:Days must be at least 1")
            .Validate(o => o.Retention.MinRunsPerPr >= 1, "Persistence:Retention:MinRunsPerPr must be at least 1")
            .ValidateOnStart();
        services.AddOptions<ReviewOptions>()
            .Bind(configuration.GetSection(ReviewOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => ReviewOptions.IsValidCleanRunVote(o.CleanRunVote),
                "Review:CleanRunVote must be NoResponse | Approved | ApprovedWithSuggestions | None")
            .Validate(o => o.Sharding.ShardMaxChars >= 1_000, "Review:Sharding:ShardMaxChars must be at least 1000")
            .Validate(o => o.Sharding.MaxShards is >= 2 and <= 32, "Review:Sharding:MaxShards must be between 2 and 32")
            .Validate(o => o.Sharding.ShardConcurrency >= 1 && o.Sharding.ShardConcurrency <= o.Sharding.MaxShards,
                "Review:Sharding:ShardConcurrency must be between 1 and MaxShards")
            .ValidateOnStart();
        AddValidatedOptions<GitOptions>(services, configuration, GitOptions.SectionName);
        services.AddOptions<HostOptions>()
            .Bind(configuration.GetSection(HostOptions.SectionName))
            .Validate(o => o.WorkerCount is >= 1 and <= 64, "Host:WorkerCount must be between 1 and 64")
            .Validate(o => o.StaleShellMinutes > 0, "Host:StaleShellMinutes must be greater than 0")
            .ValidateOnStart();
        services.AddOptions<ResolveOptions>()
            .Bind(configuration.GetSection(ResolveOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => !o.Enabled || o.AllowedAuthors.Length > 0,
                "Resolve:AllowedAuthors must be non-empty when Resolve:Enabled is true")
            .Validate(o => o.CommitGranularity is "PerThread" or "Single",
                "Resolve:CommitGranularity must be PerThread or Single")
            .Validate(o => o.VerifyCommand is null || (o.VerifyCommand.Length > 0 && !string.IsNullOrWhiteSpace(o.VerifyCommand[0])
                && o.VerifyCommand.All(arg => arg is not null && !arg.Any(c => c is ';' or '|' or '&' or '>' or '<' or '$' or '`'))),
                "Resolve:VerifyCommand must be an argv array without shell metacharacters")
            .Validate(o => !o.Enabled || (!string.IsNullOrWhiteSpace(configuration[$"{AutoFixOptions.SectionName}:CommitAuthorName"])
                                           && !string.IsNullOrWhiteSpace(configuration[$"{AutoFixOptions.SectionName}:CommitAuthorEmail"])),
                "Resolve requires AutoFix:CommitAuthorName and AutoFix:CommitAuthorEmail")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ResolveOptions>>().Value);
        AddValidatedOptions<RepoReadToolsOptions>(services, configuration, RepoReadToolsOptions.SectionName);
        services.AddOptions<AutoFixOptions>()
            .Bind(configuration.GetSection(AutoFixOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => !o.Enabled || o.AllowedAuthors.Length > 0,
                "AutoFix:AllowedAuthors must be non-empty when AutoFix:Enabled is true")
            .Validate(o => o.PublishMode is AutoFixOptions.ModeSuggestion or AutoFixOptions.ModeCommitOnHead,
                "AutoFix:PublishMode must be 'Suggestion' or 'CommitOnHead' (StackedBranch remains reserved)")
            .Validate(o => !o.IsCommitOnHead || (!string.IsNullOrWhiteSpace(o.CommitAuthorName) && !string.IsNullOrWhiteSpace(o.CommitAuthorEmail)),
                "AutoFix:CommitAuthorName and AutoFix:CommitAuthorEmail are required when AutoFix:PublishMode is CommitOnHead")
            .Validate(o => o.CommitGranularity is AutoFixOptions.GranularityPerFix or AutoFixOptions.GranularitySingle,
                "AutoFix:CommitGranularity must be 'PerFix' or 'Single'")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AutoFixOptions>>().Value);
        AddValidatedOptions<VerifyFindingsOptions>(services, configuration, VerifyFindingsOptions.SectionName);
        AddValidatedOptions<EnrichmentOptions>(services, configuration, EnrichmentOptions.SectionName);
        AddValidatedOptions<ApiDocsOptions>(services, configuration, ApiDocsOptions.SectionName);
        services.AddOptions<DiscoveryOptions>()
            .Bind(configuration.GetSection(DiscoveryOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.MaxEnqueuesPerSweep >= 1, "Discovery:MaxEnqueuesPerSweep must be at least 1")
            .Validate(o => o.MaxDegreeOfParallelism is >= 1 and <= 16, "Discovery:MaxDegreeOfParallelism must be between 1 and 16")
            .Validate(o => o.WarmupMaxPerSweep >= 1, "Discovery:WarmupMaxPerSweep must be at least 1")
            .Validate(o => o.WarmupConcurrency is >= 1 and <= 8, "Discovery:WarmupConcurrency must be between 1 and 8")
            .Validate(o => o.FailureBackoffBase > TimeSpan.Zero, "Discovery:FailureBackoffBase must be greater than 0")
            .Validate(o => o.FailureBackoffMax >= o.FailureBackoffBase, "Discovery:FailureBackoffMax must be at least FailureBackoffBase")
            .ValidateOnStart();
        return services;
    }
}
