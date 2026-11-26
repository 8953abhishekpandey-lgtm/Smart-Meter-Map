using System.Text;
using Dapper;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.Infrastructure.Persistence;

internal sealed class FleetReadRepository(ISqlConnectionFactory db) : IFleetReadRepository
{
    private const int CommandTimeoutSeconds = 60;

    public async Task<KpiRow> GetKpisAsync(FleetFilter f, AnalyticsSnapshot snapshot, DaySelection day, CancellationToken ct)
    {
        var src = FleetSource.Kpi(f);
        var p = new DynamicParameters();
        FleetSource.AddDay(p, day, snapshot.Window.Days);
        var hp = f.Health == HealthFilter.TodayFail ? "1 = 1" : src.Health(f.Health);
        var a = src.A;
        var sql = new StringBuilder($"""
            SELECT {FleetSource.Sum(src.Meters)} AS AllMeters,
                   {FleetSource.Sum($"CASE WHEN {hp} THEN {src.Meters} END")} AS TotalMeters,
                   {FleetSource.Sum($"CASE WHEN {hp} THEN {src.E(day)} END")} AS Expected,
                   {FleetSource.Sum($"CASE WHEN {hp} THEN {src.C(day)} END")} AS Communicating,
                   {FleetSource.Sum($"CASE WHEN {hp} THEN {src.WindowE(day)} END")} AS ExpectedWindow,
                   {FleetSource.Sum($"CASE WHEN {hp} THEN {src.WindowC(day)} END")} AS CommunicatingWindow,
                   {FleetSource.Sum($"CASE WHEN {a}.HealthClass = 2 THEN {src.Meters} END")} AS AllDay,
                   {FleetSource.Sum($"CASE WHEN {a}.ActiveLast7 = 1 THEN {src.Meters} END")} AS Last7,
                   {FleetSource.Sum($"CASE WHEN {a}.HealthClass = 0 THEN {src.Meters} END")} AS Dead,
                   {FleetSource.Sum($"CASE WHEN {a}.HealthClass = 1 THEN {src.Meters} END")} AS Intermittent,
                   {FleetSource.Sum($"{src.E(day)} - {src.C(day)}")} AS TodayFail
            FROM {src.Table} {a}
            WHERE 1 = 1
            """);
        src.Where(sql, p, f);
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleAsync<KpiRow>(new CommandDefinition(sql.ToString(), p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    public async Task<DaySeries> GetTrendAsync(FleetFilter f, AnalyticsSnapshot snapshot, CancellationToken ct)
    {
        var days = snapshot.Window.Days;
        var src = FleetSource.Kpi(f);
        var p = new DynamicParameters();
        var cols = src.MeterLevel ? SqlText.MaskDayColumns(src.A, days) : SqlText.DayColumns(src.A, days, sum: true);
        var hp = f.Health == HealthFilter.TodayFail ? "1 = 1" : src.Health(f.Health);
        var sql = new StringBuilder($"SELECT {cols} FROM {src.Table} {src.A} WHERE {hp}");
        src.Where(sql, p, f);
        await using var conn = await db.OpenAsync(ct);
        var row = (IDictionary<string, object?>)await conn.QuerySingleAsync(new CommandDefinition(sql.ToString(), p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        var e = new long[days];
        var c = new long[days];
        for (var k = 1; k <= days; k++)
        {
            e[k - 1] = Convert.ToInt64(row[$"E{k:00}"] ?? 0L);
            c[k - 1] = Convert.ToInt64(row[$"C{k:00}"] ?? 0L);
        }
        return new DaySeries(e, c);
    }

    public async Task<IReadOnlyList<DimensionCountRow>> GetDimensionCountsAsync(FleetFilter f, CancellationToken ct)
    {
        var src = FleetSource.Kpi(f);
        var p = new DynamicParameters();
        var hp = f.Health == HealthFilter.TodayFail ? "1 = 1" : src.Health(f.Health);
        string Part(string dim, string column, bool zone, bool nsp, bool mfr)
        {
            var sb = new StringBuilder($"SELECT '{dim}' AS Dim, {src.A}.{column} AS Id, {FleetSource.Sum(src.Meters)} AS Meters FROM {src.Table} {src.A} WHERE {hp}");
            src.Where(sb, p, f, zone, nsp, mfr);
            sb.Append($" GROUP BY {src.A}.{column}");
            return sb.ToString();
        }
        var sql = string.Join("\nUNION ALL\n",
            Part("zone", "ZoneId", zone: false, nsp: true, mfr: true),
            Part("nsp", "NspId", zone: true, nsp: false, mfr: true),
            Part("manufacturer", "ManufacturerId", zone: true, nsp: true, mfr: false));
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<DimensionCountRow>(new CommandDefinition(sql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<BubbleRow>> GetBubblesAsync(BoundingBox bbox, GridLevel? level, FleetFilter f,
        AnalyticsSnapshot snapshot, DaySelection day, int limit, CancellationToken ct)
    {
        var p = new DynamicParameters();
        FleetSource.AddDay(p, day, snapshot.Window.Days);
        p.Add("limit", limit);
        p.Add("minx", bbox.MinX); p.Add("miny", bbox.MinY); p.Add("maxx", bbox.MaxX); p.Add("maxy", bbox.MaxY);
        var todayFail = f.Health == HealthFilter.TodayFail;
        // Today-Fail: bubble size = failing meters on the day, colour = local failure rate on that day.
        var metricDay = todayFail ? day with { Metric = MetricMode.Day } : day;

        string sql;
        if (!f.UsesMeterLevel && level is not null)
        {
            var g = FleetSource.Grid();
            var meters = todayFail ? FleetSource.Sum($"{g.E(day)} - {g.C(day)}") : FleetSource.Sum(g.Meters);
            p.Add("level", level.Level);
            p.Add("x1", (int)Math.Floor(bbox.MinX / level.CellDeg)); p.Add("x2", (int)Math.Floor(bbox.MaxX / level.CellDeg));
            p.Add("y1", (int)Math.Floor(bbox.MinY / level.CellDeg)); p.Add("y2", (int)Math.Floor(bbox.MaxY / level.CellDeg));
            var where = new StringBuilder($"g.Level = @level AND g.CellX BETWEEN @x1 AND @x2 AND g.CellY BETWEEN @y1 AND @y2 AND {(todayFail ? "1 = 1" : g.Health(f.Health))}");
            g.Where(where, p, f);
            sql = $"""
                SELECT TOP (@limit) g.CellX, g.CellY,
                       SUM(g.SumLon) / NULLIF(SUM(g.MeterCount), 0) AS Lon,
                       SUM(g.SumLat) / NULLIF(SUM(g.MeterCount), 0) AS Lat,
                       {meters} AS Meters,
                       {FleetSource.SumMetric(g.MetricE(metricDay), metricDay)} AS Expected,
                       {FleetSource.SumMetric(g.MetricC(metricDay), metricDay)} AS Communicating,
                       IIF(MIN(g.MinBuildingKey) = MAX(g.MaxBuildingKey), MIN(g.MinBuildingKey), NULL) AS BuildingKey
                FROM sm.GridSummary g
                WHERE {where}
                GROUP BY g.CellX, g.CellY
                HAVING {meters} > 0
                ORDER BY Meters DESC;
                """;
        }
        else if (!f.UsesMeterLevel)
        {
            var s = FleetSource.Building(f);
            var meters = todayFail ? FleetSource.Sum($"{s.E(day)} - {s.C(day)}") : FleetSource.Sum(s.Meters);
            var where = new StringBuilder($"s.Lat BETWEEN @miny AND @maxy AND s.Lon BETWEEN @minx AND @maxx AND {(todayFail ? "1 = 1" : s.Health(f.Health))}");
            s.Where(where, p, f);
            sql = $"""
                SELECT x.BuildingKey, x.Lon, x.Lat, x.Meters, x.Expected, x.Communicating, b.Name, b.Address
                FROM (
                    SELECT TOP (@limit) s.BuildingKey, MIN(s.Lon) AS Lon, MIN(s.Lat) AS Lat,
                           {meters} AS Meters,
                           {FleetSource.SumMetric(s.MetricE(metricDay), metricDay)} AS Expected,
                           {FleetSource.SumMetric(s.MetricC(metricDay), metricDay)} AS Communicating
                    FROM sm.BuildingSummary s
                    WHERE {where}
                    GROUP BY s.BuildingKey
                    HAVING {meters} > 0
                    ORDER BY Meters DESC) x
                JOIN sm.Building b ON b.BuildingKey = x.BuildingKey
                ORDER BY x.Meters DESC;
                """;
        }
        else
        {
            // installation-lot filter -> meter level
            var h = FleetSource.Building(f, "h");
            var meters = todayFail ? FleetSource.Sum($"{h.E(day)} - {h.C(day)}") : FleetSource.Sum("1");
            var where = new StringBuilder($"h.Lat BETWEEN @miny AND @maxy AND h.Lon BETWEEN @minx AND @maxx AND {(todayFail ? "1 = 1" : h.Health(f.Health))}");
            h.Where(where, p, f);
            var e = FleetSource.SumMetric(h.MetricE(metricDay), metricDay);
            var c = FleetSource.SumMetric(h.MetricC(metricDay), metricDay);
            if (level is not null)
            {
                p.Add("cell", level.CellDeg);
                sql = $"""
                    SELECT TOP (@limit) k.CellX, k.CellY, AVG(h.Lon) AS Lon, AVG(h.Lat) AS Lat,
                           {meters} AS Meters, {e} AS Expected, {c} AS Communicating,
                           IIF(MIN(h.BuildingKey) = MAX(h.BuildingKey), MIN(h.BuildingKey), NULL) AS BuildingKey
                    FROM sm.MeterHealth h
                    CROSS APPLY (SELECT CAST(FLOOR(h.Lon / @cell) AS int) AS CellX, CAST(FLOOR(h.Lat / @cell) AS int) AS CellY) k
                    WHERE {where}
                    GROUP BY k.CellX, k.CellY
                    HAVING {meters} > 0
                    ORDER BY Meters DESC;
                    """;
            }
            else
            {
                sql = $"""
                    SELECT x.BuildingKey, x.Lon, x.Lat, x.Meters, x.Expected, x.Communicating, b.Name, b.Address
                    FROM (
                        SELECT TOP (@limit) h.BuildingKey, AVG(h.Lon) AS Lon, AVG(h.Lat) AS Lat,
                               {meters} AS Meters, {e} AS Expected, {c} AS Communicating
                        FROM sm.MeterHealth h
                        WHERE {where}
                        GROUP BY h.BuildingKey
                        HAVING {meters} > 0
                        ORDER BY Meters DESC) x
                    JOIN sm.Building b ON b.BuildingKey = x.BuildingKey
                    ORDER BY x.Meters DESC;
                    """;
            }
        }
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<BubbleRow>(new CommandDefinition(sql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<ZoneRow>> GetZonesAsync(FleetFilter f, DaySelection day, CancellationToken ct)
    {
        var src = FleetSource.Kpi(f);
        var p = new DynamicParameters();
        p.Add("bit", day.Bit);
        p.Add("rmask", day.RangeMask);
        var hp = f.Health == HealthFilter.TodayFail ? "1 = 1" : src.Health(f.Health);
        var a = src.A;
        var inner = new StringBuilder($"""
            SELECT {a}.ZoneId,
                   {FleetSource.Sum($"CASE WHEN {hp} THEN {src.Meters} END")} AS Meters,
                   {FleetSource.Sum($"CASE WHEN {hp} THEN {src.E(day)} END")} AS Expected,
                   {FleetSource.Sum($"CASE WHEN {hp} THEN {src.C(day)} END")} AS Communicating,
                   {FleetSource.Sum($"CASE WHEN {hp} THEN {src.WindowE(day)} END")} AS ExpectedWindow,
                   {FleetSource.Sum($"CASE WHEN {hp} THEN {src.WindowC(day)} END")} AS CommunicatingWindow,
                   {FleetSource.Sum($"CASE WHEN {a}.HealthClass = 2 THEN {src.Meters} END")} AS AllDay,
                   {FleetSource.Sum($"CASE WHEN {a}.ActiveLast7 = 1 THEN {src.Meters} END")} AS Last7,
                   {FleetSource.Sum($"CASE WHEN {a}.HealthClass = 0 THEN {src.Meters} END")} AS Dead,
                   {FleetSource.Sum($"CASE WHEN {a}.HealthClass = 1 THEN {src.Meters} END")} AS Intermittent,
                   {FleetSource.Sum($"{src.E(day)} - {src.C(day)}")} AS TodayFail
            FROM {src.Table} {a}
            WHERE 1 = 1
            """);
        src.Where(inner, p, f, zone: false);
        inner.Append($" GROUP BY {a}.ZoneId");
        var sql = $"""
            SELECT z.*, e.BuildingCount, e.MinLon, e.MinLat, e.MaxLon, e.MaxLat
            FROM ({inner}) z
            LEFT JOIN sm.ZoneExtent e ON e.ZoneId = z.ZoneId;
            """;
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<ZoneRow>(new CommandDefinition(sql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<ZoneBuildingRow>> GetZoneBuildingsAsync(short zoneId, FleetFilter f, DaySelection day,
        string? search, BuildingSort sort, int offset, int take, CancellationToken ct)
    {
        var s = FleetSource.Building(f);
        var p = new DynamicParameters();
        p.Add("zone", zoneId); p.Add("offset", offset); p.Add("take", take);
        FleetSource.AddDay(p, day, DaysOf(day));
        p.Add("search", string.IsNullOrWhiteSpace(search) ? null : "%" + SqlText.EscapeLike(search.Trim()) + "%");
        var hp = f.Health == HealthFilter.TodayFail ? "1 = 1" : s.Health(f.Health);
        var where = new StringBuilder($"s.ZoneId = @zone AND {hp}");
        s.Where(where, p, f, zone: false);
        var order = sort switch
        {
            BuildingSort.Meters => "a.Meters DESC",
            BuildingSort.Name => "b.Name ASC",
            BuildingSort.Failing => "(a.Expected - a.Communicating) DESC",
            _ => "CAST(a.Expected - a.Communicating AS float) / NULLIF(a.Expected, 0) DESC, a.Meters DESC",
        };
        var sql = $"""
            WITH a AS (
                SELECT s.BuildingKey,
                       {FleetSource.Sum(s.Meters)} AS Meters,
                       {FleetSource.SumMetric(s.MetricE(day), day)} AS Expected,
                       {FleetSource.SumMetric(s.MetricC(day), day)} AS Communicating
                FROM {s.Table} s
                WHERE {where}
                GROUP BY s.BuildingKey
            )
            SELECT a.BuildingKey, b.Name, b.Address, b.IsVirtual, b.NspCount, b.Lon, b.Lat,
                   a.Meters, a.Expected, a.Communicating, COUNT_BIG(*) OVER () AS TotalCount
            FROM a
            JOIN sm.Building b ON b.BuildingKey = a.BuildingKey
            WHERE (@search IS NULL OR b.Name LIKE @search OR b.Address LIKE @search)
            ORDER BY {order}, a.BuildingKey
            OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
            """;
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<ZoneBuildingRow>(new CommandDefinition(sql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
    }

    // Window metric divides by the window length; the snapshot is not passed here so derive from the bit/column.
    private static int DaysOf(DaySelection day) => day.Column + day.Bit;
}
