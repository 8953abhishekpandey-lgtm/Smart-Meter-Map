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

public sealed record GetBuildingDetailQuery(string BuildingKey, DateOnly? Date) : IRequest<BuildingDetailDto>, ICacheableQuery
{
    public string CacheKey => $"{BuildingKey}|d{Date:yyyyMMdd}";
}

internal sealed class GetBuildingDetailHandler(IAnalyticsStateProvider state, IBuildingReadRepository repo)
    : IRequestHandler<GetBuildingDetailQuery, BuildingDetailDto>
{
    public async Task<BuildingDetailDto> Handle(GetBuildingDetailQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var day = DaySelection.From(snap.Window, q.Date, MetricMode.Day);
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
        long total = 0, expected = 0, success = 0, eligTotal = 0, commTotal = 0, allDay = 0, last7 = 0, dead = 0, intermittent = 0;
        var trendE = new long[w.Days];
        var trendC = new long[w.Days];
        foreach (var s in slices)
        {
            total += s.MeterCount;
            expected += s.E[k];
            success += s.C[k];
            eligTotal += s.EligTotal;
            commTotal += s.CommTotal;
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
        var failure = Math.Max(0, expected - success);

        IReadOnlyList<BreakdownDto> Breakdown(Func<BuildingSliceRow, short> key, Func<short, string> name) =>
            slices.GroupBy(key)
                  .Select(g =>
                  {
                      long m = g.Sum(x => x.MeterCount), e = g.Sum(x => x.E[k]), c = g.Sum(x => x.C[k]);
                      long et = g.Sum(x => x.EligTotal), ct = g.Sum(x => x.CommTotal);
                      return new BreakdownDto(g.Key, name(g.Key), m, e, c, Math.Max(0, e - c), Pct.Of(c, e), Pct.Of(e - c, e), Pct.Of(ct, et));
                  })
                  .OrderByDescending(x => x.Meters).ToList();

        return new BuildingDetailDto(
            b.BuildingKey, b.BuildingId, b.IsVirtual, b.Name, b.Address, b.ZoneId, snap.ZoneName(b.ZoneId), b.Lon, b.Lat,
            total, day.Date, expected, success, failure, Pct.Of(success, expected), Pct.Of(failure, expected),
            HealthBands.FromCounts(expected, failure), Pct.Of(commTotal, eligTotal),
            new HealthCountsDto(allDay, last7, failure, dead, intermittent),
            Breakdown(s => s.NspId, snap.NspName),
            Breakdown(s => s.ManufacturerId, snap.ManufacturerName),
            Dashboard.TrendBuilder.Build(w, trendE, trendC));
    }
}
