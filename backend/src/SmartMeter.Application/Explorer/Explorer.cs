using FluentValidation;
using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;

namespace SmartMeter.Application.Explorer;

public sealed record ZoneSummaryDto(
    short ZoneId, string Name, long Meters, int? Buildings, long Expected, long Success, long Failure,
    double SuccessPct, double FailurePct, HealthBand Band, HealthCountsDto Health, double[]? Extent);

/// <summary>Level 1 of the Zone -> Building -> Meter navigator.</summary>
public sealed record GetZonesQuery(FleetFilter Filter, DateOnly? Date, MetricMode Metric) : IRequest<IReadOnlyList<ZoneSummaryDto>>, ICacheableQuery
{
    public string CacheKey => $"{Filter.Key}|d{Date:yyyyMMdd}|{Metric}";
}

internal sealed class GetZonesHandler(IAnalyticsStateProvider state, IFleetReadRepository repo)
    : IRequestHandler<GetZonesQuery, IReadOnlyList<ZoneSummaryDto>>
{
    public async Task<IReadOnlyList<ZoneSummaryDto>> Handle(GetZonesQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var day = DaySelection.From(snap.Window, q.Date, q.Metric, q.Filter);
        var rows = await repo.GetZonesAsync(q.Filter, day, ct);
        return rows.Select(r =>
        {
            var n = day.Metric == MetricMode.Window
                ? DayCounts.From(r.ExpectedWindow, r.CommunicatingWindow, day)
                : DayCounts.From(r.Expected, r.Communicating, day);
            double[]? extent = r.MinLon is { } a && r.MinLat is { } b && r.MaxLon is { } c && r.MaxLat is { } d ? [a, b, c, d] : null;
            return new ZoneSummaryDto(r.ZoneId, snap.ZoneName(r.ZoneId), r.Meters, r.BuildingCount, n.Expected, n.Communicating, n.Failing,
                n.SuccessPct, n.FailurePct, n.Band,
                new HealthCountsDto(r.AllDay, r.Last7, r.TodayFail, r.Dead, r.Intermittent), extent);
        }).OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed record BuildingListItemDto(
    string BuildingKey, string Name, string? Address, bool IsVirtual, double? Lon, double? Lat, long Meters,
    long Expected, long Success, long Failure, double FailurePct, HealthBand Band, int NspCount);

/// <summary>Level 2 of the navigator: buildings of a zone, sortable / searchable / paged.</summary>
public sealed record GetZoneBuildingsQuery(
    short ZoneId, FleetFilter Filter, DateOnly? Date, MetricMode Metric, string? Search, BuildingSort Sort,
    int Page = 1, int PageSize = 50) : IRequest<PagedResult<BuildingListItemDto>>, ICacheableQuery
{
    public string CacheKey => $"{ZoneId}|{Filter.Key}|d{Date:yyyyMMdd}|{Metric}|{Search}|{Sort}|{Page}|{PageSize}";
}

public sealed class GetZoneBuildingsValidator : AbstractValidator<GetZoneBuildingsQuery>
{
    public GetZoneBuildingsValidator()
    {
        RuleFor(x => x.Search).MaximumLength(100);
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 500);
        RuleFor(x => x.Sort).IsInEnum();
    }
}

internal sealed class GetZoneBuildingsHandler(IAnalyticsStateProvider state, IFleetReadRepository repo)
    : IRequestHandler<GetZoneBuildingsQuery, PagedResult<BuildingListItemDto>>
{
    public async Task<PagedResult<BuildingListItemDto>> Handle(GetZoneBuildingsQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        if (!snap.Zones.ContainsKey(q.ZoneId)) throw new NotFoundException("Zone", q.ZoneId.ToString());
        var day = DaySelection.From(snap.Window, q.Date, q.Metric, q.Filter);
        var rows = await repo.GetZoneBuildingsAsync(q.ZoneId, q.Filter, day, q.Search, q.Sort, (q.Page - 1) * q.PageSize, q.PageSize, ct);
        var items = rows.Select(r =>
        {
            var n = DayCounts.From(r.Expected, r.Communicating, day);
            return new BuildingListItemDto(r.BuildingKey, r.Name, r.Address, r.IsVirtual, r.Lon, r.Lat, r.Meters, n.Expected,
                n.Communicating, n.Failing, n.FailurePct, n.Band, r.NspCount);
        }).ToList();
        return new PagedResult<BuildingListItemDto>(items, q.Page, q.PageSize, rows.Count > 0 ? rows[0].TotalCount : 0);
    }
}
