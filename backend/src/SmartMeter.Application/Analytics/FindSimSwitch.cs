using FluentValidation;
using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;

namespace SmartMeter.Application.Analytics;

public sealed record SimSwitchDto(
    string BuildingKey,
    string Name,
    string? Address,
    string Zone,
    double? Lon,
    double? Lat,
    short StrongNspId,
    string StrongNsp,
    long StrongMeters,
    double StrongSuccessPct,
    short WeakNspId,
    string WeakNsp,
    long WeakMeters,
    long WeakFailing,
    double WeakFailurePct);

/// <summary>
/// SIM / NSP switch opportunities: buildings where several NSPs coexist, one is strong
/// (success &gt;= MinStrongSuccessPct) and another is failing (failure &gt;= MinWeakFailurePct).
/// Default metric = 30-day average (robust for a swap decision); Day = the selected day only.
/// </summary>
public sealed record FindSimSwitchQuery(
    FleetFilter Filter,
    DateOnly? Date,
    MetricMode Metric = MetricMode.Window,
    double MinStrongSuccessPct = 85,
    double MinWeakFailurePct = 70,
    int MinMetersPerNsp = 3,
    int Page = 1,
    int PageSize = 50) : IRequest<PagedResult<SimSwitchDto>>, ICacheableQuery
{
    public string CacheKey => $"{Filter.Key}|d{Date:yyyyMMdd}|{Metric}|{MinStrongSuccessPct}|{MinWeakFailurePct}|{MinMetersPerNsp}|{Page}|{PageSize}";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed class FindSimSwitchValidator : AbstractValidator<FindSimSwitchQuery>
{
    public FindSimSwitchValidator()
    {
        RuleFor(x => x.MinStrongSuccessPct).InclusiveBetween(0, 100);
        RuleFor(x => x.MinWeakFailurePct).InclusiveBetween(0, 100);
        RuleFor(x => x.MinMetersPerNsp).InclusiveBetween(1, 100_000);
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 500);
        RuleFor(x => x.Filter).SetValidator(new FleetFilterValidator());
    }
}

internal sealed class FindSimSwitchHandler(IAnalyticsStateProvider state, IAnalyticsToolsRepository repo)
    : IRequestHandler<FindSimSwitchQuery, PagedResult<SimSwitchDto>>
{
    public async Task<PagedResult<SimSwitchDto>> Handle(FindSimSwitchQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var day = DaySelection.From(snap.Window, q.Date, q.Metric);
        var rows = await repo.GetSimSwitchAsync(q.Filter, day, q.MinStrongSuccessPct / 100.0, q.MinWeakFailurePct / 100.0,
            q.MinMetersPerNsp, (q.Page - 1) * q.PageSize, q.PageSize, ct);
        var items = rows.Select(r => new SimSwitchDto(
            r.BuildingKey, r.Name, r.Address, snap.ZoneName(r.ZoneId), r.Lon, r.Lat,
            r.StrongNspId, snap.NspName(r.StrongNspId), r.StrongMeters, Pct.Of(r.StrongCommunicating, r.StrongExpected),
            r.WeakNspId, snap.NspName(r.WeakNspId), r.WeakMeters, Math.Max(0, r.WeakExpected - r.WeakCommunicating),
            Pct.Of(r.WeakExpected - r.WeakCommunicating, r.WeakExpected))).ToList();
        return new PagedResult<SimSwitchDto>(items, q.Page, q.PageSize, rows.Count > 0 ? rows[0].TotalCount : 0);
    }
}
