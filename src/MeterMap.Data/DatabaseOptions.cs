namespace MeterMap.Data;

public sealed class DatabaseOptions
{
    public string ActiveProfile { get; set; } = "Primary";

    public Dictionary<string, DbProfile> Profiles { get; set; } = new();
}

public sealed class ColumnMap
{
    // Equipment table
    public string EquipmentId { get; set; } = "ID";
    public string MeterId { get; set; } = "Meter_ID";
    public string ManufacturerId { get; set; } = "Manufacturer_ID";
    public string EquipmentStatus { get; set; } = "Status";

    // Device location table
    public string LocationId { get; set; } = "ID";
    public string Latitude { get; set; } = "GPS_LAT";
    public string Longitude { get; set; } = "GPS_LONG";

    // Device location link table
    public string LinkEquipmentId { get; set; } = "EquipmentId";
    public string LinkLocationId { get; set; } = "DeviceLocationId";
    public string ValidFrom { get; set; } = "ValidFromDate";
    public string ValidTo { get; set; } = "ValidToDate";

    // Interval data table
    public string IntervalEquipmentId { get; set; } = "EQUIPMENT_ID";
    public string DeviceCode { get; set; } = "DEVICE_CODE";
    public string IntervalParameterId { get; set; } = "PARAM_ID";
    public string Time { get; set; } = "INTERVAL_END_TIME";
    public string ParameterValue { get; set; } = "PARAM_VALUE";

    // Parameter master table
    public string ParameterMasterId { get; set; } = "ID";
    public string ParameterCode { get; set; } = "PARAM_CODE";
    public string ParameterName { get; set; } = "PARAM_NAME";
    public string Unit { get; set; } = "UOM";
    public string ParameterType { get; set; } = "PARAM_TYPE";
    public string Phase { get; set; } = "PHASE";
}

public sealed class ParameterIds
{
    public int VB { get; set; } = 388;
    public int VR { get; set; } = 395;
    public int VY { get; set; } = 404;

    public int IR { get; set; } = 236;
    public int IY { get; set; } = 243;
    public int IB { get; set; } = 201;

    public int IntKvahI { get; set; } = 211;
    public int IntKwhI { get; set; } = 217;
}

public sealed class DbProfile
{
    public string LatLonOrder { get; set; } = "lonlat";
    public string Server { get; set; } = @".\SQLEXPRESS";
    public string Database { get; set; } = "MeterDB";

    public string AuthMode { get; set; } = "Windows";

    public string? User { get; set; }
    public string? Password { get; set; }

    // Database tables
    public string EquipmentTable { get; set; } = "dbo.Equipment";
    public string DeviceLocationTable { get; set; } = "dbo.device_location";
    public string DeviceLinkTable { get; set; } = "dbo.devloc_device_link";
    public string IntervalTable { get; set; } = "dbo.interval_data";
    public string ParameterTable { get; set; } = "dbo.parameter_master";

    public ColumnMap Columns { get; set; } = new();
    public ParameterIds ParameterIds { get; set; } = new();
}
