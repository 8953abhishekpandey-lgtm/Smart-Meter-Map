using SmartMeter.Domain.Spatial;

namespace SmartMeter.Application.Ingestion.Parsing;

public readonly record struct CisRecord(
    string MeterId, string? BuildingId, double? Latitude, double? Longitude, string? Address,
    string Nsp, string Manufacturer, string Zone, DateOnly? InstallationDate, string? DtId);

public readonly record struct MdmRecord(string MeterId, DateOnly LogDate, bool IsCommunicating);

/// <summary>Validates one CIS master row. Invalid rows are rejected with row-level errors.</summary>
public static class CisRowParser
{
    public const string Unknown = "Unknown";

    /// <param name="serviceArea">Optional geofence of the utility's service area: points outside it are swapped when the
    /// swapped point falls inside (lat/lon column mix-ups), otherwise rejected.</param>
    public static bool TryParse(IRowSource src, HeaderMap<CisField> map, long rowNo, ErrorSink errors, out CisRecord record,
        BoundingBox? serviceArea = null)
    {
        record = default;
        var ok = true;
        var meterId = ValueParsers.NormalizeMeterId(Get(src, map, CisField.MeterId));
        if (meterId is null) { errors.Add(rowNo, "MeterId", null, "MeterId is required."); ok = false; }
        else if (meterId.Length > 32) { errors.Add(rowNo, "MeterId", meterId, "MeterId is longer than 32 characters."); ok = false; }

        var buildingId = ValueParsers.Clean(Get(src, map, CisField.BuildingId), 100);
        if (buildingId is { Length: > 64 }) { errors.Add(rowNo, "BuildingId", buildingId, "BuildingId is longer than 64 characters."); ok = false; }

        double? lat = null, lon = null;
        var latState = ReadDouble(src, map, CisField.Latitude, rowNo, errors, "Latitude", out var latV);
        var lonState = ReadDouble(src, map, CisField.Longitude, rowNo, errors, "Longitude", out var lonV);
        if (latState == NumberState.Invalid || lonState == NumberState.Invalid)
        {
            ok = false;
        }
        else if (latState == NumberState.Present && lonState == NumberState.Present)
        {
            var inService = serviceArea is not { } area || area.Contains(lonV, latV);
            if (Math.Abs(latV) > 90 && Math.Abs(lonV) <= 90 && Math.Abs(latV) <= 180) (latV, lonV) = (lonV, latV);   // swapped columns
            else if (!inService && serviceArea!.Value.Contains(latV, lonV)) (latV, lonV) = (lonV, latV);              // swapped inside service area

            if (latV == 0 && lonV == 0) { /* placeholder coordinates -> treat as unknown location */ }
            else if (latV is < -90 or > 90) { errors.Add(rowNo, "Latitude", Get(src, map, CisField.Latitude), "Latitude must be between -90 and 90."); ok = false; }
            else if (lonV is < -180 or > 180) { errors.Add(rowNo, "Longitude", Get(src, map, CisField.Longitude), "Longitude must be between -180 and 180."); ok = false; }
            else if (serviceArea is { } sa && !sa.Contains(lonV, latV))
            {
                errors.Add(rowNo, "Latitude", $"{latV}, {lonV}", "Coordinates are outside the configured service area (Ingestion:ServiceArea).");
                ok = false;
            }
            else { lat = Math.Round(latV, 7); lon = Math.Round(lonV, 7); }
        }
        else if (latState != lonState)
        {
            errors.Add(rowNo, latState == NumberState.Present ? "Longitude" : "Latitude", null, "Latitude and Longitude must both be provided.");
            ok = false;
        }

        DateOnly? installed = null;
        var instOrd = map[CisField.InstallationDate];
        if (instOrd >= 0 && !src.IsEmpty(instOrd))
        {
            if (src.TryGetDate(instOrd, out var d)) installed = d;
            else { errors.Add(rowNo, "InstallationDate", src.GetString(instOrd), "InstallationDate is not a valid date."); ok = false; }
        }

        var dt = ValueParsers.Clean(Get(src, map, CisField.DtId), 64);
        if (!ok) return false;
        record = new CisRecord(
            meterId!, buildingId, lat, lon, ValueParsers.Clean(Get(src, map, CisField.Address), 400),
            ValueParsers.Clean(Get(src, map, CisField.Nsp), 100) ?? Unknown,
            ValueParsers.Clean(Get(src, map, CisField.Manufacturer), 100) ?? Unknown,
            ValueParsers.Clean(Get(src, map, CisField.Zone), 100) ?? Unknown,
            installed, dt);
        return true;
    }

    private static string? Get(IRowSource src, HeaderMap<CisField> map, CisField f)
    {
        var o = map[f];
        return o < 0 || o >= src.FieldCount ? null : src.GetString(o);
    }

    private enum NumberState { Empty, Present, Invalid }

    private static NumberState ReadDouble(IRowSource src, HeaderMap<CisField> map, CisField f, long rowNo, ErrorSink errors, string name, out double value)
    {
        value = 0;
        var o = map[f];
        if (o < 0 || o >= src.FieldCount || src.IsEmpty(o)) return NumberState.Empty;
        if (src.TryGetDouble(o, out value)) return NumberState.Present;
        errors.Add(rowNo, name, src.GetString(o), $"{name} is not a number.");
        return NumberState.Invalid;
    }
}

/// <summary>Validates one MDM communication-log row.</summary>
public static class MdmRowParser
{
    public static bool TryParse(IRowSource src, HeaderMap<MdmField> map, long rowNo, ErrorSink errors, out MdmRecord record)
    {
        record = default;
        var ok = true;
        var idOrd = map[MdmField.MeterId];
        var meterId = ValueParsers.NormalizeMeterId(idOrd < src.FieldCount ? src.GetString(idOrd) : null);
        if (meterId is null) { errors.Add(rowNo, "MeterId", null, "MeterId is required."); ok = false; }
        else if (meterId.Length > 32) { errors.Add(rowNo, "MeterId", meterId, "MeterId is longer than 32 characters."); ok = false; }

        var dOrd = map[MdmField.LogDate];
        DateOnly date = default;
        if (dOrd >= src.FieldCount || src.IsEmpty(dOrd)) { errors.Add(rowNo, "LogDate", null, "LogDate is required."); ok = false; }
        else if (!src.TryGetDate(dOrd, out date)) { errors.Add(rowNo, "LogDate", src.GetString(dOrd), "LogDate is not a valid date."); ok = false; }

        var cOrd = map[MdmField.IsCommunicating];
        var comm = false;
        if (cOrd >= src.FieldCount || src.IsEmpty(cOrd)) { errors.Add(rowNo, "IsCommunicating", null, "IsCommunicating is required."); ok = false; }
        else if (!src.TryGetBoolean(cOrd, out comm))
        { errors.Add(rowNo, "IsCommunicating", src.GetString(cOrd), "IsCommunicating must be true/false, 1/0, yes/no or success/fail."); ok = false; }

        if (!ok) return false;
        record = new MdmRecord(meterId!, date, comm);
        return true;
    }
}
