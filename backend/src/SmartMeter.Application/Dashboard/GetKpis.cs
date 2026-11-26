using FluentValidation;
using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;

namespace SmartMeter.Application.Dashboard;

public sealed record KpiDto(
    DateOnly Date,
    MetricMode Metric,
    long TotalMeters,
    long ExpectedMeters,
    long SuccessCount,
    double SuccessPct,
    long FailureCount,
    double FailurePct,
    double? WindowSuccessPct,
    HealthCountsDto Health,
    long FleetMeters,
    string Source);

public sealed record GetKpisQuery(FleetFilter Filter, DateOnly? Date, MetricMode Metric) : IRequest<KpiDto>, ICacheableQuery
{
    public string CacheKey => $"{Filter.Key}|d{Date:yyyyMMdd}|{Metric}";
}

public sealed class GetKpisValidator : AbstractValidator<GetKpisQuery>
{
    public GetKpisValidator() => RuleFor(x => x.Filter).SetValidator(new FleetFilterValidator());
}

internal sealed class GetKpisHandler(IAnalyticsStateProvider state, IFleetReadRepository repo) : IRequestHandler<GetKpisQuery, KpiDto>
{
    public async Task<KpiDto> Handle(GetKpisQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var day = DaySelection.From(snap.Window, q.Date, q.Metric, q.Filter);
        var r = await repo.GetKpisAsync(q.Filter, snap, day, ct);
        return Build(q.Filter, day, snap.Window.Days, r);
    }

    internal static KpiDto Build(FleetFilter filter, DaySelection day, int windowDays, KpiRow r)
    {
        var health = new HealthCountsDto(r.AllDay, r.Last7, r.TodayFail, r.Dead, r.Intermittent);
        var source = filter.UsesMeterLevel ? "meter-level" : "summary";

        if (filter.Health == HealthFilter.TodayFail)
        {
            // The matched set is "meters failing on the selected day": by definition 0% success.
            return new KpiDto(day.Date, MetricMode.Day, r.TodayFail, r.TodayFail, 0, 0, r.TodayFail,
                r.TodayFail > 0 ? 100 : 0, null, health, r.AllMeters, source);
        }

        // one day, or the average day of the chosen range; percentages from the raw meter-day totals
        var n = day.Metric == MetricMode.Window
            ? DayCounts.From(r.ExpectedWindow, r.CommunicatingWindow, day)
            : DayCounts.From(r.Expected, r.Communicating, day);
        return new KpiDto(day.Date, day.Metric, r.TotalMeters, n.Expected, n.Communicating, n.SuccessPct,
            n.Failing, n.FailurePct, Pct.Of(r.CommunicatingWindow, r.ExpectedWindow), health, r.AllMeters, source);
    }
}
