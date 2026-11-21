using System.Text;
using Dapper;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Health;
using SmartMeter.Infrastructure.Persistence;

namespace SmartMeter.UnitTests;

public class SqlBuilderTests
{
    [Theory]
    [InlineData(HealthFilter.Dead, "h.HealthClass = 0")]
    [InlineData(HealthFilter.Intermittent, "h.HealthClass = 1")]
    [InlineData(HealthFilter.AllDay, "h.HealthClass = 2")]
    [InlineData(HealthFilter.Last7Days, "h.ActiveLast7 = 1")]
    public void Analytics_tools_apply_the_health_quick_filter(HealthFilter health, string expected)
    {
        var sql = new StringBuilder("1 = 1");
        FleetSource.MeterHealth("h").Where(sql, new DynamicParameters(), new FleetFilter { Health = health }, health: true);
        Assert.Contains(expected, sql.ToString());
    }

    [Theory]
    [InlineData(HealthFilter.All)]
    [InlineData(HealthFilter.TodayFail)]   // a metric switch, never a row filter
    public void All_and_fail_on_day_add_no_row_predicate(HealthFilter health)
    {
        var sql = new StringBuilder("1 = 1");
        FleetSource.MeterHealth("h").Where(sql, new DynamicParameters(), new FleetFilter { Health = health }, health: true);
        Assert.Equal("1 = 1", sql.ToString());
    }

    [Fact]
    public void Fleet_queries_keep_adding_health_themselves()
    {
        var sql = new StringBuilder("1 = 1");
        FleetSource.MeterHealth("h").Where(sql, new DynamicParameters(), new FleetFilter { Health = HealthFilter.Dead });
        Assert.DoesNotContain("HealthClass", sql.ToString());
    }

    [Fact]
    public void Varchar_key_parameters_are_sent_as_ansi_strings()
    {
        // nvarchar parameters against the varchar MeterId / BuildingKey columns turn index seeks into scans
        var key = SqlText.Key("B:BLD-0000001");
        Assert.True(key.IsAnsi);
        Assert.Equal("B:BLD-0000001", key.Value);
    }
}
