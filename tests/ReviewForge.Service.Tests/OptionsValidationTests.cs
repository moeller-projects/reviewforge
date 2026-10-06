using ReviewForge.Service.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;
using ReviewForge.Infrastructure.Ado;
using ReviewForge.Infrastructure.Chat;
using Xunit;

namespace ReviewForge.Service.Tests;

public class OptionsValidationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReviewForge(new ConfigurationBuilder()
            .AddInMemoryCollection(config.ToDictionary(c => c.Key, c => (string?)c.Value))
            .Build());
        return services.BuildServiceProvider();
    }

    private static (string, string)[] ValidConfig() =>
    [
        ("Ado:OrgUrl", "https://dev.azure.com/your-org"),
        ("Ado:Project", "Your.Project"),
        ("Reasoning:Provider", "openai"),
        ("Reasoning:Model", "gpt-5"),
        ("Workspace:WorkDir", Path.Combine(Path.GetTempPath(), "reviewforge-optval-" + Guid.NewGuid().ToString("N"))),
    ];

    private static (string, string)[] With(ICollection<(string, string)> baseConfig, params (string Key, string Value)[] overrides)
        => [.. baseConfig.Where(b => overrides.All(o => o.Key != b.Item1)), .. overrides];

    [Fact]
    public void Valid_config_resolves_all_options()
    {
        using var provider = Build([.. ValidConfig()]);

        Assert.NotNull(provider.GetRequiredService<IOptions<AdoOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<ChatProviderOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<WorkspaceOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<PersistenceOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<ReviewOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<GitOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<HostOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<DiscoveryOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<ApiDocsOptions>>().Value);
    }

    [Fact]
    public void Computed_concurrency_defaults_are_resolved_in_options()
    {
        using var provider = Build([.. ValidConfig()]);

        var host = provider.GetRequiredService<IOptions<HostOptions>>().Value;
        var git = provider.GetRequiredService<IOptions<GitOptions>>().Value;
        var chat = provider.GetRequiredService<IOptions<ChatProviderOptions>>().Value;

        Assert.Equal(Math.Clamp(Environment.ProcessorCount / 2, 2, 8), host.WorkerCount);
        Assert.Equal(Math.Clamp(Environment.ProcessorCount / 2, 2, 4), git.MaxConcurrency);
        Assert.Equal(host.WorkerCount * 2, chat.MaxConcurrentRequests);
    }

    [Fact]
    public void Review_options_bind_grep_budgets_and_symbol_usage()
    {
        using var provider = Build([.. With(ValidConfig(),
            ("Review:GrepMaxMs", "1234"),
            ("Review:GrepMaxLines", "5678"),
            ("Review:Enrichment:SymbolUsageEnabled", "true"))]);

        var options = provider.GetRequiredService<IOptions<ReviewOptions>>().Value;

        Assert.Equal(1234, options.GrepMaxMs);
        Assert.Equal(5678, options.GrepMaxLines);
        Assert.True(options.Enrichment.SymbolUsageEnabled);
        Assert.IsType<SymbolUsageEnricher>(provider.GetRequiredService<IContextEnricher>());
    }

    [Fact]
    public void Legacy_review_configuration_warns_and_does_not_override_new_defaults()
    {
        var messages = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new CollectingLoggerProvider(messages)));
        services.AddReviewForge(new ConfigurationBuilder()
            .AddInMemoryCollection(With(ValidConfig(),
                ("RepoReadTools:GrepMaxMs", "1"),
                ("RepoReadTools:GrepMaxLines", "2"),
                ("Review:Sharding:Enabled", "true"))
                .ToDictionary(c => c.Item1, c => (string?)c.Item2))
            .Build());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ReviewOptions>>().Value;

        Assert.Equal(10_000, options.GrepMaxMs);
        Assert.Equal(200_000, options.GrepMaxLines);
        Assert.Contains(messages, message => message.Contains("RepoReadTools grep settings are ignored", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("Review:Sharding configuration is obsolete", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_reviewforge_section_is_not_an_alias()
    {
        using var provider = Build([.. With(ValidConfig(), ("ReviewForge:CleanRunVote", "Approved"))]);

        Assert.Equal("NoResponse", provider.GetRequiredService<IOptions<ReviewOptions>>().Value.CleanRunVote);
    }

    [Fact]
    public void Workspace_section_binds_work_directory()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "reviewforge-workspace-" + Guid.NewGuid().ToString("N"));
        using var provider = Build([.. With(ValidConfig(), ("Workspace:WorkDir", workDir))]);

        Assert.Equal(workDir, provider.GetRequiredService<IOptions<WorkspaceOptions>>().Value.WorkDir);
    }

    [Fact]
    public void Pull_request_source_is_registered_as_singleton()
    {
        var previousPat = Environment.GetEnvironmentVariable("REVIEWFORGE_ADO_PAT");
        Environment.SetEnvironmentVariable("REVIEWFORGE_ADO_PAT", "test-pat");
        try
        {
            using var provider = Build([.. ValidConfig()]);

            var first = provider.GetRequiredService<IPullRequestSource>();
            var second = provider.GetRequiredService<IPullRequestSource>();

            // Pins the ADO pooling contract: one VssConnection (and its cached typed
            // clients) per process — see the remark on AdoPullRequestSource.
            Assert.Same(first, second);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REVIEWFORGE_ADO_PAT", previousPat);
        }
    }

    [Fact]
    public void Ado_org_url_over_http_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("Ado:OrgUrl", "http://dev.azure.com/x"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AdoOptions>>().Value);

        Assert.Contains("Ado:OrgUrl must be an https:// URL", ex.Message);
    }

    [Fact]
    public void Ado_org_url_non_url_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("Ado:OrgUrl", "not-a-url"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AdoOptions>>().Value);

        Assert.Contains("not a valid fully-qualified http, https", ex.Message);
    }

    [Fact]
    public void Chat_provider_is_restricted_to_openai_providers()
    {
        using var provider = Build([.. With(ValidConfig(), ("Reasoning:Provider", "bogus"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ChatProviderOptions>>().Value);

        Assert.Contains("Provider", ex.Message);
    }

    [Fact]
    public void Worker_count_below_one_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("Host:WorkerCount", "0"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<HostOptions>>().Value);

        Assert.Contains("WorkerCount must be between 1 and 64", ex.Message);
    }

    [Fact]
    public void Store_journal_mode_outside_wal_delete_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("Persistence:JournalMode", "Truncate"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<PersistenceOptions>>().Value);

        Assert.Contains("JournalMode must be Wal | Delete", ex.Message);
    }

    [Theory]
    [InlineData("Persistence:JournalMode", "2")] // Enum.TryParse accepts undefined numerics —
    [InlineData("Persistence:QueueMode", "2")]
    public void Undefined_numeric_enum_values_are_rejected_at_startup(string key, string value)
    {
        using var provider = Build([.. With(ValidConfig(), (key, value))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<PersistenceOptions>>().Value);

        Assert.Contains(key.Split(':')[1], ex.Message);
    }


    [Fact]
    public void Followup_model_with_mismatched_provider_prefix_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(),
            ("Reasoning:Model", "openai-codex:gpt-5.6-luna"),
            ("Reasoning:FollowUpModel", "openai:gpt-5-mini"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ChatProviderOptions>>().Value);

        Assert.Contains("FollowUpModel", ex.Message);
    }

    [Fact]
    public void Followup_model_with_matching_prefix_validates()
    {
        using var provider = Build([.. With(ValidConfig(),
            ("Reasoning:Model", "openai-codex:gpt-5.6-luna"),
            ("Reasoning:FollowUpModel", "openai-codex:gpt-5.6-luna-mini"))]);

        Assert.Equal("openai-codex:gpt-5.6-luna-mini",
            provider.GetRequiredService<IOptions<ChatProviderOptions>>().Value.FollowUpModel);
    }

    [Fact]
    public void Absent_followup_model_validates()
    {
        // Regression: the prefix cross-check used to NRE when FollowUpModel was absent —
        // the unset case is the byte-identical default and must pass validation cleanly.
        using var provider = Build(ValidConfig());

        var options = provider.GetRequiredService<IOptions<ChatProviderOptions>>().Value;
        Assert.Null(options.FollowUpModel);
    }

    [Fact]
    public void Governor_acquire_timeout_below_one_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("Reasoning:GovernorAcquireTimeoutSeconds", "0"))]);

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ChatProviderOptions>>().Value);
    }

    [Fact]
    public void Governor_max_concurrent_requests_below_one_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("Reasoning:MaxConcurrentRequests", "0"))]);

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ChatProviderOptions>>().Value);
    }

    [Fact]
    public void Governor_resolves_default_cap_from_worker_count()
    {
        using var provider = Build([.. With(ValidConfig(), ("Host:WorkerCount", "3"))]);

        var options = provider.GetRequiredService<IOptions<ChatProviderOptions>>().Value;
        Assert.Equal(6, options.MaxConcurrentRequests);
        var governor = provider.GetRequiredService<LlmGovernor>();

        Assert.Equal(options.MaxConcurrentRequests, governor.MaxConcurrency);
    }

    [Fact]
    public void Git_section_binds_concurrency_and_targeted_fetch()
    {
        using var provider = Build([.. With(
            ValidConfig(),
            ("Git:MaxConcurrency", "3"),
            ("Git:TargetedFetchEnabled", "true"))]);

        var options = provider.GetRequiredService<IOptions<GitOptions>>().Value;
        Assert.Equal(3, options.MaxConcurrency);
        Assert.True(options.TargetedFetchEnabled);
    }

    [Fact]
    public void Store_journal_mode_accepts_case_insensitive_delete()
    {
        using var provider = Build([.. With(ValidConfig(), ("Persistence:JournalMode", "DELETE"))]);

        Assert.Equal("DELETE", provider.GetRequiredService<IOptions<PersistenceOptions>>().Value.JournalMode);
    }

    [Fact]
    public void Invalid_queue_mode_is_rejected_at_options_validation()
    {
        using var provider = Build([.. With(ValidConfig(), ("Persistence:QueueMode", "Bogus"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<PersistenceOptions>>().Value);
        Assert.Contains("QueueMode must be Memory | Sqlite", ex.Message);
    }

    [Fact]
    public void Discovery_max_enqueues_below_one_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("Discovery:MaxEnqueuesPerSweep", "0"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DiscoveryOptions>>().Value);

        Assert.Contains("MaxEnqueuesPerSweep must be at least 1", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("17")]
    public void Discovery_parallelism_outside_1_to_16_is_rejected(string value)
    {
        using var provider = Build([.. With(ValidConfig(), ("Discovery:MaxDegreeOfParallelism", value))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DiscoveryOptions>>().Value);

        Assert.Contains("MaxDegreeOfParallelism must be between 1 and 16", ex.Message);
    }

    [Fact]
    public void Discovery_warmup_max_per_sweep_below_one_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("Discovery:WarmupMaxPerSweep", "0"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DiscoveryOptions>>().Value);

        Assert.Contains("WarmupMaxPerSweep must be at least 1", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    public void Discovery_warmup_concurrency_outside_1_to_8_is_rejected(string value)
    {
        using var provider = Build([.. With(ValidConfig(), ("Discovery:WarmupConcurrency", value))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DiscoveryOptions>>().Value);

        Assert.Contains("WarmupConcurrency must be between 1 and 8", ex.Message);
    }

    [Fact]
    public void Invalid_clean_run_vote_is_rejected_at_options_validation()
    {
        using var provider = Build([.. With(ValidConfig(), ("Review:CleanRunVote", "Bogus"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ReviewOptions>>().Value);

        Assert.Contains("CleanRunVote", ex.Message);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("NoResponse")]
    [InlineData("Approved")]
    [InlineData("ApprovedWithSuggestions")]
    [InlineData("approved")]
    public void Valid_clean_run_votes_pass_options_validation(string value)
    {
        using var provider = Build([.. With(ValidConfig(), ("Review:CleanRunVote", value))]);

        Assert.Equal(value, provider.GetRequiredService<IOptions<ReviewOptions>>().Value.CleanRunVote);
    }

    [Fact]
    public void Api_keys_in_serialized_configuration_are_ignored()
    {
        var previous = Environment.GetEnvironmentVariable(ApiKeyOptions.KeysEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApiKeyOptions.KeysEnvironmentVariable, null);
        try
        {
            using var provider = Build(
                [.. ValidConfig(), ("Api:Keys:0", "config-secret")]);

            var ex = Assert.Throws<OptionsValidationException>(
                () => provider.GetRequiredService<IOptions<ApiKeyOptions>>().Value);
            Assert.Contains(ApiKeyOptions.KeysEnvironmentVariable, ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApiKeyOptions.KeysEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void AutoFix_defaults_resolve()
    {
        using var provider = Build([.. ValidConfig()]);

        var options = provider.GetRequiredService<IOptions<AutoFixOptions>>().Value;
        Assert.False(options.Enabled);
        Assert.Empty(options.AllowedAuthors);
        Assert.Equal("Suggestion", options.PublishMode);
    }

    [Fact]
    public void AutoFix_enabled_without_allowed_authors_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("AutoFix:Enabled", "true"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AutoFixOptions>>().Value);

        Assert.Contains("AllowedAuthors must be non-empty", ex.Message);
    }

    [Fact]
    public void AutoFix_reserved_publish_mode_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("AutoFix:PublishMode", "StackedBranch"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AutoFixOptions>>().Value);

        Assert.Contains("Suggestion' or 'CommitOnHead", ex.Message);
    }

    [Fact]
    public void AutoFix_commit_on_head_requires_commit_identity_when_enabled()
    {
        using var provider = Build([.. With(
            ValidConfig(),
            ("AutoFix:Enabled", "true"),
            ("AutoFix:AllowedAuthors:0", "creator-1"),
            ("AutoFix:PublishMode", "CommitOnHead"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AutoFixOptions>>().Value);

        Assert.Contains("AutoFix:CommitAuthorName and AutoFix:CommitAuthorEmail are required", ex.Message);
    }

    [Fact]
    public void AutoFix_commit_on_head_with_identity_is_valid()
    {
        using var provider = Build([.. With(
            ValidConfig(),
            ("AutoFix:Enabled", "true"),
            ("AutoFix:AllowedAuthors:0", "creator-1"),
            ("AutoFix:PublishMode", "CommitOnHead"),
            ("AutoFix:CommitAuthorName", "reviewforge[bot]"),
            ("AutoFix:CommitAuthorEmail", "reviewforge@example.com"))]);

        var options = provider.GetRequiredService<IOptions<AutoFixOptions>>().Value;

        Assert.True(options.IsCommitOnHead);
    }

    [Fact]
    public void AutoFix_disabled_commit_on_head_is_inert_without_identity()
    {
        using var provider = Build([.. With(
            ValidConfig(),
            ("AutoFix:PublishMode", "CommitOnHead"))]);

        var options = provider.GetRequiredService<IOptions<AutoFixOptions>>().Value;

        Assert.False(options.Enabled);
        Assert.False(options.IsCommitOnHead);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("Stacked")]
    public void AutoFix_unknown_commit_granularity_is_rejected(string granularity)
    {
        using var provider = Build([.. With(ValidConfig(), ("AutoFix:CommitGranularity", granularity))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AutoFixOptions>>().Value);

        Assert.Contains("CommitGranularity must be 'PerFix' or 'Single'", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    public void AutoFix_max_fixes_out_of_range_is_rejected(string value)
    {
        using var provider = Build([.. With(ValidConfig(), ("AutoFix:MaxFixesPerRun", value))]);

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AutoFixOptions>>().Value);
    }

    [Fact]
    public void Resolve_enabled_without_allowed_authors_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("Resolve:Enabled", "true"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ResolveOptions>>().Value);

        Assert.Contains("Resolve:AllowedAuthors must be non-empty", ex.Message);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("2")]
    public void Resolve_commit_granularity_must_be_per_thread_or_single(string value)
    {
        using var provider = Build([.. With(ValidConfig(), ("Resolve:CommitGranularity", value))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ResolveOptions>>().Value);

        Assert.Contains("Resolve:CommitGranularity", ex.Message);
    }

    [Theory]
    [InlineData("echo", "ok;bad")]
    [InlineData("echo", "$(bad)")]
    public void Resolve_verify_command_rejects_shell_metacharacters(string executable, string argument)
    {
        using var provider = Build([.. With(ValidConfig(),
            ("Resolve:VerifyCommand:0", executable), ("Resolve:VerifyCommand:1", argument))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ResolveOptions>>().Value);

        Assert.Contains("Resolve:VerifyCommand", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    public void Resolve_max_threads_out_of_range_is_rejected(string value)
    {
        using var provider = Build([.. With(ValidConfig(), ("Resolve:MaxThreadsPerRun", value))]);

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ResolveOptions>>().Value);
    }

}
