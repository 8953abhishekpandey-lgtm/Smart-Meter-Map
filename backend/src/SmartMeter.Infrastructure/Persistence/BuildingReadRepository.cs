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
        var (sql, p) = MetersSql(buildingKey, day, search, status);
        var dir = descending ? "DESC" : "ASC";
        var order = sort switch
        {
            MeterSort.CommDays => $"h.CommDays {dir}",
            MeterSort.LastComm => $"h.LastCommDate {dir}",
            MeterSort.Nsp => $"h.NspId {dir}",
            MeterSort.Manufacturer => $"h.ManufacturerId {dir}",
            MeterSort.Installation => $"h.InstallationDate {dir}",
            _ => $"h.MeterId {dir}",
        };
        var tieBreak = sort == MeterSort.MeterId ? "" : ", h.MeterId";
        sql.Append($" ORDER BY {order}{tieBreak} OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;");
        p.Add("offset", offset);
        p.Add("take", take);
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<BuildingMeterRow>(new CommandDefinition(sql.ToString(), p, cancellationToken: ct))).AsList();
    }

    public async IAsyncEnumerable<BuildingMeterRow> StreamMetersAsync(string buildingKey, DaySelection day, string? search,
        MeterStatusFilter status, [EnumeratorCancellation] CancellationToken ct)
    {
        var (sql, p) = MetersSql(buildingKey, day, search, status);
        sql.Append(" ORDER BY h.MeterId;");
        await using var conn = await db.OpenAsync(ct);
        await foreach (var row in conn.QueryUnbufferedAsync<BuildingMeterRow>(sql.ToString(), p).WithCancellation(ct))
            yield return row;
    }

    public async Task<IReadOnlyList<MeterCommRow>> GetMeterCommunicationAsync(string buildingKey, CancellationToken ct)
    {
        const string sql = "SELECT MeterId, CommDays FROM sm.MeterHealth WHERE BuildingKey = @key;";
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<MeterCommRow>(new CommandDefinition(sql, new { key = buildingKey }, cancellationToken: ct))).AsList();
    }

    private static (StringBuilder Sql, DynamicParameters P) MetersSql(string buildingKey, DaySelection day, string? search, MeterStatusFilter status)
    {
        var p = new DynamicParameters();
        p.Add("key", buildingKey);
        p.Add("bit", day.Bit);
        var sql = new StringBuilder("""
            SELECT h.MeterId, m.Address, h.NspId, h.ManufacturerId, h.InstallationDate, h.HealthClass, h.EligDays, h.CommDays,
                   h.LastCommDate, h.EligMask, h.CommMask, COUNT_BIG(*) OVER () AS TotalCount
            FROM sm.MeterHealth h
            JOIN sm.Meter m ON m.MeterSk = h.MeterSk
            WHERE h.BuildingKey = @key
            """);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = SqlText.EscapeLike(search.Trim());
            sql.Append(" AND (h.MeterId LIKE @prefix OR m.Address LIKE @contains)");
            p.Add("prefix", term + "%");
            p.Add("contains", "%" + term + "%");
        }
        sql.Append(status switch
        {
            MeterStatusFilter.Communicating => " AND (RIGHT_SHIFT(h.CommMask, @bit) & 1) = 1",
            MeterStatusFilter.Failing => " AND (RIGHT_SHIFT(h.EligMask, @bit) & 1) = 1 AND (RIGHT_SHIFT(h.CommMask, @bit) & 1) = 0",
            MeterStatusFilter.AllDay => " AND h.HealthClass = 2",
            MeterStatusFilter.Dead => " AND h.HealthClass = 0",
            MeterStatusFilter.Intermittent => " AND h.HealthClass = 1",
            _ => "",
        });
        return (sql, p);
    }
}
