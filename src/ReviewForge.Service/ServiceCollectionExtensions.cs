using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Infrastructure.Ado;
using ReviewForge.Infrastructure.Chat;
using ReviewForge.Infrastructure.Filesystem;
using ReviewForge.Infrastructure.Git;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Service.Logging;
using ReviewForge.Service.Queue;
using ReviewForge.Service.Security;

namespace ReviewForge.Service;

/// <summary>DI wiring for the whole host — options validation at startup, fail fast on bad config.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddReviewForge(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ReviewQueue>();
        services.AddSingleton<RunTracker>();
        services.AddSingleton<InFlightClaims>();

        var runLogsEnabled = configuration.GetValue<bool?>(
            $"{ReviewForgeServiceOptions.SectionName}:RunLogs:Enabled") ?? true;
        if (runLogsEnabled)
        {
            services.AddLogging(logging => logging.AddProvider(new RunLogFileProvider(
                configuration.GetValue<string>($"{ReviewForgeServiceOptions.SectionName}:WorkDir")
                    ?? Path.Combine(Path.GetTempPath(), "reviewforge"),
                new RunLogOptions
                {
                    MinLevel = configuration.GetValue<LogLevel?>(
                        $"{ReviewForgeServiceOptions.SectionName}:RunLogs:MinLevel") ?? LogLevel.Debug,
                })));
        }

        var ado = configuration.GetSection(AdoOptions.SectionName).Get<AdoOptions>()
                  ?? throw new InvalidOperationException($"configuration section '{AdoOptions.SectionName}' missing");
        ado.Pat = Environment.GetEnvironmentVariable(AdoOptions.PatEnvironmentVariable);
        services.AddSingleton(ado);
        services.AddSingleton<IPullRequestSource>(_ => new InstrumentedPullRequestSource(new AdoPullRequestSource(ado)));

        var reasoning = configuration.GetSection(ChatProviderOptions.SectionName).Get<ChatProviderOptions>()
                        ?? throw new InvalidOperationException($"configuration section '{ChatProviderOptions.SectionName}' missing");
        services.AddSingleton(reasoning);
        services.AddSingleton<IChatClientFactory>(sp => new ChatClientFactory(sp.GetRequiredService<ChatProviderOptions>()));
        services.Configure<ReviewForgeServiceOptions>(configuration.GetSection(ReviewForgeServiceOptions.SectionName));
        services.Configure<ApiDocsOptions>(configuration.GetSection(ApiDocsOptions.SectionName));
        services.AddSingleton(sp => new GitOperationScheduler(
            sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value.GitMaxConcurrency));
        services.AddSingleton<IGitOps>(sp =>
            new LibGit2SharpGitOps(
                sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value.TargetedFetchEnabled,
                sp.GetRequiredService<GitOperationScheduler>()));
        // Register the scheduler for disposal with the host.
        services.AddSingleton(sp => (IDisposable)sp.GetRequiredService<GitOperationScheduler>());
        services.AddSingleton<IWorkspaceFs, FileSystemWorkspaceFs>();
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value;
            var git = sp.GetRequiredService<IGitOps>();
            return new RepoCheckoutPool(git, sp.GetRequiredService<IWorkspaceFs>(), opts.WorkDir, ado.Pat);
        });
        services.Configure<DiscoveryOptions>(configuration.GetSection(DiscoveryOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DiscoveryOptions>>().Value);
        services.AddSingleton<DiscoveryService>();

        var discovery = configuration.GetSection(DiscoveryOptions.SectionName).Get<DiscoveryOptions>() ?? new DiscoveryOptions();
        if (discovery.SweepInterval is not null
            && discovery.Creators.Length == 0
            && !discovery.AllowAllCreators)
        {
            throw new InvalidOperationException(
                "Discovery:Creators must be a non-empty allowlist when Discovery:SweepInterval is enabled " +
                "(or set Discovery:AllowAllCreators=true to accept PRs from any author explicitly).");
        }

        services.AddSingleton<IFindingStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value;
            return new SqliteFindingStore(opts.StoreConnectionString);
        });

        services.AddSingleton(sp => new ReviewPipelineFactory(
            sp.GetRequiredService<IPullRequestSource>(),
            sp.GetRequiredService<IFindingStore>(),
            sp.GetRequiredService<RepoCheckoutPool>(),
            sp.GetRequiredService<IChatClientFactory>(),
            sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>(),
            sp.GetRequiredService<ILoggerFactory>(),
            enricher: null,
            clock: sp.GetRequiredService<TimeProvider>()));

        var workerCount = configuration.GetValue<int?>($"{ReviewForgeServiceOptions.SectionName}:WorkerCount")
                          ?? Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        if (workerCount < 1)
        {
            throw new InvalidOperationException("ReviewForge:WorkerCount must be at least 1");
        }

        for (var i = 0; i < workerCount; i++)
        {
            services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<ReviewWorker>(sp));
        }

        services.AddHostedService<DiscoverySweepWorker>();
        services.AddHostedService<CheckoutEvictionWorker>();
        services.AddHostedService<TelemetryGaugeRegistration>();

        // API-key auth: env REVIEWFORGE_API_KEYS (',' or ';' separated) wins over Api:Keys config.
        services.AddOptions<ApiKeyOptions>()
            .Bind(configuration.GetSection(ApiKeyOptions.SectionName))
            .PostConfigure(opts =>
            {
                var fromEnv = Environment.GetEnvironmentVariable(ApiKeyOptions.KeysEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(fromEnv))
                {
                    opts.Keys = fromEnv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }
            })
            .Validate(opts => opts.AllowUnauthenticatedForDevelopment || opts.Keys.Length > 0,
                $"No API keys configured. Set {ApiKeyOptions.KeysEnvironmentVariable} or Api:Keys, " +
                "or set Api:AllowUnauthenticatedForDevelopment=true for local development only.")
            .ValidateOnStart();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(ApiKeyOptions.SubmitPolicy, httpContext =>
            {
                var api = httpContext.RequestServices.GetRequiredService<IOptions<ApiKeyOptions>>().Value;
                var partition = httpContext.Request.Headers[ApiKeyOptions.HeaderName].FirstOrDefault()
                                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                                ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = api.SubmitPermitLimit,
                    Window = TimeSpan.FromSeconds(api.SubmitWindowSeconds),
                    QueueLimit = 0,
                });
            });
        });

        // No global HttpClient resilience: the ADO read path is retried by
        // TransientRetryPolicy (Infrastructure), which honors server Retry-After
        // and respects cancellation; writes are never retried (P2-15).

        // OTLP exporters are intentionally configured ONLY from the standard environment
        // variables (OTEL_EXPORTER_OTLP_ENDPOINT / _HEADERS / _PROTOCOL, or the per-signal
        // variants). Aspire injects these when running under the AppHost; production sets
        // them via docker-compose / the container environment. Never bind exporter options
        // to appsettings — a committed endpoint breaks both environments.
        var otlpEnabled = configuration.GetValue<bool?>($"{ReviewForgeServiceOptions.SectionName}:OtlpEnabled") is true
                          || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: "reviewforge", serviceInstanceId: Environment.MachineName));
        otel.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource(ReviewForgeTelemetry.SourceName);
            if (otlpEnabled)
            {
                tracing.AddOtlpExporter();
            }
        });
        otel.WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(ReviewForgeTelemetry.SourceName);
            if (otlpEnabled)
            {
                metrics.AddOtlpExporter();
            }
        });
        return services;
    }
}