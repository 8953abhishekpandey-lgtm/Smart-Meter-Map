using System.Text.RegularExpressions;

namespace MeterMap.Data;

public static class Sql
{
    static readonly Regex Safe = new(@"^[\w $#@-]+$", RegexOptions.Compiled);

    public static string Table(string name)
    {
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(p => p.Trim('[', ']')).ToArray();
        if (parts.Length == 0 || parts.Any(p => !Safe.IsMatch(p)))
            throw new ArgumentException($"Table name sahi nahi hai: {name}");
        return string.Join('.', parts.Select(p => $"[{p}]"));
    }

    public static string Col(string name) => "[" + name.Replace("]", "]]") + "]";
}