using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Buildings;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Infrastructure.Persistence;

namespace SmartMeter.UnitTests;

public class DateRangeTests
{
    private static readonly CommWindow W = Fixtures.Window;   // 22 Oct .. 20 Nov 2026 (30 days)

    [Fact]
    public void Default_window_metric_covers_the_whole_window()
    {
        var d = DaySelection.From(W, null, MetricMode.Window, null, null);
        Assert.True(d.IsFullWindow);
        Assert.Equal((1, 30, 30), (d.FromColumn, d.ToColumn, d.RangeDays));
        Assert.Equal((1 << 30) - 1, d.RangeMask);
    }

    [Fact]
    public void A_range_maps_to_its_day_columns_and_mask_bits()
    {
        // 18..20 Nov = columns 28..30 = bits 2..0
        var d = DaySelection.From(W, null, MetricMode.Window, new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 20));
        Assert.False(d.IsFullWindow);
        Assert.Equal((28, 30, 3), (d.FromColumn, d.ToColumn, d.RangeDays));
        Assert.Equal(0b111, d.RangeMask);

        // 1..10 Nov = columns 11..20 = bits 19..10
        var e = DaySelection.From(W, null, MetricMode.Window, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 10));
        Assert.Equal((11, 20, 10), (e.FromColumn, e.ToColumn, e.RangeDays));
        Assert.Equal(0b1111111111 << 10, e.RangeMask);
    }

    [Fact]
    public void Range_days_outside_the_window_are_clamped_into_it()
    {
        var d = DaySelection.From(W, null, MetricMode.Window, new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31));
        Assert.True(d.IsFullWindow);
    }

    [Fact]
    public void Reversed_range_is_rejected() =>
        Assert.Throws<BadRequestException>(() =>
            DaySelection.From(W, null, MetricMode.Window, new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 1)));

    [Fact]
    public void Day_metric_ignores_the_range()
    {
        var d = DaySelection.From(W, new DateOnly(2026, 11, 5), MetricMode.Day, new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 20));
        Assert.True(d.IsFullWindow);
        Assert.Equal(15, d.Column);
    }

    [Fact]
    public void Summary_sql_sums_the_range_columns_and_meter_level_counts_range_bits()
    {
        var d = DaySelection.From(W, null, MetricMode.Window, new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 20));
        var summary = FleetSource.Building(FleetFilter.None, "s");
        Assert.Equal("(CAST(s.E28 AS int) + CAST(s.E29 AS int) + CAST(s.E30 AS int))", summary.MetricE(d));
        Assert.Equal("(CAST(s.C28 AS int) + CAST(s.C29 AS int) + CAST(s.C30 AS int))", summary.MetricC(d));
        var meters = FleetSource.MeterHealth("h");
        Assert.Equal("BIT_COUNT(h.EligMask & @rmask)", meters.MetricE(d));
        var full = DaySelection.From(W, null, MetricMode.Window, null, null);
        Assert.Equal("s.EligTotal", summary.MetricE(full));            // whole window keeps the stored totals
    }

    [Fact]
    public void Building_detail_averages_the_days_of_the_range()
    {
        var snap = Fixtures.Snapshot();
        long[] Days(params (int col, long v)[] set) { var a = new long[30]; foreach (var (c, v) in set) a[c - 1] = v; return a; }
        // 10 meters: 18 Nov 10/10, 19 Nov 6/10, 20 Nov 2/10
        var slice = new BuildingSliceRow(1, 1, 1, true, 10, 300, 200,
            Days((28, 10), (29, 10), (30, 10)), Days((28, 10), (29, 6), (30, 2)));
        var b = new BuildingRow { BuildingKey = "B:X", Name = "X", ZoneId = 1, MeterCount = 10 };

        var range = DaySelection.From(snap.Window, null, MetricMode.Window, new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 20));
        var dto = BuildingAggregator.Build(b, [slice], snap, range);
        Assert.Equal(10, dto.Expected);                 // 30 meter-days / 3 days
        Assert.Equal(6, dto.Success);                   // 18 / 3
        Assert.Equal(60, dto.WindowSuccessPct);         // 18 of 30

        var lastDay = DaySelection.From(snap.Window, new DateOnly(2026, 11, 20), MetricMode.Day);
        var one = BuildingAggregator.Build(b, [slice], snap, lastDay);
        Assert.Equal((10L, 2L), (one.Expected, one.Success));
    }

    [Fact]
    public void Range_percentages_come_from_meter_days_not_from_rounded_averages()
    {
        // 2 meters over 3 days: 5 of 6 meter-days communicated. Rounded averages would say 2 of 2 = 100 %.
        var d = DaySelection.From(W, null, MetricMode.Window, new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 20));
        var n = DayCounts.From(6, 5, d);
        Assert.Equal((2L, 2L), (n.Expected, n.Communicating));
        Assert.Equal(83.33, n.SuccessPct);
        Assert.Equal(16.67, n.FailurePct);

        // a 5-meter building at 11 of 15 meter-days is 26.7 % failing: amber, not the green that 4 of 5 (20 %) would give
        var b = DayCounts.From(15, 11, d);
        Assert.Equal(26.67, b.FailurePct);
        Assert.Equal(SmartMeter.Domain.Health.HealthBand.Amber, b.Band);
    }
}
