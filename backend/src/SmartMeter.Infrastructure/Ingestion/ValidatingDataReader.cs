using System.Collections;
using System.Data.Common;
using SmartMeter.Application.Ingestion.Parsing;

namespace SmartMeter.Infrastructure.Ingestion;

/// <summary>
/// Streams only VALID rows to SqlBulkCopy: each Read() pulls rows from the Excel/CSV reader,
/// validates them and records rejected ones in the <see cref="ErrorSink"/>. Constant memory,
/// so a 15M-row file is processed with the same footprint as a 15-row file.
/// RowNo = 1-based line number (line 1 = header), continuing across Excel worksheets.
/// </summary>
internal sealed class ValidatingDataReader<TRecord> : DbDataReader
{
    public delegate bool Parser(IRowSource row, long rowNo, ErrorSink errors, out TRecord record);

    private readonly TabularFile _file;
    private readonly Func<IReadOnlyList<string>, bool> _onSheet;
    private readonly Parser _parse;
    private readonly Func<TRecord, long, int, object?> _field;
    private readonly string[] _names;
    private readonly Type[] _types;
    private readonly ErrorSink _errors;
    private readonly Action<long, long>? _progress;
    private readonly int _progressEvery;
    private TRecord _current = default!;
    private long _currentRowNo;
    private long _lineNo = 1;

    public ValidatingDataReader(TabularFile file, Func<IReadOnlyList<string>, bool> onSheet, Parser parse,
        Func<TRecord, long, int, object?> field, string[] names, Type[] types, ErrorSink errors,
        Action<long, long>? progress, int progressEvery = 25_000)
    {
        _file = file;
        _onSheet = onSheet;
        _parse = parse;
        _field = field;
        _names = names;
        _types = types;
        _errors = errors;
        _progress = progress;
        _progressEvery = progressEvery;
    }

    public long RowsRead { get; private set; }
    public long RowsValid { get; private set; }

    public override bool Read()
    {
        while (true)
        {
            while (_file.Reader.Read())
            {
                _lineNo++;
                if (IsBlank()) continue;
                RowsRead++;
                if (RowsRead % _progressEvery == 0) _progress?.Invoke(RowsRead, RowsValid);
                if (_parse(_file.Rows, _lineNo, _errors, out var rec))
                {
                    _current = rec;
                    _currentRowNo = _lineNo;
                    RowsValid++;
                    return true;
                }
            }
            // Excel workbooks larger than 1,048,576 rows are split across worksheets: continue with the next one.
            if (!_file.IsExcel || !_file.Reader.NextResult()) return false;
            _lineNo++; // header line of the next worksheet
            if (!_onSheet(_file.Headers())) return false;
        }
    }

    private bool IsBlank()
    {
        for (var i = 0; i < _file.Rows.FieldCount; i++)
            if (!_file.Rows.IsEmpty(i)) return false;
        return true;
    }

    public override int FieldCount => _names.Length;
    public override object GetValue(int ordinal) => _field(_current, _currentRowNo, ordinal) ?? DBNull.Value;
    public override bool IsDBNull(int ordinal) => _field(_current, _currentRowNo, ordinal) is null;
    public override string GetName(int ordinal) => _names[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(_names, name);
    public override Type GetFieldType(int ordinal) => _types[ordinal];
    public override string GetDataTypeName(int ordinal) => _types[ordinal].Name;
    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override bool HasRows => true;
    public override bool IsClosed => false;
    public override int RecordsAffected => -1;
    public override int Depth => 0;
    public override bool NextResult() => false;
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
}
