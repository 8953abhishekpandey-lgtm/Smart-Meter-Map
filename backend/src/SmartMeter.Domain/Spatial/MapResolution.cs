namespace SmartMeter.Domain.Spatial;

/// <summary>One level of the server-side clustering pyramid (sm.GridLevel).</summary>
public sealed record GridLevel(byte Level, double CellDeg, byte MinZoom, byte MaxZoom);

/// <summary>Chooses the aggregation source for a map zoom level.</summary>
public static class MapResolution
{
    /// <summary>At this zoom and above the map shows one bubble per building.</summary>
    public const int BuildingMinZoom = 15;

    public static GridLevel? ForZoom(int zoom, IReadOnlyList<GridLevel> levels)
    {
        if (zoom >= BuildingMinZoom || levels.Count == 0) return null;
        foreach (var level in levels)
            if (zoom >= level.MinZoom && zoom <= level.MaxZoom) return level;
        return zoom < levels[0].MinZoom ? levels[0] : levels[^1];
    }
}
