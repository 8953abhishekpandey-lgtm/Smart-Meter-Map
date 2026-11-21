using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;

namespace SmartMeter.Application.Common;

/// <summary>Global dashboard filters (cascading multi-selects + installation lot + health chip).</summary>
public sealed record FleetFilter
{
    public static readonly FleetFilter None = new();

    public IReadOnlyList<short> ZoneIds { get; init; } = [];
    public IReadOnlyList<short> NspIds { get; init; } = [];
    public IReadOnlyList<short> ManufacturerIds { get; init; } = [];
    public DateOnly? InstalledFrom { get; init; }
    public DateOnly? InstalledTo { get; init; }
    public HealthFilter Health { get; init; }

    /// <summary>Exact installation-day ranges are answered from meter-level data (sm.MeterHealth).</summary>
    public bool UsesMeterLevel => InstalledFrom.HasValue || InstalledTo.HasValue;

    public FleetFilter Normalize() => this with
    {
        ZoneIds = ZoneIds.Distinct().Order().ToArray(),
        NspIds = NspIds.Distinct().Order().ToArray(),
        ManufacturerIds = ManufacturerIds.Distinct().Order().ToArray(),
    };

    public FleetFilter WithoutHealth() => this with { Health = HealthFilter.All };

    /// <summary>Canonical string used in cache keys.</summary>
    public string Key =>
        $"z{Join(ZoneIds)}|n{Join(NspIds)}|m{Join(ManufacturerIds)}|i{InstalledFrom:yyyyMMdd}-{InstalledTo:yyyyMMdd}|h{(int)Health}";

    private static string Join(IReadOnlyList<short> ids) => string.Join('.', ids.Distinct().Order());
}

/// <summary>Resolved day selection inside the analytics window.</summary>
public readonly record struct DaySelection(DateOnly Date, int Column, int Bit, MetricMode Metric)
{
    public static DaySelection From(CommWindow window, DateOnly? date, MetricMode metric)
    {
        var d = window.Resolve(date);
        if (!window.Contains(d))
            throw new BadRequestException($"date must be within the analytics window {window.Start:yyyy-MM-dd} .. {window.End:yyyy-MM-dd}.");
        return new DaySelection(d, window.ColumnIndex(d), window.BitIndex(d), metric);
    }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}
