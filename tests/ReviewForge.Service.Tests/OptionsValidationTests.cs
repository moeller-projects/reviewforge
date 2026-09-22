using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReviewForge.Infrastructure.Ado;
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
}