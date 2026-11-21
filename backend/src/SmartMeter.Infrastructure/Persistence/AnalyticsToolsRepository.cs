using System.Text;
using Dapper;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.Infrastructure.Persistence;

internal sealed class AnalyticsToolsRepository(ISqlConnectionFactory db) : IAnalyticsToolsRepository
{
    private const int AnalyticsTimeoutSeconds = 180;

    public async Task<IReadOnlyList<HotBuildingNspRow>> GetHotBuildingsAsync(FleetFilter f, DaySelection day, double minFailureRate,
        BoundingBox? bbox, int maxBuildings, CancellationToken ct)
    {
        var s = FleetSource.Building(f);
        var p = new DynamicParameters();
        FleetSource.AddDay(p, day, day.Column + day.Bit);
        p.Add("minRate", minFailureRate);
        p.Add("maxRows", maxBuildings * 4);
        var where = new StringBuilder("s.Lon IS NOT NULL");
        s.Where(where, p, f);
        if (bbox is { } b)
        {
            where.Append(" AND s.Lat BETWEEN @miny AND @maxy AND s.Lon BETWEEN @minx AND @maxx");
            p.Add("minx", b.MinX); p.Add("miny", b.MinY); p.Add("maxx", b.MaxX); p.Add("maxy", b.MaxY);
        }
        var sql = $"""
            WITH bn AS (
                SELECT s.BuildingKey, s.NspId,
                       {FleetSource.Sum(s.Meters)} AS Meters,
                       {FleetSource.Sum(s.MetricE(day))} AS Expected,
                       {FleetSource.Sum(s.MetricC(day))} AS Communicating
                FROM {s.Table} s
                WHERE {where}
                GROUP BY s.BuildingKey, s.NspId
            ), t AS (
                SELECT bn.*, SUM(bn.Expected) OVER (PARTITION BY bn.BuildingKey) AS BE,
                             SUM(bn.Communicating) OVER (PARTITION BY bn.BuildingKey) AS BC
                FROM bn
            )
            SELECT TOP (@maxRows) t.BuildingKey, b.Lon, b.Lat, t.NspId, t.Meters, t.Expected, t.Communicating
            FROM t
            JOIN sm.Building b ON b.BuildingKey = t.BuildingKey
            WHERE t.BE > 0 AND CAST(t.BE - t.BC AS float) / t.BE > @minRate AND b.Lon IS NOT NULL
            ORDER BY t.BE - t.BC DESC;
            """;
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<HotBuildingNspRow>(new CommandDefinition(sql, p, commandTimeout: AnalyticsTimeoutSeconds, cancellationToken: ct))).AsList();
    }

    public async Task<long> EstimateMetersInBoxAsync(BoundingBox bbox, CancellationToken ct)
    {
        const string sql = """
            SELECT CAST(ISNULL(SUM(CAST(MeterCount AS bigint)), 0) AS bigint) FROM sm.Building
            WHERE Lat BETWEEN @miny AND @maxy AND Lon BETWEEN @minx AND @maxx;
            """;
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql,
            new { minx = bbox.MinX, miny = bbox.MinY, maxx = bbox.MaxX, maxy = bbox.MaxY }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PolygonStatRow>> GetPolygonStatsAsync(string wkt, FleetFilter f, DaySelection day, bool approximate, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("wkt", wkt);
        p.Add("bit", day.Bit);
        string sql;
        if (!approximate || f.UsesMeterLevel)
        {
            // exact meter-level containment; MeterHealth carries every filter column incl. installation date
            var where = new StringBuilder("m.Geom.STIntersects(@poly) = 1");
            FleetSource.MeterHealth("h").Where(where, p, f);
            sql = $"""
                DECLARE @poly geometry = geometry::STGeomFromText(@wkt, 4326).MakeValid();
                SELECT {GroupingLabel("h")} AS Breakdown, {GroupingId("h")} AS Id,
                       COUNT_BIG(*) AS Meters,
                       {FleetSource.Sum("RIGHT_SHIFT(h.EligMask, @bit) & 1")} AS Expected,
                       {FleetSource.Sum("RIGHT_SHIFT(h.CommMask, @bit) & 1")} AS Communicating,
                       COUNT_BIG(DISTINCT h.BuildingKey) AS Buildings,
                       {FleetSource.Sum("CAST(h.ActiveLast7 AS int)")} AS Last7
                FROM sm.Meter m WITH (INDEX (SIX_Meter_Geom))
                JOIN sm.MeterHealth h ON h.MeterSk = m.MeterSk
                WHERE {where}
                GROUP BY GROUPING SETS ((), (h.NspId), (h.ManufacturerId), (h.HealthClass));
                """;
        }
        else
        {
            var s = FleetSource.Building(f, "h");
            var where = new StringBuilder("1 = 1");
            s.Where(where, p, f);
            sql = $"""
                DECLARE @poly geometry = geometry::STGeomFromText(@wkt, 4326).MakeValid();
                DECLARE @env geometry = @poly.STEnvelope();
                DECLARE @minx float = @env.STPointN(1).STX, @miny float = @env.STPointN(1).STY,
                        @maxx float = @env.STPointN(3).STX, @maxy float = @env.STPointN(3).STY;
                WITH bk AS (
                    SELECT b.BuildingKey FROM sm.Building b
                    WHERE b.Lat BETWEEN @miny AND @maxy AND b.Lon BETWEEN @minx AND @maxx
                      AND geometry::Point(b.Lon, b.Lat, 4326).STIntersects(@poly) = 1
                )
                SELECT {GroupingLabel("h")} AS Breakdown, {GroupingId("h")} AS Id,
                       {FleetSource.Sum("h.MeterCount")} AS Meters,
                       {FleetSource.Sum($"h.E{day.Column:00}")} AS Expected,
                       {FleetSource.Sum($"h.C{day.Column:00}")} AS Communicating,
                       COUNT_BIG(DISTINCT h.BuildingKey) AS Buildings,
                       {FleetSource.Sum("IIF(h.ActiveLast7 = 1, h.MeterCount, 0)")} AS Last7
                FROM sm.BuildingSummary h
                JOIN bk ON bk.BuildingKey = h.BuildingKey
                WHERE {where}
                GROUP BY GROUPING SETS ((), (h.NspId), (h.ManufacturerId), (h.HealthClass));
                """;
        }
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<PolygonStatRow>(new CommandDefinition(sql, p, commandTimeout: AnalyticsTimeoutSeconds, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<SimSwitchRow>> GetSimSwitchAsync(FleetFilter f, DaySelection day, double minStrongSuccess,
        double minWeakFailure, int minMetersPerNsp, int offset, int take, CancellationToken ct)
    {
        var s = FleetSource.Building(f);
        var p = new DynamicParameters();
        FleetSource.AddDay(p, day, day.Column + day.Bit);
        p.Add("strong", minStrongSuccess); p.Add("weak", minWeakFailure); p.Add("minMeters", minMetersPerNsp);
        p.Add("offset", offset); p.Add("take", take);
        var where = new StringBuilder("b.NspCount >= 2");
        s.Where(where, p, f, nsp: false);
        var sql = $"""
            WITH bn AS (
                SELECT s.BuildingKey, s.NspId,
                       {FleetSource.Sum(s.Meters)} AS Meters,
                       {FleetSource.Sum(s.MetricE(day))} AS Expected,
                       {FleetSource.Sum(s.MetricC(day))} AS Comm
                FROM {s.Table} s
                JOIN sm.Building b ON b.BuildingKey = s.BuildingKey
                WHERE {where}
                GROUP BY s.BuildingKey, s.NspId
                HAVING SUM({s.Meters}) >= @minMeters AND SUM(CAST({s.MetricE(day)} AS bigint)) > 0
            ), strong AS (
                SELECT bn.*, ROW_NUMBER() OVER (PARTITION BY bn.BuildingKey ORDER BY CAST(bn.Comm AS float) / bn.Expected DESC, bn.Meters DESC) AS rn
                FROM bn WHERE CAST(bn.Comm AS float) / bn.Expected >= @strong
            ), weak AS (
                SELECT bn.* FROM bn WHERE CAST(bn.Expected - bn.Comm AS float) / bn.Expected >= @weak
            )
            SELECT b.BuildingKey, b.Name, b.Address, b.ZoneId, b.Lon, b.Lat,
                   st.NspId AS StrongNspId, st.Meters AS StrongMeters, st.Expected AS StrongExpected, st.Comm AS StrongCommunicating,
                   w.NspId AS WeakNspId, w.Meters AS WeakMeters, w.Expected AS WeakExpected, w.Comm AS WeakCommunicating,
                   COUNT_BIG(*) OVER () AS TotalCount
            FROM weak w
            JOIN strong st ON st.BuildingKey = w.BuildingKey AND st.rn = 1 AND st.NspId <> w.NspId
            JOIN sm.Building b ON b.BuildingKey = w.BuildingKey
            ORDER BY CAST(w.Expected - w.Comm AS float) / w.Expected * w.Meters DESC, b.BuildingKey
            OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
            """;
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<SimSwitchRow>(new CommandDefinition(sql, p, commandTimeout: AnalyticsTimeoutSeconds, cancellationToken: ct))).AsList();
    }

    private static string GroupingLabel(string a) =>
        $"CASE WHEN GROUPING({a}.NspId) = 0 THEN 'nsp' WHEN GROUPING({a}.ManufacturerId) = 0 THEN 'manufacturer' WHEN GROUPING({a}.HealthClass) = 0 THEN 'health' ELSE 'total' END";

    private static string GroupingId(string a) =>
        $"CAST(CASE WHEN GROUPING({a}.NspId) = 0 THEN {a}.NspId WHEN GROUPING({a}.ManufacturerId) = 0 THEN {a}.ManufacturerId WHEN GROUPING({a}.HealthClass) = 0 THEN {a}.HealthClass END AS smallint)";
}
