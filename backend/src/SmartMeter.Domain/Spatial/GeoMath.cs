namespace SmartMeter.Domain.Spatial;

public static class GeoMath
{
    public const double MetersPerDegreeLat = 110_540.0;
    public const double MetersPerDegreeLonAtEquator = 111_320.0;
    private const double EarthRadiusM = 6_371_008.8;

    /// <summary>Equirectangular distance - accurate to &lt;0.5% below ~50 km, which is all clustering needs.</summary>
    public static double DistanceMeters(double lon1, double lat1, double lon2, double lat2)
    {
        var meanLat = (lat1 + lat2) * 0.5 * Math.PI / 180.0;
        var dx = (lon2 - lon1) * MetersPerDegreeLonAtEquator * Math.Cos(meanLat);
        var dy = (lat2 - lat1) * MetersPerDegreeLat;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Area of a lon/lat ring in km^2 (spherical, shoelace on an authalic projection).</summary>
    public static double RingAreaKm2(IReadOnlyList<(double Lon, double Lat)> ring)
    {
        if (ring.Count < 4) return 0;
        double sum = 0;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            var (lon1, lat1) = ring[i];
            var (lon2, lat2) = ring[i + 1];
            sum += ToRad(lon2 - lon1) * (2 + Math.Sin(ToRad(lat1)) + Math.Sin(ToRad(lat2)));
        }
        return Math.Abs(sum * EarthRadiusM * EarthRadiusM / 2.0) / 1_000_000.0;
    }

    /// <summary>Convex hull (Andrew's monotone chain), returned closed (first == last).</summary>
    public static IReadOnlyList<(double Lon, double Lat)> ConvexHull(IEnumerable<(double Lon, double Lat)> points)
    {
        var pts = points.Distinct().OrderBy(p => p.Lon).ThenBy(p => p.Lat).ToList();
        if (pts.Count <= 2)
        {
            if (pts.Count > 0) pts.Add(pts[0]);
            return pts;
        }
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new (double, double)[pts.Count * 2];
        var k = 0;
        foreach (var p in pts)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p) <= 0) k--;
            hull[k++] = p;
        }
        for (int i = pts.Count - 2, t = k + 1; i >= 0; i--)
        {
            var p = pts[i];
            while (k >= t && Cross(hull[k - 2], hull[k - 1], p) <= 0) k--;
            hull[k++] = p;
        }
        return hull.Take(k).ToList();
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
