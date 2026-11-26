using FluentValidation;
using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.Application.Map;

public sealed record BubbleDto(
    string Id,
    string Kind,
    double Lon,
    double Lat,
    long Meters,
    long Expected,
    long Communicating,
    long Failing,
    double FailurePct,
    HealthBand Band,
    string? BuildingKey,
    string? Name,
    string? Address,
    double[]? Extent);

public sealed record BubblesDto(
    int Zoom, string Mode, byte? Level, double? CellDeg, DateOnly Date, MetricMode Metric, bool Truncated,
    IReadOnlyList<BubbleDto> Bubbles);

/// <summary>
/// Viewport query: never returns raw meters. Zoom &lt;= 14 aggregates the pre-built grid pyramid,
/// zoom &gt;= 15 returns building bubbles. The bbox is snapped outward so neighbouring pans share
/// cache entries ("hot viewport" caching).
/// </summary>
public sealed record GetMapBubblesQuery(BoundingBox Bbox, int Zoom, FleetFilter Filter, DateOnly? Date, MetricMode Metric, int Limit = 5000)
    : IRequest<BubblesDto>, ICacheableQuery
{
    public BoundingBox Snapped => MapViewport.Snap(Bbox, Zoom);
    public string CacheKey => $"{Snapped}|z{Zoom}|{Filter.Key}|d{Date:yyyyMMdd}|{Metric}|l{Limit}";
}

public static class MapViewport
{
    /// <summary>Snaps a viewport outward to ~1/4 of a 256px tile at the current zoom.</summary>
    public static BoundingBox Snap(BoundingBox bbox, int zoom)
    {
        var step = 360.0 / Math.Pow(2, Math.Clamp(zoom, 0, 22)) / 4.0;
        return bbox.Clamp().SnapOutward(step).Clamp();
    }
}

public sealed class GetMapBubblesValidator : AbstractValidator<GetMapBubblesQuery>
{
    public GetMapBubblesValidator()
    {
        RuleFor(x => x.Bbox.IsValid).Equal(true).WithMessage("bbox must be minX,minY,maxX,maxY with min < max.");
        RuleFor(x => x.Zoom).InclusiveBetween(0, 23);
        RuleFor(x => x.Limit).InclusiveBetween(1, 20000);
        RuleFor(x => x.Filter).SetValidator(new FleetFilterValidator());
    }
}

internal sealed class GetMapBubblesHandler(IAnalyticsStateProvider state, IFleetReadRepository repo)
    : IRequestHandler<GetMapBubblesQuery, BubblesDto>
{
    public async Task<BubblesDto> Handle(GetMapBubblesQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var day = DaySelection.From(snap.Window, q.Date, q.Metric, q.Filter);
        var level = MapResolution.ForZoom(q.Zoom, snap.GridLevels);
        var rows = await repo.GetBubblesAsync(q.Snapped, level, q.Filter, snap, day, q.Limit + 1, ct);
        var truncated = rows.Count > q.Limit;

        var bubbles = new List<BubbleDto>(Math.Min(rows.Count, q.Limit));
        // "fail on selected day" always reads the selected day, never a range
        var metricDay = q.Filter.Health == HealthFilter.TodayFail ? day with { Metric = MetricMode.Day } : day;
        foreach (var r in rows.Take(q.Limit))
        {
            var counts = DayCounts.From(r.Expected, r.Communicating, metricDay);
            var isBuilding = level is null || r.BuildingKey is not null && r.CellX is null;
            string id;
            double[]? extent = null;
            if (level is not null && r.CellX is { } cx && r.CellY is { } cy)
            {
                id = $"L{level.Level}:{cx}:{cy}";
                extent = [cx * level.CellDeg, cy * level.CellDeg, (cx + 1) * level.CellDeg, (cy + 1) * level.CellDeg];
            }
            else
            {
                id = r.BuildingKey ?? $"{r.Lon:F5},{r.Lat:F5}";
            }
            bubbles.Add(new BubbleDto(
                id, isBuilding ? "building" : "cell", r.Lon, r.Lat, r.Meters, counts.Expected, counts.Communicating, counts.Failing,
                Math.Round(counts.FailurePct, 1), counts.Band,
                r.BuildingKey, r.Name, r.Address, extent));
        }
        return new BubblesDto(q.Zoom, level is null ? "building" : "grid", level?.Level, level?.CellDeg, day.Date, day.Metric, truncated, bubbles);
    }
}
