using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartMeter.Api.Infrastructure;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;

namespace SmartMeter.Api.Controllers;

[ApiController]
[Authorize(Policy = Policies.Viewer)]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    private ISender? _sender;
    protected ISender Sender => _sender ??= HttpContext.RequestServices.GetRequiredService<ISender>();
}

/// <summary>Global dashboard filters as query-string parameters (repeat keys for multi-select: zone=1&amp;zone=4).</summary>
public sealed class FleetQuery
{
    [FromQuery(Name = "zone")] public short[]? Zone { get; set; }
    [FromQuery(Name = "nsp")] public short[]? Nsp { get; set; }
    [FromQuery(Name = "mfr")] public short[]? Mfr { get; set; }
    [FromQuery(Name = "installedFrom")] public DateOnly? InstalledFrom { get; set; }
    [FromQuery(Name = "installedTo")] public DateOnly? InstalledTo { get; set; }
    [FromQuery(Name = "health")] public HealthFilter Health { get; set; }
    [FromQuery(Name = "date")] public DateOnly? Date { get; set; }
    [FromQuery(Name = "metric")] public MetricMode Metric { get; set; }
    /// <summary>Date range averaged when metric=window (defaults to the whole analytics window).</summary>
    [FromQuery(Name = "from")] public DateOnly? From { get; set; }
    [FromQuery(Name = "to")] public DateOnly? To { get; set; }

    public FleetFilter ToFilter() => new FleetFilter
    {
        ZoneIds = Zone ?? [],
        NspIds = Nsp ?? [],
        ManufacturerIds = Mfr ?? [],
        InstalledFrom = InstalledFrom,
        InstalledTo = InstalledTo,
        Health = Health,
        PeriodFrom = Metric == MetricMode.Window ? From : null,
        PeriodTo = Metric == MetricMode.Window ? To : null,
    }.Normalize();
}
