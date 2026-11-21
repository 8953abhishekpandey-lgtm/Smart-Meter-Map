using System.Data;
using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.Data.SqlClient;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Ingestion.Parsing;

namespace SmartMeter.Infrastructure.Persistence;

public sealed class IngestionRepository(ISqlConnectionFactory db) : IIngestionRepository
{
    private const string Columns = """
        JobId, Kind, FileName, FileSize, Status, Message, ProgressPct, RowsRead, RowsValid, RowsRejected,
        RowsInserted, RowsUpdated, CreatedBy, CreatedAt, StartedAt, FinishedAt
        """;

    public async Task CreateJobAsync(Guid jobId, string kind, string fileName, long fileSize, string? createdBy, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO sm.IngestionJob (JobId, Kind, FileName, FileSize, Status, CreatedBy)
            VALUES (@jobId, @kind, @fileName, @fileSize, 'uploading', @createdBy);
            """;
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { jobId, kind, fileName, fileSize, createdBy }, cancellationToken: ct));
    }

    public async Task<IngestionJobRow?> GetJobAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<IngestionJobRow>(new CommandDefinition(
            $"SELECT {Columns} FROM sm.IngestionJob WHERE JobId = @jobId;", new { jobId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<IngestionJobRow>> ListJobsAsync(int take, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<IngestionJobRow>(new CommandDefinition(
            $"SELECT TOP (@take) {Columns} FROM sm.IngestionJob ORDER BY CreatedAt DESC;", new { take }, cancellationToken: ct))).AsList();
    }

    public async Task SetStatusAsync(Guid jobId, string status, string? message, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sm.IngestionJob SET Status = @status, Message = @message WHERE JobId = @jobId;",
            new { jobId, status, message = Truncate(message) }, cancellationToken: ct));
    }

    public async IAsyncEnumerable<IngestionErrorRow> StreamErrorsAsync(Guid jobId, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await foreach (var row in conn.QueryUnbufferedAsync<IngestionErrorRow>(
                           "SELECT RowNo, ColumnName, RawValue, Message FROM sm.IngestionError WHERE JobId = @jobId ORDER BY RowNo;",
                           new { jobId }).WithCancellation(ct))
            yield return row;
    }

    // ---------------------------------------------------------------- used by the ingestion worker
    internal async Task MarkStartedAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE sm.IngestionJob SET Status = 'parsing', StartedAt = SYSUTCDATETIME(), FinishedAt = NULL, ProgressPct = 0,
                   RowsRead = 0, RowsValid = 0, RowsRejected = 0, RowsInserted = 0, RowsUpdated = 0, Message = N'Reading file'
            WHERE JobId = @jobId;
            DELETE FROM sm.IngestionError WHERE JobId = @jobId;
            """, new { jobId }, cancellationToken: ct));
    }

    internal async Task UpdateProgressAsync(Guid jobId, string status, string? message, decimal pct, long read, long valid,
        long rejected, long inserted, long updated, bool finished, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE sm.IngestionJob
               SET Status = @status, Message = @message, ProgressPct = @pct, RowsRead = @read, RowsValid = @valid,
                   RowsRejected = @rejected, RowsInserted = @inserted, RowsUpdated = @updated,
                   FinishedAt = CASE WHEN @finished = 1 THEN SYSUTCDATETIME() ELSE FinishedAt END
             WHERE JobId = @jobId;
            """, new { jobId, status, message = Truncate(message), pct = Math.Clamp(pct, 0, 100), read, valid, rejected, inserted, updated, finished },
            cancellationToken: ct));
    }

    internal async Task SaveErrorsAsync(Guid jobId, IReadOnlyList<RowError> errors, CancellationToken ct)
    {
        if (errors.Count == 0) return;
        var table = new DataTable();
        table.Columns.Add("JobId", typeof(Guid));
        table.Columns.Add("RowNo", typeof(long));
        table.Columns.Add("ColumnName", typeof(string));
        table.Columns.Add("RawValue", typeof(string));
        table.Columns.Add("Message", typeof(string));
        foreach (var e in errors) table.Rows.Add(jobId, e.RowNo, (object?)e.Column ?? DBNull.Value, (object?)e.RawValue ?? DBNull.Value, e.Message);
        await using var conn = await db.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "sm.IngestionError", BatchSize = 5000 };
        foreach (DataColumn c in table.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        await bulk.WriteToServerAsync(table, ct);
    }

    private static string? Truncate(string? s) => s is null || s.Length <= 2000 ? s : s[..2000];
}
