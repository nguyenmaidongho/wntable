using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WNTable.Controls;
using WNTable.Core;

namespace WNTable.Views;

/// <summary>What every tab in the workspace exposes to the window.</summary>
public interface IWorkspaceTab
{
    string Title { get; }
    bool HasPendingChanges { get; }
    /// <summary>Handles a window-level shortcut while this tab is active.</summary>
    bool HandleKey(KeyEventArgs e);
    void FocusMain();
    void OnClosed();
}

/// <summary>One open connection: table list on the left, table / SQL tabs on the right.</summary>
public partial class WorkspaceWindow : Window
{
    private List<TableRef> _tables = [];
    private Task<SchemaInfo>? _schemaTask;
    private SchemaInfo? _schema;
    private bool _switchingDatabase;
    private bool _scratchUsed;

    public WorkspaceWindow(DbDriver driver)
    {
        InitializeComponent();
        App.PrepareWindow(this);
        Driver = driver;
        ConnectionName.Text = driver.Info.Name;
        ConnectionKind.Text = driver.Info.KindLabel;
        var tag = EnvTags.LabelFor(driver.Info.Color);
        EnvTag.Background = ColorBrushConverter.ToBrush(driver.Info.Color);
        EnvTagText.Text = tag;
        EnvTag.Visibility = tag == "" ? Visibility.Collapsed : Visibility.Visible;
        DatabaseBox.Visibility = driver.SupportsDatabases ? Visibility.Visible : Visibility.Collapsed;

        UpdateTitle();
        Loaded += async (_, _) => await LoadDatabasesAsync();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    public DbDriver Driver { get; }

    private IWorkspaceTab? ActiveTab => (Tabs.SelectedItem as TabItem)?.Content as IWorkspaceTab;

    private void UpdateTitle()
    {
        var db = Driver.SupportsDatabases && !string.IsNullOrEmpty(Driver.CurrentDatabase) ? " — " + Driver.CurrentDatabase : "";
        Title = $"{Driver.Info.Name}{db} — WNTable";
        var hasDb = Driver.SupportsDatabases && !string.IsNullOrEmpty(Driver.CurrentDatabase);
        PillDatabase.Text = hasDb ? Driver.CurrentDatabase : "";
        PillDbSep.Visibility = PillDatabase.Visibility = hasDb ? Visibility.Visible : Visibility.Collapsed;
        PillTab.Text = ActiveTab?.Title ?? "";
        PillTabSep.Visibility = PillTab.Visibility = PillTab.Text == "" ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- databases & tables ----------

    private async Task LoadDatabasesAsync()
    {
        if (!Driver.SupportsDatabases)
        {
            await LoadTablesAsync();
            return;
        }
        try
        {
            var dbs = await Driver.GetDatabasesAsync();
            _switchingDatabase = true;
            DatabaseBox.ItemsSource = dbs;
            DatabaseBox.SelectedItem = dbs.FirstOrDefault(d => string.Equals(d, Driver.CurrentDatabase, StringComparison.OrdinalIgnoreCase));
            _switchingDatabase = false;
            if (DatabaseBox.SelectedItem is null)
            {
                SidebarStatus.Text = "Chọn database ở ô phía trên.";
                DatabaseBox.IsDropDownOpen = string.IsNullOrEmpty(Driver.CurrentDatabase);
                return;
            }
            await LoadTablesAsync();
        }
        catch (Exception ex)
        {
            SidebarStatus.Text = "Lỗi: " + ex.Message;
        }
    }

    private async Task LoadTablesAsync()
    {
        SidebarStatus.Text = "Đang tải…";
        try
        {
            _tables = (await Driver.GetTablesAsync()).ToList();
            TableList.ItemsSource = _tables;
            var view = CollectionViewSource.GetDefaultView(_tables);
            view.Filter = o => o is TableRef t && MatchesSearch(t);
            var views = _tables.Count(t => t.IsView);
            SidebarStatus.Text = $"{_tables.Count - views} bảng{(views > 0 ? $", {views} view" : "")}";
            _schema = null;
            _schemaTask = null;
            _ = GetSchemaAsync();
        }
        catch (Exception ex)
        {
            SidebarStatus.Text = "Lỗi: " + ex.Message;
        }
    }

    /// <summary>Reloads the sidebar after the SQL editor changed the schema.</summary>
    public Task ReloadTablesAsync() => LoadTablesAsync();

    private bool MatchesSearch(TableRef t)
    {
        var q = TableSearch.Text.Trim();
        return q.Length == 0 || FuzzyMatch(t.Display, q);
    }

    /// <summary>Letters of the query appear in order: "ordit" matches "order_items".</summary>
    private static bool FuzzyMatch(string text, string query)
    {
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var i = 0;
        foreach (var ch in text)
        {
            if (i < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[i])) i++;
        }
        return i == query.Length;
    }

    /// <summary>Tables and columns for autocomplete; loaded once per database in the background.</summary>
    public SchemaInfo? Schema
    {
        get
        {
            _ = GetSchemaAsync();
            return _schema;
        }
    }

    private Task<SchemaInfo> GetSchemaAsync()
    {
        return _schemaTask ??= Load();

        async Task<SchemaInfo> Load()
        {
            var tables = _tables.Select(t => t.Display).ToList();
            Dictionary<string, List<string>> columns;
            try
            {
                columns = await Driver.GetAllColumnNamesAsync();
            }
            catch
            {
                columns = [];
            }
            return _schema = new SchemaInfo(tables, columns);
        }
    }

    private async void DatabaseBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingDatabase || DatabaseBox.SelectedItem is not string db || db == Driver.CurrentDatabase) return;
        var tableTabs = Tabs.Items.OfType<TabItem>().Where(t => t.Content is TableTab).ToList();
        if (tableTabs.Any(t => ((IWorkspaceTab)t.Content).HasPendingChanges) &&
            MessageBox.Show(this, "Có thay đổi chưa lưu. Đổi database sẽ bỏ các thay đổi này. Tiếp tục?", "WNTable",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            _switchingDatabase = true;
            DatabaseBox.SelectedItem = Driver.CurrentDatabase;
            _switchingDatabase = false;
            return;
        }
        foreach (var t in tableTabs) CloseTab(t, force: true);
        Driver.CurrentDatabase = db;
        foreach (var q in Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<QueryTab>()) q.OnDatabaseChanged();
        UpdateTitle();
        TableSearch.Text = "";
        await LoadTablesAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Driver.SupportsDatabases) await LoadDatabasesAsync();
        else await LoadTablesAsync();
    }

    private void TableSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        CollectionViewSource.GetDefaultView(_tables).Refresh();
        if (TableList.Items.Count > 0) TableList.SelectedIndex = 0;
    }

    private void TableSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (TableList.SelectedIndex < TableList.Items.Count - 1) TableList.SelectedIndex++;
                TableList.ScrollIntoView(TableList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (TableList.SelectedIndex > 0) TableList.SelectedIndex--;
                TableList.ScrollIntoView(TableList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                if (TableList.SelectedItem is TableRef t) OpenTable(t, newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
            case Key.Escape:
                TableSearch.Text = "";
                ActiveTab?.FocusMain();
                e.Handled = true;
                break;
        }
    }

    private void TableList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e) is { } t) OpenTable(t, newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private void TableList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click keeps the tab: the next single click opens a new one instead of replacing it.
        if (ItemAt(e) is not null && Tabs.SelectedItem is TabItem { Content: TableTab tab }) tab.IsPinned = true;
    }

    private void TableList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ItemAt(e) is { } t)
        {
            OpenTable(t, newTab: true);
            e.Handled = true;
        }
    }

    private void TableList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TableList.SelectedItem is TableRef t)
        {
            OpenTable(t, newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            e.Handled = true;
        }
    }

    private static TableRef? ItemAt(MouseEventArgs e) =>
        (e.OriginalSource as FrameworkElement)?.DataContext as TableRef;

    private TableRef? SelectedTable => TableList.SelectedItem as TableRef;

    private void OpenTable_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTable is { } t) OpenTable(t, newTab: false);
    }

    private void OpenTableNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTable is { } t) OpenTable(t, newTab: true);
    }

    private void OpenStructure_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTable is { } t) OpenTable(t, newTab: false, structure: true);
    }

    private void SelectQuery_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTable is not { } t) return;
        var sql = Driver.Kind == DbKind.SqlServer
            ? $"SELECT TOP 100 * FROM {Driver.Qualify(t)};"
            : $"SELECT * FROM {Driver.Qualify(t)} LIMIT 100;";
        OpenQueryTab(sql, run: true);
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTable is { } t) Clipboard.SetText(t.Display);
    }

    // ---------- tabs ----------

    /// <summary>
    /// Opens a table. A single click reuses the current table tab (unless it is pinned or has edits);
    /// Ctrl+click / middle-click always opens a new tab. An already open table is just focused.
    /// </summary>
    public void OpenTable(TableRef table, bool newTab, bool structure = false)
    {
        var existing = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Content is TableTab tt && tt.Table == table);
        if (existing is not null)
        {
            Tabs.SelectedItem = existing;
            if (structure) ((TableTab)existing.Content).ShowStructure();
            return;
        }
        var tab = new TableTab(this, table, structure);
        if (!newTab && Tabs.SelectedItem is TabItem { Content: TableTab current } item && !current.IsPinned && !current.HasPendingChanges)
        {
            current.OnClosed();
            item.Content = tab;
            item.Header = MakeHeader(item, tab.Title);
            item.ToolTip = table.Display;
            UpdateTitle();
            return;
        }
        AddTab(tab, table.Display);
    }

    public void OpenQueryTab(string? text = null, bool run = false)
    {
        string? scratch = null;
        if (text is null && !_scratchUsed)
        {
            // The first SQL tab of a session brings back what was typed last time.
            _scratchUsed = true;
            try { scratch = App.Store.LoadScratch(Driver.Info.Id); } catch { scratch = null; }
        }
        var count = Tabs.Items.OfType<TabItem>().Count(t => t.Content is QueryTab) + 1;
        var tab = new QueryTab(this, $"SQL {count}", text ?? scratch ?? "", isScratch: scratch is not null);
        AddTab(tab, null);
        if (run) tab.RunAll();
    }

    private void AddTab(IWorkspaceTab tab, string? tooltip)
    {
        var item = new TabItem { Content = tab, ToolTip = tooltip };
        item.Header = MakeHeader(item, tab.Title);
        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;
        UpdateEmpty();
    }

    private object MakeHeader(TabItem item, string title)
    {
        var icon = new TextBlock
        {
            Text = item.Content is QueryTab ? "" : "",
            Style = (Style)FindResource("Icon"),
            FontSize = 10,
            Margin = new Thickness(0, 0, 6, 0),
            Opacity = 0.6,
        };
        var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button
        {
            Style = (Style)FindResource("TabCloseButton"),
            Margin = new Thickness(8, 0, -4, 0),
            ToolTip = "Đóng (Ctrl+W)",
        };
        close.Click += (_, _) => CloseTab(item);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, text, close }, Background = System.Windows.Media.Brushes.Transparent };
        panel.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) CloseTab(item);
        };
        return panel;
    }

    /// <summary>Refreshes a tab's header text (e.g. "orders •" when it has unsaved edits).</summary>
    public void UpdateTabHeader(IWorkspaceTab tab)
    {
        var item = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Content == tab);
        if (item?.Header is StackPanel { Children: [_, TextBlock text, ..] })
        {
            text.Text = tab.Title + (tab.HasPendingChanges ? " •" : "");
            text.FontStyle = tab is TableTab { IsPinned: false } && !tab.HasPendingChanges ? FontStyles.Italic : FontStyles.Normal;
        }
    }

    private bool CloseTab(TabItem item, bool force = false)
    {
        if (item.Content is not IWorkspaceTab tab) return true;
        if (!force && tab.HasPendingChanges &&
            MessageBox.Show(this, $"“{tab.Title}” có thay đổi chưa lưu. Đóng và bỏ thay đổi?", "WNTable",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return false;
        }
        tab.OnClosed();
        Tabs.Items.Remove(item);
        UpdateEmpty();
        ActiveTab?.FocusMain();
        return true;
    }

    private void UpdateEmpty() => EmptyText.Visibility = Tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        UpdateTitle();
        if (ActiveTab is TableTab tt)
        {
            TableList.SelectedItem = tt.Table;
            TableList.ScrollIntoView(tt.Table);
        }
    }

    private void NewQuery_Click(object sender, RoutedEventArgs e) => OpenQueryTab();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ctrl && key == Key.T) { OpenQueryTab(); e.Handled = true; return; }
        if (ctrl && key == Key.W) { if (Tabs.SelectedItem is TabItem item) CloseTab(item); e.Handled = true; return; }
        if (ctrl && key is Key.P or Key.K) { TableSearch.Focus(); TableSearch.SelectAll(); e.Handled = true; return; }
        if (ctrl && key == Key.R && ActiveTab is not TableTab) { Refresh_Click(this, e); e.Handled = true; return; }
        if (ctrl && key == Key.Tab || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Tab)
        {
            if (Tabs.Items.Count > 1)
            {
                var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                Tabs.SelectedIndex = (Tabs.SelectedIndex + step + Tabs.Items.Count) % Tabs.Items.Count;
            }
            e.Handled = true;
            return;
        }
        if (ActiveTab?.HandleKey(e) == true) e.Handled = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var dirty = Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<IWorkspaceTab>().Where(t => t.HasPendingChanges).ToList();
        if (dirty.Count > 0 &&
            MessageBox.Show(this, $"Có {dirty.Count} tab còn thay đổi chưa lưu. Đóng cửa sổ và bỏ các thay đổi?", "WNTable",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }
        foreach (var tab in Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<IWorkspaceTab>()) tab.OnClosed();
    }
}
