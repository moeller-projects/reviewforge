using ReviewForge.Infrastructure.Chat;
using ReviewForge.Infrastructure.Codex;
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
        var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "openai", Model = "openai:gpt-5"});
        Assert.Equal("gpt-5", factory.ModelName);
    }

    [Fact]
    public void Unknown_provider_throws()
    {
        var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "bogus", Model = "m"});
        Assert.Throws<InvalidOperationException>(() => factory.Create());
    }

    [Fact]
    public void Openai_without_api_key_throws()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        try
        {
            var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "openai", Model = "gpt-5"});
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
            var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "openai", Model = "gpt-5"});
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
        var factory = new ChatClientFactory(new ChatProviderOptions
        {
            Provider = "openai-codex",
            Model = "openai-codex:gpt-5.6-luna",
            CredentialPath = Path.Combine(Path.GetTempPath(), "unused-auth.json"),
        });
        Assert.NotNull(factory.Create());
        Assert.Equal("gpt-5.6-luna", factory.ModelName);
    }

    [Fact]
    public void Create_returns_same_instance()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "openai", Model = "gpt-5"});
            var a = factory.Create();
            var b = factory.Create();
            Assert.Same(a, b);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public void Codex_returns_same_instance()
    {
        var factory = new ChatClientFactory(new ChatProviderOptions
        {
            Provider = "openai-codex",
            Model = "openai-codex:gpt-5.6-luna",
            CredentialPath = Path.Combine(Path.GetTempPath(), "unused-auth.json"),
        });
        var a = factory.Create();
        var b = factory.Create();
        Assert.Same(a, b);
    }

    [Fact]
    public void Dispose_before_create_does_not_throw()
    {
        var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "bogus", Model = "m"});
        factory.Dispose();
    }

    [Fact]
    public void Dispose_after_create_does_not_throw()
    {
        var factory = new ChatClientFactory(new ChatProviderOptions
        {
            Provider = "openai-codex",
            Model = "openai-codex:gpt-5.6-luna",
            CredentialPath = Path.Combine(Path.GetTempPath(), "unused-auth.json"),
        });
        factory.Create();
        factory.Dispose();
    }

    [Fact]
    public void Default_credential_path_points_into_user_profile()
        => Assert.EndsWith(Path.Combine(".codex", "auth.json"), ChatProviderOptions.DefaultCredentialPath());

    [Fact]
    public void Codex_debug_flag_in_production_throws()
    {
        var debug = Environment.GetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable);
        var aspnet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable, "1");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
            var factory = new ChatClientFactory(new ChatProviderOptions
            {
                Provider = "openai-codex",
                Model = "openai-codex:gpt-5.6-luna",
                CredentialPath = Path.Combine(Path.GetTempPath(), "unused-auth.json"),
            });

            var ex = Assert.Throws<InvalidOperationException>(() => factory.Create());

            Assert.Contains("refused in Production", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable, debug);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspnet);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnet);
        }
    }

    [Fact]
    public void Codex_debug_flag_honors_dotnet_production_when_aspnet_conflicts()
    {
        var debug = Environment.GetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable);
        var aspnet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable, "1");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            var factory = new ChatClientFactory(new ChatProviderOptions
            {
                Provider = "openai-codex",
                Model = "openai-codex:gpt-5.6-luna",
                CredentialPath = Path.Combine(Path.GetTempPath(), "unused-auth.json"),
            });

            var ex = Assert.Throws<InvalidOperationException>(() => factory.Create());

            Assert.Contains("refused in Production", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable, debug);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspnet);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnet);
        }
    }

    [Fact]
    public void Codex_debug_flag_outside_production_attaches_handler()
    {
        var debug = Environment.GetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable);
        var aspnet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable, "1");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
            var factory = new ChatClientFactory(new ChatProviderOptions
            {
                Provider = "openai-codex",
                Model = "openai-codex:gpt-5.6-luna",
                CredentialPath = Path.Combine(Path.GetTempPath(), "unused-auth.json"),
            });

            Assert.NotNull(factory.Create());
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable, debug);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspnet);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnet);
        }
    }
}