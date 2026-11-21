using FluentValidation;
using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;

namespace SmartMeter.Application.Buildings;

public sealed record BuildingMeterDto(
    string MeterId,
    string? Address,
    string Nsp,
    string Manufacturer,
    DateOnly? InstallationDate,
    HealthClass Health,
    int CommDays,
    int EligDays,
    DateOnly? LastCommDate,
    bool? CommunicatedOnDay,
    string Timeline);

public sealed record GetBuildingMetersQuery(
    string BuildingKey, DateOnly? Date, string? Search, MeterStatusFilter Status, MeterSort Sort, bool Descending,
    int Page = 1, int PageSize = 50) : IRequest<PagedResult<BuildingMeterDto>>;

public sealed class GetBuildingMetersValidator : AbstractValidator<GetBuildingMetersQuery>
{
    public GetBuildingMetersValidator()
    {
        RuleFor(x => x.BuildingKey).NotEmpty().MaximumLength(72);
        RuleFor(x => x.Search).MaximumLength(32);
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 500);
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Sort).IsInEnum();
    }
}

internal sealed class GetBuildingMetersHandler(IAnalyticsStateProvider state, IBuildingReadRepository repo)
    : IRequestHandler<GetBuildingMetersQuery, PagedResult<BuildingMeterDto>>
{
    public async Task<PagedResult<BuildingMeterDto>> Handle(GetBuildingMetersQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var day = DaySelection.From(snap.Window, q.Date, MetricMode.Day);
        var rows = await repo.GetMetersPageAsync(q.BuildingKey, day, q.Search, q.Status, q.Sort, q.Descending,
            (q.Page - 1) * q.PageSize, q.PageSize, ct);
        var total = rows.Count > 0 ? rows[0].TotalCount : 0;
        return new PagedResult<BuildingMeterDto>(rows.Select(r => MeterMapper.Map(r, snap, day)).ToList(), q.Page, q.PageSize, total);
    }
}

public static class MeterMapper
{
    public static BuildingMeterDto Map(BuildingMeterRow r, AnalyticsSnapshot snap, DaySelection day)
    {
        var eligible = ((r.EligMask >> day.Bit) & 1) == 1;
        var comm = ((r.CommMask >> day.Bit) & 1) == 1;
        return new BuildingMeterDto(
            r.MeterId, r.Address, snap.NspName(r.NspId), snap.ManufacturerName(r.ManufacturerId), r.InstallationDate,
            (HealthClass)r.HealthClass, r.CommDays, r.EligDays, r.LastCommDate,
            eligible ? comm : null,
            snap.Window.DecodeTimeline(r.CommMask, r.EligMask));
    }
}

/// <summary>Streams every meter of a building (CSV export) without paging or buffering.</summary>
public sealed record ExportBuildingMetersQuery(string BuildingKey, DateOnly? Date, string? Search, MeterStatusFilter Status)
    : IStreamRequest<BuildingMeterDto>;

internal sealed class ExportBuildingMetersHandler(IAnalyticsStateProvider state, IBuildingReadRepository repo)
    : IStreamRequestHandler<ExportBuildingMetersQuery, BuildingMeterDto>
{
    public async IAsyncEnumerable<BuildingMeterDto> Handle(ExportBuildingMetersQuery q,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var day = DaySelection.From(snap.Window, q.Date, MetricMode.Day);
        await foreach (var row in repo.StreamMetersAsync(q.BuildingKey, day, q.Search, q.Status, ct))
            yield return MeterMapper.Map(row, snap, day);
    }
}
