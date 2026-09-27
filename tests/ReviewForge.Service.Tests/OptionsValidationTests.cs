using ReviewForge.Service.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Ado;
using ReviewForge.Infrastructure.AutoFix;
using ReviewForge.Infrastructure.Chat;
using Xunit;

namespace ReviewForge.Service.Tests;

public class OptionsValidationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] config)
    {
        var services = new ServiceCollection();
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
        ("ReviewForge:WorkDir", Path.Combine(Path.GetTempPath(), "reviewforge-optval-" + Guid.NewGuid().ToString("N"))),
    ];

    private static (string, string)[] With(ICollection<(string, string)> baseConfig, params (string Key, string Value)[] overrides)
        => [.. baseConfig.Where(b => overrides.All(o => o.Key != b.Item1)), .. overrides];

    [Fact]
    public void Valid_config_resolves_all_options()
    {
        using var provider = Build([.. ValidConfig()]);

        Assert.NotNull(provider.GetRequiredService<IOptions<AdoOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<ChatProviderOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<DiscoveryOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<IOptions<ApiDocsOptions>>().Value);
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
        using var provider = Build([.. With(ValidConfig(), ("ReviewForge:WorkerCount", "0"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value);

        Assert.Contains("WorkerCount must be between 1 and 64", ex.Message);
    }

    [Fact]
    public void Store_journal_mode_outside_wal_delete_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("ReviewForge:Store:JournalMode", "Truncate"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value);

        Assert.Contains("JournalMode must be Wal | Delete", ex.Message);
    }

    [Theory]
    [InlineData("ReviewForge:Store:JournalMode", "2")] // Enum.TryParse accepts undefined numerics —
    public void Undefined_numeric_enum_values_are_rejected_at_startup(string key, string value)
    {
        using var provider = Build([.. With(ValidConfig(), (key, value))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value);

        Assert.Contains(key.Split(':')[1], ex.Message);
    }

    [Fact]
    public void Undefined_numeric_queue_mode_is_rejected_at_compose_time()
    {
        // The queue backing is selected while the service collection is composed, before
        // ValidateOnStart runs — the compose-time check is what stops "2" from silently
        // selecting the memory queue.
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(With(ValidConfig(), ("ReviewForge:QueueMode", "2"))
                .ToDictionary(c => c.Item1, c => (string?)c.Item2))
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddReviewForge(config));
        Assert.Contains("QueueMode must be one of", ex.Message);
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
        using var provider = Build([.. With(ValidConfig(), ("ReviewForge:WorkerCount", "3"))]);

        var governor = provider.GetRequiredService<LlmGovernor>();

        Assert.Equal(6, governor.MaxConcurrency); // WorkerCount × 2
    }

    [Fact]
    public void Store_journal_mode_accepts_case_insensitive_delete()
    {
        using var provider = Build([.. With(ValidConfig(), ("ReviewForge:Store:JournalMode", "DELETE"))]);

        Assert.Equal("DELETE", provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value.Store.JournalMode);
    }

    [Fact]
    public void Invalid_queue_mode_is_rejected_at_compose_time()
    {
        // The queue backing is selected while the service collection is composed, so an
        // invalid QueueMode must fail there — never fall through to the memory queue.
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(With(ValidConfig(), ("ReviewForge:QueueMode", "Bogus"))
                .ToDictionary(c => c.Item1, c => (string?)c.Item2))
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddReviewForge(config));
        Assert.Contains("QueueMode must be one of", ex.Message);
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
        using var provider = Build([.. With(ValidConfig(), ("ReviewForge:CleanRunVote", "Bogus"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value);

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
        using var provider = Build([.. With(ValidConfig(), ("ReviewForge:CleanRunVote", value))]);

        Assert.Equal(value, provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value.CleanRunVote);
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
    public void AutoFix_defaults_resolve_and_use_the_null_verifier()
    {
        using var provider = Build([.. ValidConfig()]);

        var options = provider.GetRequiredService<IOptions<AutoFixOptions>>().Value;
        Assert.False(options.Enabled);
        Assert.Empty(options.AllowedAuthors);
        Assert.Equal("Suggestion", options.PublishMode);
        var verifier = provider.GetRequiredService<IFixVerifier>();
        Assert.Same(NullFixVerifier.Instance, verifier);
        Assert.False(verifier.RequiresWorkspaceWrites);
    }

    [Fact]
    public void AutoFix_enabled_without_allowed_authors_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("AutoFix:Enabled", "true"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AutoFixOptions>>().Value);

        Assert.Contains("AllowedAuthors must be non-empty", ex.Message);
    }

    [Theory]
    [InlineData("CommitOnHead")]
    [InlineData("StackedBranch")]
    public void AutoFix_reserved_publish_modes_are_rejected(string mode)
    {
        using var provider = Build([.. With(ValidConfig(), ("AutoFix:PublishMode", mode))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AutoFixOptions>>().Value);

        Assert.Contains("only supports 'Suggestion'", ex.Message);
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
    public void AutoFix_whitespace_verification_command_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("AutoFix:VerificationCommand", "   "))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AutoFixOptions>>().Value);

        Assert.Contains("VerificationCommand must not be whitespace", ex.Message);
    }

    [Fact]
    public void AutoFix_verification_command_with_metacharacters_fails_at_verifier_construction()
    {
        using var provider = Build([.. With(ValidConfig(), ("AutoFix:VerificationCommand", "make verify && echo hi"))]);

        var ex = Assert.Throws<ArgumentException>(
            () => provider.GetRequiredService<IFixVerifier>());

        Assert.Contains("metacharacter", ex.Message);
    }

    [Fact]
    public void AutoFix_verification_command_builds_the_process_verifier()
    {
        using var provider = Build([.. With(ValidConfig(), ("AutoFix:VerificationCommand", "make verify"))]);

        var verifier = provider.GetRequiredService<IFixVerifier>();
        Assert.IsType<ProcessFixVerifier>(verifier);
        Assert.True(verifier.RequiresWorkspaceWrites);
    }

    [Fact]
    public void Sharding_defaults_resolve_disabled()
    {
        using var provider = Build([.. ValidConfig()]);

        var options = provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value;

        Assert.False(options.Sharding.Enabled);
        Assert.Equal(30_000, options.Sharding.ShardMaxChars);
        Assert.Equal(8, options.Sharding.MaxShards);
        Assert.Equal(2, options.Sharding.ShardConcurrency);
    }

    [Fact]
    public void Sharding_concurrency_above_max_shards_is_rejected()
    {
        using var provider = Build(
        [
            .. With(ValidConfig(),
                ("ReviewForge:Sharding:Enabled", "true"),
                ("ReviewForge:Sharding:MaxShards", "2"),
                ("ReviewForge:Sharding:ShardConcurrency", "3")),
        ]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value);

        Assert.Contains("ShardConcurrency must be between 1 and MaxShards", ex.Message);
    }

    [Theory]
    [InlineData("ShardMaxChars", "999")]
    [InlineData("MaxShards", "1")]
    [InlineData("MaxShards", "33")]
    public void Sharding_out_of_range_values_are_rejected(string key, string value)
    {
        using var provider = Build(
        [
            .. With(ValidConfig(),
                ("ReviewForge:Sharding:Enabled", "true"),
                ($"ReviewForge:Sharding:{key}", value)),
        ]);

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value);
    }
}
