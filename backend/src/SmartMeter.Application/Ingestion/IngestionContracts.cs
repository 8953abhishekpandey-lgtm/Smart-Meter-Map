namespace SmartMeter.Application;

public enum IngestionKind
{
    Cis = 0,
    Mdm = 1,
}

public static class IngestionKinds
{
    public static string ToCode(this IngestionKind kind) => kind == IngestionKind.Cis ? "cis" : "mdm";

    public static IngestionKind Parse(string code) => code.Equals("cis", StringComparison.OrdinalIgnoreCase)
        ? IngestionKind.Cis
        : code.Equals("mdm", StringComparison.OrdinalIgnoreCase) ? IngestionKind.Mdm
        : throw new ArgumentOutOfRangeException(nameof(code), code, "kind must be cis or mdm");
}

public static class IngestionStatus
{
    public const string Uploading = "uploading";
    public const string Queued = "queued";
    public const string Parsing = "parsing";
    public const string Merging = "merging";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public sealed class IngestionOptions
{
    public const string Section = "Ingestion";
    public string StoragePath { get; set; } = Path.Combine(Path.GetTempPath(), "smartmeter-uploads");
    public long MaxFileSizeBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public int ChunkSizeBytes { get; set; } = 8 * 1024 * 1024;
    public int MaxStoredErrors { get; set; } = 10_000;
    public int BulkCopyBatchSize { get; set; } = 50_000;
    public bool DeleteFileAfterProcessing { get; set; } = true;
    public string[] AllowedExtensions { get; set; } = [".csv", ".txt", ".xlsx", ".xlsb", ".xls"];
    /// <summary>Optional service-area geofence for CIS coordinates (null = accept any valid WGS84 point).</summary>
    public ServiceAreaOptions? ServiceArea { get; set; }

    /// <summary>
    /// Folder the server watches for Excel/CSV files (empty = off). Absolute, or relative to the API's content root.
    /// New and changed files are imported automatically, CIS before MDM, one at a time; the files are never moved or deleted.
    /// </summary>
    public string? WatchFolder { get; set; }
    public int WatchFolderScanMinutes { get; set; } = 1;
    /// <summary>A file must be unchanged for this long before it is read, so half-copied files are never imported.</summary>
    public int WatchFolderStableSeconds { get; set; } = 20;
    public bool WatchFolderIncludeSubfolders { get; set; } = true;
    /// <summary>Accepted textual date formats (Excel date cells are read natively).</summary>
    public string[] DateFormats { get; set; } =
        ["yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "yyyy/MM/dd", "dd.MM.yyyy", "dd-MMM-yyyy", "d-MMM-yy", "yyyyMMdd", "yyyy-MM-dd HH:mm:ss", "dd/MM/yyyy HH:mm:ss"];
}

public sealed record IngestionJobDto(
    Guid JobId, string Kind, string FileName, long FileSize, string Status, string? Message, decimal ProgressPct,
    long RowsRead, long RowsValid, long RowsRejected, long RowsInserted, long RowsUpdated,
    string? CreatedBy, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt)
{
    public static IngestionJobDto From(Abstractions.IngestionJobRow r) => new(
        r.JobId, r.Kind, r.FileName, r.FileSize, r.Status, r.Message, r.ProgressPct, r.RowsRead, r.RowsValid,
        r.RowsRejected, r.RowsInserted, r.RowsUpdated, r.CreatedBy,
        DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
        r.StartedAt is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null,
        r.FinishedAt is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null);
}

public sealed class ServiceAreaOptions
{
    public double MinLat { get; set; }
    public double MinLon { get; set; }
    public double MaxLat { get; set; }
    public double MaxLon { get; set; }

    public Domain.Spatial.BoundingBox ToBox() => new(MinLon, MinLat, MaxLon, MaxLat);
}
