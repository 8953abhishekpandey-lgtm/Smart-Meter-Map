using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.UnitTests;

public class HealthBandTests
{
    [Theory]
    [InlineData(100, 0, HealthBand.Green)]
    [InlineData(100, 20, HealthBand.Green)]
    [InlineData(1000, 204, HealthBand.Green)]   // 20.4% rounds to 20 -> green
    [InlineData(100, 21, HealthBand.Amber)]
    [InlineData(100, 79, HealthBand.Amber)]
    [InlineData(1000, 796, HealthBand.Red)]     // 79.6% rounds to 80 -> red
    [InlineData(100, 80, HealthBand.Red)]
    [InlineData(100, 100, HealthBand.Red)]
    [InlineData(0, 0, HealthBand.None)]
    public void Bands_follow_published_thresholds(long expected, long failing, HealthBand band) =>
        Assert.Equal(band, HealthBands.FromCounts(expected, failing));
}

public class CommWindowTests
{
    private static readonly CommWindow Window = new(new DateOnly(2026, 10, 22), new DateOnly(2026, 11, 20));

    [Fact]
    public void Window_has_30_days_and_maps_columns_and_bits()
    {
        Assert.Equal(30, Window.Days);
        Assert.Equal(1, Window.ColumnIndex(Window.Start));
        Assert.Equal(30, Window.ColumnIndex(Window.End));
        Assert.Equal(0, Window.BitIndex(Window.End));
        Assert.Equal(29, Window.BitIndex(Window.Start));
        Assert.Equal(new DateOnly(2026, 11, 1), Window.DateOfColumn(11));
    }

    [Fact]
    public void Dates_outside_the_window_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Window.ColumnIndex(new DateOnly(2026, 11, 21)));

    [Fact]
    public void Timeline_decodes_masks_chronologically()
    {
        // installed 3 days before the end: eligible bits 0..2; communicated on the last day and 2 days before
        var elig = 0b111;
        var comm = 0b101;
        var timeline = Window.DecodeTimeline(comm, elig);
        Assert.Equal(30, timeline.Length);
        Assert.Equal(new string('-', 27) + "101", timeline);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(32)]
    public void Window_length_is_bounded(int days) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommWindow(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1).AddDays(days - 1)));
}

public class MapResolutionTests
{
    private static readonly GridLevel[] Levels =
    [
        new(1, 1.0, 0, 6), new(2, 0.25, 7, 8), new(3, 0.05, 9, 10), new(4, 0.0125, 11, 12), new(5, 0.003, 13, 14),
    ];

    [Theory]
    [InlineData(3, 1)]
    [InlineData(8, 2)]
    [InlineData(10, 3)]
    [InlineData(12, 4)]
    [InlineData(14, 5)]
    public void Zoom_selects_pyramid_level(int zoom, int level) => Assert.Equal(level, MapResolution.ForZoom(zoom, Levels)!.Level);

    [Fact]
    public void Street_zoom_uses_buildings() => Assert.Null(MapResolution.ForZoom(15, Levels));

    [Fact]
    public void Bbox_parses_and_snaps_outward()
    {
        Assert.True(BoundingBox.TryParse("77.1,28.4,77.3,28.6", out var box));
        var snapped = box.SnapOutward(0.25);
        Assert.Equal(new BoundingBox(77.0, 28.25, 77.5, 28.75), snapped);
        Assert.False(BoundingBox.TryParse("1,2,3", out _));
        Assert.False(BoundingBox.TryParse("5,5,1,1", out _));
    }
}

public class GeoTests
{
    [Fact]
    public void Dbscan_finds_two_pockets_and_noise()
    {
        var pts = new List<(double Lon, double Lat)>();
        for (var i = 0; i < 10; i++) pts.Add((77.2000 + i * 0.0005, 28.5000));          // ~49 m spacing
        for (var i = 0; i < 10; i++) pts.Add((72.8500 + i * 0.0005, 19.1000));
        pts.Add((80.0, 13.0));                                                           // isolated
        var labels = GeoDbscan.Cluster(pts, epsMeters: 100, minPoints: 2);
        Assert.Single(labels.Take(10).Distinct());
        Assert.Single(labels.Skip(10).Take(10).Distinct());
        Assert.NotEqual(labels[0], labels[10]);
        Assert.Equal(GeoDbscan.Noise, labels[20]);
    }

    [Fact]
    public void Distance_is_accurate_at_city_scale()
    {
        var d = GeoMath.DistanceMeters(77.2, 28.6, 77.2, 28.61);
        Assert.InRange(d, 1100, 1112);
    }

    [Fact]
    public void Convex_hull_and_area_of_a_square()
    {
        var hull = GeoMath.ConvexHull([(0, 0), (0.01, 0), (0.01, 0.01), (0, 0.01), (0.005, 0.005)]);
        Assert.Equal(5, hull.Count);                                // 4 corners + closing point
        var area = GeoMath.RingAreaKm2(hull);
        Assert.InRange(area, 1.20, 1.26);                          // ~1.11 km x 1.11 km
    }
}
