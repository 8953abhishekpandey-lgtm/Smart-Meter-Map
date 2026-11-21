using System.Globalization;
using System.Text;
using FluentValidation;
using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.Application.Analytics;

public sealed record PolygonStatsDto(
    DateOnly Date,
    double AreaKm2,
    bool Approximate,
    long Meters,
    long Buildings,
    long Expected,
    long Success,
    long Failure,
    double SuccessPct,
    double FailurePct,
    HealthBand Band,
    HealthCountsDto Health,
    IReadOnlyList<BreakdownDto> ByNsp,
    IReadOnlyList<BreakdownDto> ByManufacturer);

/// <summary>Lasso / polygon selection. <see cref="Ring"/> is the outer ring as [lon, lat] pairs (WGS84).</summary>
public sealed record AnalyzePolygonQuery(IReadOnlyList<double[]> Ring, FleetFilter Filter, DateOnly? Date) : IRequest<PolygonStatsDto>;

public sealed class AnalyzePolygonValidator : AbstractValidator<AnalyzePolygonQuery>
{
    public const int MaxVertices = 5000;

    public AnalyzePolygonValidator()
    {
        RuleFor(x => x.Ring).NotNull().Must(r => r.Count is >= 3 and <= MaxVertices)
            .WithMessage($"Polygon must have between 3 and {MaxVertices} vertices.");
        RuleForEach(x => x.Ring).Must(p => p is { Length: 2 } && double.IsFinite(p[0]) && double.IsFinite(p[1])
                                           && p[0] is >= -180 and <= 180 && p[1] is >= -90 and <= 90)
            .WithMessage("Each vertex must be [lon, lat] in WGS84 degrees.");
        RuleFor(x => x.Filter).SetValidator(new FleetFilterValidator());
    }
}

internal sealed class AnalyzePolygonHandler(IAnalyticsStateProvider state, IAnalyticsToolsRepository repo)
    : IRequestHandler<AnalyzePolygonQuery, PolygonStatsDto>
{
    /// <summary>Above this many candidate meters the containment test runs on building centroids instead of meters.</summary>
    public const long ExactMeterLimit = 3_000_000;

    public async Task<PolygonStatsDto> Handle(AnalyzePolygonQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var day = DaySelection.From(snap.Window, q.Date, MetricMode.Day);
        var ring = PolygonGeometry.Close(q.Ring);
        var box = new BoundingBox(ring.Min(p => p.Lon), ring.Min(p => p.Lat), ring.Max(p => p.Lon), ring.Max(p => p.Lat));
        var estimate = await repo.EstimateMetersInBoxAsync(box, ct);
        var approximate = estimate > ExactMeterLimit;
        var rows = await repo.GetPolygonStatsAsync(PolygonGeometry.ToWkt(ring), q.Filter, day, approximate, ct);

        var total = rows.FirstOrDefault(r => r.Breakdown == "total") ?? new PolygonStatRow();
        long Health(HealthClass h) => rows.Where(r => r.Breakdown == "health" && r.Id == (short)h).Sum(r => r.Meters);
        var failure = Math.Max(0, total.Expected - total.Communicating);
        IReadOnlyList<BreakdownDto> Breakdown(string kind, Func<short, string> name) => rows
            .Where(r => r.Breakdown == kind && r.Id.HasValue)
            .Select(r => new BreakdownDto(r.Id!.Value, name(r.Id.Value), r.Meters, r.Expected, r.Communicating,
                Math.Max(0, r.Expected - r.Communicating), Pct.Of(r.Communicating, r.Expected), Pct.Of(r.Expected - r.Communicating, r.Expected), null))
            .OrderByDescending(r => r.Meters).ToList();

        return new PolygonStatsDto(day.Date, Math.Round(GeoMath.RingAreaKm2(ring), 3), approximate,
            total.Meters, total.Buildings, total.Expected, total.Communicating, failure,
            Pct.Of(total.Communicating, total.Expected), Pct.Of(failure, total.Expected),
            HealthBands.FromCounts(total.Expected, failure),
            new HealthCountsDto(Health(HealthClass.AllDay), total.Last7, failure, Health(HealthClass.Dead), Health(HealthClass.Intermittent)),
            Breakdown("nsp", snap.NspName), Breakdown("manufacturer", snap.ManufacturerName));
    }
}

public static class PolygonGeometry
{
    public static IReadOnlyList<(double Lon, double Lat)> Close(IReadOnlyList<double[]> ring)
    {
        var pts = ring.Select(p => (p[0], p[1])).ToList();
        if (pts.Count > 0 && pts[0] != pts[^1]) pts.Add(pts[0]);
        if (pts.Count < 4) throw new BadRequestException("Polygon needs at least 3 distinct vertices.");
        return pts;
    }

    public static string ToWkt(IReadOnlyList<(double Lon, double Lat)> closedRing)
    {
        var sb = new StringBuilder("POLYGON((");
        for (var i = 0; i < closedRing.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(closedRing[i].Lon.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(closedRing[i].Lat.ToString("R", CultureInfo.InvariantCulture));
        }
        return sb.Append("))").ToString();
    }
}
