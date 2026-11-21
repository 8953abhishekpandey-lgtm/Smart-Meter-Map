using Dapper;
using Microsoft.Extensions.Logging;
using SmartMeter.Application.Abstractions;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.Infrastructure.Persistence;

/// <summary>Loads sm.AnalyticsState + dimensions; cached in-process for a few seconds (cheap, always consistent).</summary>
internal sealed class AnalyticsStateProvider(ISqlConnectionFactory db, TimeProvider clock, ILogger<AnalyticsStateProvider> logger)
    : IAnalyticsStateProvider
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AnalyticsSnapshot? _snapshot;
    private DateTimeOffset _loadedAt;

    public async ValueTask<AnalyticsSnapshot> GetAsync(CancellationToken ct)
    {
        var current = _snapshot;
        if (current is not null && clock.GetUtcNow() - _loadedAt < Ttl) return current;
        await _gate.WaitAsync(ct);
        try
        {
            if (_snapshot is not null && clock.GetUtcNow() - _loadedAt < Ttl) return _snapshot;
            _snapshot = await LoadAsync(ct);
            _loadedAt = clock.GetUtcNow();
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _loadedAt = DateTimeOffset.MinValue;

    private async Task<AnalyticsSnapshot> LoadAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT WindowStart, WindowEnd, ConfigWindowDays, DataVersion, RefreshedAt, RefreshMs, Stats FROM sm.AnalyticsState;
            SELECT Level, CellDeg, MinZoom, MaxZoom FROM sm.GridLevel ORDER BY Level;
            SELECT ZoneId AS Id, Name FROM sm.Zone;
            SELECT NspId AS Id, Name FROM sm.Nsp;
            SELECT ManufacturerId AS Id, Name FROM sm.Manufacturer;
            IF OBJECT_ID(N'sm.MeterHealth', N'U') IS NOT NULL
                SELECT MIN(InstallationDate) AS MinDate, MAX(InstallationDate) AS MaxDate FROM sm.MeterHealth;
            ELSE
                SELECT CAST(NULL AS date) AS MinDate, CAST(NULL AS date) AS MaxDate;
            """;
        await using var conn = await db.OpenAsync(ct);
        using var grid = await conn.QueryMultipleAsync(new CommandDefinition(sql, cancellationToken: ct));
        var state = await grid.ReadSingleAsync<StateRow>();
        var levels = (await grid.ReadAsync<LevelRow>()).Select(l => new GridLevel(l.Level, l.CellDeg, l.MinZoom, l.MaxZoom)).ToList();
        var zones = (await grid.ReadAsync<DimRow>()).ToDictionary(d => d.Id, d => d.Name);
        var nsps = (await grid.ReadAsync<DimRow>()).ToDictionary(d => d.Id, d => d.Name);
        var mfrs = (await grid.ReadAsync<DimRow>()).ToDictionary(d => d.Id, d => d.Name);
        var install = await grid.ReadSingleAsync<InstallRow>();

        CommWindow window;
        if (state.WindowStart is { } s && state.WindowEnd is { } e)
        {
            window = new CommWindow(s, e);
        }
        else
        {
            var end = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-1);
            window = new CommWindow(end.AddDays(-(state.ConfigWindowDays - 1)), end);
            logger.LogWarning("Analytics have never been refreshed; using a default window ending {End}", end);
        }
        return new AnalyticsSnapshot(window, state.DataVersion,
            state.RefreshedAt is { } r ? DateTime.SpecifyKind(r, DateTimeKind.Utc) : null, state.RefreshMs,
            levels, zones, nsps, mfrs, install.MinDate, install.MaxDate, state.Stats);
    }

    private sealed class StateRow
    {
        public DateOnly? WindowStart { get; set; }
        public DateOnly? WindowEnd { get; set; }
        public byte ConfigWindowDays { get; set; }
        public long DataVersion { get; set; }
        public DateTime? RefreshedAt { get; set; }
        public int? RefreshMs { get; set; }
        public string? Stats { get; set; }
    }

    private sealed class LevelRow
    {
        public byte Level { get; set; }
        public double CellDeg { get; set; }
        public byte MinZoom { get; set; }
        public byte MaxZoom { get; set; }
    }

    private sealed class DimRow
    {
        public short Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class InstallRow
    {
        public DateOnly? MinDate { get; set; }
        public DateOnly? MaxDate { get; set; }
    }
}
