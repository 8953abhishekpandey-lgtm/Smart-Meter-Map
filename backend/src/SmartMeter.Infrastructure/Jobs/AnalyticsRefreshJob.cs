using System.Data;
using System.Diagnostics;
using Dapper;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Infrastructure.Persistence;

namespace SmartMeter.Infrastructure.Jobs;

/// <summary>
/// Rebuilds the pre-aggregated generation (sm.usp_RefreshAnalytics), applies log retention and tells
/// connected dashboards that a new data version is live. Requests are coalesced: if a refresh
/// started after the change that requested this one, the job exits immediately.
/// </summary>
public sealed class AnalyticsRefreshJob(
    ISqlConnectionFactory db,
    IAnalyticsStateProvider state,
    IIngestionNotifier notifier,
    SmartMeterMetrics metrics,
    ILogger<AnalyticsRefreshJob> logger)
{
    [Queue("analytics")]
    [DisableConcurrentExecution(timeoutInSeconds: 6 * 3600)]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [60, 300])]
    [JobDisplayName("Refresh analytics")]
    public async Task RunAsync(DateTime? requestedAtUtc, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        if (requestedAtUtc is { } requested)
        {
            var started = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT RefreshStartedAt FROM sm.AnalyticsState;", cancellationToken: ct));
            if (started is { } s && s >= requested)
            {
                logger.LogInformation("Analytics refresh skipped: a refresh started at {Started:O} already covers {Requested:O}", s, requested);
                return;
            }
        }

        var sw = Stopwatch.StartNew();
        conn.InfoMessage += (_, e) =>
        {
            foreach (SqlError err in e.Errors)
                if (!err.Message.StartsWith("Caution:", StringComparison.Ordinal) && !err.Message.StartsWith("Warning: Null value", StringComparison.Ordinal))
                    logger.LogInformation("SQL: {Message}", err.Message);
        };
        await using (var cmd = new SqlCommand("sm.usp_RefreshAnalytics", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 })
        {
            var ret = cmd.Parameters.Add("@ReturnValue", SqlDbType.Int);
            ret.Direction = ParameterDirection.ReturnValue;
            await cmd.ExecuteNonQueryAsync(ct);
            if (ret.Value is 1)
            {
                logger.LogInformation("Analytics refresh skipped: another refresh holds the lock");
                return;
            }
        }
        var dropped = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "DECLARE @n int; EXEC @n = sm.usp_ApplyCommLogRetention; SELECT @n;", commandTimeout: 600, cancellationToken: ct));

        state.Invalidate();
        var snapshot = await state.GetAsync(ct);
        metrics.RecordRefresh(sw.Elapsed.TotalSeconds);
        logger.LogInformation("Analytics refreshed in {Elapsed:F1}s: version {Version}, window {Start}..{End}, {Dropped} old partitions dropped",
            sw.Elapsed.TotalSeconds, snapshot.DataVersion, snapshot.Window.Start, snapshot.Window.End, dropped);
        await notifier.DataVersionChangedAsync(snapshot.DataVersion, ct);
    }
}
