using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using SmartMeter.Application;
using SmartMeter.Application.Abstractions;
using SmartMeter.Infrastructure.Ingestion;
using SmartMeter.Infrastructure.Jobs;
using SmartMeter.Infrastructure.Mdm;
using SmartMeter.Infrastructure.Persistence;

namespace SmartMeter.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        DapperSetup.Initialize();
        services.Configure<IngestionOptions>(configuration.GetSection(IngestionOptions.Section));
        services.Configure<MdmOptions>(configuration.GetSection(MdmOptions.Section));
        services.Configure<JobsOptions>(configuration.GetSection(JobsOptions.Section));
        services.AddSingleton(TimeProvider.System);

        // ------------------------------------------------------------ persistence (stateless -> singletons)
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IAnalyticsStateProvider, AnalyticsStateProvider>();
        services.AddSingleton<IFleetReadRepository, FleetReadRepository>();
        services.AddSingleton<IBuildingReadRepository, BuildingReadRepository>();
        services.AddSingleton<IMeterReadRepository, MeterReadRepository>();
        services.AddSingleton<IAnalyticsToolsRepository, AnalyticsToolsRepository>();
        services.AddSingleton<IngestionRepository>();
        services.AddSingleton<IIngestionRepository>(sp => sp.GetRequiredService<IngestionRepository>());
        services.AddSingleton<IUploadStore, LocalUploadStore>();

        // ------------------------------------------------------------ caching: HybridCache (L1 memory + L2 Redis when configured)
        services.AddHybridCache(o =>
        {
            o.MaximumPayloadBytes = 16 * 1024 * 1024;
            o.MaximumKeyLength = 2048;
            o.DefaultEntryOptions = new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10), LocalCacheExpiration = TimeSpan.FromMinutes(2) };
        });
        var redis = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redis))
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redis;
                o.InstanceName = "smartmeter:";
            });

        // ------------------------------------------------------------ MDM consumption API
        var mdm = configuration.GetSection(MdmOptions.Section).Get<MdmOptions>() ?? new MdmOptions();
        if (mdm.IsHttp)
        {
            if (string.IsNullOrWhiteSpace(mdm.BaseUrl)) throw new InvalidOperationException("Mdm:BaseUrl is required when Mdm:Mode = Http.");
            services.AddHttpClient<IMdmConsumptionService, HttpMdmConsumptionService>((sp, client) =>
                {
                    var o = sp.GetRequiredService<IOptions<MdmOptions>>().Value;
                    client.BaseAddress = new Uri(o.BaseUrl!.TrimEnd('/') + "/");
                    client.Timeout = Timeout.InfiniteTimeSpan;   // timeouts are owned by the resilience pipeline
                    if (!string.IsNullOrEmpty(o.ApiKey)) client.DefaultRequestHeaders.Add(o.ApiKeyHeader, o.ApiKey);
                })
                .AddResilienceHandler("mdm", (builder, context) =>
                {
                    var o = context.ServiceProvider.GetRequiredService<IOptions<MdmOptions>>().Value;
                    builder.AddTimeout(TimeSpan.FromSeconds(o.TotalTimeoutSeconds));
                    builder.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = o.RetryCount,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = TimeSpan.FromMilliseconds(500),
                    });
                    builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                    {
                        FailureRatio = o.CircuitBreakerFailureRatio,
                        SamplingDuration = TimeSpan.FromSeconds(30),
                        MinimumThroughput = 10,
                        BreakDuration = TimeSpan.FromSeconds(o.CircuitBreakerBreakSeconds),
                    });
                    builder.AddTimeout(TimeSpan.FromSeconds(o.AttemptTimeoutSeconds));
                });
        }
        else
        {
            services.AddSingleton<IMdmConsumptionService, MockMdmConsumptionService>();
        }

        // ------------------------------------------------------------ Hangfire (SQL Server storage)
        var connectionString = new SqlConnectionFactory(configuration).ConnectionString;
        services.AddHangfire(cfg => cfg
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
            {
                SqlClientFactory = SqlClientFactory.Instance,
                SchemaName = "HangFire",
                PrepareSchemaIfNecessary = true,
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(30),
                QueuePollInterval = TimeSpan.FromSeconds(1),
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true,
            }));
        var jobs = configuration.GetSection(JobsOptions.Section).Get<JobsOptions>() ?? new JobsOptions();
        if (jobs.WorkersEnabled)
        {
            services.AddHangfireServer(o =>
            {
                o.Queues = ["ingestion", "analytics", "default"];
                o.WorkerCount = jobs.WorkerCount;
                o.ServerName = $"{Environment.MachineName}:{Environment.ProcessId}";
            });
            services.AddHostedService<RecurringJobsRegistrar>();
        }
        services.AddSingleton<IBackgroundJobs, HangfireBackgroundJobs>();
        services.AddTransient<IngestionJobRunner>();
        services.AddTransient<AnalyticsRefreshJob>();
        return services;
    }
}
