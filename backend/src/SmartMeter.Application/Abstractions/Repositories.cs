using SmartMeter.Application.Common;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.Application.Abstractions;

/// <summary>Dashboard KPIs, trend, cascading filter options, map bubbles and zone explorer.</summary>
public interface IFleetReadRepository
{
    Task<KpiRow> GetKpisAsync(FleetFilter filter, AnalyticsSnapshot snapshot, DaySelection day, CancellationToken ct);
    Task<DaySeries> GetTrendAsync(FleetFilter filter, AnalyticsSnapshot snapshot, CancellationToken ct);
    Task<IReadOnlyList<DimensionCountRow>> GetDimensionCountsAsync(FleetFilter filter, CancellationToken ct);

    /// <param name="level">Pyramid level, or null for building bubbles.</param>
    Task<IReadOnlyList<BubbleRow>> GetBubblesAsync(BoundingBox bbox, GridLevel? level, FleetFilter filter,
        AnalyticsSnapshot snapshot, DaySelection day, int limit, CancellationToken ct);

    Task<IReadOnlyList<ZoneRow>> GetZonesAsync(FleetFilter filter, DaySelection day, CancellationToken ct);
    Task<IReadOnlyList<ZoneBuildingRow>> GetZoneBuildingsAsync(short zoneId, FleetFilter filter, DaySelection day,
        string? search, BuildingSort sort, int offset, int take, CancellationToken ct);
}

public interface IBuildingReadRepository
{
    Task<BuildingRow?> GetBuildingAsync(string buildingKey, CancellationToken ct);
    Task<IReadOnlyList<BuildingSliceRow>> GetSlicesAsync(string buildingKey, CancellationToken ct);
    Task<IReadOnlyList<BuildingMeterRow>> GetMetersPageAsync(string buildingKey, DaySelection day, string? search,
        MeterStatusFilter status, MeterSort sort, bool descending, int offset, int take, CancellationToken ct);
    IAsyncEnumerable<BuildingMeterRow> StreamMetersAsync(string buildingKey, DaySelection day, string? search,
        MeterStatusFilter status, CancellationToken ct);
    Task<IReadOnlyList<MeterCommRow>> GetMeterCommunicationAsync(string buildingKey, CancellationToken ct);
}

public interface IMeterReadRepository
{
    Task<IReadOnlyList<MeterSearchRow>> SearchAsync(string prefix, int limit, CancellationToken ct);
    Task<MeterRow?> GetMeterAsync(string meterId, CancellationToken ct);
    Task<IReadOnlyList<TimelineRow>> GetTimelineAsync(int meterSk, DateOnly from, DateOnly to, CancellationToken ct);
}

public interface IAnalyticsToolsRepository
{
    Task<IReadOnlyList<HotBuildingNspRow>> GetHotBuildingsAsync(FleetFilter filter, DaySelection day, double minFailureRate,
        BoundingBox? bbox, int maxBuildings, CancellationToken ct);

    /// <summary>Estimated meters inside the polygon's bounding box (building centroids), used to pick exact vs approximate.</summary>
    Task<long> EstimateMetersInBoxAsync(BoundingBox bbox, CancellationToken ct);

    Task<IReadOnlyList<PolygonStatRow>> GetPolygonStatsAsync(string wkt, FleetFilter filter, DaySelection day,
        bool approximate, CancellationToken ct);

    Task<IReadOnlyList<SimSwitchRow>> GetSimSwitchAsync(FleetFilter filter, DaySelection day, double minStrongSuccess,
        double minWeakFailure, int minMetersPerNsp, int offset, int take, CancellationToken ct);
}

public interface IIngestionRepository
{
    Task CreateJobAsync(Guid jobId, string kind, string fileName, long fileSize, string? createdBy, CancellationToken ct);
    Task<IngestionJobRow?> GetJobAsync(Guid jobId, CancellationToken ct);
    Task<IReadOnlyList<IngestionJobRow>> ListJobsAsync(int take, CancellationToken ct);
    Task SetStatusAsync(Guid jobId, string status, string? message, CancellationToken ct);
    IAsyncEnumerable<IngestionErrorRow> StreamErrorsAsync(Guid jobId, CancellationToken ct);
}

/// <summary>Chunked/streaming upload storage (local disk or shared volume; swap for blob storage in the cloud).</summary>
public interface IUploadStore
{
    Task WriteChunkAsync(Guid jobId, int index, long chunkSize, Stream content, CancellationToken ct);
    Task<int> CountChunksAsync(Guid jobId, CancellationToken ct);
    Task WriteWholeAsync(Guid jobId, Stream content, long maxBytes, CancellationToken ct);
    Task<long> FinalizeAsync(Guid jobId, CancellationToken ct);
    string GetFilePath(Guid jobId);
    void Delete(Guid jobId);
}

public interface IBackgroundJobs
{
    string EnqueueIngestion(Guid jobId);
    /// <param name="requestedAtUtc">Database time of the change; a refresh that started later already covers it (coalescing). Null = always run.</param>
    string EnqueueAnalyticsRefresh(DateTime? requestedAtUtc);
    /// <summary>Scan the watch folder now, retrying files that failed before.</summary>
    string EnqueueFolderScan();
}

/// <summary>Pushes live job progress / data-version changes to connected clients (SignalR).</summary>
public interface IIngestionNotifier
{
    Task JobUpdatedAsync(IngestionJobDto job, CancellationToken ct = default);
    Task DataVersionChangedAsync(long dataVersion, CancellationToken ct = default);
}

public interface ICurrentUser
{
    string? Name { get; }
}

/// <summary>Meter list status filter inside a building.</summary>
public enum MeterStatusFilter { All = 0, Communicating, Failing, AllDay, Dead, Intermittent }

public enum MeterSort { MeterId = 0, CommDays, LastComm, Nsp, Manufacturer, Installation }

public enum BuildingSort { FailureRate = 0, Meters, Name, Failing }
