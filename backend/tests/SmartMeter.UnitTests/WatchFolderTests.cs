using SmartMeter.Application;
using SmartMeter.Application.Ingestion;
using SmartMeter.Application.Ingestion.Parsing;

namespace SmartMeter.UnitTests;

public class IngestionFileNameTests
{
    [Theory]
    [InlineData("cis_master_nov.xlsx", IngestionKind.Cis)]
    [InlineData("Customer MASTER 2026.xlsx", IngestionKind.Cis)]
    [InlineData("mdm_2026-11-20.xlsx", IngestionKind.Mdm)]
    [InlineData("comm_report_20-11-2026.csv", IngestionKind.Mdm)]
    [InlineData("daily_log 20112026.xlsx", IngestionKind.Mdm)]
    [InlineData("mdm/2026-11/report.xlsx", IngestionKind.Mdm)]          // folder decides
    [InlineData("cis/meters.xlsx", IngestionKind.Cis)]
    [InlineData("mdm/master_copy.xlsx", IngestionKind.Mdm)]             // nearest folder wins over the file name
    public void Kind_comes_from_nearest_folder_else_file_name(string path, IngestionKind expected) =>
        Assert.Equal(expected, IngestionFileNames.KindOf(path));

    [Theory]
    [InlineData("notes.xlsx")]
    [InlineData("readings/export.csv")]
    public void Unrecognised_names_have_no_kind(string path) => Assert.Null(IngestionFileNames.KindOf(path));

    [Theory]
    [InlineData("mdm_2026-11-20.xlsx", 2026, 11, 20)]
    [InlineData("mdm_2026_11_05.xlsx", 2026, 11, 5)]
    [InlineData("comm 20-11-2026.xlsx", 2026, 11, 20)]
    [InlineData("comm 5.11.2026.xlsx", 2026, 11, 5)]
    [InlineData("mdm20261120.csv", 2026, 11, 20)]
    [InlineData("mdm_20112026.csv", 2026, 11, 20)]                     // ddMMyyyy when yyyyMMdd is impossible
    [InlineData("MDM Report 20 Nov 2026.xlsx", 2026, 11, 20)]
    [InlineData("mdm_20November2026.xlsx", 2026, 11, 20)]
    [InlineData("mdm_20-nov-26.xlsx", 2026, 11, 20)]
    [InlineData("mdm/2026-11-20/report.xlsx", 2026, 11, 20)]            // date in the folder name
    public void Day_comes_from_file_name_else_folder(string path, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), IngestionFileNames.DateOf(path));

    [Theory]
    [InlineData("mdm_latest.xlsx")]
    [InlineData("mdm_2026-13-01.xlsx")]                                 // no month 13
    [InlineData("mdm_2026-02-30.xlsx")]                                 // no 30 February
    [InlineData("meter 123456789.xlsx")]                                // 9 digits is an id, not a date
    public void No_day_when_the_name_has_no_valid_date(string path) => Assert.Null(IngestionFileNames.DateOf(path));
}

public class MdmFileDateTests
{
    [Fact]
    public void Sheet_without_LogDate_uses_the_day_from_the_file_name()
    {
        var map = HeaderMap<MdmField>.Resolve(["Meter No", "Comm Status"], ColumnAliases.Mdm, ColumnAliases.MdmRequiredWithFileDate, out var missing);
        Assert.Empty(missing);
        var day = new DateOnly(2026, 11, 20);
        Assert.True(MdmRowParser.TryParse(new FakeRow("SM1", "Non-Communicating"), map, 2, new ErrorSink(10), out var r, day));
        Assert.Equal(day, r.LogDate);
        Assert.False(r.IsCommunicating);
    }

    [Fact]
    public void A_date_column_still_wins_and_an_empty_cell_falls_back_to_the_file_day()
    {
        var map = HeaderMap<MdmField>.Resolve(["MeterId", "LogDate", "IsCommunicating"], ColumnAliases.Mdm, ColumnAliases.MdmRequired, out _);
        var fileDay = new DateOnly(2026, 11, 20);
        Assert.True(MdmRowParser.TryParse(new FakeRow("SM1", "2026-11-19", "1"), map, 2, new ErrorSink(10), out var a, fileDay));
        Assert.Equal(new DateOnly(2026, 11, 19), a.LogDate);
        Assert.True(MdmRowParser.TryParse(new FakeRow("SM2", "", "1"), map, 3, new ErrorSink(10), out var b, fileDay));
        Assert.Equal(fileDay, b.LogDate);
    }

    [Fact]
    public void Without_a_file_day_a_missing_date_is_a_row_error()
    {
        var map = HeaderMap<MdmField>.Resolve(["MeterId", "LogDate", "IsCommunicating"], ColumnAliases.Mdm, ColumnAliases.MdmRequired, out _);
        var errors = new ErrorSink(10);
        Assert.False(MdmRowParser.TryParse(new FakeRow("SM1", "", "1"), map, 2, errors, out _));
        Assert.Contains("file name", errors.Errors.Single().Message);
    }
}
