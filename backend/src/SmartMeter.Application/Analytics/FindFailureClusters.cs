using System.Diagnostics;
using FluentValidation;
using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.Application.Analytics;

public sealed record NspFailureDto(short NspId, string Nsp, long Meters, long Expected, long Failing, double FailurePct);

public sealed record FailureClusterDto(
    int Rank,
    string Id,
    int Buildings,
    long Meters,
    long Expected,
    long Failing,
    double FailurePct,
    HealthBand Band,
    double Lon,
    double Lat,
    double[] Extent,
    IReadOnlyList<double[]> Hull,
    IReadOnlyList<NspFailureDto> Nsps,
    IReadOnlyList<string> TopBuildingKeys);

public sealed record FailureClustersDto(
    DateOnly Date, MetricMode Metric, int HotBuildings, int ClustersFound, bool Truncated, long ElapsedMs,
    IReadOnlyList<FailureClusterDto> Clusters);

/// <summary>
/// Detects high-failure geographic pockets: buildings whose failure rate exceeds the threshold
/// are clustered with DBSCAN (eps in metres); clusters with at least MinMeters meters are ranked
/// by failing meters, with an NSP breakdown for field-visit targeting.
/// </summary>
public sealed record FindFailureClustersQuery(
    FleetFilter Filter,
    DateOnly? Date,
    MetricMode Metric = MetricMode.Day,
    int MinMeters = 500,
    double MinFailurePct = 25,
    double EpsMeters = 400,
    int MinPoints = 1,
    BoundingBox? Bbox = null,
    int Limit = 50) : IRequest<FailureClustersDto>, ICacheableQuery
{
    public const int MaxHotBuildings = 250_000;
    public string CacheKey => $"{Filter.Key}|d{Date:yyyyMMdd}|{Metric}|{MinMeters}|{MinFailurePct}|{EpsMeters}|{MinPoints}|{Bbox}|{Limit}";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed class FindFailureClustersValidator : AbstractValidator<FindFailureClustersQuery>
{
    public FindFailureClustersValidator()
    {
        RuleFor(x => x.MinMeters).InclusiveBetween(1, 10_000_000);
        RuleFor(x => x.MinFailurePct).InclusiveBetween(0, 100);
        RuleFor(x => x.EpsMeters).InclusiveBetween(10, 20_000);
        RuleFor(x => x.MinPoints).InclusiveBetween(1, 1000);
        RuleFor(x => x.Limit).InclusiveBetween(1, 500);
        RuleFor(x => x.Bbox!.Value.IsValid).Equal(true).When(x => x.Bbox.HasValue).WithMessage("bbox is invalid.");
        RuleFor(x => x.Filter).SetValidator(new FleetFilterValidator());
    }
}

internal sealed class FindFailureClustersHandler(IAnalyticsStateProvider state, IAnalyticsToolsRepository repo)
    : IRequestHandler<FindFailureClustersQuery, FailureClustersDto>
{
    public async Task<FailureClustersDto> Handle(FindFailureClustersQuery q, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var snap = await state.GetAsync(ct);
        var day = DaySelection.From(snap.Window, q.Date, q.Metric, q.Filter);
        var rows = await repo.GetHotBuildingsAsync(q.Filter, day, q.MinFailurePct / 100.0, q.Bbox, FindFailureClustersQuery.MaxHotBuildings, ct);
        var (clusters, hot, truncated) = FailureClusterer.Cluster(rows, q, snap);
        return new FailureClustersDto(day.Date, day.Metric, hot, clusters.Count, truncated, sw.ElapsedMilliseconds, clusters.Take(q.Limit).ToList());
    }
}

public static class FailureClusterer
{
    public static (IReadOnlyList<FailureClusterDto> Clusters, int HotBuildings, bool Truncated) Cluster(
        IReadOnlyList<HotBuildingNspRow> rows, FindFailureClustersQuery q, AnalyticsSnapshot snap)
    {
        var buildings = rows.GroupBy(r => r.BuildingKey)
            .Select(g => new
            {
                Key = g.Key,
                g.First().Lon,
                g.First().Lat,
                Meters = g.Sum(x => x.Meters),
                Expected = g.Sum(x => x.Expected),
                Comm = g.Sum(x => x.Communicating),
                Nsps = g.ToList(),
            })
            .ToList();
        var truncated = buildings.Count >= FindFailureClustersQuery.MaxHotBuildings;
        var labels = GeoDbscan.Cluster(buildings.Select(b => (b.Lon, b.Lat)).ToList(), q.EpsMeters, q.MinPoints);

        var clusters = buildings
            .Select((b, i) => (b, label: labels[i]))
            .Where(x => x.label != GeoDbscan.Noise)
            .GroupBy(x => x.label, x => x.b)
            .Select(g =>
            {
                var members = g.ToList();
                long meters = members.Sum(m => m.Meters), expected = members.Sum(m => m.Expected), comm = members.Sum(m => m.Comm);
                var failing = Math.Max(0, expected - comm);
                double lon = members.Sum(m => m.Lon * m.Meters) / Math.Max(1, meters);
                double lat = members.Sum(m => m.Lat * m.Meters) / Math.Max(1, meters);
                var nsps = members.SelectMany(m => m.Nsps).GroupBy(n => n.NspId)
                    .Select(n =>
                    {
                        long e = n.Sum(x => x.Expected), c = n.Sum(x => x.Communicating);
                        return new NspFailureDto(n.Key, snap.NspName(n.Key), n.Sum(x => x.Meters), e, Math.Max(0, e - c), Pct.Of(e - c, e));
                    })
                    .OrderByDescending(n => n.Failing).ToList();
                var hull = GeoMath.ConvexHull(members.Select(m => (m.Lon, m.Lat))).Select(p => new[] { Math.Round(p.Lon, 6), Math.Round(p.Lat, 6) }).ToList();
                double[] extent = [members.Min(m => m.Lon), members.Min(m => m.Lat), members.Max(m => m.Lon), members.Max(m => m.Lat)];
                return new FailureClusterDto(0, "", members.Count, meters, expected, failing, Pct.Of(failing, expected),
                    HealthBands.FromCounts(expected, failing), Math.Round(lon, 6), Math.Round(lat, 6), extent, hull, nsps,
                    members.OrderByDescending(m => m.Expected - m.Comm).Take(10).Select(m => m.Key).ToList());
            })
            .Where(c => c.Meters >= q.MinMeters && c.FailurePct > q.MinFailurePct)
            .OrderByDescending(c => c.Failing)
            .Select((c, i) => c with { Rank = i + 1, Id = $"C{i + 1:000}" })
            .ToList();
        return (clusters, buildings.Count, truncated);
    }
}
