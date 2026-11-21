using MediatR;
using Microsoft.Extensions.Logging;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;

namespace SmartMeter.Application.Buildings;

public sealed record DailyKwhDto(DateOnly Date, double Kwh, int MetersReporting);
public sealed record MeterKwhDto(string MeterId, double TotalKwh, double DailyAvgKwh);

public sealed record ConsumptionSummaryDto(
    bool Available,
    string Source,
    string? Message,
    DateOnly From,
    DateOnly To,
    int Days,
    int MetersInBuilding,
    int CommunicatingMeters,
    int MetersWithData,
    double TotalKwh,
    double BuildingDailyAvgKwh,
    double PerMeterDailyAvgKwh,
    bool Partial,
    IReadOnlyList<DailyKwhDto> Daily,
    IReadOnlyList<MeterKwhDto> TopConsumers);

public sealed record GetBuildingConsumptionQuery(string BuildingKey) : IRequest<ConsumptionSummaryDto>, ICacheableQuery
{
    public string CacheKey => BuildingKey;
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(15);
}

internal sealed class GetBuildingConsumptionHandler(
    IAnalyticsStateProvider state, IBuildingReadRepository repo, IMdmConsumptionService mdm,
    ILogger<GetBuildingConsumptionHandler> logger) : IRequestHandler<GetBuildingConsumptionQuery, ConsumptionSummaryDto>
{
    public async Task<ConsumptionSummaryDto> Handle(GetBuildingConsumptionQuery q, CancellationToken ct)
    {
        var snap = await state.GetAsync(ct);
        var meters = await repo.GetMeterCommunicationAsync(q.BuildingKey, ct);
        if (meters.Count == 0) throw new NotFoundException("Building", q.BuildingKey);

        // Consumption is requested only for meters that communicated in the window (others have no reads).
        var communicating = meters.Where(m => m.CommDays > 0).Select(m => m.MeterId).ToList();
        try
        {
            var result = communicating.Count == 0
                ? new ConsumptionResult([], "none", false, "No communicating meters in the window.")
                : await mdm.GetDailyConsumptionAsync(communicating, snap.Window.Start, snap.Window.End, ct);
            return ConsumptionCalculator.Summarize(result, snap.Window, meters.Count, communicating.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MDM consumption API unavailable for building {BuildingKey}", q.BuildingKey);
            return new ConsumptionSummaryDto(false, "mdm-api", "MDM consumption service is unavailable: " + ex.Message,
                snap.Window.Start, snap.Window.End, snap.Window.Days, meters.Count, communicating.Count, 0, 0, 0, 0, false, [], []);
        }
    }
}

/// <summary>
/// Building energy summary:
///   total            = sum of daily kWh of all meters over the window
///   building daily   = total / window days
///   per-meter daily  = total / communicating meters / window days  (communicating = &gt;= 1 comm day)
/// </summary>
public static class ConsumptionCalculator
{
    public static ConsumptionSummaryDto Summarize(ConsumptionResult result, Domain.Analytics.CommWindow window, int metersInBuilding, int communicatingMeters)
    {
        var daily = new double[window.Days];
        var reporting = new int[window.Days];
        var perMeter = new List<MeterKwhDto>(result.Meters.Count);
        double total = 0;
        var withData = 0;
        foreach (var m in result.Meters)
        {
            double meterTotal = 0;
            var any = false;
            foreach (var r in m.Readings)
            {
                if (r.Kwh is not { } kwh || !window.Contains(r.Date) || !double.IsFinite(kwh) || kwh < 0) continue;
                var k = window.ColumnIndex(r.Date) - 1;
                daily[k] += kwh;
                reporting[k]++;
                meterTotal += kwh;
                any = true;
            }
            if (!any) continue;
            withData++;
            total += meterTotal;
            perMeter.Add(new MeterKwhDto(m.MeterId, Math.Round(meterTotal, 2), Math.Round(meterTotal / window.Days, 3)));
        }
        var buildingDaily = total / window.Days;
        var perMeterDaily = communicatingMeters > 0 ? total / communicatingMeters / window.Days : 0;
        return new ConsumptionSummaryDto(
            Available: true, result.Source, result.Message, window.Start, window.End, window.Days,
            metersInBuilding, communicatingMeters, withData,
            Math.Round(total, 2), Math.Round(buildingDaily, 2), Math.Round(perMeterDaily, 3), result.Partial,
            window.Dates().Select((d, i) => new DailyKwhDto(d, Math.Round(daily[i], 2), reporting[i])).ToList(),
            perMeter.OrderByDescending(p => p.TotalKwh).Take(10).ToList());
    }
}
