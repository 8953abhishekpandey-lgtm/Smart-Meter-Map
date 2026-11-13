using MeterMap.Core;
using Xunit;

namespace MeterMap.Tests;

public class IdKeyTests
{
    [Theory]
    [InlineData("79032217", "79032217")]
    [InlineData("79032217.0", "79032217")]
    [InlineData("079032217", "79032217")]
    [InlineData("  79032217  ", "79032217")]
    [InlineData(" ab 12 ", "AB12")]
    public void Variants_give_the_same_key(string input, string expected)
        => Assert.Equal(expected, IdKey.Make(input));

    [Fact]
    public void Null_becomes_empty() => Assert.Equal("", IdKey.Make(null));
}

public class IntervalTests
{
    static IEnumerable<(string, DateTime)> Series(string meter, int stepMin, int count)
    {
        var t = new DateTime(2026, 9, 1, 0, 0, 0);
        for (int i = 0; i < count; i++) yield return (meter, t.AddMinutes(i * stepMin));
    }

    [Fact]
    public void Detects_15_and_30_minute_meters_separately()
    {
        var data = Series("A", 15, 96).Concat(Series("B", 30, 48)).ToList();
        var gaps = IntervalInference.CountGaps(data);
        var result = IntervalInference.Infer(gaps, new[] { "A", "B" });

        Assert.Equal(15, result["A"]);
        Assert.Equal(30, result["B"]);
    }

    [Fact]
    public void Smallest_gap_with_at_least_5_percent_wins()
    {
        // 37 baar 30 min, 3 baar 15 min -> 15 ka share 7.5% -> 15 jeetega
        var gaps = new[] { new GapCount("C", 30, 37), new GapCount("C", 15, 3) };
        Assert.Equal(15, IntervalInference.Infer(gaps, new[] { "C" })["C"]);
    }

    [Fact]
    public void Rare_small_gap_is_ignored()
    {
        // 1 baar 15 min out of 40 = 2.5% -> ignore, 30 jeetega
        var gaps = new[] { new GapCount("C", 30, 39), new GapCount("C", 15, 1) };
        Assert.Equal(30, IntervalInference.Infer(gaps, new[] { "C" })["C"]);
    }

    [Fact]
    public void Odd_gaps_like_45_are_ignored()
    {
        var gaps = new[] { new GapCount("D", 45, 100), new GapCount("D", 30, 10) };
        Assert.Equal(30, IntervalInference.Infer(gaps, new[] { "D" })["D"]);
    }

    [Fact]
    public void Meter_without_gaps_gets_fleet_interval()
    {
        var gaps = new[] { new GapCount("A", 15, 50), new GapCount("B", 15, 50), new GapCount("C", 30, 50) };
        var result = IntervalInference.Infer(gaps, new[] { "A", "B", "C", "NEW" });
        Assert.Equal(15, result["NEW"]);
    }

    [Fact]
    public void No_gaps_anywhere_defaults_to_30()
    {
        var result = IntervalInference.Infer(Array.Empty<GapCount>(), new[] { "X" });
        Assert.Equal(30, result["X"]);
    }
}
public class CommTests
{
    static readonly DateTime Ref = new(2026, 9, 30, 23, 45, 0);

    static MeterStats Stats(DateTime? first, DateTime? last, DateTime? lastValid,
                            long valid, long validRecent)
        => new("M1", first, last, lastValid, valid, validRecent);

    [Fact]
    public void Full_day_all_readings_is_100_percent()
    {
        // 15-min meter, poore din ki 96 readings
        var s = Stats(new DateTime(2026, 9, 1), Ref, Ref, 2000, 96);
        var r = CommCalculator.Calculate(s, 15, Ref, Ref, 24);
        Assert.Equal(96, r.ExpectedRecent);
        Assert.Equal(100.0, r.CommPct);
    }

    [Fact]
    public void Half_the_readings_is_50_percent()
    {
        var s = Stats(new DateTime(2026, 9, 1), Ref, Ref, 2000, 48);
        Assert.Equal(50.0, CommCalculator.Calculate(s, 15, Ref, Ref, 24).CommPct);
    }

    [Fact]
    public void Meter_that_appeared_inside_window_gets_plus_one()
    {
        // 2 ghante pehle aaya, 15-min: floor(120/15)=8, +1 = 9
        var first = Ref.AddHours(-2);
        var s = Stats(first, Ref, Ref, 9, 9);
        var r = CommCalculator.Calculate(s, 15, Ref, Ref, 24);
        Assert.Equal(9, r.ExpectedRecent);
        Assert.Equal(100.0, r.CommPct);
    }

    [Fact]
    public void Quality_never_exceeds_100()
    {
        var s = Stats(new DateTime(2026, 9, 1), Ref, Ref, 2000, 200);
        Assert.Equal(100.0, CommCalculator.Calculate(s, 15, Ref, Ref, 24).CommPct);
    }

    [Fact]
    public void Lifetime_percent_uses_whole_range()
    {
        // 1 Sep 00:00 se 23:45 tak, 15-min: floor(1425/15)+1 = 96 readings expected
        var first = new DateTime(2026, 9, 1, 0, 0, 0);
        var last = new DateTime(2026, 9, 1, 23, 45, 0);
        var s = Stats(first, last, last, 48, 0);
        Assert.Equal(50.0, CommCalculator.Calculate(s, 15, Ref, Ref, 24).LifeCommPct);
    }

    [Fact]
    public void Silent_exactly_at_limit_is_still_ok()
    {
        var s = Stats(new DateTime(2026, 9, 1), Ref, Ref.AddHours(-24), 100, 0);
        var r = CommCalculator.Calculate(s, 15, Ref, Ref, 24);
        Assert.Equal(24.0, r.HoursSilent);
        Assert.True(r.Ok);
    }

    [Fact]
    public void Silent_longer_than_limit_is_not_ok()
    {
        var s = Stats(new DateTime(2026, 9, 1), Ref, Ref.AddHours(-30), 100, 0);
        var r = CommCalculator.Calculate(s, 15, Ref, Ref, 24);
        Assert.Equal(30.0, r.HoursSilent);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Limit_is_at_least_two_intervals()
    {
        // 60-min meter, stale 1h -> limit 2h; 1.5h silent abhi bhi ok
        var s = Stats(new DateTime(2026, 9, 1), Ref, Ref.AddHours(-1.5), 100, 0);
        var r = CommCalculator.Calculate(s, 60, Ref, Ref, 1);
        Assert.Equal(2.0, r.StaleLimitHours);
        Assert.True(r.Ok);
    }

    [Fact]
    public void Never_communicated_is_9999_and_not_ok()
    {
        var s = Stats(null, null, null, 0, 0);
        var r = CommCalculator.Calculate(s, 30, Ref, Ref, 24);
        Assert.Equal(9999.0, r.HoursSilent);
        Assert.False(r.Ok);
        Assert.Equal(0.0, r.CommPct);
        Assert.True(double.IsNaN(r.LifeCommPct));
    }

    [Fact]
    public void Status_is_judged_at_statusRef_not_refTime()
    {
        // Data 30 Sep tak, par status 27 Oct ko dekha -> bahut purana -> red
        var s = Stats(new DateTime(2026, 9, 1), Ref, Ref, 2000, 96);
        var today = new DateTime(2026, 10, 27, 12, 0, 0);
        var r = CommCalculator.Calculate(s, 15, Ref, today, 24);
        Assert.False(r.Ok);
    }
}
public class ReasonTests
{
    static readonly DateTime Now = new(2026, 9, 30, 23, 45, 0);

    // Red meter jo hoursAgo pehle silent hua, aur uski rows bhi tabhi band hui
    static ReasonInput Silent(string id, double hoursAgo = 48, double? v = null,
                              double? temp = null, double life = 100)
        => new(id, false, Now.AddHours(-hoursAgo), Now.AddHours(-hoursAgo),
               false, v, temp, life, 24);

    static string One(ReasonInput m) => ReasonClassifier.Classify(new[] { m }, Now)[0];

    [Fact]
    public void Green_meter_gets_dash()
        => Assert.Equal("-", One(new ReasonInput("A", true, Now, Now, false, null, null, 100, 24)));

    [Fact]
    public void Rows_still_arriving_is_reading_fault()
        => Assert.Equal("Reading fault",
            One(new ReasonInput("A", false, Now.AddHours(-48), Now.AddHours(-1), false, null, null, 100, 24)));

    [Fact]
    public void Outside_window_is_silent_before_range()
        => Assert.Equal("Silent before range",
            One(new ReasonInput("A", false, Now.AddHours(-500), Now.AddHours(-500), true, null, null, 100, 24)));

    [Fact]
    public void No_valid_reading_ever_is_never_communicated()
        => Assert.Equal("Never communicated",
            One(new ReasonInput("A", false, null, null, false, null, null, double.NaN, 24)));

    [Fact]
    public void Twelve_meters_in_same_3h_window_is_mass_outage()
    {
        var list = Enumerable.Range(0, 12).Select(i => Silent("M" + i)).ToList();
        list.Add(Silent("OTHER", hoursAgo: 100));          // alag bucket
        var r = ReasonClassifier.Classify(list, Now);
        Assert.All(r.Take(12), x => Assert.Equal("Mass outage", x));
        Assert.Equal("Sudden stop", r[12]);
    }

    [Fact]
    public void Nine_meters_is_below_minimum_so_no_mass_outage()
    {
        var list = Enumerable.Range(0, 9).Select(i => Silent("M" + i)).ToList();
        Assert.All(ReasonClassifier.Classify(list, Now), x => Assert.Equal("Sudden stop", x));
    }

    [Fact]
    public void Ten_out_of_hundred_silent_is_below_20_percent_share()
    {
        var list = Enumerable.Range(0, 10).Select(i => Silent("M" + i, 48)).ToList();
        // 90 aur meters, har ek alag 3h bucket me
        list.AddRange(Enumerable.Range(0, 90).Select(i => Silent("X" + i, 60 + 3 * i)));
        var r = ReasonClassifier.Classify(list, Now);
        Assert.All(r.Take(10), x => Assert.Equal("Sudden stop", x));
    }

    [Fact]
    public void Mass_outage_beats_power_loss()
    {
        var list = Enumerable.Range(0, 12).Select(i => Silent("M" + i, v: 0)).ToList();
        Assert.All(ReasonClassifier.Classify(list, Now), x => Assert.Equal("Mass outage", x));
    }

    [Fact]
    public void Low_voltage_is_power_loss()
        => Assert.Equal("Power loss", One(Silent("A", v: 5)));

    [Fact]
    public void High_temperature_is_overheating()
        => Assert.Equal("Overheating", One(Silent("A", temp: 90)));

    [Fact]
    public void Power_loss_beats_overheating()
        => Assert.Equal("Power loss", One(Silent("A", v: 5, temp: 90)));

    [Fact]
    public void Poor_lifetime_quality_is_intermittent()
        => Assert.Equal("Intermittent signal", One(Silent("A", life: 60)));

    [Fact]
    public void Lifetime_exactly_70_is_not_intermittent()
        => Assert.Equal("Sudden stop", One(Silent("A", life: 70)));

    [Fact]
    public void Nothing_matches_is_sudden_stop()
        => Assert.Equal("Sudden stop", One(Silent("A")));

    [Fact]
    public void Bucket_floors_to_3_hour_boundary()
    {
        var a = ReasonClassifier.Bucket(new DateTime(2026, 9, 29, 2, 59, 0));
        var b = ReasonClassifier.Bucket(new DateTime(2026, 9, 29, 0, 0, 0));
        var c = ReasonClassifier.Bucket(new DateTime(2026, 9, 29, 3, 0, 0));
        Assert.Equal(b, a);
        Assert.NotEqual(a, c);
    }
}