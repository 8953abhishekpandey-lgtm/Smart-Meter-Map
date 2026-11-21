using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Domain.Health;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.Application.Meta;

public sealed record DimensionItemDto(short Id, string Name);
public sealed record GridLevelDto(byte Level, double CellDeg, byte MinZoom, byte MaxZoom);
public sealed record WindowDto(DateOnly Start, DateOnly End, int Days);
public sealed record BandThresholdsDto(int GreenMaxFailurePct, int RedMinFailurePct);

public sealed record MetaDto(
    WindowDto Window,
    long DataVersion,
    DateTime? RefreshedAtUtc,
    int? RefreshMs,
    IReadOnlyList<DimensionItemDto> Zones,
    IReadOnlyList<DimensionItemDto> Nsps,
    IReadOnlyList<DimensionItemDto> Manufacturers,
    DateOnly? InstalledMin,
    DateOnly? InstalledMax,
    IReadOnlyList<GridLevelDto> GridLevels,
    int BuildingMinZoom,
    BandThresholdsDto Bands,
    string? Stats);

public sealed record GetMetaQuery : IRequest<MetaDto>;

internal sealed class GetMetaHandler(IAnalyticsStateProvider state) : IRequestHandler<GetMetaQuery, MetaDto>
{
    public async Task<MetaDto> Handle(GetMetaQuery request, CancellationToken ct)
    {
        var s = await state.GetAsync(ct);
        static IReadOnlyList<DimensionItemDto> Dims(IReadOnlyDictionary<short, string> d) =>
            d.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).Select(kv => new DimensionItemDto(kv.Key, kv.Value)).ToList();
        return new MetaDto(
            new WindowDto(s.Window.Start, s.Window.End, s.Window.Days),
            s.DataVersion, s.RefreshedAtUtc, s.RefreshMs,
            Dims(s.Zones), Dims(s.Nsps), Dims(s.Manufacturers),
            s.InstalledMin, s.InstalledMax,
            s.GridLevels.Select(g => new GridLevelDto(g.Level, g.CellDeg, g.MinZoom, g.MaxZoom)).ToList(),
            MapResolution.BuildingMinZoom,
            new BandThresholdsDto(HealthBands.GreenMaxFailurePct, HealthBands.RedMinFailurePct),
            s.StatsJson);
    }
}
