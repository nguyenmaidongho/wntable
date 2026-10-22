using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WNTable.Controls;
using WNTable.Core;

namespace WNTable.Views;

public partial class ConnectionDialog : Window
{
    private readonly ConnectionInfo _info;
    private DbKind _previousKind;

    public ConnectionDialog(ConnectionInfo info)
    {
        InitializeComponent();
        _info = info;
        Title = string.IsNullOrEmpty(info.Name) ? "Kết nối mới" : "Sửa kết nối — " + info.Name;

        KindBox.ItemsSource = Enum.GetValues<DbKind>().Select(k => new ComboBoxItem { Content = ConnectionInfo.KindName(k), Tag = k }).ToList();
        ColorBox.ItemsSource = EnvTags.All;
        ColorBox.SelectedItem = EnvTags.All.FirstOrDefault(c => string.Equals(c.Hex, info.Color, StringComparison.OrdinalIgnoreCase)) ?? EnvTags.All[0];

        NameBox.Text = info.Name;
        HostBox.Text = info.Host;
        PortBox.Text = info.Port > 0 ? info.Port.ToString() : "";
        UserBox.Text = info.User;
        PasswordBox.Password = info.Password;
        DatabaseBox.Text = info.Database;
        FileBox.Text = info.FilePath;
        TrustCertBox.IsChecked = info.TrustServerCertificate;

        _previousKind = info.Kind;
        KindBox.SelectedIndex = (int)info.Kind;
        if (string.IsNullOrEmpty(info.User) && info.Kind == DbKind.MySql && string.IsNullOrEmpty(info.Name))
        {
            UserBox.Text = "root";
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    public ConnectionInfo Result => _info;
    public bool ConnectAfterSave { get; private set; }

    private DbKind SelectedKind => KindBox.SelectedItem is ComboBoxItem { Tag: DbKind k } ? k : DbKind.MySql;

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var kind = SelectedKind;
        // Follow the default port when the user has not typed a custom one.
        if (PortBox.Text == "" || PortBox.Text == ConnectionInfo.DefaultPort(_previousKind).ToString())
        {
            var port = ConnectionInfo.DefaultPort(kind);
            PortBox.Text = port > 0 ? port.ToString() : "";
        }
        _previousKind = kind;

        var isFile = kind == DbKind.Sqlite;
        foreach (var el in new FrameworkElement[] { HostLabel, HostBox, PortLabel, PortBox, UserLabel, UserBox, PasswordLabel, PasswordBox, DatabaseLabel, DatabaseBox })
        {
            el.Visibility = isFile ? Visibility.Collapsed : Visibility.Visible;
        }
        FileLabel.Visibility = FilePanel.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
        TrustCertBox.Visibility = kind == DbKind.SqlServer ? Visibility.Visible : Visibility.Collapsed;

        HintText.Text = kind switch
        {
            DbKind.MySql => "Laragon mặc định: host 127.0.0.1, port 3306, user root, không mật khẩu.",
            DbKind.PostgreSql => "Để trống Database sẽ dùng “postgres”.",
            DbKind.SqlServer => "Để trống User để đăng nhập bằng tài khoản Windows. Host dạng MAY\\SQLEXPRESS để chọn instance.",
            DbKind.Sqlite => "Chọn file .db / .sqlite có sẵn, hoặc bấm “Mới…” để tạo file mới.",
            _ => "",
        };
        HintText.Visibility = HintText.Text == "" ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SQLite (*.db;*.sqlite;*.sqlite3;*.db3)|*.db;*.sqlite;*.sqlite3;*.db3|Tất cả file (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true) SetFile(dlg.FileName);
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "SQLite (*.db)|*.db|SQLite (*.sqlite)|*.sqlite", FileName = "database.db" };
        if (dlg.ShowDialog(this) == true) SetFile(dlg.FileName);
    }

    private void SetFile(string path)
    {
        FileBox.Text = path;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = System.IO.Path.GetFileName(path);
    }

    /// <summary>Copies the form into <see cref="_info"/>; returns an error message when invalid.</summary>
    private string? Apply()
    {
        var kind = SelectedKind;
        _info.Kind = kind;
        _info.Color = (ColorBox.SelectedItem as EnvTags.Tag)?.Hex ?? "";
        _info.TrustServerCertificate = TrustCertBox.IsChecked == true;
        if (kind == DbKind.Sqlite)
        {
            _info.FilePath = FileBox.Text.Trim();
            _info.Port = 0;
            if (_info.FilePath == "") return "Chưa chọn file SQLite.";
        }
        else
        {
            _info.Host = HostBox.Text.Trim();
            _info.User = UserBox.Text.Trim();
            _info.Password = PasswordBox.Password;
            _info.Database = DatabaseBox.Text.Trim();
            if (_info.Host == "") return "Chưa nhập host.";
            if (PortBox.Text.Trim() == "")
            {
                _info.Port = ConnectionInfo.DefaultPort(kind);
            }
            else if (int.TryParse(PortBox.Text.Trim(), out var port) && port is > 0 and < 65536)
            {
                _info.Port = port;
            }
            else
            {
                return "Port không hợp lệ.";
            }
        }
        _info.Name = NameBox.Text.Trim();
        if (_info.Name == "")
        {
            _info.Name = kind == DbKind.Sqlite
                ? System.IO.Path.GetFileName(_info.FilePath)
                : $"{_info.Host}{(string.IsNullOrEmpty(_info.Database) ? "" : "/" + _info.Database)}";
        }
        return null;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Apply() is { } error)
        {
            ShowResult(error, ok: false);
            return;
        }
        TestButton.IsEnabled = false;
        ShowResult("Đang kiểm tra…", ok: true);
        try
        {
            await DbDriver.Create(_info.Clone()).TestAsync();
            ShowResult("✓ Kết nối thành công.", ok: true);
        }
        catch (Exception ex)
        {
            ShowResult("✗ " + ex.Message, ok: false);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void ShowResult(string text, bool ok)
    {
        TestResult.Text = text;
        TestResult.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
        TestResult.Visibility = Visibility.Visible;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Finish(connect: false);

    private void SaveConnect_Click(object sender, RoutedEventArgs e) => Finish(connect: true);

    private void Finish(bool connect)
    {
        if (Apply() is { } error)
        {
            ShowResult(error, ok: false);
            return;
        }
        ConnectAfterSave = connect;
        DialogResult = true;
    }
}
