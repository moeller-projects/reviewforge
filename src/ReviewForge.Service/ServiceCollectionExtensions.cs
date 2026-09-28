using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.AutoFix.Fixers;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Infrastructure.Ado;
using ReviewForge.Infrastructure.Chat;
using ReviewForge.Infrastructure.Filesystem;
using ReviewForge.Infrastructure.Git;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Service.Queue;
using ReviewForge.Service.Security;

namespace ReviewForge.Service;

/// <summary>DI wiring for the whole host — options validation at startup, fail fast on bad config.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddReviewForge(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RunTracker>();
        services.AddSingleton<InFlightClaims>();

        // Ingest queue backing: memory channel (default) or durable SQLite rows on the store's
        // database file (ReviewForge:QueueMode). Worker and endpoints only see IReviewQueue.
        // Enum.TryParse accepts undefined numeric values, so definedness is checked here at
        // compose time too — "2" must never silently become Memory and disable durability.
        var queueModeText = configuration.GetValue<string>($"{ReviewForgeServiceOptions.SectionName}:QueueMode");
        if (queueModeText is not null
            && (!Enum.TryParse<QueueMode>(queueModeText, ignoreCase: true, out var queueMode) || !Enum.IsDefined(queueMode)))
        {
            throw new InvalidOperationException(
                $"ReviewForge:QueueMode must be one of {string.Join(" | ", Enum.GetNames<QueueMode>())} (got '{queueModeText}')");
        }

        if (Enum.TryParse<QueueMode>(queueModeText, ignoreCase: true, out var parsedQueueMode)
            && parsedQueueMode == QueueMode.Sqlite)
        {
            services.AddSingleton<IReviewQueue>(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value;
                return new SqliteReviewQueue(
                    opts.StoreConnectionString,
                    journalMode: Enum.Parse<StoreJournalMode>(opts.Store.JournalMode, ignoreCase: true));
            });
        }
        else
        {
            // Concrete registration is the test seam for queue-failure behavior
            // (ReviewQueue.Complete); production traffic only resolves IReviewQueue.
            services.AddSingleton<ReviewQueue>();
            services.AddSingleton<IReviewQueue>(sp => sp.GetRequiredService<ReviewQueue>());
        }

        // Runtime directories are created once at composition time; the per-run pipeline
        // factory must not touch the filesystem (P3-m).
        var workDir = configuration.GetValue<string>($"{ReviewForgeServiceOptions.SectionName}:WorkDir")
                      ?? Path.Combine(Path.GetTempPath(), "reviewforge");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(Path.Combine(workDir, "findings"));

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
        // Singleton deliberately: one VssConnection (and its cached typed clients) per
        // process — see the pooling-contract remark on AdoPullRequestSource.
        services.AddSingleton<IPullRequestSource>(sp =>
            new InstrumentedPullRequestSource(
                new AdoPullRequestSource(sp.GetRequiredService<IOptions<AdoOptions>>().Value)));

        services.AddOptions<ChatProviderOptions>()
            .Bind(configuration.GetSection(ChatProviderOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        // One governor per host, shared by both model tiers: bounds concurrent provider
        // requests process-wide (WorkerCount × iterations, later × shards). Default cap is
        // permissive (WorkerCount × 2) — tighten from reviewforge.llm.governor.wait_ms.
        services.AddSingleton(sp =>
        {
            var chat = sp.GetRequiredService<IOptions<ChatProviderOptions>>().Value;
            var service = sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value;
            return new LlmGovernor(chat.MaxConcurrentRequests ?? service.WorkerCount * 2);
        });
        services.AddSingleton<IChatClientFactory>(sp =>
            new ChatClientFactory(
                sp.GetRequiredService<IOptions<ChatProviderOptions>>().Value,
                governor: sp.GetRequiredService<LlmGovernor>()));

        services.AddOptions<ReviewForgeServiceOptions>()
            .Bind(configuration.GetSection(ReviewForgeServiceOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.WorkerCount is >= 1 and <= 64,
                "ReviewForge:WorkerCount must be between 1 and 64")
            .Validate(o => ReviewForgeServiceOptions.IsValidCleanRunVote(o.CleanRunVote),
                "ReviewForge:CleanRunVote must be NoResponse | Approved | ApprovedWithSuggestions | None")
            .Validate(o => o.StaleShellMinutes > 0, "ReviewForge:StaleShellMinutes must be greater than 0")
            .Validate(o => o.Retention.Days >= 1, "ReviewForge:Retention:Days must be at least 1")
            .Validate(o => o.Retention.MinRunsPerPr >= 1, "ReviewForge:Retention:MinRunsPerPr must be at least 1")
            .Validate(o => Enum.TryParse<StoreJournalMode>(o.Store.JournalMode, ignoreCase: true, out var jm)
                    && Enum.IsDefined(jm),
                "ReviewForge:Store:JournalMode must be Wal | Delete")
            .Validate(o => Enum.TryParse<QueueMode>(o.QueueMode, ignoreCase: true, out var qm)
                    && Enum.IsDefined(qm),
                "ReviewForge:QueueMode must be Memory | Sqlite")
            .Validate(o => o.Sharding.ShardMaxChars >= 1_000, "ReviewForge:Sharding:ShardMaxChars must be at least 1000")
            .Validate(o => o.Sharding.MaxShards is >= 2 and <= 32, "ReviewForge:Sharding:MaxShards must be between 2 and 32")
            .Validate(o => o.Sharding.ShardConcurrency >= 1 && o.Sharding.ShardConcurrency <= o.Sharding.MaxShards,
                "ReviewForge:Sharding:ShardConcurrency must be between 1 and MaxShards")
            .ValidateOnStart();

        services.AddOptions<RepoReadToolsOptions>()
            .Bind(configuration.GetSection(RepoReadToolsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AutoFixOptions>()
            .Bind(configuration.GetSection(AutoFixOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => !o.Enabled || o.AllowedAuthors.Length > 0,
                "AutoFix:AllowedAuthors must be non-empty when AutoFix:Enabled is true")
            .Validate(o => o.PublishMode == "Suggestion",
                "AutoFix:PublishMode only supports 'Suggestion' in this version (CommitOnHead/StackedBranch are reserved)")
            .ValidateOnStart();

        // Deterministic fixers: one class per rule; adding a fixer = one line here + a test file.
        services.AddSingleton<IFindingFixer>(_ => new HomoglyphIdentifierFixer("homoglyph/mixed-script-identifier"));
        services.AddSingleton<IFindingFixer>(_ => new HomoglyphIdentifierFixer("homoglyph/confusable-keyword"));
        services.AddSingleton<IFindingFixer, BashUnquotedVarsFixer>();
        services.AddSingleton<IFindingFixer, BashSetEMissingFixer>();
        services.AddSingleton<IFindingFixer, PythonMutableDefaultArgFixer>();
        services.AddSingleton<IFindingFixer, DockerAddToCopyFixer>();
        services.AddSingleton<IFindingFixer[]>(sp => sp.GetServices<IFindingFixer>().ToArray());

        services.AddOptions<ApiDocsOptions>()
            .Bind(configuration.GetSection(ApiDocsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<DiscoveryOptions>()
            .Bind(configuration.GetSection(DiscoveryOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.MaxEnqueuesPerSweep >= 1, "Discovery:MaxEnqueuesPerSweep must be at least 1")
            .Validate(o => o.MaxDegreeOfParallelism is >= 1 and <= 16,
                "Discovery:MaxDegreeOfParallelism must be between 1 and 16")
            .Validate(o => o.WarmupMaxPerSweep >= 1, "Discovery:WarmupMaxPerSweep must be at least 1")
            .Validate(o => o.WarmupConcurrency is >= 1 and <= 8,
                "Discovery:WarmupConcurrency must be between 1 and 8")
            .Validate(o => o.FailureBackoffBase > TimeSpan.Zero, "Discovery:FailureBackoffBase must be greater than 0")
            .Validate(o => o.FailureBackoffMax >= o.FailureBackoffBase, "Discovery:FailureBackoffMax must be at least FailureBackoffBase")
            .ValidateOnStart();
        services.AddSingleton(sp => new GitOperationScheduler(
            sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value.GitMaxConcurrency));
        services.AddSingleton<IGitOps>(sp =>
            new LibGit2SharpGitOps(
                sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value.TargetedFetchEnabled,
                sp.GetRequiredService<GitOperationScheduler>(),
                // The PAT may only be presented to the configured org's host.
                credentialHost: new Uri(sp.GetRequiredService<IOptions<AdoOptions>>().Value.OrgUrl).Host));
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
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value.Retention);
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
            return new SqliteFindingStore(
                opts.StoreConnectionString,
                Enum.Parse<StoreJournalMode>(opts.Store.JournalMode, ignoreCase: true));
        });

        services.AddSingleton(sp => new ReviewPipelineFactory(
            sp.GetRequiredService<IPullRequestSource>(),
            sp.GetRequiredService<IFindingStore>(),
            sp.GetRequiredService<RepoCheckoutPool>(),
            sp.GetRequiredService<IChatClientFactory>(),
            sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>(),
            sp.GetRequiredService<IOptions<RepoReadToolsOptions>>(),
            sp.GetRequiredService<ILoggerFactory>(),
            enricher: null,
            clock: sp.GetRequiredService<TimeProvider>(),
            findingFixers: sp.GetRequiredService<IFindingFixer[]>(),
            autoFixOptions: sp.GetRequiredService<IOptions<AutoFixOptions>>()));

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
        services.AddHostedService<ShellReaperService>();

        // API keys are intentionally not bound from configuration. Secrets may only enter
        // through REVIEWFORGE_API_KEYS; other Api settings remain ordinary configuration.
        services.AddOptions<ApiKeyOptions>()
            .Configure(opts =>
            {
                opts.AllowUnauthenticatedForDevelopment = configuration.GetValue<bool>(
                    $"{ApiKeyOptions.SectionName}:AllowUnauthenticatedForDevelopment");
                opts.SubmitPermitLimit = configuration.GetValue(
                    $"{ApiKeyOptions.SectionName}:SubmitPermitLimit", opts.SubmitPermitLimit);
                opts.SubmitWindowSeconds = configuration.GetValue(
                    $"{ApiKeyOptions.SectionName}:SubmitWindowSeconds", opts.SubmitWindowSeconds);
                opts.StatusPermitLimit = configuration.GetValue(
                    $"{ApiKeyOptions.SectionName}:StatusPermitLimit", opts.StatusPermitLimit);
                opts.StatusWindowSeconds = configuration.GetValue(
                    $"{ApiKeyOptions.SectionName}:StatusWindowSeconds", opts.StatusWindowSeconds);
                var fromEnv = Environment.GetEnvironmentVariable(ApiKeyOptions.KeysEnvironmentVariable);
                opts.SetEnvironmentKeys(string.IsNullOrWhiteSpace(fromEnv)
                    ? []
                    : fromEnv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            })
            .Validate(opts => opts.AllowUnauthenticatedForDevelopment ||
                              opts.Keys.Any(key => !string.IsNullOrWhiteSpace(key)),
                $"No API keys configured. Set {ApiKeyOptions.KeysEnvironmentVariable}, " +
                "or set Api:AllowUnauthenticatedForDevelopment=true in Development.")
            .Validate(opts => opts.SubmitPermitLimit > 0, "Api:SubmitPermitLimit must be greater than 0.")
            .Validate(opts => opts.SubmitWindowSeconds > 0, "Api:SubmitWindowSeconds must be greater than 0.")
            .Validate(opts => opts.StatusPermitLimit > 0, "Api:StatusPermitLimit must be greater than 0.")
            .Validate(opts => opts.StatusWindowSeconds > 0, "Api:StatusWindowSeconds must be greater than 0.")
            .ValidateOnStart();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(ApiKeyOptions.SubmitPolicy, httpContext =>
            {
                var api = httpContext.RequestServices.GetRequiredService<IOptions<ApiKeyOptions>>().Value;
                // Partition by client identity only: the presented X-Api-Key is
                // unauthenticated input — keying on it would hand each key guess
                // a fresh permit budget and unbounded partition cardinality.
                var partition = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = api.SubmitPermitLimit,
                    Window = TimeSpan.FromSeconds(api.SubmitWindowSeconds),
                    QueueLimit = 0,
                });
            });
            limiter.AddPolicy(ApiKeyOptions.StatusPolicy, httpContext =>
            {
                var api = httpContext.RequestServices.GetRequiredService<IOptions<ApiKeyOptions>>().Value;
                var partition = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = api.StatusPermitLimit,
                    Window = TimeSpan.FromSeconds(api.StatusWindowSeconds),
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
        var explicitOtlpEnabled =
            configuration.GetValue<bool?>($"{ReviewForgeServiceOptions.SectionName}:OtlpEnabled") is true;
        var commonOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var tracesOtlpEnabled = ShouldEnableOtlpExporter(
            explicitOtlpEnabled,
            commonOtlpEndpoint,
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"));
        var metricsOtlpEnabled = ShouldEnableOtlpExporter(
            explicitOtlpEnabled,
            commonOtlpEndpoint,
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"));
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: "reviewforge", serviceInstanceId: Environment.MachineName));
        otel.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource(ReviewForgeTelemetry.SourceName);
            if (tracesOtlpEnabled)
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
            if (metricsOtlpEnabled)
            {
                metrics.AddOtlpExporter();
            }
        });
        return services;
    }

    internal static bool ShouldEnableOtlpExporter(
        bool configured,
        string? commonEndpoint,
        string? signalEndpoint)
        => configured ||
           !string.IsNullOrWhiteSpace(commonEndpoint) ||
           !string.IsNullOrWhiteSpace(signalEndpoint);
}