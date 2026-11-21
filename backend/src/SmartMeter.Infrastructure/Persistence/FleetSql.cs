using System.Text;
using Dapper;
using SmartMeter.Application.Common;
using SmartMeter.Domain.Analytics;
using SmartMeter.Domain.Health;

namespace SmartMeter.Infrastructure.Persistence;

/// <summary>
/// Describes an aggregation source so the same query shape can run either on a pre-aggregated
/// summary table (sm.KpiCube / sm.BuildingSummary / sm.GridSummary: counters in MeterCount, Ekk, Ckk)
/// or on meter level (sm.MeterHealth: one row per meter, counters decoded from bit masks).
/// Every identifier placed in SQL text comes from code, never from user input; user values are parameters.
/// </summary>
internal sealed class FleetSource
{
    private FleetSource(string table, string alias, bool meterLevel)
    {
        Table = table;
        A = alias;
        MeterLevel = meterLevel;
    }

    public string Table { get; }
    public string A { get; }
    public bool MeterLevel { get; }

    public static FleetSource Kpi(FleetFilter f, string alias = "q") =>
        f.UsesMeterLevel ? new("sm.MeterHealth", alias, true) : new("sm.KpiCube", alias, false);

    public static FleetSource Building(FleetFilter f, string alias = "s") =>
        f.UsesMeterLevel ? new("sm.MeterHealth", alias, true) : new("sm.BuildingSummary", alias, false);

    public static FleetSource Grid(string alias = "g") => new("sm.GridSummary", alias, false);

    public static FleetSource MeterHealth(string alias = "h") => new("sm.MeterHealth", alias, true);

    public string Meters => MeterLevel ? "1" : $"{A}.MeterCount";
    public string E(DaySelection d) => MeterLevel ? $"(RIGHT_SHIFT({A}.EligMask, @bit) & 1)" : $"{A}.E{d.Column:00}";
    public string C(DaySelection d) => MeterLevel ? $"(RIGHT_SHIFT({A}.CommMask, @bit) & 1)" : $"{A}.C{d.Column:00}";
    public string EligTotal => MeterLevel ? $"{A}.EligDays" : $"{A}.EligTotal";
    public string CommTotal => MeterLevel ? $"{A}.CommDays" : $"{A}.CommTotal";
    public string MetricE(DaySelection d) => d.Metric == MetricMode.Window ? EligTotal : E(d);
    public string MetricC(DaySelection d) => d.Metric == MetricMode.Window ? CommTotal : C(d);

    /// <summary>Health quick-filter as a row predicate. "Today Fail" is a metric switch handled by callers.</summary>
    public string Health(HealthFilter h) => h switch
    {
        HealthFilter.AllDay => $"{A}.HealthClass = 2",
        HealthFilter.Last7Days => $"{A}.ActiveLast7 = 1",
        HealthFilter.Dead => $"{A}.HealthClass = 0",
        HealthFilter.Intermittent => $"{A}.HealthClass = 1",
        _ => "1 = 1",
    };

    public static string Sum(string expr) => $"CAST(ISNULL(SUM(CAST({expr} AS bigint)), 0) AS bigint)";

    /// <summary>Day metric = counts on the day; Window metric = average daily counts over the window.</summary>
    public static string SumMetric(string expr, DaySelection d) => d.Metric == MetricMode.Window
        ? $"CAST(ISNULL(ROUND(SUM(CAST({expr} AS float)) / @days, 0), 0) AS bigint)"
        : Sum(expr);

    /// <summary>Appends dimension (and, on meter level, installation-date) predicates.</summary>
    public void Where(StringBuilder sql, DynamicParameters p, FleetFilter f, bool zone = true, bool nsp = true, bool manufacturer = true)
    {
        if (zone && f.ZoneIds.Count > 0) { sql.Append($" AND {A}.ZoneId IN @zoneIds"); p.Add("zoneIds", f.ZoneIds.ToArray()); }
        if (nsp && f.NspIds.Count > 0) { sql.Append($" AND {A}.NspId IN @nspIds"); p.Add("nspIds", f.NspIds.ToArray()); }
        if (manufacturer && f.ManufacturerIds.Count > 0) { sql.Append($" AND {A}.ManufacturerId IN @mfrIds"); p.Add("mfrIds", f.ManufacturerIds.ToArray()); }
        if (MeterLevel)
        {
            if (f.InstalledFrom is { } from) { sql.Append($" AND {A}.InstallationDate >= @installedFrom"); p.Add("installedFrom", from); }
            if (f.InstalledTo is { } to) { sql.Append($" AND {A}.InstallationDate <= @installedTo"); p.Add("installedTo", to); }
        }
    }

    public static void AddDay(DynamicParameters p, DaySelection d, int windowDays)
    {
        p.Add("bit", d.Bit);
        p.Add("days", (double)windowDays);
    }
}

internal static class SqlText
{
    /// <summary>Escapes LIKE wildcards in user input (SQL Server bracket syntax).</summary>
    public static string EscapeLike(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    public static string DayColumns(string alias, int days, bool sum)
    {
        var sb = new StringBuilder();
        for (var k = 1; k <= days; k++)
        {
            if (k > 1) sb.Append(", ");
            if (sum) sb.Append($"SUM(CAST({alias}.E{k:00} AS bigint)) AS E{k:00}, SUM(CAST({alias}.C{k:00} AS bigint)) AS C{k:00}");
            else sb.Append($"{alias}.E{k:00}, {alias}.C{k:00}");
        }
        return sb.ToString();
    }

    public static string MaskDayColumns(string alias, int days)
    {
        var sb = new StringBuilder();
        for (var k = 1; k <= days; k++)
        {
            if (k > 1) sb.Append(", ");
            var bit = days - k;
            sb.Append($"SUM(CAST(RIGHT_SHIFT({alias}.EligMask, {bit}) & 1 AS bigint)) AS E{k:00}, ")
              .Append($"SUM(CAST(RIGHT_SHIFT({alias}.CommMask, {bit}) & 1 AS bigint)) AS C{k:00}");
        }
        return sb.ToString();
    }
}
