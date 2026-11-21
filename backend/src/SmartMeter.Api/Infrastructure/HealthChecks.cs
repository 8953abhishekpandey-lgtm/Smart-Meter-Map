using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartMeter.Application.Abstractions;

namespace SmartMeter.Api.Infrastructure;

/// <summary>Degraded when the pre-aggregated generation is stale (refresh failing or no new MDM data).</summary>
internal sealed class AnalyticsFreshnessHealthCheck(IAnalyticsStateProvider state, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var s = await state.GetAsync(ct);
        var data = new Dictionary<string, object>
        {
            ["dataVersion"] = s.DataVersion,
            ["windowStart"] = s.Window.Start.ToString("yyyy-MM-dd"),
            ["windowEnd"] = s.Window.End.ToString("yyyy-MM-dd"),
            ["refreshedAtUtc"] = s.RefreshedAtUtc?.ToString("O") ?? "never",
        };
        if (s.RefreshedAtUtc is null) return HealthCheckResult.Degraded("Analytics have never been refreshed.", data: data);
        var now = clock.GetUtcNow().UtcDateTime;
        if (now - s.RefreshedAtUtc.Value > TimeSpan.FromHours(36))
            return HealthCheckResult.Degraded("Last analytics refresh is older than 36 hours.", data: data);
        if (DateOnly.FromDateTime(now).DayNumber - s.Window.End.DayNumber > 3)
            return HealthCheckResult.Degraded("Newest MDM day in the window is more than 3 days old.", data: data);
        return HealthCheckResult.Healthy("Analytics are current.", data);
    }
}

internal static class HealthResponse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static Task WriteAsync(HttpContext http, HealthReport report)
    {
        http.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                description = e.Value.Description,
                data = e.Value.Data,
                error = e.Value.Exception?.Message,
            }),
        };
        return http.Response.WriteAsync(JsonSerializer.Serialize(payload, Json));
    }

    public static HealthCheckOptions Options(Func<HealthCheckRegistration, bool>? predicate = null) => new()
    {
        Predicate = predicate ?? (_ => true),
        ResponseWriter = WriteAsync,
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
        },
    };
}
