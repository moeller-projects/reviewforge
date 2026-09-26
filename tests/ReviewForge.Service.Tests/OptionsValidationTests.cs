using ReviewForge.Service.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
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

        Assert.Contains("WorkerCount must be at least 1", ex.Message);
    }

    [Fact]
    public void Discovery_max_enqueues_below_one_is_rejected()
    {
        using var provider = Build([.. With(ValidConfig(), ("Discovery:MaxEnqueuesPerSweep", "0"))]);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DiscoveryOptions>>().Value);

        Assert.Contains("MaxEnqueuesPerSweep must be at least 1", ex.Message);
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
}
