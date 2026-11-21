namespace SmartMeter.Domain.Analytics;

/// <summary>
/// The rolling 30/31-day communication window that all pre-aggregated data refers to.
/// Storage conventions (see database/03_analytics.sql):
///   * mask bit i      &lt;-&gt; day (End - i)                 bit 0 = most recent day
///   * column E/Ckk    &lt;-&gt; day (Start + kk - 1)           kk = 1..Days, chronological
/// </summary>
public sealed record CommWindow
{
    public const int MaxDays = 31;
    public const int MinDays = 7;

    public CommWindow(DateOnly start, DateOnly end)
    {
        if (end < start) throw new ArgumentException("Window end precedes its start.");
        var days = end.DayNumber - start.DayNumber + 1;
        if (days is < MinDays or > MaxDays) throw new ArgumentOutOfRangeException(nameof(end), days, "Window must span 7-31 days.");
        Start = start;
        End = end;
        Days = days;
    }

    public DateOnly Start { get; }
    public DateOnly End { get; }
    public int Days { get; }

    public bool Contains(DateOnly date) => date >= Start && date <= End;

    /// <summary>Selected day, defaulting to the newest day of the window.</summary>
    public DateOnly Resolve(DateOnly? requested) => requested ?? End;

    /// <summary>1-based chronological day column index (E01..E31).</summary>
    public int ColumnIndex(DateOnly date)
    {
        EnsureContains(date);
        return date.DayNumber - Start.DayNumber + 1;
    }

    /// <summary>Mask bit index (0 = window end).</summary>
    public int BitIndex(DateOnly date)
    {
        EnsureContains(date);
        return End.DayNumber - date.DayNumber;
    }

    public DateOnly DateOfColumn(int columnIndex) => Start.AddDays(columnIndex - 1);

    public IEnumerable<DateOnly> Dates()
    {
        for (var d = Start; d <= End; d = d.AddDays(1)) yield return d;
    }

    /// <summary>Last-7-days mask (bits 0..6).</summary>
    public const int Last7Mask = 0b111_1111;

    /// <summary>
    /// Decodes a meter's masks into a chronological per-day string:
    /// '1' communicated, '0' expected but failed, '-' not yet installed / not expected.
    /// </summary>
    public string DecodeTimeline(int commMask, int eligMask)
    {
        return string.Create(Days, (commMask, eligMask, Days), static (span, s) =>
        {
            for (var k = 0; k < s.Days; k++)
            {
                var bit = s.Days - 1 - k;
                var elig = ((s.eligMask >> bit) & 1) == 1;
                var comm = ((s.commMask >> bit) & 1) == 1;
                span[k] = comm ? '1' : elig ? '0' : '-';
            }
        });
    }

    private void EnsureContains(DateOnly date)
    {
        if (!Contains(date))
            throw new ArgumentOutOfRangeException(nameof(date), date, $"Date must be within the analytics window {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}.");
    }
}

/// <summary>Metric used for KPIs and bubble colours: a single selected day, or the window average.</summary>
public enum MetricMode
{
    Day = 0,
    Window = 1,
}
