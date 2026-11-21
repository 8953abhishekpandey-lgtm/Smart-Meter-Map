using System.Data.Common;
using System.Globalization;
using Sylvan.Data.Csv;
using Sylvan.Data.Excel;
using SmartMeter.Application.Ingestion.Parsing;

namespace SmartMeter.Infrastructure.Ingestion;

/// <summary>Opens CSV (Sylvan.Data.Csv) or Excel (Sylvan.Data.Excel) as a forward-only, low-memory reader.</summary>
internal sealed class TabularFile : IAsyncDisposable
{
    private readonly Stream? _stream;

    private TabularFile(DbDataReader reader, IRowSource rows, Stream? stream, bool isExcel)
    {
        Reader = reader;
        Rows = rows;
        _stream = stream;
        IsExcel = isExcel;
    }

    public DbDataReader Reader { get; }
    public IRowSource Rows { get; }
    public bool IsExcel { get; }

    /// <summary>Best-effort percentage of the file consumed (CSV: byte position; Excel: row count when known).</summary>
    public double? ProgressPct(long rowsRead)
    {
        if (_stream is { CanSeek: true, Length: > 0 } s) return Math.Min(100, 100.0 * s.Position / s.Length);
        if (Reader is ExcelDataReader x && x.RowCount > 0) return Math.Min(100, 100.0 * rowsRead / x.RowCount);
        return null;
    }

    public IReadOnlyList<string> Headers()
    {
        var names = new string[Reader.FieldCount];
        for (var i = 0; i < names.Length; i++) names[i] = Reader.GetName(i) ?? "";
        return names;
    }

    public static async Task<TabularFile> OpenAsync(string path, string fileName, IReadOnlyList<string> dateFormats, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".xlsx" or ".xlsb" or ".xls")
        {
            // Uploads are stored under a job id, so the workbook type comes from the ORIGINAL file name.
            var type = ExcelDataReader.GetWorkbookType(fileName);
            var xs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
            var excel = await ExcelDataReader.CreateAsync(xs, type,
                new ExcelDataReaderOptions { FormulaErrorHandling = FormulaErrorHandling.Null, OwnsStream = true }, ct);
            return new TabularFile(excel, new ExcelRowSource(excel, dateFormats), null, isExcel: true);
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var text = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var csv = await CsvDataReader.CreateAsync(text, new CsvDataReaderOptions { HasHeaders = true, BufferSize = 1 << 18 }, ct);
        return new TabularFile(csv, new CsvRowSource(csv, dateFormats), stream, isExcel: false);
    }

    public async ValueTask DisposeAsync()
    {
        await Reader.DisposeAsync();
        if (_stream is not null) await _stream.DisposeAsync();
    }
}

internal sealed class CsvRowSource(CsvDataReader csv, IReadOnlyList<string> dateFormats) : IRowSource
{
    public int FieldCount => csv.FieldCount;
    public bool IsEmpty(int ordinal) => ordinal >= csv.RowFieldCount || string.IsNullOrWhiteSpace(csv.GetString(ordinal));
    public string? GetString(int ordinal) => ordinal >= csv.RowFieldCount ? null : csv.GetString(ordinal);
    public bool TryGetDouble(int ordinal, out double value) => ValueParsers.TryParseDouble(GetString(ordinal), out value);
    public bool TryGetDate(int ordinal, out DateOnly value) => ValueParsers.TryParseDate(GetString(ordinal), dateFormats, out value);
    public bool TryGetBoolean(int ordinal, out bool value) => ValueParsers.TryParseBool(GetString(ordinal), out value);
}

/// <summary>Typed Excel access: numeric/date/boolean cells are read natively, text cells are parsed.</summary>
internal sealed class ExcelRowSource(ExcelDataReader excel, IReadOnlyList<string> dateFormats) : IRowSource
{
    public int FieldCount => excel.FieldCount;

    private ExcelDataType Type(int ordinal) => ordinal >= excel.RowFieldCount ? ExcelDataType.Null : excel.GetExcelDataType(ordinal);

    public bool IsEmpty(int ordinal) => Type(ordinal) switch
    {
        ExcelDataType.Null or ExcelDataType.Error => true,
        ExcelDataType.String => string.IsNullOrWhiteSpace(excel.GetString(ordinal)),
        _ => false,
    };

    public string? GetString(int ordinal) => Type(ordinal) switch
    {
        ExcelDataType.Null or ExcelDataType.Error => null,
        ExcelDataType.Numeric => FormatNumber(excel.GetDouble(ordinal)),
        ExcelDataType.DateTime => excel.GetDateTime(ordinal).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ExcelDataType.Boolean => excel.GetBoolean(ordinal) ? "true" : "false",
        _ => excel.GetString(ordinal),
    };

    public bool TryGetDouble(int ordinal, out double value)
    {
        if (Type(ordinal) == ExcelDataType.Numeric) { value = excel.GetDouble(ordinal); return double.IsFinite(value); }
        return ValueParsers.TryParseDouble(GetString(ordinal), out value);
    }

    public bool TryGetDate(int ordinal, out DateOnly value)
    {
        switch (Type(ordinal))
        {
            case ExcelDataType.DateTime:
                value = DateOnly.FromDateTime(excel.GetDateTime(ordinal));
                return ValueParsers.Plausible(value);
            case ExcelDataType.Numeric:
                var serial = excel.GetDouble(ordinal);
                value = default;
                if (serial is < 20000 or > 80000) return false;
                value = DateOnly.FromDateTime(DateTime.FromOADate(serial));
                return true;
            default:
                return ValueParsers.TryParseDate(GetString(ordinal), dateFormats, out value);
        }
    }

    public bool TryGetBoolean(int ordinal, out bool value)
    {
        switch (Type(ordinal))
        {
            case ExcelDataType.Boolean: value = excel.GetBoolean(ordinal); return true;
            case ExcelDataType.Numeric:
                var d = excel.GetDouble(ordinal);
                value = d != 0;
                return d is 0 or 1;
            default: return ValueParsers.TryParseBool(GetString(ordinal), out value);
        }
    }

    /// <summary>Numeric meter ids must not come out as "1.23E+11".</summary>
    private static string FormatNumber(double v) =>
        Math.Abs(v) < 1e15 && v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("R", CultureInfo.InvariantCulture);
}
