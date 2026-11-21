using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMeter.Application.Abstractions;

namespace SmartMeter.Infrastructure.Mdm;

public sealed class MdmOptions
{
    public const string Section = "Mdm";
    /// <summary>"Mock" (deterministic simulated readings) or "Http" (live MDM REST API).</summary>
    public string Mode { get; set; } = "Mock";
    public string? BaseUrl { get; set; }
    public string DailyConsumptionPath { get; set; } = "api/v1/consumption/daily";
    public string ApiKeyHeader { get; set; } = "X-Api-Key";
    /// <summary>Supply via environment / secret store (Mdm__ApiKey), never in appsettings.json.</summary>
    public string? ApiKey { get; set; }
    public int BatchSize { get; set; } = 500;
    public int MaxParallelBatches { get; set; } = 4;
    public int RetryCount { get; set; } = 3;
    public int AttemptTimeoutSeconds { get; set; } = 10;
    public int TotalTimeoutSeconds { get; set; } = 60;
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;
    public int CircuitBreakerBreakSeconds { get; set; } = 30;

    public bool IsHttp => string.Equals(Mode, "Http", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Deterministic simulated MDM: stable per-meter base load, weekly cycle, ~4% missing reads.</summary>
internal sealed class MockMdmConsumptionService : IMdmConsumptionService
{
    public Task<ConsumptionResult> GetDailyConsumptionAsync(IReadOnlyList<string> meterIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var meters = new List<MeterConsumption>(meterIds.Count);
        foreach (var id in meterIds)
        {
            ct.ThrowIfCancellationRequested();
            var h = Fnv1a(id);
            var baseKwh = 3.0 + h % 1500 / 100.0;        // 3 .. 18 kWh/day
            var readings = new List<DailyReading>(to.DayNumber - from.DayNumber + 1);
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var dh = Fnv1a(id, d.DayNumber);
                if (dh % 100 < 4) { readings.Add(new DailyReading(d, null)); continue; }
                var weekly = 1 + 0.12 * Math.Sin(2 * Math.PI * d.DayNumber / 7.0);
                var noise = 0.85 + dh % 300 / 1000.0;
                readings.Add(new DailyReading(d, Math.Round(baseKwh * weekly * noise, 3)));
            }
            meters.Add(new MeterConsumption(id, readings));
        }
        return Task.FromResult(new ConsumptionResult(meters, "mock", false, "Simulated MDM consumption (Mdm:Mode = Mock)."));
    }

    private static uint Fnv1a(string s, int salt = 0)
    {
        var hash = 2166136261u ^ (uint)salt;
        foreach (var ch in s) { hash ^= ch; hash *= 16777619u; }
        hash ^= (uint)salt * 2654435761u;
        hash *= 16777619u;
        return hash;
    }
}

/// <summary>
/// Live MDM REST client. Batches meter ids, runs a bounded number of requests in parallel; each request
/// goes through the resilience pipeline (retry w/ jitter, circuit breaker, per-attempt + total timeout)
/// configured in DependencyInjection. Partial failures return partial totals flagged as such.
/// Contract (adapt in one place if your MDM differs):
///   POST {BaseUrl}/{DailyConsumptionPath}  { "meterIds": [...], "from": "yyyy-MM-dd", "to": "yyyy-MM-dd" }
///   200 { "meters": [ { "meterId": "...", "readings": [ { "date": "yyyy-MM-dd", "kwh": 12.3 } ] } ] }
/// </summary>
internal sealed class HttpMdmConsumptionService(HttpClient http, IOptions<MdmOptions> options, ILogger<HttpMdmConsumptionService> logger)
    : IMdmConsumptionService
{
    public async Task<ConsumptionResult> GetDailyConsumptionAsync(IReadOnlyList<string> meterIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var o = options.Value;
        var batches = meterIds.Chunk(Math.Max(1, o.BatchSize)).ToList();
        var results = new ConcurrentBag<MeterConsumption>();
        var failures = 0;
        Exception? lastError = null;
        await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.MaxParallelBatches), CancellationToken = ct },
            async (batch, token) =>
            {
                try
                {
                    using var response = await http.PostAsJsonAsync(o.DailyConsumptionPath, new MdmRequest(batch, from, to), token);
                    response.EnsureSuccessStatusCode();
                    var body = await response.Content.ReadFromJsonAsync<MdmResponse>(token);
                    foreach (var m in body?.Meters ?? [])
                        results.Add(new MeterConsumption(m.MeterId, (m.Readings ?? []).Select(r => new DailyReading(r.Date, r.Kwh)).ToList()));
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref failures);
                    lastError = ex;
                    logger.LogWarning(ex, "MDM consumption batch of {Count} meters failed", batch.Length);
                }
            });
        if (batches.Count > 0 && failures == batches.Count)
            throw new HttpRequestException("All MDM consumption requests failed: " + lastError?.Message, lastError);
        return new ConsumptionResult(results.ToList(), "mdm-api", failures > 0,
            failures > 0 ? $"{failures} of {batches.Count} MDM batches failed; totals are partial." : null);
    }

    private sealed record MdmRequest(IReadOnlyList<string> MeterIds, DateOnly From, DateOnly To);
    private sealed record MdmResponse(List<MdmMeter>? Meters);
    private sealed record MdmMeter(string MeterId, List<MdmReading>? Readings);
    private sealed record MdmReading(DateOnly Date, double? Kwh);
}
