using System.Runtime.CompilerServices;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;

namespace SmartMeter.Infrastructure.Persistence;

internal sealed class BuildingReadRepository(ISqlConnectionFactory db) : IBuildingReadRepository
{
    private const int MaxDays = CommWindow.MaxDays;

    public async Task<BuildingRow?> GetBuildingAsync(string buildingKey, CancellationToken ct)
    {
        const string sql = """
            SELECT BuildingKey, BuildingId, IsVirtual, Name, Address, ZoneId, MeterCount, NspCount, Lon, Lat
            FROM sm.Building WHERE BuildingKey = @key;
            """;
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BuildingRow>(new CommandDefinition(sql, new { key = buildingKey }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<BuildingSliceRow>> GetSlicesAsync(string buildingKey, CancellationToken ct)
    {
        var sql = $"""
            SELECT s.NspId, s.ManufacturerId, s.HealthClass, s.ActiveLast7, s.MeterCount, s.EligTotal, s.CommTotal,
                   {SqlText.DayColumns("s", MaxDays, sum: false)}
            FROM sm.BuildingSummary s WHERE s.BuildingKey = @key;
            """;
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@key", System.Data.SqlDbType.VarChar, 72) { Value = buildingKey });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<BuildingSliceRow>();
        while (await reader.ReadAsync(ct))
        {
            var e = new long[MaxDays];
            var c = new long[MaxDays];
            for (var k = 0; k < MaxDays; k++)
            {
                e[k] = Convert.ToInt64(reader.GetValue(7 + 2 * k));
                c[k] = Convert.ToInt64(reader.GetValue(8 + 2 * k));
            }
            result.Add(new BuildingSliceRow(
                reader.GetInt16(0), reader.GetInt16(1), reader.GetByte(2), reader.GetBoolean(3),
                Convert.ToInt64(reader.GetValue(4)), Convert.ToInt64(reader.GetValue(5)), Convert.ToInt64(reader.GetValue(6)), e, c));
        }
        return result;
    }

    public async Task<IReadOnlyList<BuildingMeterRow>> GetMetersPageAsync(string buildingKey, DaySelection day, string? search,
        MeterStatusFilter status, MeterSort sort, bool descending, int offset, int take, CancellationToken ct)
    {
        var (where, p) = MetersWhere(buildingKey, day, search, status);
        var dir = descending ? "DESC" : "ASC";
        var column = sort switch
        {
            MeterSort.CommDays => "CommDays",
            MeterSort.LastComm => "LastCommDate",
            MeterSort.Nsp => "NspId",
            MeterSort.Manufacturer => "ManufacturerId",
            MeterSort.Installation => "InstallationDate",
            _ => "MeterId",
        };
        string OrderBy(string alias) => column == "MeterId"
            ? $"{alias}.MeterId {dir}"
            : $"{alias}.{column} {dir}, {alias}.MeterId";
        // Page on MeterHealth alone (index seek on BuildingKey), then fetch addresses for the page rows only.
        var sql = $"""
            WITH pg AS (
                SELECT h.MeterSk, h.MeterId, h.NspId, h.ManufacturerId, h.InstallationDate, h.HealthClass, h.EligDays, h.CommDays,
                       h.LastCommDate, h.EligMask, h.CommMask, COUNT_BIG(*) OVER () AS TotalCount
                FROM sm.MeterHealth h
                WHERE {where}
                ORDER BY {OrderBy("h")}
                OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY
            )
            SELECT pg.MeterId, m.Address, pg.NspId, pg.ManufacturerId, pg.InstallationDate, pg.HealthClass, pg.EligDays, pg.CommDays,
                   pg.LastCommDate, pg.EligMask, pg.CommMask, pg.TotalCount
            FROM pg
            JOIN sm.Meter m ON m.MeterSk = pg.MeterSk
            ORDER BY {OrderBy("pg")}
            OPTION (LOOP JOIN);
            """;
        p.Add("offset", offset);
        p.Add("take", take);
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<BuildingMeterRow>(new CommandDefinition(sql, p, cancellationToken: ct))).AsList();
    }

    public async IAsyncEnumerable<BuildingMeterRow> StreamMetersAsync(string buildingKey, DaySelection day, string? search,
        MeterStatusFilter status, [EnumeratorCancellation] CancellationToken ct)
    {
        var (where, p) = MetersWhere(buildingKey, day, search, status);
        var sql = $"""
            SELECT h.MeterId, m.Address, h.NspId, h.ManufacturerId, h.InstallationDate, h.HealthClass, h.EligDays, h.CommDays,
                   h.LastCommDate, h.EligMask, h.CommMask, CAST(0 AS bigint) AS TotalCount
            FROM sm.MeterHealth h
            JOIN sm.Meter m ON m.MeterSk = h.MeterSk
            WHERE {where}
            ORDER BY h.MeterId
            OPTION (LOOP JOIN);
            """;
        await using var conn = await db.OpenAsync(ct);
        await foreach (var row in conn.QueryUnbufferedAsync<BuildingMeterRow>(sql, p).WithCancellation(ct))
            yield return row;
    }

    public async Task<IReadOnlyList<MeterCommRow>> GetMeterCommunicationAsync(string buildingKey, CancellationToken ct)
    {
        const string sql = "SELECT MeterId, CommDays FROM sm.MeterHealth WHERE BuildingKey = @key;";
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<MeterCommRow>(new CommandDefinition(sql, new { key = buildingKey }, cancellationToken: ct))).AsList();
    }

    private static (string Where, DynamicParameters P) MetersWhere(string buildingKey, DaySelection day, string? search, MeterStatusFilter status)
    {
        var p = new DynamicParameters();
        p.Add("key", buildingKey);
        p.Add("bit", day.Bit);
        var where = new StringBuilder("h.BuildingKey = @key");
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = SqlText.EscapeLike(search.Trim());
            // meter id prefix, or address contains (address lives on sm.Meter: correlated seek per candidate row)
            where.Append(" AND (h.MeterId LIKE @prefix OR EXISTS (SELECT 1 FROM sm.Meter ma WHERE ma.MeterSk = h.MeterSk AND ma.Address LIKE @contains))");
            p.Add("prefix", term + "%");
            p.Add("contains", "%" + term + "%");
        }
        where.Append(status switch
        {
            MeterStatusFilter.Communicating => " AND (RIGHT_SHIFT(h.CommMask, @bit) & 1) = 1",
            MeterStatusFilter.Failing => " AND (RIGHT_SHIFT(h.EligMask, @bit) & 1) = 1 AND (RIGHT_SHIFT(h.CommMask, @bit) & 1) = 0",
            MeterStatusFilter.AllDay => " AND h.HealthClass = 2",
            MeterStatusFilter.Dead => " AND h.HealthClass = 0",
            MeterStatusFilter.Intermittent => " AND h.HealthClass = 1",
            _ => "",
        });
        return (where.ToString(), p);
    }
}
