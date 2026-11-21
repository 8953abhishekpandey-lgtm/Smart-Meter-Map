namespace SmartMeter.Domain.Health;

/// <summary>30/31-day communication health of a single meter (stored as tinyint in sm.MeterHealth).</summary>
public enum HealthClass : byte
{
    /// <summary>Zero successful communication days in the window.</summary>
    Dead = 0,
    /// <summary>Communicated on some, but not all, eligible days.</summary>
    Intermittent = 1,
    /// <summary>Communicated on every eligible day of the window.</summary>
    AllDay = 2,
    /// <summary>Installed after the window end - not yet expected to communicate.</summary>
    NotInstalled = 3,
}

/// <summary>Health quick-filters offered on the dashboard.</summary>
public enum HealthFilter
{
    All = 0,
    AllDay,
    Last7Days,
    TodayFail,
    Dead,
    Intermittent,
}

/// <summary>Map colour band derived from a failure rate.</summary>
public enum HealthBand
{
    None = 0,
    Green,
    Amber,
    Red,
}

public static class HealthBands
{
    /// <summary>Green = 0-20% failure.</summary>
    public const int GreenMaxFailurePct = 20;

    /// <summary>Red = 80-100% failure. Everything in between (21-79%) is Amber.</summary>
    public const int RedMinFailurePct = 80;

    /// <summary>Classifies a bubble by its failure rate, rounded to whole percent so the published
    /// integer bands (0-20 / 21-79 / 80-100) have no gaps.</summary>
    public static HealthBand FromCounts(long expected, long failing)
    {
        if (expected <= 0) return HealthBand.None;
        var pct = FailurePct(expected, failing);
        var rounded = Math.Round(pct, MidpointRounding.AwayFromZero);
        return rounded <= GreenMaxFailurePct ? HealthBand.Green
             : rounded >= RedMinFailurePct ? HealthBand.Red
             : HealthBand.Amber;
    }

    public static double FailurePct(long expected, long failing) =>
        expected <= 0 ? 0 : Math.Clamp(100.0 * failing / expected, 0, 100);

    public static double SuccessPct(long expected, long communicating) =>
        expected <= 0 ? 0 : Math.Clamp(100.0 * communicating / expected, 0, 100);
}
