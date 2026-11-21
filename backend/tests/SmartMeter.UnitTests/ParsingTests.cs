using SmartMeter.Application.Ingestion.Parsing;

namespace SmartMeter.UnitTests;

internal sealed class FakeRow(params string?[] values) : IRowSource
{
    private static readonly string[] Formats = ["yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "dd-MMM-yyyy"];
    public int FieldCount => values.Length;
    public bool IsEmpty(int ordinal) => string.IsNullOrWhiteSpace(values[ordinal]);
    public string? GetString(int ordinal) => values[ordinal];
    public bool TryGetDouble(int ordinal, out double value) => ValueParsers.TryParseDouble(values[ordinal], out value);
    public bool TryGetDate(int ordinal, out DateOnly value) => ValueParsers.TryParseDate(values[ordinal], Formats, out value);
    public bool TryGetBoolean(int ordinal, out bool value) => ValueParsers.TryParseBool(values[ordinal], out value);
}

public class HeaderMapTests
{
    [Fact]
    public void Aliases_are_case_space_and_punctuation_insensitive()
    {
        var map = HeaderMap<CisField>.Resolve(["﻿Meter No", "GPS_LAT", "gps long", "SIM Operator", "Meter Make", "Zone Name"],
            ColumnAliases.Cis, ColumnAliases.CisRequired, out var missing);
        Assert.Empty(missing);
        Assert.Equal(0, map[CisField.MeterId]);
        Assert.Equal(1, map[CisField.Latitude]);
        Assert.Equal(2, map[CisField.Longitude]);
        Assert.Equal(3, map[CisField.Nsp]);
        Assert.Equal(-1, map[CisField.BuildingId]);
    }

    [Fact]
    public void Missing_required_columns_are_reported()
    {
        HeaderMap<MdmField>.Resolve(["MeterId", "Date"], ColumnAliases.Mdm, ColumnAliases.MdmRequired, out var missing);
        Assert.Equal([MdmField.IsCommunicating], missing);
    }
}

public class CisParserTests
{
    private static readonly HeaderMap<CisField> Map = HeaderMap<CisField>.Resolve(
        ["MeterId", "BuildingId", "Latitude", "Longitude", "Address", "NSP", "Manufacturer", "Zone", "InstallationDate", "DtId"],
        ColumnAliases.Cis, ColumnAliases.CisRequired, out _);

    [Fact]
    public void Valid_row_is_parsed_and_defaults_applied()
    {
        var errors = new ErrorSink(100);
        Assert.True(CisRowParser.TryParse(new FakeRow(" SM1 ", "", "28.6", "77.2", "  Flat  1 ", "", "Apex", "", "12/05/2025", ""), Map, 2, errors, out var r));
        Assert.Equal("SM1", r.MeterId);
        Assert.Null(r.BuildingId);
        Assert.Equal("Flat 1", r.Address);
        Assert.Equal(CisRowParser.Unknown, r.Nsp);
        Assert.Equal(CisRowParser.Unknown, r.Zone);
        Assert.Equal(new DateOnly(2025, 5, 12), r.InstallationDate);
        Assert.Equal(0, errors.RejectedRows);
    }

    private static readonly SmartMeter.Domain.Spatial.BoundingBox India = new(68, 6, 97.5, 37.5);

    [Fact]
    public void Swapped_coordinates_are_corrected_and_zero_zero_is_unknown()
    {
        var errors = new ErrorSink(100);
        // latitude out of range -> obvious swap
        Assert.True(CisRowParser.TryParse(new FakeRow("SM1", "", "97.2", "28.6", "", "Jio", "Apex", "Z", "", ""), Map, 2, errors, out var r));
        Assert.Equal(28.6, r.Latitude);
        // both values legal globally, but only the swapped point is inside the service area
        Assert.True(CisRowParser.TryParse(new FakeRow("SM2", "", "77.2", "28.6", "", "Jio", "Apex", "Z", "", ""), Map, 3, errors, out r, India));
        Assert.Equal(28.6, r.Latitude);
        Assert.Equal(77.2, r.Longitude);
        Assert.True(CisRowParser.TryParse(new FakeRow("SM3", "", "0", "0", "", "Jio", "Apex", "Z", "", ""), Map, 4, errors, out r, India));
        Assert.Null(r.Latitude);
        Assert.Equal(0, errors.RejectedRows);
    }

    [Fact]
    public void Coordinates_outside_the_service_area_are_rejected()
    {
        var errors = new ErrorSink(100);
        Assert.False(CisRowParser.TryParse(new FakeRow("SM4", "", "51.5", "-0.12", "", "Jio", "Apex", "Z", "", ""), Map, 2, errors, out _, India));
        Assert.Contains("service area", errors.Errors[0].Message);
    }

    [Fact]
    public void Invalid_rows_are_rejected_with_one_rejection_per_row()
    {
        var errors = new ErrorSink(100);
        Assert.False(CisRowParser.TryParse(new FakeRow("", "", "abc", "77.2", "", "Jio", "Apex", "Z", "31/31/2025", ""), Map, 7, errors, out _));
        Assert.Equal(1, errors.RejectedRows);
        Assert.Equal(3, errors.Errors.Count);
        Assert.All(errors.Errors, e => Assert.Equal(7, e.RowNo));
    }

    [Fact]
    public void Excel_numeric_ids_lose_the_decimal_suffix() => Assert.Equal("123456", ValueParsers.NormalizeMeterId("123456.0"));
}

public class MdmParserTests
{
    private static readonly HeaderMap<MdmField> Map = HeaderMap<MdmField>.Resolve(["MeterId", "LogDate", "Status"], ColumnAliases.Mdm, ColumnAliases.MdmRequired, out _);

    [Theory]
    [InlineData("1", true)]
    [InlineData("Success", true)]
    [InlineData("YES", true)]
    [InlineData("0", false)]
    [InlineData("Fail", false)]
    [InlineData("not communicating", false)]
    public void Communication_status_values_are_understood(string raw, bool expected)
    {
        Assert.True(MdmRowParser.TryParse(new FakeRow("SM1", "2026-11-20", raw), Map, 2, new ErrorSink(10), out var r));
        Assert.Equal(expected, r.IsCommunicating);
    }

    [Fact]
    public void Excel_serial_and_text_dates_parse()
    {
        Assert.True(ValueParsers.TryParseDate("46346", ["yyyy-MM-dd"], out var d1));
        Assert.Equal(new DateOnly(2026, 11, 20), d1);
        Assert.True(ValueParsers.TryParseDate("20-Nov-2026", ["dd-MMM-yyyy"], out var d2));
        Assert.Equal(new DateOnly(2026, 11, 20), d2);
    }

    [Fact]
    public void Unknown_status_is_rejected()
    {
        var errors = new ErrorSink(10);
        Assert.False(MdmRowParser.TryParse(new FakeRow("SM1", "2026-11-20", "maybe"), Map, 2, errors, out _));
        Assert.Equal("IsCommunicating", errors.Errors[0].Column);
    }

    [Fact]
    public void Error_sink_caps_stored_errors_but_counts_all()
    {
        var sink = new ErrorSink(2);
        for (var i = 0; i < 5; i++) sink.Add(i, "c", "v", "m");
        Assert.Equal(5, sink.RejectedRows);
        Assert.Equal(2, sink.Errors.Count);
    }
}
