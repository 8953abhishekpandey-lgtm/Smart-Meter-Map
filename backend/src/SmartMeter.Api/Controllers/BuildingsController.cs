using Microsoft.AspNetCore.Mvc;
using SmartMeter.Api.Infrastructure;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Buildings;
using SmartMeter.Application.Common;

namespace SmartMeter.Api.Controllers;

[Route("api/v1/buildings")]
public sealed class BuildingsController : ApiControllerBase
{
    /// <summary>Building drill-down: totals, NSP / manufacturer breakdowns and the 30-day trend.</summary>
    [HttpGet("{buildingKey}")]
    public async Task<ActionResult<BuildingDetailDto>> Get(string buildingKey, [FromQuery] DateOnly? date, CancellationToken ct) =>
        await Sender.Send(new GetBuildingDetailQuery(buildingKey, date), ct);

    /// <summary>Server-side paginated, searchable meter list of a building.</summary>
    [HttpGet("{buildingKey}/meters")]
    public async Task<ActionResult<PagedResult<BuildingMeterDto>>> Meters(string buildingKey, [FromQuery] DateOnly? date,
        [FromQuery] string? search, [FromQuery] MeterStatusFilter status = MeterStatusFilter.All, [FromQuery] MeterSort sort = MeterSort.MeterId,
        [FromQuery] bool desc = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        await Sender.Send(new GetBuildingMetersQuery(buildingKey, date, search, status, sort, desc, page, pageSize), ct);

    /// <summary>CSV export of every meter in the building (streamed).</summary>
    [HttpGet("{buildingKey}/meters/export")]
    [Produces("text/csv")]
    public async Task Export(string buildingKey, [FromQuery] DateOnly? date, [FromQuery] string? search,
        [FromQuery] MeterStatusFilter status = MeterStatusFilter.All, CancellationToken ct = default)
    {
        var rows = Sender.CreateStream(new ExportBuildingMetersQuery(buildingKey, date, search, status), ct);
        var safe = string.Concat(buildingKey.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_'));
        await Csv.WriteAsync(Response, $"meters_{safe}_{date:yyyyMMdd}.csv",
            ["MeterId", "Address", "NSP", "Manufacturer", "InstallationDate", "Health", "CommDays", "EligibleDays", "LastCommDate", "CommunicatedOnDay", "Timeline"],
            rows, m => [m.MeterId, m.Address, m.Nsp, m.Manufacturer, m.InstallationDate, m.Health.ToString(), m.CommDays, m.EligDays, m.LastCommDate, m.CommunicatedOnDay, m.Timeline],
            ct);
    }

    /// <summary>Energy consumption summary from the MDM API (monthly total, building daily avg, per-meter daily avg).</summary>
    [HttpGet("{buildingKey}/consumption")]
    public async Task<ActionResult<ConsumptionSummaryDto>> Consumption(string buildingKey, CancellationToken ct) =>
        await Sender.Send(new GetBuildingConsumptionQuery(buildingKey), ct);
}
