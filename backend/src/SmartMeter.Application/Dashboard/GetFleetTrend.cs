using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;

namespace SmartMeter.Application.Dashboard;

public sealed record TrendDto(IReadOnlyList<TrendPointDto> Points);

public sealed record GetFleetTrendQuery(FleetFilter Filter) : IRequest<TrendDto>, ICacheableQuery
{
    public string CacheKey => Filter.Key;
}

internal sealed class GetFleetTrendHandler(IAnalyticsStateProvider state, IFleetReadRepository repo) : IRequestHandler<GetFleetTrendQuery, TrendDto>
{
    public async Task<TrendDto> Handle(GetFleetTrendQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var series = await repo.GetTrendAsync(q.Filter, snap, ct);
        return new TrendDto(TrendBuilder.Build(snap.Window, series.Expected, series.Communicating));
    }
}

public static class TrendBuilder
{
    public static IReadOnlyList<TrendPointDto> Build(Domain.Analytics.CommWindow window, long[] expected, long[] communicating)
    {
        var points = new List<TrendPointDto>(window.Days);
        for (var k = 0; k < window.Days && k < expected.Length; k++)
            points.Add(new TrendPointDto(window.DateOfColumn(k + 1), expected[k], communicating[k], Pct.Of(communicating[k], expected[k])));
        return points;
    }
}
