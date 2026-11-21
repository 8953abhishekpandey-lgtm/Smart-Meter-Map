using Microsoft.AspNetCore.Mvc;
using SmartMeter.Application.Dashboard;
using SmartMeter.Application.Map;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.Api.Controllers;

[Route("api/v1")]
public sealed class DashboardController : ApiControllerBase
{
    /// <summary>KPI bar: total meters, success / failure count and % for the selected day (or 30-day average) + health chip counts.</summary>
    [HttpGet("dashboard/kpis")]
    public async Task<ActionResult<KpiDto>> Kpis([FromQuery] FleetQuery q, CancellationToken ct) =>
        await Sender.Send(new GetKpisQuery(q.ToFilter(), q.Date, q.Metric), ct);

    /// <summary>Daily communication success over the rolling window for the filtered fleet.</summary>
    [HttpGet("dashboard/trend")]
    public async Task<ActionResult<TrendDto>> Trend([FromQuery] FleetQuery q, CancellationToken ct) =>
        await Sender.Send(new GetFleetTrendQuery(q.ToFilter()), ct);

    /// <summary>Cascading multi-select options with meter counts.</summary>
    [HttpGet("dashboard/filter-options")]
    public async Task<ActionResult<FilterOptionsDto>> FilterOptions([FromQuery] FleetQuery q, CancellationToken ct) =>
        await Sender.Send(new GetFilterOptionsQuery(q.ToFilter()), ct);

    /// <summary>Viewport bubbles (server-side clustering). bbox = minLon,minLat,maxLon,maxLat (WGS84).</summary>
    [HttpGet("map/bubbles")]
    public async Task<ActionResult<BubblesDto>> Bubbles([FromQuery] string bbox, [FromQuery] int zoom, [FromQuery] FleetQuery q,
        [FromQuery] int limit = 5000, CancellationToken ct = default)
    {
        if (!BoundingBox.TryParse(bbox, out var box))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["bbox"] = ["bbox must be minLon,minLat,maxLon,maxLat"] }));
        return await Sender.Send(new GetMapBubblesQuery(box, zoom, q.ToFilter(), q.Date, q.Metric, limit), ct);
    }
}
