using FluentValidation;
using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Health;

namespace SmartMeter.Application.Meters;

public sealed record MeterSearchItemDto(string MeterId, string? Address, string BuildingKey, double? Lon, double? Lat, string Zone);

public sealed record SearchMetersQuery(string Q, int Limit = 10) : IRequest<IReadOnlyList<MeterSearchItemDto>>;

public sealed class SearchMetersValidator : AbstractValidator<SearchMetersQuery>
{
    public SearchMetersValidator()
    {
        RuleFor(x => x.Q).NotEmpty().MinimumLength(2).MaximumLength(32);
        RuleFor(x => x.Limit).InclusiveBetween(1, 50);
    }
}

internal sealed class SearchMetersHandler(IAnalyticsStateProvider state, IMeterReadRepository repo)
    : IRequestHandler<SearchMetersQuery, IReadOnlyList<MeterSearchItemDto>>
{
    public async Task<IReadOnlyList<MeterSearchItemDto>> Handle(SearchMetersQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var rows = await repo.SearchAsync(q.Q.Trim(), q.Limit, ct);
        return rows.Select(r => new MeterSearchItemDto(r.MeterId, r.Address, r.BuildingKey, r.Lon, r.Lat, snap.ZoneName(r.ZoneId))).ToList();
    }
}

public sealed record TimelineDayDto(DateOnly Date, string Status);

public sealed record MeterDetailDto(
    string MeterId,
    string? Address,
    double? Lat,
    double? Lon,
    string BuildingKey,
    string? BuildingId,
    string? BuildingName,
    short ZoneId,
    string Zone,
    short NspId,
    string Nsp,
    short ManufacturerId,
    string Manufacturer,
    DateOnly? InstallationDate,
    string? DtId,
    HealthClass? Health,
    int CommDays,
    int EligDays,
    double CommPct,
    DateOnly? LastCommDate,
    IReadOnlyList<TimelineDayDto> Timeline);

public sealed record GetMeterDetailQuery(string MeterId) : IRequest<MeterDetailDto>, ICacheableQuery
{
    public string CacheKey => MeterId.ToUpperInvariant();
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

internal sealed class GetMeterDetailHandler(IAnalyticsStateProvider state, IMeterReadRepository repo)
    : IRequestHandler<GetMeterDetailQuery, MeterDetailDto>
{
    public async Task<MeterDetailDto> Handle(GetMeterDetailQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var m = await repo.GetMeterAsync(q.MeterId.Trim(), ct) ?? throw new NotFoundException("Meter", q.MeterId);
        var logs = await repo.GetTimelineAsync(m.MeterSk, snap.Window.Start, snap.Window.End, ct);
        var byDate = logs.ToDictionary(l => l.LogDate, l => l.IsCommunicating);
        var timeline = snap.Window.Dates().Select(d =>
        {
            var installed = m.InstallationDate is not { } inst || d >= inst;
            var status = byDate.TryGetValue(d, out var ok)
                ? ok ? "communicating" : "failed"
                : installed ? "no-record" : "not-installed";
            return new TimelineDayDto(d, status);
        }).ToList();
        int comm = m.CommDays ?? 0, elig = m.EligDays ?? 0;
        return new MeterDetailDto(
            m.MeterId, m.Address, m.Lat, m.Lon, m.BuildingKey, m.BuildingId, m.BuildingName, m.ZoneId, snap.ZoneName(m.ZoneId),
            m.NspId, snap.NspName(m.NspId), m.ManufacturerId, snap.ManufacturerName(m.ManufacturerId), m.InstallationDate, m.DtId,
            m.HealthClass is { } h ? (HealthClass)h : null, comm, elig, Pct.Of(comm, elig), m.LastCommDate, timeline);
    }
}
