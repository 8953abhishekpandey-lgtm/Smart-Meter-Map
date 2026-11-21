using System.Globalization;

namespace SmartMeter.Domain.Spatial;

/// <summary>WGS84 bounding box (lon/lat degrees).</summary>
public readonly record struct BoundingBox(double MinX, double MinY, double MaxX, double MaxY)
{
    public const double MaxMercatorLat = 85.05112878;

    public bool IsValid =>
        double.IsFinite(MinX) && double.IsFinite(MinY) && double.IsFinite(MaxX) && double.IsFinite(MaxY)
        && MinX < MaxX && MinY < MaxY;

    /// <summary>Clamps to valid WGS84 / Web Mercator ranges (map views can extend past the antimeridian).</summary>
    public BoundingBox Clamp() => new(
        Math.Clamp(MinX, -180, 180), Math.Clamp(MinY, -MaxMercatorLat, MaxMercatorLat),
        Math.Clamp(MaxX, -180, 180), Math.Clamp(MaxY, -MaxMercatorLat, MaxMercatorLat));

    public bool Contains(double lon, double lat) => lon >= MinX && lon <= MaxX && lat >= MinY && lat <= MaxY;

    /// <summary>Snaps outward to a grid so nearby viewports share one cache entry.</summary>
    public BoundingBox SnapOutward(double step) => new(
        Math.Floor(MinX / step) * step, Math.Floor(MinY / step) * step,
        Math.Ceiling(MaxX / step) * step, Math.Ceiling(MaxY / step) * step);

    /// <summary>Parses "minX,minY,maxX,maxY".</summary>
    public static bool TryParse(string? value, out BoundingBox box)
    {
        box = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return false;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
        box = new BoundingBox(v[0], v[1], v[2], v[3]);
        return box.IsValid;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{MinX:R},{MinY:R},{MaxX:R},{MaxY:R}");
}
