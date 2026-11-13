using Dapper;
using Microsoft.Data.SqlClient;
using MeterMap.Core;

namespace MeterMap.Data;

// ======================= SQL result models =======================

public sealed class SummaryRow
{
    public string MeterId { get; set; } = "";
    public DateTime? FirstRow { get; set; }
    public DateTime? LastRow { get; set; }
    public DateTime? LastValid { get; set; }
    public long ValidCount { get; set; }
    public long ValidRecent { get; set; }
}

public sealed class GapRow
{
    public string MeterId { get; set; } = "";
    public int GapMinutes { get; set; }
    public long N { get; set; }
}

public sealed class LastValidRow
{
    public string MeterId { get; set; } = "";
    public double? VMin { get; set; }
    public double? Temp { get; set; }
}

public sealed class LastSeenRow
{
    public string MeterId { get; set; } = "";
    public DateTime? LastSeen { get; set; }
}

// ======================= Time range =======================

public sealed record TimeRange(
    DateTime? From,
    bool FromInclusive,
    DateTime? To);

// ======================= Meter Repository =======================

public sealed class MeterRepository(SqlConnectionFactory factory)
{
    private const int CmdTimeout = 300;

    // SQL date range condition
    private static string Where(TimeRange range, string timeColumn)
    {
        var conditions = new List<string>();

        if (range.From.HasValue)
        {
            conditions.Add(
                $"{timeColumn} {(range.FromInclusive ? ">=" : ">")} @fromTs");
        }

        if (range.To.HasValue)
        {
            conditions.Add($"{timeColumn} <= @toTs");
        }

        return conditions.Count == 0
            ? "1=1"
            : string.Join(" AND ", conditions);
    }

    // Meter ID expression
    private static string MeterIdExpr(string alias, string column)
    {
        return $"CAST({alias}.{Sql.Col(column)} AS nvarchar(100))";
    }

    // Parameter values from profile
    private static object GetParameterValues(DbProfile profile)
    {
        var ids = profile.ParameterIds;

        return new
        {
            VB = ids.VB,
            VR = ids.VR,
            VY = ids.VY,
            IR = ids.IR,
            IY = ids.IY,
            IB = ids.IB,
            IntKvahI = ids.IntKvahI,
            IntKwhI = ids.IntKwhI
        };
    }

    // Valid telemetry check
    private static string ValidTelemetryCondition(string alias)
    {
        return $"""
            (
                ({alias}.PARAM_ID = @VB AND TRY_CONVERT(float, {alias}.PARAM_VALUE) IS NOT NULL)
                OR ({alias}.PARAM_ID = @VR AND TRY_CONVERT(float, {alias}.PARAM_VALUE) IS NOT NULL)
                OR ({alias}.PARAM_ID = @VY AND TRY_CONVERT(float, {alias}.PARAM_VALUE) IS NOT NULL)
                OR ({alias}.PARAM_ID = @IR AND TRY_CONVERT(float, {alias}.PARAM_VALUE) IS NOT NULL)
                OR ({alias}.PARAM_ID = @IY AND TRY_CONVERT(float, {alias}.PARAM_VALUE) IS NOT NULL)
                OR ({alias}.PARAM_ID = @IB AND TRY_CONVERT(float, {alias}.PARAM_VALUE) IS NOT NULL)
                OR ({alias}.PARAM_ID = @IntKvahI AND TRY_CONVERT(float, {alias}.PARAM_VALUE) IS NOT NULL)
                OR ({alias}.PARAM_ID = @IntKwhI AND TRY_CONVERT(float, {alias}.PARAM_VALUE) IS NOT NULL)
            )
            AND (
                @VB > 0 OR @VR > 0 OR @VY > 0
                OR @IR > 0 OR @IY > 0 OR @IB > 0
                OR @IntKvahI > 0 OR @IntKwhI > 0
            )
            """;
    }

    // ======================= Maximum timestamp =======================

    public async Task<DateTime?> GetMaxTimeAsync(
        CancellationToken ct)
    {
        var p = factory.Profile();

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = $"""
            SELECT MAX({Sql.Col(p.Columns.Time)})
            FROM {Sql.Table(p.IntervalTable)};
            """;

        return await conn.ExecuteScalarAsync<DateTime?>(
            new CommandDefinition(
                sql,
                commandTimeout: CmdTimeout,
                cancellationToken: ct));
    }

    public async Task<DateTime?> GetMaxTimeAsync(
        TimeRange range,
        CancellationToken ct)
    {
        var p = factory.Profile();
        var time = Sql.Col(p.Columns.Time);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = $"""
            SELECT MAX({time})
            FROM {Sql.Table(p.IntervalTable)}
            WHERE {Where(range, time)};
            """;

        return await conn.ExecuteScalarAsync<DateTime?>(
            new CommandDefinition(
                sql,
                new
                {
                    fromTs = range.From,
                    toTs = range.To
                },
                commandTimeout: CmdTimeout,
                cancellationToken: ct));
    }

    // ======================= Meter summary =======================

    public async Task<List<SummaryRow>> GetSummaryAsync(
        TimeRange range,
        DateTime recentStart,
        CancellationToken ct)
    {
        var p = factory.Profile();
        var c = p.Columns;

        var equipmentTable = Sql.Table(p.EquipmentTable);
        var intervalTable = Sql.Table(p.IntervalTable);

        var equipmentId = Sql.Col(c.EquipmentId);
        var intervalEquipmentId = Sql.Col(c.IntervalEquipmentId);
        var time = Sql.Col(c.Time);

        var id = MeterIdExpr("e", c.MeterId);

        var sql = $"""
            WITH ReadingRows AS
            (
                SELECT
                    {id} AS MeterId,
                    i.{time} AS T,

                    MAX(
                        CASE
                            WHEN {ValidTelemetryCondition("i")}
                            THEN 1
                            ELSE 0
                        END
                    ) AS IsValid

                FROM {intervalTable} i

                INNER JOIN {equipmentTable} e
                    ON e.{equipmentId} = i.{intervalEquipmentId}

                WHERE {Where(range, "i." + time)}

                GROUP BY
                    {id},
                    i.{time}
            )

            SELECT
                MeterId,
                MIN(T) AS FirstRow,
                MAX(T) AS LastRow,

                MAX(
                    CASE WHEN IsValid = 1
                    THEN T END
                ) AS LastValid,

                CAST(SUM(IsValid) AS bigint) AS ValidCount,

                CAST(
                    SUM(
                        CASE
                            WHEN IsValid = 1 AND T > @recentStart
                            THEN 1
                            ELSE 0
                        END
                    ) AS bigint
                ) AS ValidRecent

            FROM ReadingRows
            GROUP BY MeterId;
            """;

        var parameters = new DynamicParameters(GetParameterValues(p));

        parameters.Add("fromTs", range.From);
        parameters.Add("toTs", range.To);
        parameters.Add("recentStart", recentStart);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<SummaryRow>(
            new CommandDefinition(
                sql,
                parameters,
                commandTimeout: CmdTimeout,
                cancellationToken: ct));

        return rows.AsList();
    }

    // ======================= Reading gaps =======================

    public async Task<List<GapRow>> GetGapsAsync(
        TimeRange range,
        CancellationToken ct)
    {
        var p = factory.Profile();
        var c = p.Columns;

        var equipmentTable = Sql.Table(p.EquipmentTable);
        var intervalTable = Sql.Table(p.IntervalTable);

        var id = MeterIdExpr("e", c.MeterId);
        var equipmentId = Sql.Col(c.EquipmentId);
        var intervalEquipmentId = Sql.Col(c.IntervalEquipmentId);
        var time = Sql.Col(c.Time);

        var sql = $"""
            WITH ReadingTimes AS
            (
                SELECT DISTINCT
                    {id} AS MeterId,
                    i.{time} AS T

                FROM {intervalTable} i

                INNER JOIN {equipmentTable} e
                    ON e.{equipmentId} = i.{intervalEquipmentId}

                WHERE {Where(range, "i." + time)}
            ),

            ReadingGaps AS
            (
                SELECT
                    MeterId,

                    CAST(
                        ROUND(
                            DATEDIFF(
                                SECOND,
                                LAG(T) OVER (
                                    PARTITION BY MeterId
                                    ORDER BY T
                                ),
                                T
                            ) / 60.0,
                            0
                        ) AS int
                    ) AS GapMin

                FROM ReadingTimes
            )

            SELECT
                MeterId,
                GapMin AS GapMinutes,
                COUNT_BIG(*) AS N

            FROM ReadingGaps

            WHERE GapMin IN (5, 10, 15, 30, 60)

            GROUP BY
                MeterId,
                GapMin;
            """;

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<GapRow>(
            new CommandDefinition(
                sql,
                new
                {
                    fromTs = range.From,
                    toTs = range.To
                },
                commandTimeout: CmdTimeout,
                cancellationToken: ct));

        return rows.AsList();
    }

    // ======================= Last valid reading =======================

    public async Task<List<LastValidRow>> GetLastValidAsync(
        TimeRange range,
        CancellationToken ct)
    {
        var p = factory.Profile();
        var c = p.Columns;

        var equipmentTable = Sql.Table(p.EquipmentTable);
        var intervalTable = Sql.Table(p.IntervalTable);

        var equipmentId = Sql.Col(c.EquipmentId);
        var intervalEquipmentId = Sql.Col(c.IntervalEquipmentId);

        var parameterId = Sql.Col(c.IntervalParameterId);
        var parameterValue = Sql.Col(c.ParameterValue);
        var time = Sql.Col(c.Time);

        var id = MeterIdExpr("e", c.MeterId);

        var sql = $"""
            WITH PivotedReadings AS
            (
                SELECT
                    {id} AS MeterId,
                    i.{time} AS T,

                    MAX(
                        CASE WHEN i.{parameterId} = @VB
                        THEN TRY_CONVERT(float, i.{parameterValue})
                        END
                    ) AS VB,

                    MAX(
                        CASE WHEN i.{parameterId} = @VR
                        THEN TRY_CONVERT(float, i.{parameterValue})
                        END
                    ) AS VR,

                    MAX(
                        CASE WHEN i.{parameterId} = @VY
                        THEN TRY_CONVERT(float, i.{parameterValue})
                        END
                    ) AS VY,

                    MAX(
                        CASE WHEN {ValidTelemetryCondition("i")}
                        THEN 1 ELSE 0
                        END
                    ) AS HasValid

                FROM {intervalTable} i

                INNER JOIN {equipmentTable} e
                    ON e.{equipmentId} = i.{intervalEquipmentId}

                WHERE {Where(range, "i." + time)}

                GROUP BY
                    {id},
                    i.{time}
            ),

            ValidReadings AS
            (
                SELECT
                    r.MeterId,
                    r.T,

                    voltage.VMin,

                    CAST(NULL AS float) AS Temp,

                    ROW_NUMBER() OVER (
                        PARTITION BY r.MeterId
                        ORDER BY r.T DESC
                    ) AS rn

                FROM PivotedReadings r

                CROSS APPLY
                (
                    SELECT MIN(v) AS VMin
                    FROM
                    (
                        VALUES
                            (r.VB),
                            (r.VR),
                            (r.VY)
                    ) AS valuesTable(v)
                ) voltage

                WHERE r.HasValid = 1
            )

            SELECT
                MeterId,
                VMin,
                Temp

            FROM ValidReadings
            WHERE rn = 1;
            """;

        var parameters = new DynamicParameters(GetParameterValues(p));

        parameters.Add("fromTs", range.From);
        parameters.Add("toTs", range.To);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<LastValidRow>(
            new CommandDefinition(
                sql,
                parameters,
                commandTimeout: CmdTimeout,
                cancellationToken: ct));

        return rows.AsList();
    }

    // ======================= Last seen timestamp =======================

    public async Task<List<LastSeenRow>> GetLastSeenAsync(
        DateTime? from,
        DateTime? to,
        CancellationToken ct)
    {
        var p = factory.Profile();
        var c = p.Columns;

        var equipmentTable = Sql.Table(p.EquipmentTable);
        var intervalTable = Sql.Table(p.IntervalTable);

        var id = MeterIdExpr("e", c.MeterId);
        var equipmentId = Sql.Col(c.EquipmentId);
        var intervalEquipmentId = Sql.Col(c.IntervalEquipmentId);
        var time = Sql.Col(c.Time);

        var conditions = new List<string>();
        if (from.HasValue) conditions.Add($"i.{time} >= @fromTs");
        if (to.HasValue) conditions.Add($"i.{time} <= @toTs");

        var whereSql = conditions.Count == 0
            ? ""
            : "WHERE " + string.Join(" AND ", conditions);

        var sql = $"""
            SELECT
                {id} AS MeterId,
                MAX(i.{time}) AS LastSeen

            FROM {intervalTable} i

            INNER JOIN {equipmentTable} e
                ON e.{equipmentId} = i.{intervalEquipmentId}

            {whereSql}

            GROUP BY {id};
            """;

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<LastSeenRow>(
            new CommandDefinition(
                sql,
                new { fromTs = from, toTs = to },
                commandTimeout: CmdTimeout,
                cancellationToken: ct));

        return rows.AsList();
    }

    // ======================= Meter IDs =======================

    public async Task<List<string>> GetMappedIdsAsync(
        CancellationToken ct)
    {
        var p = factory.Profile();
        var c = p.Columns;

        var sql = $"""
            SELECT DISTINCT
                CAST({Sql.Col(c.MeterId)} AS nvarchar(100))
            FROM {Sql.Table(p.EquipmentTable)}
            WHERE {Sql.Col(c.MeterId)} IS NOT NULL;
            """;

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(
                sql,
                commandTimeout: CmdTimeout,
                cancellationToken: ct));

        return rows
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    // ======================= GPS Mapping =======================

    public async Task<List<RawMapRow>> GetMappingAsync(
        CancellationToken ct)
    {
        var p = factory.Profile();
        var c = p.Columns;

        var equipmentTable = Sql.Table(p.EquipmentTable);
        var linkTable = Sql.Table(p.DeviceLinkTable);
        var locationTable = Sql.Table(p.DeviceLocationTable);

        var sql = $"""
            SELECT
                CAST(e.{Sql.Col(c.MeterId)} AS nvarchar(100)) AS MeterId,

                TRY_CONVERT(float, dl.{Sql.Col(c.Latitude)}) AS Lat,
                TRY_CONVERT(float, dl.{Sql.Col(c.Longitude)}) AS Lon,

                CAST(NULL AS nvarchar(200)) AS LongLat,

                CAST(dl.{Sql.Col(c.LocationId)} AS nvarchar(100)) AS BuildingId,

                CAST(dl.[Premise] AS nvarchar(200)) AS BuildingName,

                CAST(dl.[Utility_Office_ID] AS nvarchar(100)) AS Area,

                CAST(NULL AS nvarchar(100)) AS FlatNo,

                CAST(e.{Sql.Col(c.ManufacturerId)} AS nvarchar(100)) AS Manufacturer,

                CAST(NULL AS nvarchar(100)) AS Nsp

            FROM {equipmentTable} e

            OUTER APPLY
            (
                SELECT TOP (1)
                    l.{Sql.Col(c.LinkLocationId)} AS DeviceLocationId

                FROM {linkTable} l

                WHERE l.{Sql.Col(c.LinkEquipmentId)}
                    = e.{Sql.Col(c.EquipmentId)}

                ORDER BY
                    l.{Sql.Col(c.ValidFrom)} DESC,
                    l.[ID] DESC

            ) latestLink

            LEFT JOIN {locationTable} dl
                ON dl.{Sql.Col(c.LocationId)}
                    = latestLink.DeviceLocationId

            WHERE e.{Sql.Col(c.MeterId)} IS NOT NULL;
            """;

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<RawMapRow>(
            new CommandDefinition(
                sql,
                commandTimeout: CmdTimeout,
                cancellationToken: ct));

        return rows.AsList();
    }
}

// ======================= Service: SQL + Core logic ko jodta hai =======================

public sealed record MeterStatusRow(
    string MeterId,
    int IntervalMinutes,
    double CommPct,
    double? LifeCommPct,
    double HoursSilent,
    string Status,
    string Reason,
    DateTime? LastCommTime,
    bool OutsideWindow,
    double? VMin,
    double? Temp,
    string BuildingId,
    string BuildingName,
    string Area,
    double Lat,
    double Lon,
    string FlatNo,
    string Manufacturer,
    string Nsp);

public sealed record MeterStatusResult(
    DateTime RefTime,
    DateTime StatusRef,
    int Days,
    double StaleHours,
    int Total,
    int Communicating,
    int Buildings,
    int Areas,
    List<MeterStatusRow> Meters,
    DateTime? DataMax = null,
    List<string>? Warnings = null);

public sealed class MeterStatusService(MeterRepository repo, SqlConnectionFactory factory)
{
    private static double Median(List<double> v)
    {
        if (v.Count == 0) return 30;
        v.Sort();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
    }

    public async Task<MeterStatusResult> GetAsync(
        DateTime? from,
        DateTime? to,
        int? days,
        bool useToday,
        double staleHours,
        CancellationToken ct)
    {
        var dataMax = await repo.GetMaxTimeAsync(ct);
        DateTime refTime;
        TimeRange range;
        int effectiveDays = days ?? 7;

        if (from.HasValue || to.HasValue)
        {
            DateTime end = to.HasValue
                ? (to.Value.TimeOfDay == TimeSpan.Zero
                    ? to.Value.Date.AddDays(1).AddTicks(-1)
                    : to.Value)
                : (dataMax ?? DateTime.Now);

            DateTime start = from ?? end.AddDays(-effectiveDays);
            if (start > end)
                (start, end) = (end, start);

            range = new TimeRange(start, true, end);
            refTime = end;
            effectiveDays = (int)Math.Max(1, Math.Round((end - start).TotalDays));
        }
        else
        {
            if (dataMax.HasValue)
            {
                refTime = dataMax.Value;
                var start = refTime.AddDays(-effectiveDays);
                range = new TimeRange(start, true, refTime);
            }
            else
            {
                refTime = DateTime.Now;
                var start = refTime.AddDays(-effectiveDays);
                range = new TimeRange(start, true, refTime);
            }
        }

        var now = DateTime.Now;
        var nowHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        var statusRef = useToday && nowHour > refTime ? nowHour : refTime;

        // If no interval data exists in database, gracefully return mapped meters with informative status
        if (dataMax is null)
        {
            var rawMapping = await repo.GetMappingAsync(ct);
            var mappedFallback = MeterMapping.Prepare(rawMapping, factory.Profile().LatLonOrder);
            var emptyRows = mappedFallback.Select(m => new MeterStatusRow(
                m.MeterId,
                IntervalInference.FallbackMinutes,
                0,
                null,
                CommCalculator.NeverHours,
                "Not communicating",
                "Never communicated",
                null,
                false,
                null,
                null,
                m.BuildingId,
                m.BuildingName,
                m.Area,
                m.Lat,
                m.Lon,
                m.FlatNo,
                m.Manufacturer,
                m.Nsp)).ToList();

            emptyRows.Sort((a, b) => string.CompareOrdinal(a.MeterId, b.MeterId));
            return new MeterStatusResult(
                refTime,
                statusRef,
                effectiveDays,
                staleHours,
                emptyRows.Count,
                0,
                emptyRows.Select(m => m.BuildingId).Distinct().Count(),
                emptyRows.Select(m => m.Area).Distinct().Count(),
                emptyRows,
                null,
                new List<string> { "No interval readings found in database. All meters shown as Not communicating." });
        }

        // Run queries in parallel
        var tSummary = repo.GetSummaryAsync(range, refTime.AddHours(-CommCalculator.WindowHours), ct);
        var tGaps = repo.GetGapsAsync(range, ct);
        var tLast = repo.GetLastValidAsync(range, ct);
        var tSeen = repo.GetLastSeenAsync(null, null, ct);
        var tMap = repo.GetMappingAsync(ct);
        await Task.WhenAll(tSummary, tGaps, tLast, tSeen, tMap);

        // Match using IdKey (ignores spaces, .0, leading zeros)
        var summaryByKey = new Dictionary<string, SummaryRow>();
        foreach (var s in tSummary.Result) summaryByKey.TryAdd(IdKey.Make(s.MeterId), s);

        var lastByKey = new Dictionary<string, LastValidRow>();
        foreach (var l in tLast.Result) lastByKey.TryAdd(IdKey.Make(l.MeterId), l);

        var seenByKey = new Dictionary<string, DateTime>();
        foreach (var s in tSeen.Result)
            if (s.LastSeen.HasValue) seenByKey.TryAdd(IdKey.Make(s.MeterId), s.LastSeen.Value);

        var gaps = tGaps.Result.Select(g => new GapCount(g.MeterId, g.GapMinutes, g.N));
        var intervalByKey = new Dictionary<string, int>();
        foreach (var kv in IntervalInference.Infer(gaps, tSummary.Result.Select(s => s.MeterId)))
            intervalByKey.TryAdd(IdKey.Make(kv.Key), kv.Value);

        // Prepare mapping: filters invalid coordinates and derives/assigns buildings
        var mapped = MeterMapping.Prepare(tMap.Result, factory.Profile().LatLonOrder);
        var universe = new List<(MapMeter M, string Key)>();
        var seenKeys = new HashSet<string>();
        foreach (var m in mapped)
        {
            var key = IdKey.Make(m.MeterId);
            if (seenKeys.Add(key)) universe.Add((m, key));
        }

        int fallbackInterval = (int)Math.Round(Median(universe
            .Where(u => intervalByKey.ContainsKey(u.Key))
            .Select(u => (double)intervalByKey[u.Key]).ToList()));

        var results = new List<CommResult>();
        var inputs = new List<ReasonInput>();
        var interval = new List<int>();
        var outside = new List<bool>();
        var lastComm = new List<DateTime?>();
        var vMin = new List<double?>();
        var temps = new List<double?>();

        foreach (var (m, key) in universe)
        {
            var id = m.MeterId;
            int iv = intervalByKey.TryGetValue(key, out var x) ? x : fallbackInterval;
            lastByKey.TryGetValue(key, out var lv);

            MeterStats stats;
            bool outsideWindow = false;
            DateTime? lastRow, lastValid;

            if (summaryByKey.TryGetValue(key, out var s))
            {
                stats = new MeterStats(id, s.FirstRow, s.LastRow, s.LastValid, s.ValidCount, s.ValidRecent);
                lastRow = s.LastRow; lastValid = s.LastValid;
            }
            else if (seenByKey.TryGetValue(key, out var seen))
            {
                stats = new MeterStats(id, null, null, seen, 0, 0);
                lastRow = seen; lastValid = seen; outsideWindow = true;
            }
            else
            {
                stats = new MeterStats(id, null, null, null, 0, 0);
                lastRow = null; lastValid = null;
            }

            var r = CommCalculator.Calculate(stats, iv, refTime, statusRef, staleHours);
            results.Add(r);
            interval.Add(iv);
            outside.Add(outsideWindow);
            lastComm.Add(lastValid);
            vMin.Add(lv?.VMin);
            temps.Add(lv?.Temp);
            inputs.Add(new ReasonInput(id, r.Ok, lastValid, lastRow, outsideWindow,
                                       lv?.VMin, lv?.Temp, r.LifeCommPct, r.StaleLimitHours));
        }

        var reasons = ReasonClassifier.Classify(inputs, statusRef);

        var rows = new List<MeterStatusRow>(universe.Count);
        for (int i = 0; i < universe.Count; i++)
        {
            var r = results[i];
            var m = universe[i].M;
            rows.Add(new MeterStatusRow(
                m.MeterId, interval[i], r.CommPct,
                double.IsNaN(r.LifeCommPct) ? null : r.LifeCommPct,
                r.HoursSilent, r.Ok ? "Communicating" : "Not communicating",
                reasons[i], lastComm[i], outside[i], vMin[i], temps[i],
                m.BuildingId, m.BuildingName, m.Area, m.Lat, m.Lon,
                m.FlatNo, m.Manufacturer, m.Nsp));
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.MeterId, b.MeterId));
        int ok = rows.Count(m => m.Status == "Communicating");

        return new MeterStatusResult(
            refTime, statusRef, effectiveDays, staleHours,
            rows.Count, ok,
            rows.Select(m => m.BuildingId).Distinct().Count(),
            rows.Select(m => m.Area).Distinct().Count(),
            rows,
            dataMax);
    }
}
