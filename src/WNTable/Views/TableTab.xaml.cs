using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WNTable.Controls;
using WNTable.Core;

namespace WNTable.Views;

/// <summary>Browse and edit one table: paged rows, WHERE filter, server-side sort, structure view.</summary>
public partial class TableTab : UserControl, IWorkspaceTab
{
    private static readonly int[] PageSizes = [100, 300, 1000, 5000];

    private readonly WorkspaceWindow _workspace;
    private DbDriver Driver => _workspace.Driver;
    private IReadOnlyList<ColumnInfo>? _columns;
    private DataTable? _data;
    private CancellationTokenSource? _cts;
    private int _page;
    private int _pageSize = 300;
    private long? _total;
    private string? _sortCaption;
    private ListSortDirection? _sortDirection;
    private string _appliedFilter = "";
    private bool _editable;
    private bool _structureLoaded;
    private bool _pinned;
    private bool _dirtyQueued;

    public TableTab(WorkspaceWindow workspace, TableRef table, bool structure)
    {
        InitializeComponent();
        _workspace = workspace;
        Table = table;
        GridBinder.Configure(Grid);
        SqlEditor.Setup(DdlEditor, readOnly: true);
        PageSizeBox.ItemsSource = PageSizes;
        PageSizeBox.SelectedItem = _pageSize;
        if (structure) StructureMode.IsChecked = true;
        Loaded += OnFirstLoaded;
    }

    public TableRef Table { get; }
    public string Title => Table.Display;
    public bool HasPendingChanges => _data is not null && ChangeBuilder.HasChanges(_data);

    /// <summary>Unpinned tabs get replaced when another table is clicked.</summary>
    public bool IsPinned
    {
        get => _pinned;
        set
        {
            if (_pinned == value) return;
            _pinned = value;
            _workspace.UpdateTabHeader(this);
        }
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        _workspace.UpdateTabHeader(this);
        await LoadAsync();
        if (StructureMode.IsChecked == true) await LoadStructureAsync();
    }

    public void FocusMain()
    {
        if (DataMode.IsChecked == true) Grid.Focus();
    }

    public void OnClosed() => _cts?.Cancel();

    public void ShowStructure() => StructureMode.IsChecked = true;

    public bool HandleKey(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ctrl && key == Key.S) { Save_Click(this, e); return true; }
        if (key == Key.F5 || ctrl && key == Key.R) { Reload_Click(this, e); return true; }
        if (ctrl && key == Key.F) { ShowFilter(); return true; }
        if (ctrl && key == Key.I) { AddRow_Click(this, e); return true; }
        return false;
    }

    // ---------- loading ----------

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        HideError();
        StatusText.Text = "Đang tải…";
        Cursor = Cursors.AppStarting;
        try
        {
            _columns ??= await Driver.GetColumnsAsync(Table, ct);
            _editable = !Table.IsView && _columns.Any(c => c.IsPrimaryKey);

            var orderBy = _sortCaption is null ? null : $"{Driver.Quote(_sortCaption)} {(_sortDirection == ListSortDirection.Descending ? "DESC" : "ASC")}";
            var where = _appliedFilter.Length > 0 ? _appliedFilter : null;
            var sql = Driver.BuildSelect(Table, where, orderBy, _pageSize, _page * _pageSize);

            var sw = Stopwatch.StartNew();
            ExecuteResult result;
            await using (var conn = await Driver.OpenAsync(ct))
            {
                result = await Driver.ExecuteAsync(conn, [sql], _pageSize, ct);
            }
            if (ct.IsCancellationRequested) return;

            if (_data is not null) Detach(_data);
            _data = result.Tables.Single();
            Attach(_data);
            GridBinder.Bind(Grid, _data, readOnly: !_editable);
            foreach (var col in Grid.Columns.OfType<CellColumn>())
            {
                col.SortDirection = col.Caption == _sortCaption ? _sortDirection : null;
            }
            UpdateEditUi();
            UpdateDirty();
            var elapsed = sw.ElapsedMilliseconds;
            UpdateStatus(elapsed);
            _ = CountAsync(where, elapsed, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            StatusText.Text = "";
        }
        finally
        {
            if (_cts == cts) Cursor = null;
        }
    }

    private async Task CountAsync(string? where, long elapsed, CancellationToken ct)
    {
        _total = null;
        try
        {
            await using var conn = await Driver.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Driver.BuildCount(Table, where);
            cmd.CommandTimeout = 30;
            var value = await cmd.ExecuteScalarAsync(ct);
            if (ct.IsCancellationRequested) return;
            _total = Convert.ToInt64(value);
            UpdateStatus(elapsed);
        }
        catch
        {
            // Counting is best-effort (huge tables, timeouts); paging still works without it.
        }
    }

    private void UpdateStatus(long elapsedMs)
    {
        var rows = _data?.Rows.Count ?? 0;
        var from = rows == 0 ? 0 : _page * _pageSize + 1;
        var to = _page * _pageSize + rows;
        var total = _total is { } t ? $" / {t:N0}" : "";
        var readOnlyNote = _editable ? "" : Table.IsView ? " · chỉ đọc (view)" : " · chỉ đọc (bảng không có khoá chính)";
        StatusText.Text = $"Dòng {from:N0}–{to:N0}{total} · {elapsedMs} ms{readOnlyNote}";

        var pages = _total is { } tt ? Math.Max(1, (int)Math.Ceiling(tt / (double)_pageSize)) : (int?)null;
        PageText.Text = pages is null ? $"Trang {_page + 1}" : $"{_page + 1} / {pages:N0}";
        PrevButton.IsEnabled = _page > 0;
        NextButton.IsEnabled = pages is null ? rows >= _pageSize : _page + 1 < pages;
    }

    private void UpdateEditUi()
    {
        EditButtons.Visibility = _editable && DataMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AddRowItem.IsEnabled = DeleteRowItem.IsEnabled = SetNullItem.IsEnabled = _editable;
    }

    // ---------- change tracking ----------

    private void Attach(DataTable t)
    {
        t.RowChanged += OnRowChanged;
        t.RowDeleted += OnRowChanged;
    }

    private void Detach(DataTable t)
    {
        t.RowChanged -= OnRowChanged;
        t.RowDeleted -= OnRowChanged;
    }

    private void OnRowChanged(object sender, DataRowChangeEventArgs e)
    {
        // AcceptChanges/RejectChanges raise one event per row: recount once.
        if (_dirtyQueued) return;
        _dirtyQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _dirtyQueued = false;
            UpdateDirty();
        });
    }

    private void UpdateDirty()
    {
        var n = _data is null ? 0 : ChangeBuilder.CountChanges(_data);
        SaveButton.IsEnabled = true;
        ChangesPanel.Visibility = _editable && n > 0 && DataMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SaveText.Text = n > 0 ? $"Lưu ({n})" : "Lưu";
        if (n > 0) IsPinned = true;
        _workspace.UpdateTabHeader(this);
    }

    private bool ConfirmDiscard()
    {
        if (!HasPendingChanges) return true;
        return MessageBox.Show(Window.GetWindow(this), "Có thay đổi chưa lưu. Bỏ các thay đổi này?", "WNTable",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    private bool CommitGridEdit()
    {
        // Pending cell edits are only written to the row when the grid commits them.
        return Grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private List<SqlStatement>? BuildStatements()
    {
        if (_data is null || _columns is null) return null;
        try
        {
            return ChangeBuilder.Build(Driver, Table, _data, _columns);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return null;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_editable) return;
        CommitGridEdit();
        var statements = BuildStatements();
        if (statements is null || statements.Count == 0) return;
        SaveButton.IsEnabled = false;
        try
        {
            await Driver.ApplyChangesAsync(statements);
            _data!.AcceptChanges();
            StatusText.Text = $"Đã lưu {statements.Count} thay đổi.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ShowError("Lưu thất bại, không có thay đổi nào được ghi: " + ex.Message);
            UpdateDirty();
        }
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        Grid.CancelEdit(DataGridEditingUnit.Row);
        _data?.RejectChanges();
        UpdateDirty();
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdit();
        var statements = BuildStatements();
        if (statements is null) return;
        TextDialog.ShowSql(Window.GetWindow(this), "SQL sẽ chạy khi lưu", string.Join("\n", statements.Select(s => s.Preview)));
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        if (!_editable || _data is null || DataMode.IsChecked != true) return;
        CommitGridEdit();
        var row = _data.NewRow();
        _data.Rows.Add(row);
        var view = (DataView)Grid.ItemsSource;
        var item = view.Cast<DataRowView>().LastOrDefault(r => r.Row == row);
        if (item is null) return;
        Grid.ScrollIntoView(item);
        var firstColumn = Grid.Columns.FirstOrDefault(c => !c.IsReadOnly);
        if (firstColumn is null) return;
        Grid.SelectedCells.Clear();
        Grid.CurrentCell = new DataGridCellInfo(item, firstColumn);
        Grid.Focus();
        Grid.BeginEdit();
    }

    private void DeleteRows_Click(object sender, RoutedEventArgs e)
    {
        if (!_editable) return;
        CommitGridEdit();
        foreach (var row in GridBinder.SelectedRows(Grid)) row.Delete();
        UpdateDirty();
    }

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit) IsPinned = true;
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsEditingCell()) return;
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            DeleteRows_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            EditValue_Click(sender, e);
            e.Handled = true;
        }
    }

    private bool IsEditingCell() => Keyboard.FocusedElement is TextBox tb && IsInsideCell(tb);

    private bool IsInsideCell(DependencyObject d)
    {
        for (var p = d; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
        {
            if (p is DataGridCell) return true;
            if (p == Grid) return false;
        }
        return false;
    }

    // ---------- cell actions ----------

    private void SetNull_Click(object sender, RoutedEventArgs e)
    {
        if (!_editable || GridBinder.CurrentCell(Grid) is not var (row, col) || col.IsReadOnly) return;
        CommitGridEdit();
        row.Row[col.ColumnName] = DBNull.Value;
    }

    private void EditValue_Click(object sender, RoutedEventArgs e)
    {
        if (GridBinder.CurrentCell(Grid) is not var (row, col)) return;
        CommitGridEdit();
        var value = row.Row[col.ColumnName];
        var canEdit = _editable && !col.IsReadOnly;
        var result = TextDialog.EditValue(Window.GetWindow(this), col.Caption, value, canEdit);
        if (canEdit && result is not null) row.Row[col.ColumnName] = result;
    }

    private void FilterByValue_Click(object sender, RoutedEventArgs e)
    {
        if (GridBinder.CurrentCell(Grid) is not var (row, col)) return;
        if (!ConfirmDiscard()) return;
        var value = row.Row[col.ColumnName];
        var name = Driver.Quote(col.Caption);
        FilterBox.Text = value is DBNull ? $"{name} IS NULL" : $"{name} = {ValueFormatter.SqlLiteral(value)}";
        FilterToggle.IsChecked = true;
        ApplyFilter();
    }

    private void CopyInsert_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null) return;
        var rows = GridBinder.SelectedRows(Grid);
        if (rows.Count == 0) return;
        var cols = string.Join(", ", _data.Columns.Cast<DataColumn>().Select(c => Driver.Quote(c.Caption)));
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            var values = string.Join(", ", r.Row.ItemArray.Select(ValueFormatter.SqlLiteral));
            sb.AppendLine($"INSERT INTO {Driver.Qualify(Table)} ({cols}) VALUES ({values});");
        }
        Clipboard.SetText(sb.ToString());
        StatusText.Text = $"Đã sao chép {rows.Count} câu INSERT.";
    }

    // ---------- filter, sort, paging ----------

    private void FilterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (ConfirmDiscard()) ApplyFilter();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FilterToggle.IsChecked = false;
            e.Handled = true;
        }
    }

    private void ShowFilter()
    {
        DataMode.IsChecked = true;
        FilterToggle.IsChecked = true;
        Dispatcher.BeginInvoke(() =>
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FilterToggle_Changed(object sender, RoutedEventArgs e)
    {
        var on = FilterToggle.IsChecked == true;
        FilterBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            FilterBox.Focus();
            return;
        }
        // Closing the bar drops an applied filter, so the grid matches what is visible.
        if (_appliedFilter.Length > 0)
        {
            if (!ConfirmDiscard())
            {
                FilterToggle.IsChecked = true;
                return;
            }
            FilterBox.Text = "";
            ApplyFilter();
        }
        Grid.Focus();
    }

    private void ApplyFilter_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscard()) ApplyFilter();
    }

    private void CloseFilter_Click(object sender, RoutedEventArgs e) => FilterToggle.IsChecked = false;

    private void ApplyFilter()
    {
        _appliedFilter = FilterBox.Text.Trim();
        _page = 0;
        _ = LoadAsync();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (DataMode.IsChecked != true)
        {
            _structureLoaded = false;
            _columns = null;
            _ = LoadStructureAsync();
            return;
        }
        if (!ConfirmDiscard()) return;
        _appliedFilter = FilterBox.Text.Trim();
        _ = LoadAsync();
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column is not CellColumn col || !ConfirmDiscard()) return;
        if (_sortCaption != col.Caption)
        {
            _sortCaption = col.Caption;
            _sortDirection = ListSortDirection.Ascending;
        }
        else if (_sortDirection == ListSortDirection.Ascending)
        {
            _sortDirection = ListSortDirection.Descending;
        }
        else
        {
            _sortCaption = null;
            _sortDirection = null;
        }
        _page = 0;
        IsPinned = true;
        _ = LoadAsync();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page == 0 || !ConfirmDiscard()) return;
        _page--;
        _ = LoadAsync();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _page++;
        _ = LoadAsync();
    }

    private void PageSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PageSizeBox.SelectedItem is not int size || size == _pageSize || _data is null) return;
        if (!ConfirmDiscard())
        {
            PageSizeBox.SelectedItem = _pageSize;
            return;
        }
        _pageSize = size;
        _page = 0;
        _ = LoadAsync();
    }

    // ---------- structure ----------

    private async void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        var structure = StructureMode.IsChecked == true;
        Grid.Visibility = PagingBar.Visibility = StatusText.Visibility = structure ? Visibility.Collapsed : Visibility.Visible;
        FilterBar.Visibility = !structure && FilterToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateEditUi();
        UpdateDirty();
        StructurePanel.Visibility = structure ? Visibility.Visible : Visibility.Collapsed;
        if (structure && IsLoaded) await LoadStructureAsync();
    }

    private async Task LoadStructureAsync()
    {
        if (_structureLoaded) return;
        _structureLoaded = true;
        HideError();
        try
        {
            _columns ??= await Driver.GetColumnsAsync(Table);
            ColumnsGrid.ItemsSource = _columns;
            IndexesGrid.ItemsSource = await Driver.GetIndexesAsync(Table);
            DdlEditor.Text = await Driver.GetCreateStatementAsync(Table);
        }
        catch (Exception ex)
        {
            _structureLoaded = false;
            ShowError(ex.Message);
        }
    }

    // ---------- errors ----------

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBar.Visibility = Visibility.Collapsed;

    private void CloseError_Click(object sender, RoutedEventArgs e) => HideError();
}
