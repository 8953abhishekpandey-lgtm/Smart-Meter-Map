using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartMeter.Api.Infrastructure;
using SmartMeter.Application;
using SmartMeter.Application.Ingestion;

namespace SmartMeter.Api.Controllers;

[Route("api/v1/ingestion")]
[Authorize(Policy = Policies.Operator)]
public sealed class IngestionController : ApiControllerBase
{
    public sealed record CreateUploadRequest(string Kind, string FileName, long FileSize);

    /// <summary>Starts a chunked upload (kind = cis | mdm). Returns the job id and chunk size.</summary>
    [HttpPost("uploads")]
    public async Task<ActionResult<UploadSessionDto>> CreateUpload([FromBody] CreateUploadRequest body, CancellationToken ct) =>
        await Sender.Send(new CreateUploadCommand(IngestionKinds.Parse(body.Kind), body.FileName, body.FileSize), ct);

    /// <summary>Uploads one chunk (raw bytes). Chunks may arrive in any order / in parallel.</summary>
    [HttpPut("uploads/{jobId:guid}/chunks/{index:int}")]
    [Consumes("application/octet-stream")]
    [RequestSizeLimit(64L * 1024 * 1024)]
    public async Task<IActionResult> UploadChunk(Guid jobId, int index, CancellationToken ct)
    {
        await Sender.Send(new UploadChunkCommand(jobId, index, Request.Body), ct);
        return NoContent();
    }

    /// <summary>Completes the upload and queues the ingestion job.</summary>
    [HttpPost("uploads/{jobId:guid}/complete")]
    public async Task<ActionResult<IngestionJobDto>> Complete(Guid jobId, CancellationToken ct) =>
        Accepted(await Sender.Send(new CompleteUploadCommand(jobId), ct));

    /// <summary>Single-request streaming upload for scripts: curl --data-binary @file.csv "/api/v1/ingestion/files?kind=mdm&amp;fileName=file.csv"</summary>
    [HttpPost("files")]
    [DisableRequestSizeLimit]
    public async Task<ActionResult<IngestionJobDto>> UploadFile([FromQuery] string kind, [FromQuery] string fileName, CancellationToken ct) =>
        Accepted(await Sender.Send(new UploadFileCommand(IngestionKinds.Parse(kind), fileName, Request.Body), ct));

    [HttpGet("jobs")]
    public async Task<ActionResult<IReadOnlyList<IngestionJobDto>>> Jobs([FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await Sender.Send(new ListIngestionJobsQuery(take), ct));

    [HttpGet("jobs/{jobId:guid}")]
    public async Task<ActionResult<IngestionJobDto>> Job(Guid jobId, CancellationToken ct) =>
        await Sender.Send(new GetIngestionJobQuery(jobId), ct);

    /// <summary>Row-level validation error report (CSV).</summary>
    [HttpGet("jobs/{jobId:guid}/errors")]
    [Produces("text/csv")]
    public async Task Errors(Guid jobId, CancellationToken ct) =>
        await Csv.WriteAsync(Response, $"ingestion_errors_{jobId:N}.csv", ["RowNo", "Column", "Value", "Message"],
            Sender.CreateStream(new GetIngestionErrorsQuery(jobId), ct), e => [e.RowNo, e.Column, e.RawValue, e.Message], ct);

    /// <summary>Template file with the expected headers and two sample rows.</summary>
    /// <summary>Watch folder: configured path, last scan and the state of every file in it.</summary>
    [HttpGet("watch-folder")]
    public async Task<ActionResult<WatchFolderStatusDto>> WatchFolder(CancellationToken ct) =>
        await Sender.Send(new GetWatchFolderStatusQuery(), ct);

    /// <summary>Scan the watch folder now (also retries files that failed).</summary>
    [HttpPost("watch-folder/scan")]
    public async Task<IActionResult> ScanWatchFolder(CancellationToken ct) =>
        Accepted(new { jobId = await Sender.Send(new ScanWatchFolderCommand(), ct) });

    [HttpGet("templates/{kind}")]
    [AllowAnonymous]
    [Produces("text/csv")]
    public IActionResult Template(string kind)
    {
        var csv = IngestionKinds.Parse(kind) == IngestionKind.Cis
            ? "MeterId,BuildingId,Latitude,Longitude,Address,NSP,Manufacturer,Zone,InstallationDate,DtId\n" +
              "SM900000001,BLD-9000001,28.6139,77.2090,\"Flat 101, Tower A, Example Residency\",Jio,Apex Meters,Delhi South,2025-04-12,DT-2-000101\n" +
              "SM900000002,,28.6201,77.2154,\"House 12, Lane 4\",Airtel,GridSense,Delhi South,12/05/2025,\n"
            : "MeterId,LogDate,IsCommunicating\nSM900000001,2026-11-20,1\nSM900000002,2026-11-20,0\n";
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"{kind.ToLowerInvariant()}_template.csv");
    }
}

[Route("api/v1/admin")]
[Authorize(Policy = Policies.Operator)]
public sealed class AdminController : ApiControllerBase
{
    /// <summary>Queues an analytics refresh (normally triggered automatically after each ingestion and nightly).</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct) =>
        Accepted(new { jobId = await Sender.Send(new RequestAnalyticsRefreshCommand(), ct) });
}
