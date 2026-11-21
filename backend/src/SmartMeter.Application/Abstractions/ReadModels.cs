namespace SmartMeter.Application.Abstractions;

// Flat row types returned by the Infrastructure repositories (mutable for Dapper materialisation).

public sealed class KpiRow
{
    public long AllMeters { get; set; }
    public long TotalMeters { get; set; }
    public long Expected { get; set; }
    public long Communicating { get; set; }
    public long ExpectedWindow { get; set; }
    public long CommunicatingWindow { get; set; }
    public long AllDay { get; set; }
    public long Last7 { get; set; }
    public long Dead { get; set; }
    public long Intermittent { get; set; }
    public long TodayFail { get; set; }
}

/// <summary>Per-day counters, index 0 = first day of the window.</summary>
public sealed record DaySeries(long[] Expected, long[] Communicating);

public sealed class DimensionCountRow
{
    public string Dim { get; set; } = "";
    public short Id { get; set; }
    public long Meters { get; set; }
}

public sealed class BubbleRow
{
    public int? CellX { get; set; }
    public int? CellY { get; set; }
    public string? BuildingKey { get; set; }
    public double Lon { get; set; }
    public double Lat { get; set; }
    public long Meters { get; set; }
    public long Expected { get; set; }
    public long Communicating { get; set; }
    public string? Name { get; set; }
    public string? Address { get; set; }
}

public sealed class BuildingRow
{
    public string BuildingKey { get; set; } = "";
    public string? BuildingId { get; set; }
    public bool IsVirtual { get; set; }
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public short ZoneId { get; set; }
    public int MeterCount { get; set; }
    public byte NspCount { get; set; }
    public double? Lon { get; set; }
    public double? Lat { get; set; }
}

/// <summary>One (NSP, manufacturer, health, last7) slice of a building with per-day counters.</summary>
public sealed record BuildingSliceRow(
    short NspId, short ManufacturerId, byte HealthClass, bool ActiveLast7,
    long MeterCount, long EligTotal, long CommTotal, long[] E, long[] C);

public sealed class BuildingMeterRow
{
    public string MeterId { get; set; } = "";
    public string? Address { get; set; }
    public short NspId { get; set; }
    public short ManufacturerId { get; set; }
    public DateOnly? InstallationDate { get; set; }
    public byte HealthClass { get; set; }
    public byte EligDays { get; set; }
    public byte CommDays { get; set; }
    public DateOnly? LastCommDate { get; set; }
    public int EligMask { get; set; }
    public int CommMask { get; set; }
    public long TotalCount { get; set; }
}

public sealed class MeterCommRow
{
    public string MeterId { get; set; } = "";
    public byte CommDays { get; set; }
}

public sealed class MeterSearchRow
{
    public string MeterId { get; set; } = "";
    public string? Address { get; set; }
    public string BuildingKey { get; set; } = "";
    public double? Lon { get; set; }
    public double? Lat { get; set; }
    public short ZoneId { get; set; }
}

public sealed class MeterRow
{
    public int MeterSk { get; set; }
    public string MeterId { get; set; } = "";
    public string? BuildingId { get; set; }
    public string BuildingKey { get; set; } = "";
    public string? BuildingName { get; set; }
    public string? Address { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public short ZoneId { get; set; }
    public short NspId { get; set; }
    public short ManufacturerId { get; set; }
    public DateOnly? InstallationDate { get; set; }
    public string? DtId { get; set; }
    public byte? HealthClass { get; set; }
    public byte? EligDays { get; set; }
    public byte? CommDays { get; set; }
    public DateOnly? LastCommDate { get; set; }
    public int? EligMask { get; set; }
    public int? CommMask { get; set; }
}

public sealed class TimelineRow
{
    public DateOnly LogDate { get; set; }
    public bool IsCommunicating { get; set; }
}

public sealed class HotBuildingNspRow
{
    public string BuildingKey { get; set; } = "";
    public double Lon { get; set; }
    public double Lat { get; set; }
    public short NspId { get; set; }
    public long Meters { get; set; }
    public long Expected { get; set; }
    public long Communicating { get; set; }
}

public sealed class PolygonStatRow
{
    public string Breakdown { get; set; } = "";
    public short? Id { get; set; }
    public long Meters { get; set; }
    public long Expected { get; set; }
    public long Communicating { get; set; }
    public long Buildings { get; set; }
    public long Last7 { get; set; }
}

public sealed class SimSwitchRow
{
    public string BuildingKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public short ZoneId { get; set; }
    public double? Lon { get; set; }
    public double? Lat { get; set; }
    public short StrongNspId { get; set; }
    public long StrongMeters { get; set; }
    public long StrongExpected { get; set; }
    public long StrongCommunicating { get; set; }
    public short WeakNspId { get; set; }
    public long WeakMeters { get; set; }
    public long WeakExpected { get; set; }
    public long WeakCommunicating { get; set; }
    public long TotalCount { get; set; }
}

public sealed class ZoneRow
{
    public short ZoneId { get; set; }
    public long Meters { get; set; }
    public long Expected { get; set; }
    public long Communicating { get; set; }
    public long ExpectedWindow { get; set; }
    public long CommunicatingWindow { get; set; }
    public long AllDay { get; set; }
    public long Last7 { get; set; }
    public long Dead { get; set; }
    public long Intermittent { get; set; }
    public long TodayFail { get; set; }
    public int? BuildingCount { get; set; }
    public double? MinLon { get; set; }
    public double? MinLat { get; set; }
    public double? MaxLon { get; set; }
    public double? MaxLat { get; set; }
}

public sealed class ZoneBuildingRow
{
    public string BuildingKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public bool IsVirtual { get; set; }
    public byte NspCount { get; set; }
    public double? Lon { get; set; }
    public double? Lat { get; set; }
    public long Meters { get; set; }
    public long Expected { get; set; }
    public long Communicating { get; set; }
    public long TotalCount { get; set; }
}

public sealed class IngestionJobRow
{
    public Guid JobId { get; set; }
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string Status { get; set; } = "";
    public string? Message { get; set; }
    public decimal ProgressPct { get; set; }
    public long RowsRead { get; set; }
    public long RowsValid { get; set; }
    public long RowsRejected { get; set; }
    public long RowsInserted { get; set; }
    public long RowsUpdated { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public sealed class IngestionErrorRow
{
    public long RowNo { get; set; }
    public string? ColumnName { get; set; }
    public string? RawValue { get; set; }
    public string Message { get; set; } = "";
}
