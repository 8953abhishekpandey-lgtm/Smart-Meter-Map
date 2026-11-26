using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;

namespace SmartMeter.Application.Buildings;

public sealed record BuildingDetailDto(
    string BuildingKey,
    string? BuildingId,
    bool IsVirtual,
    string Name,
    string? Address,
    short ZoneId,
    string Zone,
    double? Lon,
    double? Lat,
    long TotalMeters,
    DateOnly Date,
    long Expected,
    long Success,
    long Failure,
    double SuccessPct,
    double FailurePct,
    HealthBand Band,
    double WindowSuccessPct,
    HealthCountsDto Health,
    IReadOnlyList<BreakdownDto> ByNsp,
    IReadOnlyList<BreakdownDto> ByManufacturer,
    IReadOnlyList<TrendPointDto> Trend);

/// <param name="Metric">Day = figures of <paramref name="Date"/>; Window = average day over From..To (default: the whole window).</param>
public sealed record GetBuildingDetailQuery(string BuildingKey, DateOnly? Date, MetricMode Metric = MetricMode.Day,
    DateOnly? From = null, DateOnly? To = null) : IRequest<BuildingDetailDto>, ICacheableQuery
{
    public string CacheKey => $"{BuildingKey}|d{Date:yyyyMMdd}|{Metric}|p{From:yyyyMMdd}-{To:yyyyMMdd}";
}

internal sealed class GetBuildingDetailHandler(IAnalyticsStateProvider state, IBuildingReadRepository repo)
    : IRequestHandler<GetBuildingDetailQuery, BuildingDetailDto>
{
    public async Task<BuildingDetailDto> Handle(GetBuildingDetailQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var day = DaySelection.From(snap.Window, q.Date, q.Metric, q.From, q.To);
        var building = await repo.GetBuildingAsync(q.BuildingKey, ct) ?? throw new NotFoundException("Building", q.BuildingKey);
        var slices = await repo.GetSlicesAsync(q.BuildingKey, ct);
        return BuildingAggregator.Build(building, slices, snap, day);
    }
}

/// <summary>Pure aggregation of a building's summary slices (unit-tested).</summary>
public static class BuildingAggregator
{
    public static BuildingDetailDto Build(BuildingRow b, IReadOnlyList<BuildingSliceRow> slices, AnalyticsSnapshot snap, DaySelection day)
    {
        var w = snap.Window;
        var k = day.Column - 1;
        var range = day.Metric == MetricMode.Window;
        // the selected day, or the average day of the chosen range (one stored column per day of the window)
        long DayE(BuildingSliceRow s) => range ? SumRange(s.E, day) : s.E[k];
        long DayC(BuildingSliceRow s) => range ? SumRange(s.C, day) : s.C[k];
        long RangeE(BuildingSliceRow s) => range ? SumRange(s.E, day) : s.EligTotal;
        long RangeC(BuildingSliceRow s) => range ? SumRange(s.C, day) : s.CommTotal;
        long total = 0, expected = 0, success = 0, eligTotal = 0, commTotal = 0, allDay = 0, last7 = 0, dead = 0, intermittent = 0;
        var trendE = new long[w.Days];
        var trendC = new long[w.Days];
        foreach (var s in slices)
        {
            total += s.MeterCount;
            expected += DayE(s);
            success += DayC(s);
            eligTotal += RangeE(s);
            commTotal += RangeC(s);
            if (s.HealthClass == (byte)HealthClass.AllDay) allDay += s.MeterCount;
            if (s.HealthClass == (byte)HealthClass.Dead) dead += s.MeterCount;
            if (s.HealthClass == (byte)HealthClass.Intermittent) intermittent += s.MeterCount;
            if (s.ActiveLast7) last7 += s.MeterCount;
            for (var i = 0; i < w.Days; i++)
            {
                trendE[i] += s.E[i];
                trendC[i] += s.C[i];
            }
        }
        var counts = DayCounts.From(expected, success, day);

        IReadOnlyList<BreakdownDto> Breakdown(Func<BuildingSliceRow, short> key, Func<short, string> name) =>
            slices.GroupBy(key)
                  .Select(g =>
                  {
                      long m = g.Sum(x => x.MeterCount), et = g.Sum(RangeE), ct = g.Sum(RangeC);
                      var n = DayCounts.From(g.Sum(DayE), g.Sum(DayC), day);
                      return new BreakdownDto(g.Key, name(g.Key), m, n.Expected, n.Communicating, n.Failing, n.SuccessPct, n.FailurePct, Pct.Of(ct, et));
                  })
                  .OrderByDescending(x => x.Meters).ToList();

        return new BuildingDetailDto(
            b.BuildingKey, b.BuildingId, b.IsVirtual, b.Name, b.Address, b.ZoneId, snap.ZoneName(b.ZoneId), b.Lon, b.Lat,
            total, day.Date, counts.Expected, counts.Communicating, counts.Failing, counts.SuccessPct, counts.FailurePct,
            counts.Band, Pct.Of(commTotal, eligTotal),
            new HealthCountsDto(allDay, last7, counts.Failing, dead, intermittent),
            Breakdown(s => s.NspId, snap.NspName),
            Breakdown(s => s.ManufacturerId, snap.ManufacturerName),
            Dashboard.TrendBuilder.Build(w, trendE, trendC));
    }

    private static long SumRange(long[] perDay, DaySelection day)
    {
        long sum = 0;
        for (var i = day.FromColumn - 1; i < day.ToColumn && i < perDay.Length; i++) sum += perDay[i];
        return sum;
    }
}
