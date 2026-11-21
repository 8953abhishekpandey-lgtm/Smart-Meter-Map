namespace SmartMeter.Domain.Spatial;

/// <summary>
/// DBSCAN over lon/lat points with a uniform grid index (O(n) expected for clustered data).
/// Grid cells are sized so every neighbour within <c>epsMeters</c> lies in the 3x3 block around a
/// point, using the narrowest longitude cell of the data set (highest |latitude|).
/// </summary>
public static class GeoDbscan
{
    public const int Noise = -1;

    /// <returns>Cluster id per input point (0..n-1) or <see cref="Noise"/>.</returns>
    public static int[] Cluster(IReadOnlyList<(double Lon, double Lat)> points, double epsMeters, int minPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(epsMeters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minPoints);
        var n = points.Count;
        var labels = new int[n];
        Array.Fill(labels, int.MinValue); // unvisited
        if (n == 0) return labels;

        var maxAbsLat = 0.0;
        foreach (var p in points) maxAbsLat = Math.Max(maxAbsLat, Math.Abs(p.Lat));
        var cellLat = epsMeters / GeoMath.MetersPerDegreeLat;
        var cellLon = epsMeters / (GeoMath.MetersPerDegreeLonAtEquator * Math.Max(0.01, Math.Cos(Math.Min(89.0, maxAbsLat) * Math.PI / 180.0)));

        var grid = new Dictionary<(long, long), List<int>>();
        var keys = new (long X, long Y)[n];
        for (var i = 0; i < n; i++)
        {
            var key = ((long)Math.Floor(points[i].Lon / cellLon), (long)Math.Floor(points[i].Lat / cellLat));
            keys[i] = key;
            if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = [];
            bucket.Add(i);
        }

        var neighbours = new List<int>();
        var seeds = new Queue<int>();
        var clusterId = 0;
        for (var i = 0; i < n; i++)
        {
            if (labels[i] != int.MinValue) continue;
            RegionQuery(i, neighbours);
            if (neighbours.Count < minPoints)
            {
                labels[i] = Noise;
                continue;
            }
            labels[i] = clusterId;
            foreach (var j in neighbours) seeds.Enqueue(j);
            while (seeds.Count > 0)
            {
                var j = seeds.Dequeue();
                if (labels[j] == Noise) labels[j] = clusterId;      // border point
                if (labels[j] != int.MinValue) continue;
                labels[j] = clusterId;
                RegionQuery(j, neighbours);
                if (neighbours.Count >= minPoints)
                    foreach (var q in neighbours)
                        if (labels[q] == int.MinValue || labels[q] == Noise) seeds.Enqueue(q);
            }
            clusterId++;
        }
        return labels;

        void RegionQuery(int index, List<int> result)
        {
            result.Clear();
            var (cx, cy) = keys[index];
            var (lon, lat) = points[index];
            for (var dx = -1L; dx <= 1; dx++)
            for (var dy = -1L; dy <= 1; dy++)
            {
                if (!grid.TryGetValue((cx + dx, cy + dy), out var bucket)) continue;
                foreach (var j in bucket)
                    if (GeoMath.DistanceMeters(lon, lat, points[j].Lon, points[j].Lat) <= epsMeters) result.Add(j);
            }
        }
    }
}
