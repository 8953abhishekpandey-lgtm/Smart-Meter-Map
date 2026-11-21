using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMeter.Application.Abstractions;
using SmartMeter.Infrastructure.Ingestion;

namespace SmartMeter.Infrastructure.Jobs;

public sealed class JobsOptions
{
    public const string Section = "Jobs";
    /// <summary>Run Hangfire workers in this process (disable on API-only nodes when using a separate worker).</summary>
    public bool WorkersEnabled { get; set; } = true;
    public int WorkerCount { get; set; } = Math.Max(2, Environment.ProcessorCount);
    public string NightlyRefreshCron { get; set; } = "15 2 * * *";
    public string TimeZone { get; set; } = "Asia/Kolkata";
    public bool DashboardEnabled { get; set; } = true;
}

internal sealed class HangfireBackgroundJobs(IBackgroundJobClient client) : IBackgroundJobs
{
    public string EnqueueIngestion(Guid jobId) =>
        client.Enqueue<IngestionJobRunner>("ingestion", j => j.RunAsync(jobId, CancellationToken.None));

    public string EnqueueAnalyticsRefresh(DateTime? requestedAtUtc) =>
        client.Enqueue<AnalyticsRefreshJob>("analytics", j => j.RunAsync(requestedAtUtc, CancellationToken.None));
}

/// <summary>Registers recurring jobs (nightly safety-net refresh incl. retention) at start-up.</summary>
internal sealed class RecurringJobsRegistrar(IRecurringJobManager manager, IOptions<JobsOptions> options, ILogger<RecurringJobsRegistrar> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(o.TimeZone); }
        catch (Exception) { tz = TimeZoneInfo.Utc; logger.LogWarning("Time zone {TimeZone} not found; scheduling in UTC", o.TimeZone); }
        manager.AddOrUpdate<AnalyticsRefreshJob>("analytics-nightly-refresh", "analytics",
            j => j.RunAsync(null, CancellationToken.None), o.NightlyRefreshCron, new RecurringJobOptions { TimeZone = tz });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
