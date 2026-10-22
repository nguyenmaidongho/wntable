using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using WNTable.Core;
using WNTable.Views;

namespace WNTable;

/// <summary>Start screen: saved connections, like TablePlus' welcome window.</summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ConnectionInfo> _connections;
    private readonly ICollectionView _view;
    private bool _connecting;

    public MainWindow()
    {
        InitializeComponent();
        App.PrepareWindow(this);
        VersionText.Text = "Phiên bản " + typeof(App).Assembly.GetName().Version?.ToString(3);
        List<ConnectionInfo> saved;
        try
        {
            saved = App.Store.Load();
        }
        catch (Exception ex)
        {
            saved = [];
            StatusText.Text = "Không đọc được danh sách kết nối: " + ex.Message;
        }
        _connections = new ObservableCollection<ConnectionInfo>(saved);
        _view = CollectionViewSource.GetDefaultView(_connections);
        _view.Filter = o => o is ConnectionInfo c && Matches(c, SearchBox.Text);
        ConnectionList.ItemsSource = _view;
        UpdateEmpty();
        if (_connections.Count > 0) ConnectionList.SelectedIndex = 0;

        InputBindings.Add(new KeyBinding(new RelayCommand(() => New_Click(this, new RoutedEventArgs())), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OpenSqlite_Click(this, new RoutedEventArgs())), Key.O, ModifierKeys.Control));
        Loaded += (_, _) =>
        {
            FocusList();
            if (App.StartupConnection is { } wanted &&
                _connections.FirstOrDefault(c => c.Id == wanted || string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)) is { } start)
            {
                ConnectionList.SelectedItem = start;
                _ = ConnectAsync(start);
            }
        };
    }

    /// <summary>Focuses the selected row itself so arrow keys move the selection right away.</summary>
    private void FocusList()
    {
        if (ConnectionList.SelectedItem is null)
        {
            SearchBox.Focus();
            return;
        }
        ConnectionList.UpdateLayout();
        if (ConnectionList.ItemContainerGenerator.ContainerFromItem(ConnectionList.SelectedItem) is ListBoxItem item) item.Focus();
        else ConnectionList.Focus();
    }

    private static bool Matches(ConnectionInfo c, string query) =>
        string.IsNullOrWhiteSpace(query)
        || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || c.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase);

    private ConnectionInfo? Selected => ConnectionList.SelectedItem as ConnectionInfo;

    private void UpdateEmpty() => EmptyText.Visibility = _connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Persist()
    {
        try
        {
            App.Store.Save(_connections);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không lưu được danh sách kết nối: " + ex.Message, "WNTable", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _view.Refresh();
        UpdateEmpty();
    }

    private void New_Click(object sender, RoutedEventArgs e) => ShowEditor(new ConnectionInfo { Name = "" }, isNew: true);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } c) ShowEditor(c, isNew: false);
    }

    private void ShowEditor(ConnectionInfo info, bool isNew)
    {
        var dialog = new ConnectionDialog(info.Clone()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var result = dialog.Result;
        if (isNew)
        {
            _connections.Add(result);
        }
        else
        {
            var index = _connections.IndexOf(info);
            _connections[index] = result;
        }
        Persist();
        ConnectionList.SelectedItem = result;
        if (dialog.ConnectAfterSave) _ = ConnectAsync(result);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } c) return;
        var copy = c.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = c.Name + " (bản sao)";
        _connections.Insert(_connections.IndexOf(c) + 1, copy);
        Persist();
        ConnectionList.SelectedItem = copy;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } c) return;
        if (MessageBox.Show(this, $"Xoá kết nối “{c.Name}”?\n(Chỉ xoá khỏi danh sách, không ảnh hưởng database.)", "WNTable",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _connections.Remove(c);
        Persist();
    }

    private void OpenSqlite_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "SQLite (*.db;*.sqlite;*.sqlite3;*.db3)|*.db;*.sqlite;*.sqlite3;*.db3|Tất cả file (*.*)|*.*",
            Title = "Mở database SQLite",
        };
        if (dlg.ShowDialog(this) != true) return;
        var existing = _connections.FirstOrDefault(c =>
            c.Kind == DbKind.Sqlite && string.Equals(c.FilePath, dlg.FileName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ConnectionInfo { Kind = DbKind.Sqlite, FilePath = dlg.FileName, Name = System.IO.Path.GetFileName(dlg.FileName), Port = 0 };
            _connections.Add(existing);
            Persist();
        }
        ConnectionList.SelectedItem = existing;
        _ = ConnectAsync(existing);
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } c) _ = ConnectAsync(c);
    }

    private async Task ConnectAsync(ConnectionInfo info)
    {
        if (_connecting) return;
        _connecting = true;
        Cursor = Cursors.AppStarting;
        StatusText.Text = $"Đang kết nối “{info.Name}”…";
        try
        {
            var driver = DbDriver.Create(info.Clone());
            await driver.TestAsync();
            var window = new WorkspaceWindow(driver);
            window.Show();
            StatusText.Text = $"Đã mở “{info.Name}”.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Kết nối thất bại: " + ex.Message;
            MessageBox.Show(this, ex.Message, $"Không kết nối được “{info.Name}”", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _connecting = false;
            Cursor = null;
        }
    }

    private void ConnectionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ConnectionInfo c }) _ = ConnectAsync(c);
    }

    private void ConnectionList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Connect_Click(sender, e); e.Handled = true; break;
            case Key.F2: Edit_Click(sender, e); e.Handled = true; break;
            case Key.Delete: Delete_Click(sender, e); e.Handled = true; break;
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _view.Refresh();
        if (ConnectionList.SelectedItem is null && !_view.IsEmpty) ConnectionList.SelectedIndex = 0;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Selected is { } c)
        {
            _ = ConnectAsync(c);
            e.Handled = true;
        }
    }
}

/// <summary>Minimal ICommand for key bindings.</summary>
public sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
