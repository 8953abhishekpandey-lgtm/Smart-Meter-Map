using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Analytics;
using SmartMeter.Application.Buildings;
using SmartMeter.Application.Common;
using SmartMeter.Application.Dashboard;
using SmartMeter.Application.Map;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;
using SmartMeter.Domain.Spatial;

namespace SmartMeter.UnitTests;

internal static class Fixtures
{
    public static readonly CommWindow Window = new(new DateOnly(2026, 10, 22), new DateOnly(2026, 11, 20));

    public static AnalyticsSnapshot Snapshot() => new(Window, 7, DateTime.UtcNow, 1000,
        [new GridLevel(1, 1.0, 0, 6)],
        new Dictionary<short, string> { [1] = "Delhi South" },
        new Dictionary<short, string> { [1] = "Jio", [2] = "Airtel" },
        new Dictionary<short, string> { [1] = "Apex" },
        null, null, null);

    public static long[] Fill(long v) => Enumerable.Repeat(v, CommWindow.MaxDays).ToArray();
}

public class KpiTests
{
    private static readonly DaySelection Day = DaySelection.From(Fixtures.Window, null, MetricMode.Day);

    private static KpiRow Row() => new()
    {
        AllMeters = 1000, TotalMeters = 1000, Expected = 900, Communicating = 810, ExpectedWindow = 27000, CommunicatingWindow = 24300,
        AllDay = 600, Last7 = 950, Dead = 40, Intermittent = 360, TodayFail = 90,
    };

    [Fact]
    public void Day_metric_uses_selected_day_counts()
    {
        var k = GetKpisHandler.Build(FleetFilter.None, Day, 30, Row());
        Assert.Equal(810, k.SuccessCount);
        Assert.Equal(90, k.FailureCount);
        Assert.Equal(90, k.SuccessPct);
        Assert.Equal("summary", k.Source);
    }

    [Fact]
    public void Window_metric_reports_average_daily_communication()
    {
        var k = GetKpisHandler.Build(FleetFilter.None, Day with { Metric = MetricMode.Window }, 30, Row());
        Assert.Equal(900, k.ExpectedMeters);
        Assert.Equal(810, k.SuccessCount);
    }

    [Fact]
    public void Today_fail_filter_is_the_failing_set()
    {
        var k = GetKpisHandler.Build(new FleetFilter { Health = HealthFilter.TodayFail }, Day, 30, Row());
        Assert.Equal(90, k.TotalMeters);
        Assert.Equal(0, k.SuccessCount);
        Assert.Equal(100, k.FailurePct);
    }

    [Fact]
    public void Installation_filter_switches_to_meter_level()
    {
        Assert.True(new FleetFilter { InstalledFrom = new DateOnly(2026, 11, 1) }.UsesMeterLevel);
        Assert.Equal("meter-level", GetKpisHandler.Build(new FleetFilter { InstalledTo = new DateOnly(2026, 1, 1) }, Day, 30, Row()).Source);
    }

    [Fact]
    public void Dates_outside_window_are_bad_requests() =>
        Assert.Throws<BadRequestException>(() => DaySelection.From(Fixtures.Window, new DateOnly(2025, 1, 1), MetricMode.Day));

    [Fact]
    public void Filter_key_is_canonical()
    {
        var a = new FleetFilter { ZoneIds = [3, 1, 3], NspIds = [2] };
        var b = new FleetFilter { ZoneIds = [1, 3], NspIds = [2] };
        Assert.Equal(a.Key, b.Key);
    }
}

public class BuildingAggregatorTests
{
    [Fact]
    public void Breakdowns_and_trend_sum_slices()
    {
        var snap = Fixtures.Snapshot();
        var day = DaySelection.From(snap.Window, null, MetricMode.Day);
        var slices = new List<BuildingSliceRow>
        {
            new(1, 1, (byte)HealthClass.AllDay, true, 10, 300, 300, Fixtures.Fill(10), Fixtures.Fill(10)),
            new(2, 1, (byte)HealthClass.Dead, false, 5, 150, 0, Fixtures.Fill(5), Fixtures.Fill(0)),
        };
        var b = new BuildingRow { BuildingKey = "B:X", Name = "X", ZoneId = 1, MeterCount = 15 };
        var dto = BuildingAggregator.Build(b, slices, snap, day);
        Assert.Equal(15, dto.TotalMeters);
        Assert.Equal(10, dto.Success);
        Assert.Equal(5, dto.Failure);
        Assert.Equal(HealthBand.Amber, dto.Band);              // 33% failing
        Assert.Equal(30, dto.Trend.Count);
        var airtel = dto.ByNsp.Single(n => n.Name == "Airtel");
        Assert.Equal(100, airtel.FailurePct);
        Assert.Equal(5, dto.Health.Dead);
        Assert.Equal("Delhi South", dto.Zone);
    }
}

public class ConsumptionTests
{
    [Fact]
    public void Averages_use_window_days_and_communicating_meters()
    {
        var w = Fixtures.Window;
        var readings = w.Dates().Select(d => new DailyReading(d, 10.0)).ToList();
        var result = new ConsumptionResult(
        [
            new MeterConsumption("A", readings),
            new MeterConsumption("B", w.Dates().Select(d => new DailyReading(d, d.Day % 2 == 0 ? 20.0 : null)).ToList()),
        ], "mock", false, null);
        var s = ConsumptionCalculator.Summarize(result, w, metersInBuilding: 3, communicatingMeters: 2);
        var bTotal = w.Dates().Count(d => d.Day % 2 == 0) * 20.0;
        Assert.Equal(300 + bTotal, s.TotalKwh);
        Assert.Equal(Math.Round((300 + bTotal) / 30, 2), s.BuildingDailyAvgKwh);
        Assert.Equal(Math.Round((300 + bTotal) / 2 / 30, 3), s.PerMeterDailyAvgKwh);
        Assert.Equal(2, s.MetersWithData);
        Assert.Equal(30, s.Daily.Count);
    }
}

public class FailureClusterTests
{
    [Fact]
    public void Clusters_below_min_meters_are_dropped_and_ranked_by_failing()
    {
        var rows = new List<HotBuildingNspRow>();
        for (var i = 0; i < 6; i++)   // pocket A: 6 buildings x 100 meters, 90% failing
            rows.Add(new HotBuildingNspRow { BuildingKey = $"A{i}", Lon = 77.2 + i * 0.001, Lat = 28.5, NspId = 1, Meters = 100, Expected = 100, Communicating = 10 });
        for (var i = 0; i < 2; i++)   // pocket B: too small
            rows.Add(new HotBuildingNspRow { BuildingKey = $"B{i}", Lon = 72.8 + i * 0.001, Lat = 19.1, NspId = 2, Meters = 50, Expected = 50, Communicating = 5 });
        var q = new FindFailureClustersQuery(FleetFilter.None, null, MinMeters: 500, MinFailurePct: 25, EpsMeters: 200);
        var (clusters, hot, _) = FailureClusterer.Cluster(rows, q, Fixtures.Snapshot());
        Assert.Equal(8, hot);
        var c = Assert.Single(clusters);
        Assert.Equal(600, c.Meters);
        Assert.Equal(540, c.Failing);
        Assert.Equal("C001", c.Id);
        Assert.Equal("Jio", c.Nsps[0].Nsp);
        Assert.Equal(HealthBand.Red, c.Band);
    }
}

public class ViewportTests
{
    [Fact]
    public void Nearby_pans_share_a_snapped_viewport()
    {
        var a = MapViewport.Snap(new BoundingBox(77.101, 28.401, 77.299, 28.599), 10);
        var b = MapViewport.Snap(new BoundingBox(77.102, 28.402, 77.298, 28.598), 10);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Polygon_wkt_is_closed_and_invariant()
    {
        var ring = PolygonGeometry.Close([[77.1, 28.5], [77.2, 28.5], [77.2, 28.6]]);
        Assert.Equal(4, ring.Count);
        Assert.Equal("POLYGON((77.1 28.5, 77.2 28.5, 77.2 28.6, 77.1 28.5))", PolygonGeometry.ToWkt(ring));
    }
}
