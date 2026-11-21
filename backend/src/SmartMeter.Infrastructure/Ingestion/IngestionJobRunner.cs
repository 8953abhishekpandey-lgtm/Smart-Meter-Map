using System.Data;
using Dapper;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMeter.Application;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Application.Ingestion.Parsing;
using SmartMeter.Infrastructure.Persistence;

namespace SmartMeter.Infrastructure.Ingestion;

/// <summary>
/// Hangfire job: streaming parse (Sylvan) -> validation -> SqlBulkCopy into an UNLOGGED-style staging
/// heap -> set-based merge procedures -> analytics refresh enqueue. Progress is pushed every second.
/// </summary>
public sealed class IngestionJobRunner(
    ISqlConnectionFactory db,
    IngestionRepository repo,
    IUploadStore store,
    IIngestionNotifier notifier,
    IBackgroundJobs jobs,
    IOptions<IngestionOptions> options,
    SmartMeterMetrics metrics,
    ILogger<IngestionJobRunner> logger)
{
    private static readonly string[] CisColumns =
        ["RowNo", "MeterId", "BuildingId", "Latitude", "Longitude", "Address", "Nsp", "Manufacturer", "Zone", "InstallationDate", "DtId"];
    private static readonly Type[] CisTypes =
        [typeof(long), typeof(string), typeof(string), typeof(double), typeof(double), typeof(string), typeof(string), typeof(string), typeof(string), typeof(DateTime), typeof(string)];
    private static readonly string[] MdmColumns = ["RowNo", "MeterId", "LogDate", "IsCommunicating"];
    private static readonly Type[] MdmTypes = [typeof(long), typeof(string), typeof(DateTime), typeof(bool)];

    [Queue("ingestion")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [JobDisplayName("Ingest upload {0}")]
    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var job = await repo.GetJobAsync(jobId, ct) ?? throw new NotFoundException("Ingestion job", jobId.ToString());
        if (job.Status is IngestionStatus.Completed) return;          // idempotent re-delivery
        var kind = IngestionKinds.Parse(job.Kind);
        var o = options.Value;
        var errors = new ErrorSink(o.MaxStoredErrors);
        var progress = new JobProgress { Status = IngestionStatus.Parsing, Message = "Reading and validating rows" };
        string? stage = null;
        using var activity = SmartMeterMetrics.ActivitySource.StartActivity("Ingestion " + job.Kind);
        logger.LogInformation("Ingestion {JobId} ({Kind}) started for {FileName}", jobId, job.Kind, job.FileName);

        await repo.MarkStartedAsync(jobId, ct);
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpProgressAsync(jobId, progress, errors, pumpCts.Token);
        try
        {
            await using var file = await TabularFile.OpenAsync(store.GetFilePath(jobId), job.FileName, o.DateFormats, ct);
            stage = await CreateStageAsync(kind, jobId, ct);

            long rowsValid;
            if (kind == IngestionKind.Cis)
            {
                var map = ResolveHeaders(file.Headers(), ColumnAliases.Cis, ColumnAliases.CisRequired);
                var serviceArea = o.ServiceArea?.ToBox();
                using var reader = new ValidatingDataReader<CisRecord>(file,
                    headers => TryRemap(headers, ColumnAliases.Cis, ColumnAliases.CisRequired, errors, ref map),
                    (IRowSource row, long rowNo, ErrorSink sink, out CisRecord rec) => CisRowParser.TryParse(row, map, rowNo, sink, out rec, serviceArea),
                    CisValue, CisColumns, CisTypes, errors,
                    (read, valid) => progress.Update(read, valid, file.ProgressPct(read)));
                await BulkCopyAsync(stage, reader, CisColumns, o.BulkCopyBatchSize, ct);
                progress.Update(reader.RowsRead, reader.RowsValid, 100);
                rowsValid = reader.RowsValid;

                progress.Phase(IngestionStatus.Merging, "Merging into the CIS master", 70);
                var (inserted, updated) = await MergeCisAsync(stage, ct);
                progress.Inserted = inserted;
                progress.Updated = updated;
            }
            else
            {
                var map = ResolveHeaders(file.Headers(), ColumnAliases.Mdm, ColumnAliases.MdmRequired);
                using var reader = new ValidatingDataReader<MdmRecord>(file,
                    headers => TryRemap(headers, ColumnAliases.Mdm, ColumnAliases.MdmRequired, errors, ref map),
                    (IRowSource row, long rowNo, ErrorSink sink, out MdmRecord rec) => MdmRowParser.TryParse(row, map, rowNo, sink, out rec),
                    MdmValue, MdmColumns, MdmTypes, errors,
                    (read, valid) => progress.Update(read, valid, file.ProgressPct(read)));
                await BulkCopyAsync(stage, reader, MdmColumns, o.BulkCopyBatchSize, ct);
                progress.Update(reader.RowsRead, reader.RowsValid, 100);
                rowsValid = reader.RowsValid;

                var unknown = await ReportUnknownMetersAsync(stage, errors, o.MaxStoredErrors, ct);
                progress.ExtraRejected = unknown;
                await MergeMdmAsync(stage, progress, ct);
            }

            var dbNow = await DbUtcNowAsync(ct);
            jobs.EnqueueAnalyticsRefresh(dbNow);
            progress.Phase(IngestionStatus.Completed,
                $"Completed: {progress.Inserted:N0} inserted, {progress.Updated:N0} updated, {errors.RejectedRows + progress.ExtraRejected:N0} rejected. Analytics refresh queued.", 100);
            metrics.RecordIngestion(job.Kind, rowsValid - progress.ExtraRejected, errors.RejectedRows + progress.ExtraRejected);
            logger.LogInformation("Ingestion {JobId} completed: {Read} read, {Valid} valid, {Inserted} inserted, {Updated} updated",
                jobId, progress.Read, progress.Valid, progress.Inserted, progress.Updated);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ingestion {JobId} failed", jobId);
            progress.Phase(IngestionStatus.Failed, ex is IngestionFormatException or BadRequestException ? ex.Message : "Ingestion failed: " + ex.Message, progress.Pct);
        }
        finally
        {
            await pumpCts.CancelAsync();
            try { await pump; } catch (OperationCanceledException) { }
            await SafeAsync(() => repo.SaveErrorsAsync(jobId, errors.Errors, CancellationToken.None));
            await SafeAsync(() => PublishAsync(jobId, progress, errors, finished: true, CancellationToken.None));
            if (stage is not null) await SafeAsync(() => DropStageAsync(stage));
            if (options.Value.DeleteFileAfterProcessing) await SafeAsync(() => { store.Delete(jobId); return Task.CompletedTask; });
        }
    }

    // ------------------------------------------------------------------ helpers
    private static object? CisValue(CisRecord r, long rowNo, int i) => i switch
    {
        0 => rowNo,
        1 => r.MeterId,
        2 => r.BuildingId,
        3 => r.Latitude,
        4 => r.Longitude,
        5 => r.Address,
        6 => r.Nsp,
        7 => r.Manufacturer,
        8 => r.Zone,
        9 => r.InstallationDate?.ToDateTime(TimeOnly.MinValue),
        10 => r.DtId,
        _ => null,
    };

    private static object? MdmValue(MdmRecord r, long rowNo, int i) => i switch
    {
        0 => rowNo,
        1 => r.MeterId,
        2 => r.LogDate.ToDateTime(TimeOnly.MinValue),
        3 => r.IsCommunicating,
        _ => null,
    };

    private static HeaderMap<TField> ResolveHeaders<TField>(IReadOnlyList<string> headers, IReadOnlyDictionary<TField, string[]> aliases,
        IReadOnlyCollection<TField> required) where TField : struct, Enum
    {
        var map = HeaderMap<TField>.Resolve(headers, aliases, required, out var missing);
        if (missing.Count > 0)
            throw new IngestionFormatException(
                $"Missing required column(s): {string.Join(", ", missing)}. Columns found: {string.Join(", ", headers.Where(h => h.Length > 0))}.");
        return map;
    }

    private static bool TryRemap<TField>(IReadOnlyList<string> headers, IReadOnlyDictionary<TField, string[]> aliases,
        IReadOnlyCollection<TField> required, ErrorSink errors, ref HeaderMap<TField> map) where TField : struct, Enum
    {
        var next = HeaderMap<TField>.Resolve(headers, aliases, required, out var missing);
        if (missing.Count > 0)
        {
            errors.AddUncounted(0, null, null, $"A worksheet was skipped: missing column(s) {string.Join(", ", missing)}.");
            return false;
        }
        map = next;
        return true;
    }

    private async Task<string> CreateStageAsync(IngestionKind kind, Guid jobId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new SqlCommand("sm.usp_CreateStageTable", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@Kind", SqlDbType.VarChar, 8).Value = kind.ToCode();
        cmd.Parameters.Add("@JobId", SqlDbType.UniqueIdentifier).Value = jobId;
        var output = cmd.Parameters.Add("@StageTable", SqlDbType.NVarChar, 300);
        output.Direction = ParameterDirection.Output;
        await cmd.ExecuteNonQueryAsync(ct);
        return (string)output.Value;
    }

    private async Task BulkCopyAsync(string stage, System.Data.Common.DbDataReader reader, string[] columns, int batchSize, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = stage,
            BatchSize = batchSize,
            BulkCopyTimeout = 0,
            EnableStreaming = true,
        };
        foreach (var c in columns) bulk.ColumnMappings.Add(c, c);
        await bulk.WriteToServerAsync(reader, ct);
    }

    private async Task<(long Inserted, long Updated)> MergeCisAsync(string stage, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new SqlCommand("sm.usp_MergeCisStage", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };
        cmd.Parameters.Add("@StageTable", SqlDbType.NVarChar, 300).Value = stage;
        var ins = cmd.Parameters.Add("@Inserted", SqlDbType.BigInt);
        ins.Direction = ParameterDirection.Output;
        var upd = cmd.Parameters.Add("@Updated", SqlDbType.BigInt);
        upd.Direction = ParameterDirection.Output;
        await cmd.ExecuteNonQueryAsync(ct);
        return ((long)ins.Value, (long)upd.Value);
    }

    private async Task MergeMdmAsync(string stage, JobProgress progress, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var days = (await conn.QueryAsync<(DateTime LogDate, long Rows)>(new CommandDefinition(
            "sm.usp_StageDays", new { StageTable = stage }, commandType: CommandType.StoredProcedure, cancellationToken: ct))).ToList();
        for (var i = 0; i < days.Count; i++)
        {
            var day = DateOnly.FromDateTime(days[i].LogDate);
            progress.Phase(IngestionStatus.Merging, $"Merging day {i + 1} of {days.Count} ({day:yyyy-MM-dd})", 70 + 29m * i / Math.Max(1, days.Count));
            await using var cmd = new SqlCommand("sm.usp_MergeMdmStageDay", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };
            cmd.Parameters.Add("@StageTable", SqlDbType.NVarChar, 300).Value = stage;
            cmd.Parameters.Add("@LogDate", SqlDbType.Date).Value = day.ToDateTime(TimeOnly.MinValue);
            var ins = cmd.Parameters.Add("@Inserted", SqlDbType.BigInt);
            ins.Direction = ParameterDirection.Output;
            var upd = cmd.Parameters.Add("@Updated", SqlDbType.BigInt);
            upd.Direction = ParameterDirection.Output;
            await cmd.ExecuteNonQueryAsync(ct);
            progress.Inserted += (long)ins.Value;
            progress.Updated += (long)upd.Value;
        }
    }

    private async Task<long> ReportUnknownMetersAsync(string stage, ErrorSink errors, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        using var grid = await conn.QueryMultipleAsync(new CommandDefinition("sm.usp_StageUnknownMeters",
            new { StageTable = stage, Limit = limit }, commandType: CommandType.StoredProcedure, commandTimeout: 0, cancellationToken: ct));
        var count = await grid.ReadSingleAsync<long>();
        foreach (var (rowNo, meterId) in await grid.ReadAsync<(long RowNo, string MeterId)>())
            errors.AddUncounted(rowNo, "MeterId", meterId, "MeterId is not in the CIS master - upload the CIS file first.");
        return count;
    }

    private async Task DropStageAsync(string stage)
    {
        await using var conn = await db.OpenAsync(CancellationToken.None);
        await conn.ExecuteAsync("sm.usp_DropStageTable", new { StageTable = stage }, commandType: CommandType.StoredProcedure);
    }

    private async Task<DateTime> DbUtcNowAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition("SELECT SYSUTCDATETIME();", cancellationToken: ct));
    }

    private async Task PumpProgressAsync(Guid jobId, JobProgress progress, ErrorSink errors, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            await SafeAsync(() => PublishAsync(jobId, progress, errors, finished: false, ct));
        }
    }

    private async Task PublishAsync(Guid jobId, JobProgress p, ErrorSink errors, bool finished, CancellationToken ct)
    {
        var rejected = errors.RejectedRows + p.ExtraRejected;
        await repo.UpdateProgressAsync(jobId, p.Status, p.Message, p.Pct, p.Read, Math.Max(0, p.Valid - p.ExtraRejected), rejected,
            p.Inserted, p.Updated, finished, ct);
        var row = await repo.GetJobAsync(jobId, ct);
        if (row is not null) await notifier.JobUpdatedAsync(IngestionJobDto.From(row), ct);
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "Ingestion housekeeping step failed"); }
    }

    /// <summary>Thread-safe progress snapshot shared between the bulk-copy thread and the progress pump.</summary>
    private sealed class JobProgress
    {
        private readonly Lock _lock = new();
        private long _read, _valid;
        private decimal _pct;

        public volatile string Status = IngestionStatus.Parsing;
        public volatile string? Message;
        public long Inserted;
        public long Updated;
        public long ExtraRejected;

        public long Read { get { lock (_lock) return _read; } }
        public long Valid { get { lock (_lock) return _valid; } }
        public decimal Pct { get { lock (_lock) return _pct; } }

        public void Update(long read, long valid, double? filePct)
        {
            lock (_lock)
            {
                _read = read;
                _valid = valid;
                if (filePct is { } f) _pct = Math.Round((decimal)f * 0.70m, 2);   // parsing = 0..70%
            }
        }

        public void Phase(string status, string message, decimal pct)
        {
            Status = status;
            Message = message;
            lock (_lock) _pct = pct;
        }
    }
}

public sealed class IngestionFormatException(string message) : Exception(message);
