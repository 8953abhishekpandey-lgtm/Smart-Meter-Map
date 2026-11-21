using Microsoft.AspNetCore.Mvc;
using SmartMeter.Application.Meters;

namespace SmartMeter.Api.Controllers;

[Route("api/v1/meters")]
public sealed class MetersController : ApiControllerBase
{
    /// <summary>Fast MeterId prefix search (index seek).</summary>
    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<MeterSearchItemDto>>> Search([FromQuery] string q, [FromQuery] int limit = 10, CancellationToken ct = default) =>
        Ok(await Sender.Send(new SearchMetersQuery(q, limit), ct));

    /// <summary>Single meter card: address, location, attributes and the 30-day daily communication timeline.</summary>
    [HttpGet("{meterId}")]
    public async Task<ActionResult<MeterDetailDto>> Get(string meterId, CancellationToken ct) =>
        await Sender.Send(new GetMeterDetailQuery(meterId), ct);
}
