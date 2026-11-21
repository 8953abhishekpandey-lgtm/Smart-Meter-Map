using SmartMeter.Domain.Health;

namespace SmartMeter.Application.Common;

public sealed record HealthCountsDto(long AllDay, long Last7Days, long TodayFail, long Dead, long Intermittent);

public sealed record TrendPointDto(DateOnly Date, long Expected, long Communicating, double SuccessPct);

/// <summary>NSP / manufacturer breakdown line.</summary>
public sealed record BreakdownDto(
    short Id, string Name, long Meters, long Expected, long Success, long Failure,
    double SuccessPct, double FailurePct, double? WindowSuccessPct);

public static class Pct
{
    public static double Of(long part, long whole) => whole <= 0 ? 0 : Math.Round(100.0 * part / whole, 2);
}

public static class BandOf
{
    public static HealthBand Counts(long expected, long communicating) =>
        HealthBands.FromCounts(expected, Math.Max(0, expected - communicating));
}
