using System.ClientModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Chat;
using ReviewForge.Infrastructure.Codex;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

[Collection(EnvironmentVariableCollection.Name)]
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
    public void Transport_retry_classification_preserves_transient_boundaries()
    {
        var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "openai", Model = "m"});

        Assert.True(factory.IsTransientFailure(new HttpRequestException("connection lost")));
        Assert.True(factory.IsTransientFailure(new HttpRequestException("timeout", null, HttpStatusCode.RequestTimeout)));
        Assert.True(factory.IsTransientFailure(new HttpRequestException("throttled", null, HttpStatusCode.TooManyRequests)));
        Assert.True(factory.IsTransientFailure(new HttpRequestException("server", null, HttpStatusCode.InternalServerError)));
        Assert.True(factory.IsTransientFailure(new TimeoutException()));
        Assert.False(factory.IsTransientFailure(new HttpRequestException("bad request", null, HttpStatusCode.BadRequest)));
        Assert.False(factory.IsTransientFailure(new InvalidOperationException()));
    }

    [Fact]
    public void ModelName_uses_resolved_model()
    {
        var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "openai", Model = "openai:gpt-5"});
        Assert.Equal("gpt-5", factory.ModelName(ChatTier.Full));
    }

    [Fact]
    public void Unknown_provider_throws()
    {
        var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "bogus", Model = "m"});
        Assert.Throws<InvalidOperationException>(() => factory.Create(ChatTier.Full));
    }

    [Fact]
    public void Openai_without_api_key_throws()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        try
        {
            var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "openai", Model = "gpt-5"});
            Assert.Throws<InvalidOperationException>(() => factory.Create(ChatTier.Full));
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
            Assert.NotNull(factory.Create(ChatTier.Full));
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
        Assert.NotNull(factory.Create(ChatTier.Full));
        Assert.Equal("gpt-5.6-luna", factory.ModelName(ChatTier.Full));
    }

    [Fact]
    public void Create_returns_same_instance()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "openai", Model = "gpt-5"});
            var a = factory.Create(ChatTier.Full);
            var b = factory.Create(ChatTier.Full);
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
        var a = factory.Create(ChatTier.Full);
        var b = factory.Create(ChatTier.Full);
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
        factory.Create(ChatTier.Full);
        factory.Dispose();
    }

    [Fact]
    public void Dispose_after_both_tiers_created_does_not_throw()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var factory = new ChatClientFactory(new ChatProviderOptions
            {
                Provider = "openai", Model = "gpt-5", FollowUpModel = "gpt-5-mini",
            });
            factory.Create(ChatTier.Full);
            factory.Create(ChatTier.Fast);
            factory.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public void Fast_tier_aliases_full_when_followup_model_unset()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "openai", Model = "gpt-5"});

            Assert.Equal("gpt-5", factory.ModelName(ChatTier.Fast));
            Assert.Same(factory.Create(ChatTier.Full), factory.Create(ChatTier.Fast));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fast_tier_aliases_full_when_followup_model_is_blank(string? followUpModel)
    {
        // ModelName(Fast) and Create(Fast) must agree on "unset": a blank FollowUpModel
        // aliases the Full tier — never a blank ModelId on the Full client.
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var factory = new ChatClientFactory(new ChatProviderOptions
            {
                Provider = "openai", Model = "gpt-5", FollowUpModel = followUpModel,
            });

            Assert.Equal("gpt-5", factory.ModelName(ChatTier.Fast));
            Assert.Same(factory.Create(ChatTier.Full), factory.Create(ChatTier.Fast));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public void Fast_tier_uses_distinct_client_and_model_when_followup_configured()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var factory = new ChatClientFactory(new ChatProviderOptions
            {
                Provider = "openai", Model = "gpt-5", FollowUpModel = "gpt-5-mini",
            });

            Assert.Equal("gpt-5", factory.ModelName(ChatTier.Full));
            Assert.Equal("gpt-5-mini", factory.ModelName(ChatTier.Fast));
            Assert.NotSame(factory.Create(ChatTier.Full), factory.Create(ChatTier.Fast));
            Assert.Same(factory.Create(ChatTier.Fast), factory.Create(ChatTier.Fast)); // cached per tier
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public void Mismatched_provider_prefixes_are_rejected()
    {
        var options = new ChatProviderOptions
        {
            Provider = "openai-codex",
            Model = "openai-codex:gpt-5.6-luna",
            FollowUpModel = "openai:gpt-5-mini",
            MaxConcurrentRequests = 1,
        };

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ChatProviderOptions.FollowUpModel)));
    }

    [Fact]
    public void Matching_provider_prefixes_validate()
    {
        var options = new ChatProviderOptions
        {
            Provider = "openai-codex",
            Model = "openai-codex:gpt-5.6-luna",
            FollowUpModel = "openai-codex:gpt-5.6-luna-mini",
            MaxConcurrentRequests = 1,
        };

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.True(valid);
        Assert.Empty(results);
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

            var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(ChatTier.Full));

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

            var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(ChatTier.Full));

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

            Assert.NotNull(factory.Create(ChatTier.Full));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexHttpDebugHandler.EnvironmentVariable, debug);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspnet);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnet);
        }
    }

    [Fact]
    public async Task Codex_request_disables_storage_and_enables_streaming()
    {
        var directory = Path.Combine(Path.GetTempPath(), "reviewforge-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var credentialPath = Path.Combine(directory, "auth.json");
            File.WriteAllText(credentialPath, JsonSerializer.Serialize(new
            {
                tokens = new
                {
                    access_token = CodexCredentialTests.Jwt(DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()),
                    refresh_token = "refresh",
                    account_id = "account",
                },
            }));
            var handler = new CaptureRequestHandler();
            using var factory = new ChatClientFactory(new ChatProviderOptions
            {
                Provider = "openai-codex",
                Model = "openai-codex:gpt-5.6-luna",
                CredentialPath = credentialPath,
            }, handler);

            var exception = await Assert.ThrowsAsync<ClientResultException>(() =>
                factory.Create(ChatTier.Full).GetResponseAsync(
                    [new ChatMessage(ChatRole.User, "review")],
                    new ChatOptions {ModelId = factory.ModelName(ChatTier.Full)}));

            Assert.Equal((int) HttpStatusCode.BadRequest, exception.Status);
            using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validate_with_blank_model_yields_nothing_for_required_to_report()
    {
        // [Required] reports a missing Model; the cross-check has nothing to add.
        var options = new ChatProviderOptions {Provider = "openai", Model = " "};

        Assert.Empty(options.Validate(new ValidationContext(options)));
    }

    private sealed class CaptureRequestHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{}"),
            };
        }
    }
}
