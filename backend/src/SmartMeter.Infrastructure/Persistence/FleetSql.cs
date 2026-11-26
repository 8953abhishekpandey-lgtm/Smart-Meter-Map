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
    public string MetricE(DaySelection d) => d.Metric == MetricMode.Window ? RangeE(d) : E(d);
    public string MetricC(DaySelection d) => d.Metric == MetricMode.Window ? RangeC(d) : C(d);

    /// <summary>Expected / communicating meter-days of the chosen range: the stored totals for the whole window, else the
    /// day columns of the range summed (summary tables) or the range bits counted (meter level).</summary>
    public string RangeE(DaySelection d) => d.IsFullWindow ? EligTotal : MeterLevel ? $"BIT_COUNT({A}.EligMask & @rmask)" : DaySum("E", d);
    public string RangeC(DaySelection d) => d.IsFullWindow ? CommTotal : MeterLevel ? $"BIT_COUNT({A}.CommMask & @rmask)" : DaySum("C", d);

    /// <summary>Window card: the chosen range in window mode, the whole window in day mode.</summary>
    public string WindowE(DaySelection d) => d.Metric == MetricMode.Window ? RangeE(d) : EligTotal;
    public string WindowC(DaySelection d) => d.Metric == MetricMode.Window ? RangeC(d) : CommTotal;

    private string DaySum(string prefix, DaySelection d) =>
        "(" + string.Join(" + ", Enumerable.Range(d.FromColumn, d.RangeDays).Select(k => $"CAST({A}.{prefix}{k:00} AS int)")) + ")";

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
    /// <remarks>
    /// Window/range metric: the RAW meter-day totals of the range. Callers turn them into an average day for display
    /// (<c>DayCounts</c>) but take percentages and colour bands from the raw totals, so small buildings are not distorted by
    /// rounding (5 meters over 3 days: 11 of 15 meter-days = 73 %, not 4 of 5 = 80 %).
    /// </remarks>
    public static string SumMetric(string expr, DaySelection d) => Sum(expr);

    /// <summary>
    /// Appends dimension (and, on meter level, installation-date) predicates; with <paramref name="health"/> also the
    /// health quick-filter as a row predicate ("Fail on selected day" is a metric, not a row filter).
    /// </summary>
    public void Where(StringBuilder sql, DynamicParameters p, FleetFilter f, bool zone = true, bool nsp = true, bool manufacturer = true,
        bool health = false)
    {
        if (health && f.Health != HealthFilter.TodayFail && f.Health != HealthFilter.All) sql.Append($" AND {Health(f.Health)}");
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
        p.Add("days", (double)(d.Metric == MetricMode.Window ? d.RangeDays : windowDays));
        p.Add("rmask", d.RangeMask);
    }
}

internal static class SqlText
{
    /// <summary>
    /// Parameter for the <c>varchar</c> key columns (MeterId, BuildingKey). Dapper sends .NET strings as nvarchar; comparing
    /// a varchar column with an nvarchar value under a SQL collation forces CONVERT_IMPLICIT on the column, which turns
    /// every index seek on 15M meters into a scan.
    /// </summary>
    public static DbString Key(string? value) => new() { Value = value, IsAnsi = true };

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
