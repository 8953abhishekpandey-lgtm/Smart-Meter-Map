using System.Globalization;
using System.Text;

namespace SmartMeter.Api.Infrastructure;

/// <summary>Minimal RFC 4180 CSV writer streaming straight to the response body.</summary>
internal static class Csv
{
    public static async Task WriteAsync<T>(HttpResponse response, string fileName, string[] header, IAsyncEnumerable<T> rows,
        Func<T, IEnumerable<object?>> fields, CancellationToken ct)
    {
        response.ContentType = "text/csv; charset=utf-8";
        response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        await using var writer = new StreamWriter(response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16);
        await writer.WriteLineAsync(string.Join(',', header.Select(Escape)));
        var n = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            await writer.WriteLineAsync(string.Join(',', fields(row).Select(Format)));
            if (++n % 5000 == 0) await writer.FlushAsync(ct);
        }
        await writer.FlushAsync(ct);
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Escape(value.ToString() ?? ""),
    };

    private static string Escape(string s)
    {
        // neutralise spreadsheet formula injection, then quote if needed
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
