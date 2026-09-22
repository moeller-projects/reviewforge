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

        // Runtime directories are created once at composition time; the per-run pipeline
        // factory must not touch the filesystem (P3-m). RunLogFileProvider uses the same
        // work dir resolution below.
        var workDir = configuration.GetValue<string>($"{ReviewForgeServiceOptions.SectionName}:WorkDir")
                      ?? Path.Combine(Path.GetTempPath(), "reviewforge");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(Path.Combine(workDir, "findings"));

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

        // Validated options (P3-d): DataAnnotations + rule checks, fail-fast at startup and on
        // first IOptions<T>.Value access — README's "typed options with validation, fail-fast"
        // was previously just comments on the classes.
        services.AddOptions<AdoOptions>()
            .Bind(configuration.GetSection(AdoOptions.SectionName))
            .PostConfigure(o => o.Pat = Environment.GetEnvironmentVariable(AdoOptions.PatEnvironmentVariable))
            .ValidateDataAnnotations()
            .Validate(o => o.OrgUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true,
                "Ado:OrgUrl must be an https:// URL — the PAT is sent to this endpoint.")
            .ValidateOnStart();
        services.AddSingleton<IPullRequestSource>(sp =>
            new InstrumentedPullRequestSource(
                new AdoPullRequestSource(sp.GetRequiredService<IOptions<AdoOptions>>().Value)));

        services.AddOptions<ChatProviderOptions>()
            .Bind(configuration.GetSection(ChatProviderOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IChatClientFactory>(sp =>
            new ChatClientFactory(sp.GetRequiredService<IOptions<ChatProviderOptions>>().Value));

        services.AddOptions<ReviewForgeServiceOptions>()
            .Bind(configuration.GetSection(ReviewForgeServiceOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.WorkerCount >= 1, "ReviewForge:WorkerCount must be at least 1")
            .ValidateOnStart();

        services.AddOptions<ApiDocsOptions>()
            .Bind(configuration.GetSection(ApiDocsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<DiscoveryOptions>()
            .Bind(configuration.GetSection(DiscoveryOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.MaxEnqueuesPerSweep >= 1, "Discovery:MaxEnqueuesPerSweep must be at least 1")
            .Validate(o => o.FailureBackoffBase > TimeSpan.Zero, "Discovery:FailureBackoffBase must be greater than 0")
            .Validate(o => o.FailureBackoffMax >= o.FailureBackoffBase, "Discovery:FailureBackoffMax must be at least FailureBackoffBase")
            .ValidateOnStart();
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
            return new RepoCheckoutPool(git, sp.GetRequiredService<IWorkspaceFs>(), opts.WorkDir,
                sp.GetRequiredService<IOptions<AdoOptions>>().Value.Pat);
        });
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

        // WorkerCount < 1 is rejected by the options validation above (fail-fast at startup);
        // when unset it defaults to a processor-count-derived clamp, always >= 2.
        var workerCount = configuration.GetValue<int?>($"{ReviewForgeServiceOptions.SectionName}:WorkerCount")
                          ?? Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

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
                opts.Keys = [.. opts.Keys.Where(key => !string.IsNullOrWhiteSpace(key))];
            })
            .Validate(opts => opts.AllowUnauthenticatedForDevelopment ||
                              opts.Keys.Any(key => !string.IsNullOrWhiteSpace(key)),
                $"No API keys configured. Set {ApiKeyOptions.KeysEnvironmentVariable} or Api:Keys, " +
                "or set Api:AllowUnauthenticatedForDevelopment=true for local development only.")
            .Validate(opts => opts.SubmitPermitLimit > 0, "Api:SubmitPermitLimit must be greater than 0.")
            .Validate(opts => opts.SubmitWindowSeconds > 0, "Api:SubmitWindowSeconds must be greater than 0.")
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