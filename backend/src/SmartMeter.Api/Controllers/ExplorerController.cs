using Microsoft.AspNetCore.Mvc;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Application.Explorer;

namespace SmartMeter.Api.Controllers;

/// <summary>Zone -> Building -> Meter navigator.</summary>
[Route("api/v1/zones")]
public sealed class ExplorerController : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ZoneSummaryDto>>> Zones([FromQuery] FleetQuery q, CancellationToken ct) =>
        Ok(await Sender.Send(new GetZonesQuery(q.ToFilter(), q.Date, q.Metric), ct));

    [HttpGet("{zoneId:int}/buildings")]
    public async Task<ActionResult<PagedResult<BuildingListItemDto>>> Buildings(short zoneId, [FromQuery] FleetQuery q,
        [FromQuery] string? search, [FromQuery] BuildingSort sort = BuildingSort.FailureRate, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        await Sender.Send(new GetZoneBuildingsQuery(zoneId, q.ToFilter(), q.Date, q.Metric, search, sort, page, pageSize), ct);
}
