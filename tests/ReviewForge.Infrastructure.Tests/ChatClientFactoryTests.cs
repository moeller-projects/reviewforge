using ReviewForge.Infrastructure.Chat;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class ChatClientFactoryTests
{
    [Theory]
    [InlineData("openai-codex:gpt-5.6-luna", "gpt-5.6-luna")]
    [InlineData("openai:gpt-5", "gpt-5")]
    [InlineData("gpt-5", "gpt-5")]
    [InlineData("other:model", "other:model")]
    public void ResolveModelName_strips_only_openai_prefixes(string configured, string expected)
        => Assert.Equal(expected, ChatClientFactory.ResolveModelName(configured));

    [Fact]
    public void ModelName_uses_resolved_model()
    {
        var factory = new ChatClientFactory(new ReasoningOptions {Provider = "openai", Model = "openai:gpt-5"});
        Assert.Equal("gpt-5", factory.ModelName);
    }

    [Fact]
    public void Unknown_provider_throws()
    {
        var factory = new ChatClientFactory(new ReasoningOptions {Provider = "bogus", Model = "m"});
        Assert.Throws<InvalidOperationException>(() => factory.Create());
    }

    [Fact]
    public void Openai_without_api_key_throws()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        try
        {
            var factory = new ChatClientFactory(new ReasoningOptions {Provider = "openai", Model = "gpt-5"});
            Assert.Throws<InvalidOperationException>(() => factory.Create());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public void Openai_with_api_key_builds_client()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var factory = new ChatClientFactory(new ReasoningOptions {Provider = "openai", Model = "gpt-5"});
            Assert.NotNull(factory.Create());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public void Codex_builds_client_without_io()
    {
        var factory = new ChatClientFactory(new ReasoningOptions
        {
            Provider = "openai-codex",
            Model = "openai-codex:gpt-5.6-luna",
            CredentialPath = Path.Combine(Path.GetTempPath(), "unused-auth.json"),
        });
        Assert.NotNull(factory.Create());
        Assert.Equal("gpt-5.6-luna", factory.ModelName);
    }

    [Fact]
    public void Default_credential_path_points_into_user_profile()
        => Assert.EndsWith(Path.Combine(".codex", "auth.json"), ReasoningOptions.DefaultCredentialPath());
}