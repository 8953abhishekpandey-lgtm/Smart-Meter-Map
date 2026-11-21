using Dapper;
using SmartMeter.Application.Abstractions;

namespace SmartMeter.Infrastructure.Persistence;

internal sealed class MeterReadRepository(ISqlConnectionFactory db) : IMeterReadRepository
{
    public async Task<IReadOnlyList<MeterSearchRow>> SearchAsync(string prefix, int limit, CancellationToken ct)
    {
        // Prefix LIKE on the unique MeterId index = index seek (case-insensitive collation).
        const string sql = """
            SELECT TOP (@limit) m.MeterId, m.Address, m.BuildingKey, m.Longitude AS Lon, m.Latitude AS Lat, m.ZoneId
            FROM sm.Meter m
            WHERE m.MeterId LIKE @prefix
            ORDER BY m.MeterId;
            """;
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<MeterSearchRow>(new CommandDefinition(sql,
            new { limit, prefix = SqlText.EscapeLike(prefix) + "%" }, cancellationToken: ct))).AsList();
    }

    public async Task<MeterRow?> GetMeterAsync(string meterId, CancellationToken ct)
    {
        const string sql = """
            SELECT m.MeterSk, m.MeterId, m.BuildingId, m.BuildingKey, b.Name AS BuildingName, m.Address,
                   m.Latitude AS Lat, m.Longitude AS Lon, m.ZoneId, m.NspId, m.ManufacturerId, m.InstallationDate, m.DtId,
                   h.HealthClass, h.EligDays, h.CommDays, h.LastCommDate, h.EligMask, h.CommMask
            FROM sm.Meter m
            LEFT JOIN sm.MeterHealth h ON h.MeterSk = m.MeterSk
            LEFT JOIN sm.Building b ON b.BuildingKey = m.BuildingKey
            WHERE m.MeterId = @meterId;
            """;
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MeterRow>(new CommandDefinition(sql, new { meterId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TimelineRow>> GetTimelineAsync(int meterSk, DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string sql = """
            SELECT LogDate, IsCommunicating FROM sm.CommLog
            WHERE MeterSk = @meterSk AND LogDate BETWEEN @from AND @to
            ORDER BY LogDate;
            """;
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<TimelineRow>(new CommandDefinition(sql, new { meterSk, from, to }, cancellationToken: ct))).AsList();
    }
}
