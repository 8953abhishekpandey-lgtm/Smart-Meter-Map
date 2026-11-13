using System.Text.RegularExpressions;

namespace MeterMap.Core;

// ---------- Models ----------

/// for one meter: "number of gap reading"
public readonly record struct GapCount(string MeterId, int GapMinutes, long N);

// ---------- Meter ID normalize (Python: _id_key) ----------

public static class IdKey
{
    static readonly Regex TrailingZeroDecimal = new(@"\.0+$", RegexOptions.Compiled);
    static readonly Regex AnyWhitespace = new(@"\s+", RegexOptions.Compiled);

    
    public static string Make(string? id)
    {
        var s = (id ?? "").Trim();
        s = TrailingZeroDecimal.Replace(s, "");
        s = AnyWhitespace.Replace(s, "");
        s = s.TrimStart('0');
        return s.ToUpperInvariant();
    }
}

// ---------- Interval detect (infer_intervals) ----------

public static class IntervalInference
{
    public static readonly int[] StdIntervals = { 5, 10, 15, 30, 60 };
    public const double MinShare = 0.05;     // atleast 5% gap
    public const int FallbackMinutes = 30;   // no gap found

    
    /// counts come from sql
    public static List<GapCount> CountGaps(IEnumerable<(string MeterId, DateTime Time)> readings)
    {
        var result = new List<GapCount>();
        foreach (var g in readings.GroupBy(r => r.MeterId))
        {
            var times = g.Select(r => r.Time).Distinct().OrderBy(t => t).ToList();
            var counts = new Dictionary<int, long>();
            for (int i = 1; i < times.Count; i++)
            {
                int gap = (int)Math.Round((times[i] - times[i - 1]).TotalMinutes);
                if (Array.IndexOf(StdIntervals, gap) >= 0)
                    counts[gap] = counts.GetValueOrDefault(gap) + 1;
            }
            foreach (var kv in counts)
                result.Add(new GapCount(g.Key, kv.Key, kv.Value));
        }
        return result;
    }

    /// expected interval of every meter
    public static Dictionary<string, int> Infer(IEnumerable<GapCount> gaps, IEnumerable<string> allMeters)
    {
        var meters = allMeters.Distinct().ToList();
        var valid = gaps.Where(g => Array.IndexOf(StdIntervals, g.GapMinutes) >= 0).ToList();

        if (valid.Count == 0)
            return meters.ToDictionary(m => m, _ => FallbackMinutes);

        //Among the gaps that occur at least 5% of the time, pick the smallest one
        var perMeter = valid.GroupBy(g => g.MeterId).ToDictionary(
            g => g.Key,
            g =>
            {
                long total = g.Sum(x => x.N);
                return g.Where(x => x.N >= MinShare * total).Min(x => x.GapMinutes);
            });

        // For meters where no gap was found, use the most common interval across the other meters. 
        // If two intervals are equally common, pick the smaller one.
        int fleet = perMeter.Values
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .First().Key;

        return meters.ToDictionary(m => m, m => perMeter.TryGetValue(m, out var v) ? v : fleet);
    }
}
// ---------- Communication % and status (summarize_intervals + enrich) ----------

/// Summary statistics for one meter that came from SQL, not the raw rows
public sealed record MeterStats(
    string MeterId,
    DateTime? FirstRow,     
    DateTime? LastRow,      
    DateTime? LastValid,    
    long ValidCount,        
    long ValidRecent);      

public sealed record CommResult(
    double CommPct,          // last 24h quality (0-100)
    double LifeCommPct,      // Total range quality 
    long ExpectedRecent,     // expected readings in last 24 hors
    double HoursSilent,      // no valid readings in range =9999
    double StaleLimitHours,  // The limit after which this meter turns ‘red’ (not communicating)
    bool Ok);                // true = Communicating

public static class CommCalculator
{
    public const int WindowHours = 24;
    public const double NeverHours = 9999;

    static double Round1(double x) => Math.Round(x, 1, MidpointRounding.ToEven);
    static double Pct(double got, double expected) =>
        Round1(Math.Clamp(got / expected * 100.0, 0, 100));

    /// refTime    :The newest timestamp in the loaded data. The 24-hour window is measured back from this time
    /// statusRef  : The time against which the status is judged: either today (the current time) or the latest data time
    /// staleHours : The ‘Not communicating’ threshold set in the sidebar.
    public static CommResult Calculate(
        MeterStats s, int intervalMinutes, DateTime refTime, DateTime statusRef, double staleHours)
    {
        // The minimum limit is 2 intervals (a 15-minute meter: 0.5 hours, a 30-minute meter: 1 hour
        double staleLimit = Math.Max(staleHours, intervalMinutes * 2 / 60.0);

        // Hours silent: From the last valid reading up to statusRef
        double hoursSilent = s.LastValid is null
            ? NeverHours
            : Round1(Math.Max(0, (statusRef - s.LastValid.Value).TotalHours));
        bool ok = hoursSilent <= staleLimit;

        // In the selected date range, this meter has no row at al
        if (s.FirstRow is null || s.LastRow is null)
            return new CommResult(0, double.NaN, 0, hoursSilent, staleLimit, ok);

        var first = s.FirstRow.Value;
        var last = s.LastRow.Value;

        // ---- Recent (last 24h) quality ----
        var windowStart = refTime.AddHours(-WindowHours);
        var firstEffective = first > windowStart ? first : windowStart;
        double elapsedRecent = (refTime - firstEffective).TotalMinutes;
        double expectedRecent = Math.Floor(elapsedRecent / intervalMinutes);
        if (first > windowStart) expectedRecent += 1;     // meter window ke andar hi aaya
        expectedRecent = Math.Max(expectedRecent, 1);
        double commPct = Pct(s.ValidRecent, expectedRecent);

        // ---- Lifetime quality ( loaded range) ----
        double elapsedLife = (last - first).TotalMinutes;
        double expectedLife = Math.Max(Math.Floor(elapsedLife / intervalMinutes) + 1, 1);
        double lifePct = Pct(s.ValidCount, expectedLife);

        return new CommResult(commPct, lifePct, (long)expectedRecent, hoursSilent, staleLimit, ok);
    }
}


/// The fields of one meter that are needed to decide its reason.
public sealed record ReasonInput(
    string MeterId,
    bool Ok,                  // CommCalculator status
    DateTime? LastCommTime,   // last valid reading ( last_seen)
    DateTime? LastRowTime,    // last row (no matter valid or not)
    bool OutsideWindow,       // No rows in given range, last_seen 
    double? VLastMin,         // last valid reading  minimum voltage
    double? TempLast,         // last valid reading  temperature
    double LifeCommPct,       //  range  quality (NaN allowed)
    double StaleLimitHours);  // CommResult.StaleLimitHours

public static class ReasonClassifier
{
    public const double VoltDead = 20.0;
    public const double TempHigh = 85.0;
    public const int MassMin = 10;
    public const double MassShare = 0.20;
    public const int MassBucketMinutes = 180;
    public const double LifeFlaky = 70.0;

    /// .
    public static DateTime Bucket(DateTime t)
    {
        long size = TimeSpan.FromMinutes(MassBucketMinutes).Ticks;
        return new DateTime(t.Ticks - t.Ticks % size, t.Kind);
    }

    /// give rason for every minute .
    public static string[] Classify(IReadOnlyList<ReasonInput> meters, DateTime statusRef)
    {
        var bad = meters.Where(m => !m.Ok).ToList();

        // In which 3-hour window, how many red meters had their last communication
        var bucketCounts = bad
            .Where(m => m.LastCommTime.HasValue)
            .GroupBy(m => Bucket(m.LastCommTime!.Value))
            .ToDictionary(g => g.Key, g => g.Count());

        double threshold = MassShare * Math.Max(bad.Count, 1);
        var bigBuckets = bucketCounts
            .Where(kv => kv.Value >= MassMin && kv.Value >= threshold)
            .Select(kv => kv.Key)
            .ToHashSet();

        var result = new string[meters.Count];
        for (int i = 0; i < meters.Count; i++)
        {
            var m = meters[i];
            bool rowRecent = m.LastRowTime.HasValue
                && (statusRef - m.LastRowTime.Value).TotalHours <= m.StaleLimitHours;

            // The order is very important: the first matching rule wins
            if (m.Ok) result[i] = "-";
            else if (rowRecent) result[i] = "Reading fault";
            else if (m.OutsideWindow) result[i] = "Silent before range";
            else if (m.LastCommTime is null) result[i] = "Never communicated";
            else if (bigBuckets.Contains(Bucket(m.LastCommTime.Value))) result[i] = "Mass outage";
            else if (m.VLastMin is < VoltDead) result[i] = "Power loss";
            else if (m.TempLast is >= TempHigh) result[i] = "Overheating";
            else if (m.LifeCommPct < LifeFlaky) result[i] = "Intermittent signal";   // NaN par false
            else result[i] = "Sudden stop";
        }
        return result;
    }
}
// ---------- Mapping: long_lat parse + buildings ----------

/// One row of the meter_location table, exactly as it came from SQL.
public sealed record RawMapRow(
    string MeterId, double? Lat, double? Lon, string? LongLat,
    string? BuildingId, string? BuildingName, string? Area,
    string? FlatNo, string? Manufacturer, string? Nsp);

/// A cleaned meter: valid coordinates + building + location.
public sealed record MapMeter(
    string MeterId, string BuildingId, string BuildingName, string Area,
    double Lat, double Lon, string FlatNo, string Manufacturer, string Nsp);

public static class MeterMapping
{
    public const double BuildingGridM = 25;    // itne metre ke andar ke meters = ek building
    public const double ZoneGridM = 2000;      // itne bade square = ek Location

    static readonly Regex Pair = new(@"(-?\d+(?:\.\d+)?)[^\d.+-]+(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    /// "77.2090,28.6139" / "(77.2, 28.6)" / "POINT(77.2 28.6)" -> (lat, lon).
    /// If the order is wrong (lat > 90), it swaps them automatically.
    public static (double? Lat, double? Lon) ParseLongLat(string? text, string order = "lonlat")
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var m = Pair.Match(text);
        if (!m.Success) return (null, null);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var a) ||
            !double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, inv, out var b))
            return (null, null);

        double lat, lon;
        if (order.Equals("latlon", StringComparison.OrdinalIgnoreCase)) { lat = a; lon = b; }
        else { lon = a; lat = b; }

        if (Math.Abs(lat) > 90 && Math.Abs(lon) <= 90) (lat, lon) = (lon, lat);   // auto-fix
        return (lat, lon);
    }

    /// It cleans the mapping rows and returns the output in the same order as the input
    public static List<MapMeter> Prepare(IEnumerable<RawMapRow> rows, string order = "lonlat")
    {
        var all = rows.ToList();
        var seen = new HashSet<string>();
        var kept = new List<(RawMapRow Row, string Id, double Lat, double Lon)>();

        foreach (var r in all)
        {
            var id = (r.MeterId ?? "").Trim();
            if (id.Length == 0) continue;
            if (!seen.Add(id)) continue;                 // duplicate meter_id: pehla jeetega

            double? lat = r.Lat, lon = r.Lon;
            if (!(lat.HasValue && lon.HasValue))
                (lat, lon) = ParseLongLat(r.LongLat, order);
            if (!lat.HasValue || !lon.HasValue) continue;

            double la = lat.Value, lo = lon.Value;
            if (double.IsNaN(la) || double.IsNaN(lo)) continue;
            if (la < -90 || la > 90 || lo < -180 || lo > 180) continue;
            if (la == 0 && lo == 0) continue;
            kept.Add((r, id, la, lo));
        }

        if (kept.Count == 0) return new List<MapMeter>();

        static string Or(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
        bool hasBuilding = kept.Any(k => !string.IsNullOrWhiteSpace(k.Row.BuildingId));

        string[] bid, bname, area;
        double[] blat, blon;

        if (hasBuilding)
        {
            // If the table already has a building ID, use that, and keep each meter’s own coordinates
            bid = kept.Select(k => Or(k.Row.BuildingId, "Unknown")).ToArray();
            bname = kept.Select((k, i) => Or(k.Row.BuildingName, "Building " + bid[i])).ToArray();
            area = kept.Select(k => Or(k.Row.Area, "All")).ToArray();
            blat = kept.Select(k => k.Lat).ToArray();
            blon = kept.Select(k => k.Lon).ToArray();
        }
        else
        {
            Derive(kept.Select(k => k.Lat).ToArray(), kept.Select(k => k.Lon).ToArray(),
                   out bid, out bname, out blat, out blon, out area);
        }

        var result = new List<MapMeter>(kept.Count);
        for (int i = 0; i < kept.Count; i++)
        {
            var k = kept[i];
            result.Add(new MapMeter(k.Id, bid[i], bname[i], area[i], blat[i], blon[i],
                Or(k.Row.FlatNo, "-"), Or(k.Row.Manufacturer, "Unknown"), Or(k.Row.Nsp, "Unknown")));
        }
        return result;
    }

    ///If there is no building ID, it turns meters that are close together (within 25 m) into one building.
    static void Derive(double[] lat, double[] lon,
        out string[] bid, out string[] bname, out double[] blat, out double[] blon, out string[] area)
    {
        int n = lat.Length;
        const double ky = 111_000.0;
        double lat0 = lat.Average();
        double kx = 111_000.0 * Math.Cos(lat0 * Math.PI / 180.0);

        // 
        var codeOf = new Dictionary<(double, double), int>();
        var codes = new int[n];
        var firstIdx = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var key = (Math.Round(lat[i], 6), Math.Round(lon[i], 6));
            if (!codeOf.TryGetValue(key, out int c))
            {
                c = codeOf.Count;
                codeOf[key] = c;
                firstIdx.Add(i);
            }
            codes[i] = c;
        }

        int u = firstIdx.Count;
        var ux = new double[u];
        var uy = new double[u];
        for (int c = 0; c < u; c++) { ux[c] = lon[firstIdx[c]] * kx; uy[c] = lat[firstIdx[c]] * ky; }

        // Process the points in a fixed order (first by x, then by y), so the building IDs come out the same every time.
        var order = Enumerable.Range(0, u).OrderBy(i => ux[i]).ThenBy(i => uy[i]).ToArray();
        var sx = order.Select(i => ux[i]).ToArray();
        var sy = order.Select(i => uy[i]).ToArray();
        var labSorted = Cluster(sx, sy, BuildingGridM);
        var lab = new int[u];
        for (int j = 0; j < u; j++) lab[order[j]] = labSorted[j];

        var num = new int[n];
        for (int i = 0; i < n; i++) num[i] = lab[codes[i]] + 1;

        bid = num.Select(x => "B" + x.ToString().PadLeft(5, '0')).ToArray();
        bname = num.Select(x => "Building " + x).ToArray();

        ///center of buildings =  meters average
        var sumLat = new Dictionary<int, double>();
        var sumLon = new Dictionary<int, double>();
        var cnt = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            sumLat[num[i]] = sumLat.GetValueOrDefault(num[i]) + lat[i];
            sumLon[num[i]] = sumLon.GetValueOrDefault(num[i]) + lon[i];
            cnt[num[i]] = cnt.GetValueOrDefault(num[i]) + 1;
        }
        blat = new double[n];
        blon = new double[n];
        for (int i = 0; i < n; i++)
        {
            blat[i] = sumLat[num[i]] / cnt[num[i]];
            blon[i] = sumLon[num[i]] / cnt[num[i]];
        }

        // Location =  (2 km). 
        var zoneOf = new Dictionary<(long, long), int>();
        area = new string[n];
        for (int i = 0; i < n; i++)
        {
            long zy = (long)Math.Floor(blat[i] * ky / ZoneGridM);
            long zx = (long)Math.Floor(blon[i] * kx / ZoneGridM);
            if (!zoneOf.TryGetValue((zx, zy), out int z)) { z = zoneOf.Count; zoneOf[(zx, zy)] = z; }
            area[i] = "Location " + (z + 1);
        }
    }

    /// Greedy clustering: One seed point, plus all unassigned points within r metres of it, make up one building
    static int[] Cluster(double[] x, double[] y, double r)
    {
        int n = x.Length;
        var cx = new long[n];
        var cy = new long[n];
        var cells = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < n; i++)
        {
            cx[i] = (long)Math.Floor(x[i] / r);
            cy[i] = (long)Math.Floor(y[i] / r);
            if (!cells.TryGetValue((cx[i], cy[i]), out var list)) { list = new List<int>(); cells[(cx[i], cy[i])] = list; }
            list.Add(i);
        }

        var label = new int[n];
        Array.Fill(label, -1);
        double r2 = r * r;
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            if (label[i] >= 0) continue;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (cells.TryGetValue((cx[i] + dx, cy[i] + dy), out var lst))
                        foreach (int j in lst)
                        {
                            double ddx = x[j] - x[i], ddy = y[j] - y[i];
                            if (label[j] < 0 && ddx * ddx + ddy * ddy <= r2) label[j] = k;
                        }
            k++;
        }
        return label;
    }
}