using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using WNTable.Core;

namespace WNTable;

public partial class App : Application
{
    /// <summary>WNTABLE_DATA_DIR overrides %APPDATA%\WNTable (handy for testing with a throwaway profile).</summary>
    public static ConnectionStore Store { get; } = new(Environment.GetEnvironmentVariable("WNTABLE_DATA_DIR") is { Length: > 0 } dir ? dir : null);

    /// <summary>Windows "app mode" is dark (WNTABLE_THEME=dark|light forces one). Read once at startup.</summary>
    public static bool IsDark { get; } = ReadIsDark();

    /// <summary>Connection name or id from <c>--connect "Name"</c>: opened right after start (handy for desktop shortcuts).</summary>
    public static string? StartupConnection { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var i = Array.IndexOf(e.Args, "--connect");
        if (i >= 0 && i + 1 < e.Args.Length) StartupConnection = e.Args[i + 1];
        var palette = IsDark ? "Theme/Colors.Dark.xaml" : "Theme/Colors.Light.xaml";
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(palette, UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Theme/Controls.xaml", UriKind.Relative) });
        // Scrollbar corners and a few built-in parts paint with the system control color.
        Resources[SystemColors.ControlBrushKey] = FindResource("BgBrush");
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    /// <summary>Call from every window's constructor: gives it a dark caption bar in dark mode.</summary>
    public static void PrepareWindow(Window window)
    {
        if (!IsDark) return;
        // SourceInitialized runs after the HWND exists but before the window is shown, so the caption is painted dark from the start.
        window.SourceInitialized += (_, _) =>
        {
            var on = 1;
            const int DwmwaUseImmersiveDarkMode = 20; // Windows 10 20H1+ / 11
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "WNTable — lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static bool ReadIsDark()
    {
        switch (Environment.GetEnvironmentVariable("WNTABLE_THEME"))
        {
            case "dark": return true;
            case "light": return false;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
