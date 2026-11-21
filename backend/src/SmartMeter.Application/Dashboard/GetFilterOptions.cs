using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;

namespace SmartMeter.Application.Dashboard;

public sealed record FilterOptionDto(short Id, string Name, long Meters);

public sealed record FilterOptionsDto(
    IReadOnlyList<FilterOptionDto> Zones,
    IReadOnlyList<FilterOptionDto> Nsps,
    IReadOnlyList<FilterOptionDto> Manufacturers);

/// <summary>Cascading options: each dimension is counted under the OTHER dimensions' selections.</summary>
public sealed record GetFilterOptionsQuery(FleetFilter Filter) : IRequest<FilterOptionsDto>, ICacheableQuery
{
    public string CacheKey => Filter.Key;
}

internal sealed class GetFilterOptionsHandler(IAnalyticsStateProvider state, IFleetReadRepository repo)
    : IRequestHandler<GetFilterOptionsQuery, FilterOptionsDto>
{
    public async Task<FilterOptionsDto> Handle(GetFilterOptionsQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var rows = await repo.GetDimensionCountsAsync(q.Filter, ct);
        IReadOnlyList<FilterOptionDto> Build(string dim, IReadOnlyDictionary<short, string> all)
        {
            var counts = rows.Where(r => r.Dim == dim).ToDictionary(r => r.Id, r => r.Meters);
            return all.Select(kv => new FilterOptionDto(kv.Key, kv.Value, counts.GetValueOrDefault(kv.Key)))
                      .OrderByDescending(o => o.Meters > 0).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                      .ToList();
        }
        return new FilterOptionsDto(Build("zone", snap.Zones), Build("nsp", snap.Nsps), Build("manufacturer", snap.Manufacturers));
    }
}
