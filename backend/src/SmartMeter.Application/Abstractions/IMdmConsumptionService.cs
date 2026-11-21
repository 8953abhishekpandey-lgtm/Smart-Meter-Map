namespace SmartMeter.Application.Abstractions;

/// <summary>Daily energy reading (kWh) for one meter; null when the MDM has no reading for that day.</summary>
public readonly record struct DailyReading(DateOnly Date, double? Kwh);

public sealed record MeterConsumption(string MeterId, IReadOnlyList<DailyReading> Readings);

public sealed record ConsumptionResult(IReadOnlyList<MeterConsumption> Meters, string Source, bool Partial, string? Message);

/// <summary>
/// Live MDM consumption hook. Implementations: a deterministic mock (default) and an HTTP client with
/// Polly retry + circuit breaker (Microsoft.Extensions.Http.Resilience).
/// </summary>
public interface IMdmConsumptionService
{
    Task<ConsumptionResult> GetDailyConsumptionAsync(IReadOnlyList<string> meterIds, DateOnly from, DateOnly to, CancellationToken ct);
}
