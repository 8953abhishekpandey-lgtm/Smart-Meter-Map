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

    /// <summary>
    /// Days averaged when the metric is "window" (date range picker). Null = the whole analytics window.
    /// Every day's file is stored separately, so any range inside the window combines the files of those days.
    /// </summary>
    public DateOnly? PeriodFrom { get; init; }
    public DateOnly? PeriodTo { get; init; }

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
        $"z{Join(ZoneIds)}|n{Join(NspIds)}|m{Join(ManufacturerIds)}|i{InstalledFrom:yyyyMMdd}-{InstalledTo:yyyyMMdd}|h{(int)Health}|p{PeriodFrom:yyyyMMdd}-{PeriodTo:yyyyMMdd}";

    private static string Join(IReadOnlyList<short> ids) => string.Join('.', ids.Distinct().Order());
}

/// <summary>Resolved day selection inside the analytics window.</summary>
public readonly record struct DaySelection(DateOnly Date, int Column, int Bit, MetricMode Metric)
{
    /// <summary>First and last day averaged when <see cref="Metric"/> is Window (the whole window unless a range was chosen).</summary>
    public DateOnly RangeFrom { get; init; }
    public DateOnly RangeTo { get; init; }
    /// <summary>1-based day columns (E/Cnn) of the range.</summary>
    public int FromColumn { get; init; }
    public int ToColumn { get; init; }
    public bool IsFullWindow { get; init; }
    /// <summary>Mask bits of the range days (bit i = window end - i), for meter-level BIT_COUNT.</summary>
    public int RangeMask { get; init; }
    public int RangeDays => ToColumn - FromColumn + 1;

    public static DaySelection From(CommWindow window, DateOnly? date, MetricMode metric) => From(window, date, metric, null, null);

    public static DaySelection From(CommWindow window, DateOnly? date, MetricMode metric, FleetFilter? filter) =>
        From(window, date, metric, filter?.PeriodFrom, filter?.PeriodTo);

    public static DaySelection From(CommWindow window, DateOnly? date, MetricMode metric, DateOnly? rangeFrom, DateOnly? rangeTo)
    {
        var d = window.Resolve(date);
        if (!window.Contains(d))
            throw new BadRequestException($"date must be within the analytics window {window.Start:yyyy-MM-dd} .. {window.End:yyyy-MM-dd}.");

        // the range only applies to the "window" metric; a saved link whose days slid out of the window is clamped into it
        DateOnly from = window.Start, to = window.End;
        if (metric == MetricMode.Window)
        {
            if (rangeFrom is { } rf) from = rf < window.Start ? window.Start : rf > window.End ? window.End : rf;
            if (rangeTo is { } rt) to = rt > window.End ? window.End : rt < window.Start ? window.Start : rt;
            if (from > to) throw new BadRequestException("The range start (from) must not be after its end (to).");
        }
        int hiBit = window.BitIndex(from), loBit = window.BitIndex(to);
        var mask = (int)(((1L << (hiBit + 1)) - 1) & ~((1L << loBit) - 1));
        return new DaySelection(d, window.ColumnIndex(d), window.BitIndex(d), metric)
        {
            RangeFrom = from,
            RangeTo = to,
            FromColumn = window.ColumnIndex(from),
            ToColumn = window.ColumnIndex(to),
            IsFullWindow = from == window.Start && to == window.End,
            RangeMask = mask,
        };
    }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

/// <summary>
/// Counts for one day, or the average day of a range, with percentages and the colour band taken from the raw
/// meter-day totals (exact), and the displayed counts rounded to whole meters.
/// </summary>
public readonly record struct DayCounts(long Expected, long Communicating, long Failing, double SuccessPct, double FailurePct, HealthBand Band)
{
    public static DayCounts From(long rawExpected, long rawCommunicating, DaySelection day)
    {
        var rawFailing = Math.Max(0, rawExpected - rawCommunicating);
        var days = day.Metric == MetricMode.Window ? Math.Max(1, day.RangeDays) : 1;
        long expected = (long)Math.Round(rawExpected / (double)days), communicating = (long)Math.Round(rawCommunicating / (double)days);
        return new DayCounts(expected, communicating, Math.Max(0, expected - communicating),
            Pct.Of(rawCommunicating, rawExpected), Pct.Of(rawFailing, rawExpected), HealthBands.FromCounts(rawExpected, rawFailing));
    }
}
