using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartMeter.Application.Ingestion;

/// <summary>
/// Naming conventions for files dropped into the watch folder (and for uploads):
/// <list type="bullet">
/// <item>the file TYPE comes from the nearest folder name, else the file name: "cis" / "master" = CIS master,
///       "mdm" / "comm" / "log" = MDM communication log (<c>data/mdm/…</c>, <c>cis_master_nov.xlsx</c>);</item>
/// <item>the DAY comes from the file name, else the nearest folder name, in any of the usual spellings
///       (<c>2026-11-20</c>, <c>20-11-2026</c>, <c>20.11.2026</c>, <c>20261120</c>, <c>20 Nov 2026</c>, <c>20Nov26</c>), so a daily
///       MDM sheet does not need a date column.</item>
/// </list>
/// </summary>
public static partial class IngestionFileNames
{
    public static IngestionKind? KindOf(string relativePath)
    {
        var parts = Split(relativePath);
        if (parts.Length == 0) return null;
        for (var i = parts.Length - 2; i >= 0; i--)
            if (KindFromToken(parts[i]) is { } k) return k;
        return KindFromToken(Path.GetFileNameWithoutExtension(parts[^1]));
    }

    public static DateOnly? DateOf(string relativePath)
    {
        var parts = Split(relativePath);
        if (parts.Length == 0) return null;
        if (DateFromToken(Path.GetFileNameWithoutExtension(parts[^1])) is { } d) return d;
        for (var i = parts.Length - 2; i >= 0; i--)
            if (DateFromToken(parts[i]) is { } f) return f;
        return null;
    }

    private static string[] Split(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IngestionKind? KindFromToken(string token)
    {
        var t = token.ToLowerInvariant();
        if (t.Contains("cis") || t.Contains("master")) return IngestionKind.Cis;
        if (t.Contains("mdm") || t.Contains("comm") || t.Contains("log")) return IngestionKind.Mdm;
        return null;
    }

    internal static DateOnly? DateFromToken(string token)
    {
        // year first, then day first (Indian convention), then month names, then 8-digit runs
        foreach (Match m in YearFirst().Matches(token))
            if (Make(m.Groups["y"].Value, m.Groups["m"].Value, m.Groups["d"].Value) is { } a) return a;
        foreach (Match m in DayFirst().Matches(token))
            if (Make(m.Groups["y"].Value, m.Groups["m"].Value, m.Groups["d"].Value) is { } b) return b;
        foreach (Match m in MonthName().Matches(token))
        {
            var month = Array.FindIndex(Months, x => m.Groups["mon"].Value.StartsWith(x, StringComparison.OrdinalIgnoreCase)) + 1;
            var year = m.Groups["y"].Value.Length == 2 ? "20" + m.Groups["y"].Value : m.Groups["y"].Value;
            if (month > 0 && Make(year, month.ToString(CultureInfo.InvariantCulture), m.Groups["d"].Value) is { } c) return c;
        }
        foreach (Match m in EightDigits().Matches(token))
        {
            var s = m.Value;
            if (Make(s[..4], s[4..6], s[6..]) is { } ymd) return ymd;          // yyyyMMdd
            if (Make(s[4..], s[2..4], s[..2]) is { } dmy) return dmy;          // ddMMyyyy
        }
        return null;
    }

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static DateOnly? Make(string y, string m, string d)
    {
        if (!int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year is < 2000 or > 2099) return null;
        if (!int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out var month) || month is < 1 or > 12) return null;
        if (!int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var day) || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return new DateOnly(year, month, day);
    }

    [GeneratedRegex(@"(?<!\d)(?<y>20\d{2})[-_. ](?<m>\d{1,2})[-_. ](?<d>\d{1,2})(?!\d)")]
    private static partial Regex YearFirst();

    [GeneratedRegex(@"(?<!\d)(?<d>\d{1,2})[-_. ](?<m>\d{1,2})[-_. ](?<y>20\d{2})(?!\d)")]
    private static partial Regex DayFirst();

    [GeneratedRegex(@"(?<!\d)(?<d>\d{1,2})[-_. ]?(?<mon>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*[-_. ]?(?<y>20\d{2}|\d{2})(?!\d)",
        RegexOptions.IgnoreCase)]
    private static partial Regex MonthName();

    [GeneratedRegex(@"(?<!\d)\d{8}(?!\d)")]
    private static partial Regex EightDigits();
}
