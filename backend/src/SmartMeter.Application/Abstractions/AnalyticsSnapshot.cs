using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.Application.Abstractions;

/// <summary>
/// Point-in-time view of the pre-aggregated generation: window, data version (part of every cache
/// key) and the small dimension tables. Cached for a few seconds by the provider.
/// </summary>
public sealed record AnalyticsSnapshot(
    CommWindow Window,
    long DataVersion,
    DateTime? RefreshedAtUtc,
    int? RefreshMs,
    IReadOnlyList<GridLevel> GridLevels,
    IReadOnlyDictionary<short, string> Zones,
    IReadOnlyDictionary<short, string> Nsps,
    IReadOnlyDictionary<short, string> Manufacturers,
    DateOnly? InstalledMin,
    DateOnly? InstalledMax,
    string? StatsJson)
{
    public string ZoneName(short id) => Zones.TryGetValue(id, out var n) ? n : $"Zone {id}";
    public string NspName(short id) => Nsps.TryGetValue(id, out var n) ? n : $"NSP {id}";
    public string ManufacturerName(short id) => Manufacturers.TryGetValue(id, out var n) ? n : $"Manufacturer {id}";
}

public interface IAnalyticsStateProvider
{
    ValueTask<AnalyticsSnapshot> GetAsync(CancellationToken ct);
    void Invalidate();
}
