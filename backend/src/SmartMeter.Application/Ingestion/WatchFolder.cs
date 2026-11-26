using MediatR;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;

namespace SmartMeter.Application.Ingestion;

/// <summary>One file in the watch folder and what happened to it.</summary>
/// <param name="State">waiting (new or changed, imported at the next scan) · copying (still being written) · imported · failed · unrecognised (name says neither CIS nor MDM)</param>
public sealed record WatchFolderFileDto(string Path, string? Kind, DateOnly? FileDate, long SizeBytes, DateTime LastWriteUtc,
    string State, Guid? JobId, DateTime? ImportedAt, string? Message);

public sealed record WatchFolderStatusDto(bool Enabled, string? Path, bool Exists, int ScanMinutes, DateTime? LastScanAt,
    string? LastScanMessage, int Waiting, int Imported, int Failed, int Unrecognised, IReadOnlyList<WatchFolderFileDto> Files);

public interface IWatchFolder
{
    bool Enabled { get; }
    Task<WatchFolderStatusDto> GetStatusAsync(CancellationToken ct);
}

public sealed record GetWatchFolderStatusQuery : IRequest<WatchFolderStatusDto>;

internal sealed class GetWatchFolderStatusHandler(IWatchFolder folder) : IRequestHandler<GetWatchFolderStatusQuery, WatchFolderStatusDto>
{
    public Task<WatchFolderStatusDto> Handle(GetWatchFolderStatusQuery q, CancellationToken ct) => folder.GetStatusAsync(ct);
}

/// <summary>Scan now (also retries files that failed before), instead of waiting for the next scheduled scan.</summary>
public sealed record ScanWatchFolderCommand : IRequest<string>;

internal sealed class ScanWatchFolderHandler(IWatchFolder folder, IBackgroundJobs jobs) : IRequestHandler<ScanWatchFolderCommand, string>
{
    public Task<string> Handle(ScanWatchFolderCommand c, CancellationToken ct)
    {
        if (!folder.Enabled) throw new BadRequestException("No watch folder is configured (Ingestion:WatchFolder in appsettings).");
        return Task.FromResult(jobs.EnqueueFolderScan());
    }
}
