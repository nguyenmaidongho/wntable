using System.Text.RegularExpressions;

namespace WNTable.Core;

public readonly record struct SqlSegment(int Start, int End, string Text);

/// <summary>
/// Splits a script into statements. Understands quotes, comments, PostgreSQL dollar
/// quoting, MySQL DELIMITER and SQL Server GO batches.
/// </summary>
public static partial class SqlSplitter
{
    public static List<SqlSegment> Split(string sql, DbKind kind)
    {
        return kind == DbKind.SqlServer ? SplitGoBatches(sql) : SplitStatements(sql, kind);
    }

    /// <summary>The statement under the caret (or the one just before it, after a trailing ';').</summary>
    public static SqlSegment? StatementAt(string sql, int offset, DbKind kind)
    {
        var segments = Split(sql, kind);
        if (segments.Count == 0) return null;
        foreach (var s in segments)
        {
            if (offset >= s.Start && offset <= s.End) return s;
        }
        SqlSegment? before = null;
        foreach (var s in segments)
        {
            if (s.End <= offset) before = s;
        }
        return before ?? segments[0];
    }

    private static List<SqlSegment> SplitStatements(string sql, DbKind kind)
    {
        var result = new List<SqlSegment>();
        var delimiter = ";";
        var start = 0;
        var i = 0;
        var n = sql.Length;
        var backslashEscapes = kind == DbKind.MySql;

        while (i < n)
        {
            var c = sql[i];

            if (kind == DbKind.MySql && IsLineStart(sql, i))
            {
                var m = DelimiterRegex().Match(sql, i);
                if (m.Success && m.Index == SkipSpaces(sql, i))
                {
                    Add(result, sql, start, i);
                    delimiter = m.Groups[1].Value;
                    i = m.Index + m.Length;
                    start = i;
                    continue;
                }
            }

            if (c == '\'' || c == '"' || c == '`')
            {
                i = SkipQuoted(sql, i, c, backslashEscapes && c != '`');
                continue;
            }
            if (c == '-' && i + 1 < n && sql[i + 1] == '-' || c == '#' && kind == DbKind.MySql)
            {
                while (i < n && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? n : close + 2;
                continue;
            }
            if (c == '$' && kind == DbKind.PostgreSql)
            {
                var m = DollarTagRegex().Match(sql, i);
                if (m.Success && m.Index == i)
                {
                    var close = sql.IndexOf(m.Value, i + m.Length, StringComparison.Ordinal);
                    i = close < 0 ? n : close + m.Length;
                    continue;
                }
            }
            if (string.CompareOrdinal(sql, i, delimiter, 0, delimiter.Length) == 0)
            {
                Add(result, sql, start, i);
                i += delimiter.Length;
                start = i;
                continue;
            }
            i++;
        }
        Add(result, sql, start, n);
        return result;
    }

    private static List<SqlSegment> SplitGoBatches(string sql)
    {
        var result = new List<SqlSegment>();
        var start = 0;
        foreach (Match m in GoRegex().Matches(sql))
        {
            Add(result, sql, start, m.Index);
            start = m.Index + m.Length;
        }
        Add(result, sql, start, sql.Length);
        return result;
    }

    private static int SkipQuoted(string sql, int i, char quote, bool backslashEscapes)
    {
        i++;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (backslashEscapes && c == '\\') { i += 2; continue; }
            if (c == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    private static void Add(List<SqlSegment> result, string sql, int start, int end)
    {
        var text = sql[start..end];
        if (!HasCode(text)) return;
        // Trim surrounding whitespace but keep offsets pointing into the original text.
        var lead = text.Length - text.TrimStart().Length;
        var trail = text.Length - text.TrimEnd().Length;
        result.Add(new SqlSegment(start + lead, end - trail, text.Trim()));
    }

    /// <summary>False when the text is only whitespace and comments.</summary>
    private static bool HasCode(string text)
    {
        var stripped = CommentRegex().Replace(text, "");
        return !string.IsNullOrWhiteSpace(stripped);
    }

    private static bool IsLineStart(string sql, int i)
    {
        var j = i - 1;
        while (j >= 0 && (sql[j] == ' ' || sql[j] == '\t')) j--;
        return j < 0 || sql[j] == '\n';
    }

    private static int SkipSpaces(string sql, int i)
    {
        while (i < sql.Length && (sql[i] == ' ' || sql[i] == '\t')) i++;
        return i;
    }

    [GeneratedRegex(@"[ \t]*DELIMITER[ \t]+(\S+)[ \t]*\r?(\n|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DelimiterRegex();

    [GeneratedRegex(@"\$[A-Za-z_]*\$")]
    private static partial Regex DollarTagRegex();

    [GeneratedRegex(@"^[ \t]*GO[ \t]*(\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GoRegex();

    [GeneratedRegex(@"--[^\n]*|#[^\n]*|/\*.*?(\*/|$)", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();
}
