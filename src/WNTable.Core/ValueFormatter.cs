using System.Collections;
using System.Globalization;
using System.Text;

namespace WNTable.Core;

/// <summary>
/// One place that turns cell values into text, so the grid, copy/paste and change
/// detection all agree (an int 5 and an edited string "5" are the same value).
/// </summary>
public static class ValueFormatter
{
    public const string NullText = "NULL";

    public static string Format(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return NullText;
            case string s:
                return s;
            case DateTime dt:
                return dt.TimeOfDay == TimeSpan.Zero && dt.Kind != DateTimeKind.Utc
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString(dt.Millisecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
            case DateOnly d:
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeOnly t:
                return t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            case bool b:
                return b ? "1" : "0";
            case byte[] bytes:
                return bytes.Length <= 32
                    ? "0x" + Convert.ToHexString(bytes)
                    : $"(BLOB {bytes.Length:N0} bytes)";
            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture);
            case IEnumerable e:
                var sb = new StringBuilder("{");
                foreach (var item in e)
                {
                    if (sb.Length > 1) sb.Append(',');
                    sb.Append(Format(item));
                }
                return sb.Append('}').ToString();
            default:
                return value.ToString() ?? "";
        }
    }

    /// <summary>Short single-line version for grid cells (long text/JSON is cut).</summary>
    public static string FormatCell(object? value)
    {
        var s = Format(value);
        if (s.Length > 300) s = s[..300] + "…";
        return s.IndexOfAny(['\r', '\n']) >= 0 ? s.Replace("\r\n", " ⏎ ").Replace('\n', ' ').Replace('\r', ' ') : s;
    }

    public static string SqlLiteral(object? value)
    {
        return value switch
        {
            null or DBNull => "NULL",
            bool b => b ? "1" : "0",
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            _ => "'" + Format(value).Replace("'", "''") + "'",
        };
    }
}
