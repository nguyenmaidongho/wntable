using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WNTable.Controls;
using WNTable.Core;

namespace WNTable.Views;

/// <summary>SQL editor with its own connection (session state such as variables and temp tables survives between runs).</summary>
public partial class QueryTab : UserControl, IWorkspaceTab
{
    private const int MaxRows = 10_000;

    private readonly WorkspaceWindow _workspace;
    private readonly bool _isScratch;
    private DbConnection? _connection;
    private CancellationTokenSource? _cts;

    public QueryTab(WorkspaceWindow workspace, string title, string text, bool isScratch)
    {
        InitializeComponent();
        _workspace = workspace;
        _isScratch = isScratch;
        Title = title;
        SqlEditor.Setup(Editor);
        SqlEditor.AttachCompletion(Editor, () => _workspace.Schema);
        Editor.Text = text;
        Editor.CaretOffset = text.Length;
        Loaded += (_, _) => FocusMain();
        ShowMessages(["Ctrl+Enter: chạy câu lệnh tại con trỏ (hoặc đoạn bôi đen) · Ctrl+Shift+Enter / F5: chạy tất cả · Ctrl+Space: gợi ý"], error: false);
    }

    private DbDriver Driver => _workspace.Driver;
    public string Title { get; }
    public bool HasPendingChanges => false;
    private bool IsRunning => _cts is not null;

    public void FocusMain() => Dispatcher.BeginInvoke(() => Editor.TextArea.Focus());

    public bool HandleKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.Enter && mods == (ModifierKeys.Control | ModifierKeys.Shift) || key == Key.F5 && mods == ModifierKeys.None)
        {
            RunAll();
            return true;
        }
        if (key == Key.Enter && mods == ModifierKeys.Control)
        {
            RunCurrent();
            return true;
        }
        if (key == Key.Escape && IsRunning)
        {
            Stop_Click(this, e);
            return true;
        }
        return false;
    }

    public void OnDatabaseChanged()
    {
        _cts?.Cancel();
        CloseConnection();
    }

    public void OnClosed()
    {
        _cts?.Cancel();
        CloseConnection();
        if (_isScratch)
        {
            try { App.Store.SaveScratch(Driver.Info.Id, Editor.Text); } catch { /* best effort */ }
        }
    }

    private void CloseConnection()
    {
        var conn = _connection;
        _connection = null;
        if (conn is not null) _ = conn.DisposeAsync().AsTask();
    }

    private void Run_Click(object sender, RoutedEventArgs e) => RunCurrent();
    private void RunAll_Click(object sender, RoutedEventArgs e) => RunAll();
    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    public void RunAll() => _ = RunAsync(SqlSplitter.Split(Editor.Text, Driver.Kind).Select(s => s.Text).ToList());

    private void RunCurrent()
    {
        if (Editor.SelectionLength > 0)
        {
            _ = RunAsync(SqlSplitter.Split(Editor.SelectedText, Driver.Kind).Select(s => s.Text).ToList());
            return;
        }
        var segment = SqlSplitter.StatementAt(Editor.Text, Editor.CaretOffset, Driver.Kind);
        _ = RunAsync(segment is { } s ? [s.Text] : []);
    }

    private async Task RunAsync(List<string> statements)
    {
        if (IsRunning) return;
        if (statements.Count == 0)
        {
            StatusText.Text = "Không có câu lệnh nào để chạy.";
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        SetRunning(true);
        StatusText.Text = statements.Count == 1 ? "Đang chạy…" : $"Đang chạy {statements.Count} câu lệnh…";

        var tables = new List<DataTable>();
        var messages = new List<string>();
        var affected = 0;
        var truncated = false;
        var sw = Stopwatch.StartNew();
        string? error = null;
        for (var i = 0; i < statements.Count; i++)
        {
            try
            {
                if (_connection is null || _connection.State != ConnectionState.Open)
                {
                    CloseConnection();
                    _connection = await Driver.OpenAsync(cts.Token);
                }
                var result = await Driver.ExecuteAsync(_connection, [statements[i]], MaxRows, cts.Token);
                tables.AddRange(result.Tables);
                messages.AddRange(result.Messages);
                affected += result.RecordsAffected;
                truncated |= result.Truncated;
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                error = "Đã dừng truy vấn.";
                CloseConnection();
                break;
            }
            catch (Exception ex)
            {
                var where = statements.Count > 1 ? $"Câu lệnh {i + 1}/{statements.Count} lỗi:\n{Shorten(statements[i])}\n\n" : "";
                error = where + ex.Message;
                if (_connection?.State != ConnectionState.Open) CloseConnection();
                break;
            }
        }
        sw.Stop();

        ShowResults(tables, messages, error);
        var rows = tables.Sum(t => t.Rows.Count);
        var summary = tables.Count > 0 ? $"{rows:N0} dòng" : $"{affected:N0} dòng bị ảnh hưởng";
        if (truncated) summary += $" (chỉ hiển thị tối đa {MaxRows:N0} dòng mỗi kết quả)";
        StatusText.Text = error is null ? $"{summary} · {sw.ElapsedMilliseconds:N0} ms" : $"Lỗi · {sw.ElapsedMilliseconds:N0} ms";
        StatusText.Foreground = error is null ? (Brush)FindResource("MutedBrush") : (Brush)FindResource("DangerBrush");

        _cts = null;
        SetRunning(false);
        cts.Dispose();
        if (statements.Any(IsSchemaChange)) await _workspace.ReloadTablesAsync();
    }

    private static bool IsSchemaChange(string sql) =>
        System.Text.RegularExpressions.Regex.IsMatch(sql, @"^\s*(CREATE|DROP|ALTER|RENAME)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string Shorten(string sql) => sql.Length > 300 ? sql[..300] + "…" : sql;

    private void SetRunning(bool running)
    {
        RunButton.IsEnabled = RunAllButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        Cursor = running ? Cursors.AppStarting : null;
    }

    private void ShowResults(List<DataTable> tables, List<string> messages, string? error)
    {
        Results.Items.Clear();
        for (var i = 0; i < tables.Count; i++)
        {
            var grid = new DataGrid();
            GridBinder.Configure(grid);
            GridBinder.Bind(grid, tables[i], readOnly: true);
            var header = tables.Count == 1 ? $"Kết quả ({tables[i].Rows.Count:N0})" : $"Kết quả {i + 1} ({tables[i].Rows.Count:N0})";
            Results.Items.Add(new TabItem { Header = header, Content = grid });
        }
        var lines = new List<string>(messages);
        if (error is not null) lines.Add(error);
        if (lines.Count > 0 || tables.Count == 0)
        {
            if (lines.Count == 0) lines.Add("Xong.");
            AddMessagesTab(lines, error is not null);
        }
        Results.SelectedIndex = error is not null ? Results.Items.Count - 1 : 0;
    }

    private void ShowMessages(List<string> lines, bool error)
    {
        Results.Items.Clear();
        AddMessagesTab(lines, error);
        Results.SelectedIndex = 0;
    }

    private void AddMessagesTab(List<string> lines, bool error)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("TextArea"),
            Text = string.Join(Environment.NewLine, lines),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = (FontFamily)FindResource("CodeFont"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8),
        };
        if (error) box.Foreground = (Brush)FindResource("DangerBrush");
        Results.Items.Add(new TabItem { Header = error ? "Lỗi" : "Thông báo", Content = box });
    }
}
