using Dapper;
using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMeter.Application;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Application.Ingestion;
using SmartMeter.Infrastructure.Persistence;

namespace SmartMeter.Infrastructure.Ingestion;

/// <summary>A file found in the watch folder.</summary>
internal sealed record FolderFile(string RelativePath, string FullPath, long SizeBytes, DateTime LastWriteUtc, IngestionKind? Kind, DateOnly? FileDate);

internal sealed record FolderImportRow(string RelativePath, string Kind, DateOnly? FileDate, long SizeBytes, DateTime LastWriteUtc,
    string Status, Guid? JobId, string? Message, DateTime ImportedAt);

/// <summary>Lists the watch folder and compares it with what has been imported (sm.FolderImport).</summary>
public sealed class WatchFolderService(IOptions<IngestionOptions> options, IHostEnvironment env, ISqlConnectionFactory db) : IWatchFolder
{
    private const int MaxListed = 5000;
    private const int MaxReturned = 300;

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.WatchFolder);

    /// <summary>Absolute folder path (a relative setting is resolved against the API's content root), or null when off.</summary>
    public string? Root => Enabled ? Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.WatchFolder!)) : null;

    public async Task<WatchFolderStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var o = options.Value;
        var root = Root;
        if (root is null)
            return new WatchFolderStatusDto(false, null, false, o.WatchFolderScanMinutes, null, null, 0, 0, 0, 0, []);

        await using var conn = await db.OpenAsync(ct);
        var scan = await conn.QuerySingleOrDefaultAsync<(DateTime? LastScanAt, string? LastScanMessage)>(
            new CommandDefinition("SELECT LastScanAt, LastScanMessage FROM sm.FolderWatchState WHERE Id = 1;", cancellationToken: ct));
        var known = (await LoadKnownAsync(conn, ct)).ToDictionary(r => r.RelativePath, StringComparer.OrdinalIgnoreCase);
        var exists = Directory.Exists(root);
        var files = exists ? List(root, o.WatchFolderIncludeSubfolders, o.AllowedExtensions) : [];
        var now = DateTime.UtcNow;

        var rows = files.Select(f =>
        {
            known.TryGetValue(f.RelativePath, out var k);
            var fileState = StateOf(f, k, now, o.WatchFolderStableSeconds);
            return new WatchFolderFileDto(f.RelativePath, f.Kind?.ToCode(), f.FileDate, f.SizeBytes, f.LastWriteUtc, fileState,
                k is not null && Unchanged(f, k) ? k.JobId : null, k is not null && Unchanged(f, k) ? DateTime.SpecifyKind(k.ImportedAt, DateTimeKind.Utc) : null,
                k is not null && Unchanged(f, k) ? k.Message : null);
        }).ToList();

        int Count(string s) => rows.Count(r => r.State == s);
        var ordered = rows
            .OrderBy(r => r.State switch { "failed" => 0, "waiting" => 1, "copying" => 2, "unrecognised" => 3, _ => 4 })
            .ThenByDescending(r => r.ImportedAt ?? r.LastWriteUtc)
            .Take(MaxReturned).ToList();
        return new WatchFolderStatusDto(true, root, exists, o.WatchFolderScanMinutes,
            scan.LastScanAt is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null, scan.LastScanMessage,
            Count("waiting") + Count("copying"), Count("imported"), Count("failed"), Count("unrecognised"), ordered);
    }

    // ------------------------------------------------------------------ shared with the scan job
    internal static string StateOf(FolderFile f, FolderImportRow? known, DateTime nowUtc, int stableSeconds)
    {
        if (f.Kind is null) return "unrecognised";
        if (known is not null && Unchanged(f, known)) return known.Status;
        return (nowUtc - f.LastWriteUtc).TotalSeconds < stableSeconds ? "copying" : "waiting";
    }

    internal static bool Unchanged(FolderFile f, FolderImportRow k) => f.SizeBytes == k.SizeBytes && f.LastWriteUtc == k.LastWriteUtc;

    internal static List<FolderFile> List(string root, bool recursive, IReadOnlyCollection<string> extensions)
    {
        var result = new List<FolderFile>();
        var opts = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var path in Directory.EnumerateFiles(root, "*", opts))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith('.')) continue;     // Excel lock files, hidden files
            if (name.EndsWith(".errors.csv", StringComparison.OrdinalIgnoreCase)) continue;             // our own error reports
            if (!extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(path);
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.Length > 400) continue;
            result.Add(new FolderFile(rel, path, info.Length, Truncate(info.LastWriteTimeUtc), IngestionFileNames.KindOf(rel), IngestionFileNames.DateOf(rel)));
            if (result.Count >= MaxListed) break;
        }
        return result;
    }

    /// <summary>datetime2(0) keeps whole seconds: compare file times at that precision.</summary>
    internal static DateTime Truncate(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    internal static async Task<IReadOnlyList<FolderImportRow>> LoadKnownAsync(System.Data.Common.DbConnection conn, CancellationToken ct) =>
        (await conn.QueryAsync<FolderImportRow>(new CommandDefinition(
            "SELECT RelativePath, Kind, FileDate, SizeBytes, LastWriteUtc, Status, JobId, Message, ImportedAt FROM sm.FolderImport;",
            cancellationToken: ct))).AsList();
}

/// <summary>
/// Hangfire job: imports new and changed files from the watch folder, one at a time (CIS master files before MDM logs,
/// MDM logs in date order), then queues ONE analytics refresh. A SQL Server application lock keeps two scans from
/// overlapping, also across several API nodes. Source files are only read, never moved or deleted.
/// </summary>
public sealed class WatchFolderScanJob(
    WatchFolderService folder,
    ISqlConnectionFactory db,
    IngestionRepository repo,
    IUploadStore store,
    IngestionJobRunner runner,
    IBackgroundJobs jobs,
    IOptions<IngestionOptions> options,
    ILogger<WatchFolderScanJob> logger)
{
    [Queue("ingestion")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [JobDisplayName("Watch folder scan")]
    public async Task ScanAsync(bool retryFailed, CancellationToken ct)
    {
        var root = folder.Root;
        if (root is null) return;
        var o = options.Value;

        await using var lockConn = await db.OpenAsync(ct);
        var got = await lockConn.ExecuteScalarAsync<int>(new CommandDefinition(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'smartmeter-watch-folder', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;",
            cancellationToken: ct));
        if (got < 0) { logger.LogInformation("Watch folder scan skipped: another scan is running"); return; }
        try
        {
            if (!Directory.Exists(root))
            {
                await SaveStateAsync(root, $"Folder not found: {root}", ct);
                return;
            }
            var known = (await WatchFolderService.LoadKnownAsync(lockConn, ct)).ToDictionary(r => r.RelativePath, StringComparer.OrdinalIgnoreCase);
            var files = WatchFolderService.List(root, o.WatchFolderIncludeSubfolders, o.AllowedExtensions);
            var now = DateTime.UtcNow;
            var todo = files
                .Where(f =>
                {
                    known.TryGetValue(f.RelativePath, out var k);
                    var s = WatchFolderService.StateOf(f, k, now, o.WatchFolderStableSeconds);
                    return s == "waiting" || (retryFailed && s == "failed");
                })
                .OrderBy(f => f.Kind == IngestionKind.Cis ? 0 : 1)
                .ThenBy(f => f.FileDate ?? DateOnly.MinValue)
                .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int ok = 0, failed = 0;
            foreach (var f in todo)
            {
                ct.ThrowIfCancellationRequested();
                if (await ImportAsync(f, ct)) ok++; else failed++;
            }
            if (ok > 0) jobs.EnqueueAnalyticsRefresh(null);

            var message = todo.Count == 0
                ? $"No new or changed files ({files.Count:N0} file(s) in the folder)."
                : $"Imported {ok:N0} file(s)" + (failed > 0 ? $", {failed:N0} failed" : "") + (ok > 0 ? "; analytics refresh queued." : ".");
            await SaveStateAsync(root, message, ct);
            if (todo.Count > 0) logger.LogInformation("Watch folder {Folder}: {Message}", root, message);
        }
        finally
        {
            await lockConn.ExecuteAsync("EXEC sp_releaseapplock @Resource = N'smartmeter-watch-folder', @LockOwner = 'Session';");
        }
    }

    private async Task<bool> ImportAsync(FolderFile f, CancellationToken ct)
    {
        var jobId = Guid.NewGuid();
        var kind = f.Kind!.Value;
        string status, message;
        try
        {
            // a file still open for writing by another program (copy in progress, open in Excel with a lock) waits for the next scan
            FileStream source;
            try { source = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (IOException) { logger.LogInformation("Watch folder: {File} is in use, retrying at the next scan", f.RelativePath); return false; }

            await repo.CreateJobAsync(jobId, kind.ToCode(), f.RelativePath, f.SizeBytes, "watch-folder", ct);
            await using (source) await store.WriteWholeAsync(jobId, source, options.Value.MaxFileSizeBytes, ct);
            await store.FinalizeAsync(jobId, ct);
            await repo.SetStatusAsync(jobId, IngestionStatus.Queued, "Picked up from the watch folder.", ct);
            await runner.RunCoreAsync(jobId, queueRefresh: false, ct);

            var job = await repo.GetJobAsync(jobId, ct);
            status = job?.Status == IngestionStatus.Completed ? "imported" : "failed";
            message = job?.Message ?? "No job record.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Watch folder import of {File} failed", f.RelativePath);
            status = "failed";
            message = ex is BadRequestException ? ex.Message : "Import failed: " + ex.Message;
        }

        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE sm.FolderImport AS t
            USING (SELECT @path AS RelativePath) AS s ON t.RelativePath = s.RelativePath
            WHEN MATCHED THEN UPDATE SET Kind = @kind, FileDate = @fileDate, SizeBytes = @size, LastWriteUtc = @lastWrite,
                 Status = @status, JobId = @jobId, Message = @message, ImportedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (RelativePath, Kind, FileDate, SizeBytes, LastWriteUtc, Status, JobId, Message)
                 VALUES (@path, @kind, @fileDate, @size, @lastWrite, @status, @jobId, @message);
            """, new
        {
            path = f.RelativePath, kind = kind.ToCode(), fileDate = f.FileDate, size = f.SizeBytes, lastWrite = f.LastWriteUtc,
            status, jobId, message = message.Length > 400 ? message[..400] : message,
        }, cancellationToken: ct));
        return status == "imported";
    }

    private async Task SaveStateAsync(string root, string message, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE sm.FolderWatchState AS t USING (SELECT 1 AS Id) AS s ON t.Id = s.Id
            WHEN MATCHED THEN UPDATE SET FolderPath = @root, LastScanAt = SYSUTCDATETIME(), LastScanMessage = @message
            WHEN NOT MATCHED THEN INSERT (Id, FolderPath, LastScanAt, LastScanMessage) VALUES (1, @root, SYSUTCDATETIME(), @message);
            """, new { root = root.Length > 400 ? root[..400] : root, message }, cancellationToken: ct));
    }
}
