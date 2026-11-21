using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartMeter.Application.Analytics;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.Api.Controllers;

[Route("api/v1/analytics")]
[EnableRateLimiting("analytics")]
public sealed class AnalyticsController : ApiControllerBase
{
    public sealed record ClusterRequest(int MinMeters = 500, double MinFailurePct = 25, double EpsMeters = 400, int MinPoints = 1,
        double[]? Bbox = null, int Limit = 50);

    /// <summary>Failure Cluster Finder (DBSCAN over high-failure buildings).</summary>
    [HttpPost("failure-clusters")]
    public async Task<ActionResult<FailureClustersDto>> FailureClusters([FromQuery] FleetQuery q, [FromBody] ClusterRequest body, CancellationToken ct)
    {
        BoundingBox? bbox = body.Bbox is { Length: 4 } b ? new BoundingBox(b[0], b[1], b[2], b[3]) : null;
        return await Sender.Send(new FindFailureClustersQuery(q.ToFilter(), q.Date, q.Metric, body.MinMeters, body.MinFailurePct,
            body.EpsMeters, body.MinPoints, bbox, body.Limit), ct);
    }

    public sealed record PolygonRequest(GeoJsonPolygon Geometry);
    public sealed record GeoJsonPolygon(string Type, double[][][] Coordinates);

    /// <summary>Lasso / polygon selection (GeoJSON Polygon in WGS84): meters, health and NSP failure breakdown inside the boundary.</summary>
    [HttpPost("polygon")]
    public async Task<ActionResult<PolygonStatsDto>> Polygon([FromQuery] FleetQuery q, [FromBody] PolygonRequest body, CancellationToken ct)
    {
        if (body.Geometry is not { Coordinates: { Length: > 0 } rings } geometry
            || !string.Equals(geometry.Type, "Polygon", StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("geometry must be a GeoJSON Polygon.");
        return await Sender.Send(new AnalyzePolygonQuery(rings[0], q.ToFilter(), q.Date), ct);
    }

    /// <summary>SIM / NSP switch opportunities.</summary>
    [HttpGet("sim-switch")]
    public async Task<ActionResult<PagedResult<SimSwitchDto>>> SimSwitch([FromQuery] FleetQuery q,
        [FromQuery] double minStrongSuccessPct = 85, [FromQuery] double minWeakFailurePct = 70, [FromQuery] int minMetersPerNsp = 3,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] MetricMode? basis = null, CancellationToken ct = default) =>
        await Sender.Send(new FindSimSwitchQuery(q.ToFilter(), q.Date, basis ?? MetricMode.Window, minStrongSuccessPct, minWeakFailurePct,
            minMetersPerNsp, page, pageSize), ct);
}
