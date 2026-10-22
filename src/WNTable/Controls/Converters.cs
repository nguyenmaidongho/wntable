using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WNTable.Core;

namespace WNTable.Controls;

public sealed class ColorBrushConverter : IValueConverter
{
    public static readonly ColorBrushConverter Instance = new();
    private static readonly Brush Fallback = new SolidColorBrush(Color.FromRgb(0x5B, 0x6B, 0x7F));

    public static Brush ToBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Fallback;
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return Fallback; }
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ToBrush(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Environment tags (TablePlus-style): a color plus a short label shown on the connection.</summary>
public static class EnvTags
{
    public sealed record Tag(string Label, string Hex);

    public static readonly Tag[] All =
    [
        new("none", ""),
        new("production", "#E5484D"),
        new("staging", "#F76B15"),
        new("testing", "#E2A336"),
        new("local", "#30A46C"),
        new("development", "#0090FF"),
        new("other", "#8E4EC6"),
    ];

    public static string LabelFor(string? hex) =>
        All.FirstOrDefault(t => t.Hex != "" && string.Equals(t.Hex, hex, StringComparison.OrdinalIgnoreCase))?.Label ?? "";
}

public sealed class EnvTagConverter : IValueConverter
{
    public static readonly EnvTagConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => EnvTags.LabelFor(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EnvTagVisibilityConverter : IValueConverter
{
    public static readonly EnvTagVisibilityConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        EnvTags.LabelFor(value as string) == "" ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class TableIconConverter : IValueConverter
{
    public static readonly TableIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class KindBadgeConverter : IValueConverter
{
    public static readonly KindBadgeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DbKind.MySql => "My",
        DbKind.PostgreSql => "Pg",
        DbKind.Sqlite => "Lt",
        DbKind.SqlServer => "Ms",
        _ => "?",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
