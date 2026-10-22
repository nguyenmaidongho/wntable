using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace WNTable.Controls;

/// <summary>Tables and columns offered by autocomplete.</summary>
public sealed record SchemaInfo(IReadOnlyList<string> Tables, IReadOnlyDictionary<string, List<string>> Columns);

public static class SqlEditor
{
    private static readonly Lazy<IHighlightingDefinition> Highlighting = new(LoadHighlighting);

    public static readonly string[] Keywords =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "NULL", "IS", "IN", "LIKE", "BETWEEN", "EXISTS",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "CREATE TABLE", "ALTER TABLE", "DROP TABLE",
        "TRUNCATE TABLE", "CREATE INDEX", "JOIN", "LEFT JOIN", "RIGHT JOIN", "INNER JOIN", "ON", "AS",
        "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET", "DISTINCT", "UNION", "UNION ALL", "CASE", "WHEN",
        "THEN", "ELSE", "END", "ASC", "DESC", "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "CAST",
        "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "DEFAULT", "SHOW TABLES", "DESCRIBE", "EXPLAIN",
        "BEGIN", "COMMIT", "ROLLBACK", "WITH", "RETURNING", "USE",
    ];

    public static void Setup(TextEditor editor, bool readOnly = false)
    {
        editor.SyntaxHighlighting = Highlighting.Value;
        editor.FontFamily = (FontFamily)Application.Current.FindResource("CodeFont");
        editor.FontSize = 13;
        editor.ShowLineNumbers = true;
        editor.IsReadOnly = readOnly;
        editor.WordWrap = readOnly;
        editor.Padding = new Thickness(6, 6, 0, 0);
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = !readOnly;
        if (App.IsDark)
        {
            editor.Background = (Brush)Application.Current.FindResource("BgBrush");
            editor.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
            editor.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x81));
            editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x26, 0x4F, 0x78));
            editor.TextArea.SelectionForeground = null;
        }
        else
        {
            editor.Background = (Brush)Application.Current.FindResource("BgBrush");
            editor.Foreground = Brushes.Black;
            editor.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB5));
            editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        }
        editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        editor.TextArea.Caret.CaretBrush = editor.Foreground;
    }

    private static IHighlightingDefinition LoadHighlighting()
    {
        using var stream = typeof(SqlEditor).Assembly.GetManifestResourceStream("WNTable.Resources.SQL.xshd")!;
        using var reader = XmlReader.Create(stream);
        var def = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        var palette = App.IsDark
            ? new Dictionary<string, string>
            {
                ["Comment"] = "#6A9955", ["String"] = "#CE9178", ["Identifier"] = "#9CDCFE", ["Number"] = "#B5CEA8",
                ["Keyword"] = "#569CD6", ["Function"] = "#DCDCAA", ["Variable"] = "#C586C0",
            }
            : new Dictionary<string, string>
            {
                ["Comment"] = "#008000", ["String"] = "#A31515", ["Identifier"] = "#001080", ["Number"] = "#098658",
                ["Keyword"] = "#0000FF", ["Function"] = "#795E26", ["Variable"] = "#AF00DB",
            };
        foreach (var (name, hex) in palette)
        {
            var color = def.GetNamedColor(name);
            if (color is not null) color.Foreground = new SimpleHighlightingBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        return def;
    }

    /// <summary>Autocomplete for keywords, table names and (after "table.") column names.</summary>
    public static void AttachCompletion(TextEditor editor, Func<SchemaInfo?> schema)
    {
        CompletionWindow? window = null;

        void Open(int startOffset, IEnumerable<ICompletionData> items)
        {
            window?.Close();
            var list = items.ToList();
            if (list.Count == 0) return;
            window = new CompletionWindow(editor.TextArea)
            {
                StartOffset = startOffset,
                CloseWhenCaretAtBeginning = true,
                Width = 320,
            };
            window.CompletionList.IsFiltering = true;
            foreach (var item in list) window.CompletionList.CompletionData.Add(item);
            window.Closed += (_, _) => window = null;
            window.Show();
            var typed = editor.Document.GetText(startOffset, editor.CaretOffset - startOffset);
            if (typed.Length > 0) window.CompletionList.SelectItem(typed);
        }

        IEnumerable<ICompletionData> General(SchemaInfo? s)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (s is not null)
            {
                foreach (var t in s.Tables)
                {
                    if (seen.Add(t)) yield return new Item(t, "bảng", 2);
                }
                foreach (var col in s.Columns.Values.SelectMany(c => c))
                {
                    if (seen.Add(col)) yield return new Item(col, "cột", 1);
                }
            }
            foreach (var k in Keywords)
            {
                if (seen.Add(k)) yield return new Item(k, "từ khoá", 0);
            }
        }

        editor.TextArea.TextEntering += (_, e) =>
        {
            if (window is not null && e.Text.Length > 0 && !IsWordChar(e.Text[0])) window.Close();
        };

        editor.TextArea.TextEntered += (_, e) =>
        {
            if (e.Text.Length != 1) return;
            var c = e.Text[0];
            var caret = editor.CaretOffset;
            if (c == '.')
            {
                var name = WordBefore(editor.Document, caret - 1);
                var s = schema();
                if (s is not null && name.Length > 0 && s.Columns.TryGetValue(name, out var cols))
                {
                    Open(caret, cols.Select(col => new Item(col, "cột", 1)));
                }
                return;
            }
            if (window is null && IsWordChar(c) && !char.IsDigit(c))
            {
                var start = WordStart(editor.Document, caret);
                if (caret - start == 1) Open(start, General(schema()));
            }
            else if (window is not null && window.CompletionList.ListBox.Items.Count == 0)
            {
                window.Close();
            }
        };

        editor.TextArea.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Open(WordStart(editor.Document, editor.CaretOffset), General(schema()));
                e.Handled = true;
            }
        };
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static int WordStart(TextDocument doc, int offset)
    {
        var i = offset;
        while (i > 0 && IsWordChar(doc.GetCharAt(i - 1))) i--;
        return i;
    }

    /// <summary>Identifier ending at <paramref name="end"/>, without quotes (users, `users`, "users", [users]).</summary>
    private static string WordBefore(TextDocument doc, int end)
    {
        var i = end;
        if (i > 0 && doc.GetCharAt(i - 1) is '`' or '"' or ']')
        {
            var close = doc.GetCharAt(i - 1);
            var open = close == ']' ? '[' : close;
            var j = i - 2;
            while (j >= 0 && doc.GetCharAt(j) != open) j--;
            return j < 0 ? "" : doc.GetText(j + 1, i - j - 2);
        }
        var start = WordStart(doc, i);
        return doc.GetText(start, i - start);
    }

    private sealed class Item(string text, string kind, double priority) : ICompletionData
    {
        public System.Windows.Media.ImageSource? Image => null;
        public string Text => text;
        public object Content => text;
        public object Description => kind;
        public double Priority => priority;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, text);
    }
}
