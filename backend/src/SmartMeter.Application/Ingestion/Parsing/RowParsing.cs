using System.Globalization;
using System.Text;

namespace SmartMeter.Application.Ingestion.Parsing;

/// <summary>Abstraction over one row of a CSV or Excel reader (typed access where the format supports it).</summary>
public interface IRowSource
{
    int FieldCount { get; }
    bool IsEmpty(int ordinal);
    string? GetString(int ordinal);
    bool TryGetDouble(int ordinal, out double value);
    bool TryGetDate(int ordinal, out DateOnly value);
    bool TryGetBoolean(int ordinal, out bool value);
}

public enum CisField { MeterId, BuildingId, Latitude, Longitude, Address, Nsp, Manufacturer, Zone, InstallationDate, DtId }

public enum MdmField { MeterId, LogDate, IsCommunicating }

public readonly record struct RowError(long RowNo, string? Column, string? RawValue, string Message);

/// <summary>Collects row-level validation errors (stores the first N, counts all rejected rows).</summary>
public sealed class ErrorSink(int maxStored)
{
    private readonly List<RowError> _errors = [];
    private long _lastRejectedRow = -1;

    public long RejectedRows { get; private set; }
    public long TotalErrors { get; private set; }
    public IReadOnlyList<RowError> Errors => _errors;

    public void Add(long rowNo, string? column, string? raw, string message)
    {
        TotalErrors++;
        if (rowNo != _lastRejectedRow)
        {
            RejectedRows++;
            _lastRejectedRow = rowNo;
        }
        if (_errors.Count < maxStored)
            _errors.Add(new RowError(rowNo, column, Truncate(raw, 400), Truncate(message, 400)!));
    }

    public void AddUncounted(long rowNo, string? column, string? raw, string message)
    {
        TotalErrors++;
        if (_errors.Count < maxStored) _errors.Add(new RowError(rowNo, column, Truncate(raw, 400), Truncate(message, 400)!));
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

/// <summary>Resolves file headers to logical fields using case/space/punctuation-insensitive aliases.</summary>
public sealed class HeaderMap<TField> where TField : struct, Enum
{
    private readonly Dictionary<TField, int> _ordinals;

    private HeaderMap(Dictionary<TField, int> ordinals) => _ordinals = ordinals;

    public int this[TField field] => _ordinals.TryGetValue(field, out var o) ? o : -1;
    public bool Has(TField field) => _ordinals.ContainsKey(field);

    public static HeaderMap<TField> Resolve(IReadOnlyList<string> headers, IReadOnlyDictionary<TField, string[]> aliases,
        IReadOnlyCollection<TField> required, out IReadOnlyList<TField> missing)
    {
        var normalized = headers.Select(Normalize).ToList();
        var map = new Dictionary<TField, int>();
        foreach (var (field, names) in aliases)
        {
            foreach (var alias in names)
            {
                var idx = normalized.IndexOf(Normalize(alias));
                if (idx < 0 || map.ContainsValue(idx)) continue;
                map[field] = idx;
                break;
            }
        }
        missing = required.Where(f => !map.ContainsKey(f)).ToList();
        return new HeaderMap<TField>(map);
    }

    public static string Normalize(string header)
    {
        var sb = new StringBuilder(header.Length);
        foreach (var ch in header.Trim().TrimStart('﻿'))
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}

public static class ColumnAliases
{
    private static readonly string[] MeterIdAliases =
        ["MeterId", "Meter_ID", "Meter No", "MeterNo", "Meter Number", "Meter Serial No", "MeterSerialNumber", "Serial No", "MSN", "Meter"];

    public static readonly IReadOnlyDictionary<CisField, string[]> Cis = new Dictionary<CisField, string[]>
    {
        [CisField.MeterId] = MeterIdAliases,
        [CisField.BuildingId] = ["BuildingId", "Building_ID", "Building", "Bldg Id", "Building Code", "Society Id"],
        [CisField.Latitude] = ["Latitude", "Lat", "GPS_LAT", "GPS Latitude", "Y"],
        [CisField.Longitude] = ["Longitude", "Lon", "Lng", "Long", "GPS_LONG", "GPS Longitude", "X"],
        [CisField.Address] = ["Address", "Customer Address", "Addr", "Premise Address", "Installation Address"],
        [CisField.Nsp] = ["NSP", "Network Service Provider", "SIM Operator", "Operator", "Carrier", "Telecom Operator", "SIM Provider"],
        [CisField.Manufacturer] = ["Manufacturer", "Make", "Mfr", "Meter Make", "OEM", "Meter Manufacturer"],
        [CisField.Zone] = ["Zone", "Zone Name", "Area", "Region", "Circle", "Division", "Location"],
        [CisField.InstallationDate] = ["InstallationDate", "Installation Date", "Install Date", "Installed On", "Date Of Installation", "Commissioning Date"],
        [CisField.DtId] = ["DtId", "DT_ID", "DT Code", "Transformer Id", "Distribution Transformer", "DT"],
    };

    public static readonly IReadOnlyCollection<CisField> CisRequired = [CisField.MeterId];

    public static readonly IReadOnlyDictionary<MdmField, string[]> Mdm = new Dictionary<MdmField, string[]>
    {
        [MdmField.MeterId] = MeterIdAliases,
        [MdmField.LogDate] = ["LogDate", "Log Date", "Date", "Read Date", "Comm Date", "Communication Date", "Day", "Report Date"],
        [MdmField.IsCommunicating] = ["IsCommunicating", "Is Communicating", "Communicating", "Comm Status", "Communication Status", "Status", "Success", "Comm"],
    };

    public static readonly IReadOnlyCollection<MdmField> MdmRequired = [MdmField.MeterId, MdmField.LogDate, MdmField.IsCommunicating];
}

public static class ValueParsers
{
    private static readonly HashSet<string> TrueValues = new(StringComparer.OrdinalIgnoreCase)
        { "1", "true", "t", "yes", "y", "success", "successful", "communicating", "comm", "ok", "connected", "online", "pass" };
    private static readonly HashSet<string> FalseValues = new(StringComparer.OrdinalIgnoreCase)
        { "0", "false", "f", "no", "n", "fail", "failed", "failure", "not communicating", "noncomm", "non-comm", "non comm", "nc", "disconnected", "offline" };

    public static bool TryParseBool(string? s, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (TrueValues.Contains(t)) { value = true; return true; }
        if (FalseValues.Contains(t)) return true;
        return false;
    }

    public static bool TryParseDouble(string? s, out double value) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static bool TryParseDate(string? s, IReadOnlyList<string> formats, out DateOnly value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        foreach (var f in formats)
        {
            if (DateTime.TryParseExact(t, f, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
            {
                value = DateOnly.FromDateTime(dt);
                return Plausible(value);
            }
        }
        // Excel serial date stored as text ("45983")
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 80000)
        {
            value = DateOnly.FromDateTime(DateTime.FromOADate(serial));
            return true;
        }
        return false;
    }

    public static bool Plausible(DateOnly d) => d.Year is >= 1990 and <= 2100;

    /// <summary>Trims, removes Excel's trailing ".0" on numeric ids, rejects ids longer than the column.</summary>
    public static string? NormalizeMeterId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t.EndsWith(".0", StringComparison.Ordinal) && t.Length > 2 && t[..^2].All(char.IsDigit)) t = t[..^2];
        return t;
    }

    public static string? Clean(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= maxLength ? t : t[..maxLength];
    }
}
