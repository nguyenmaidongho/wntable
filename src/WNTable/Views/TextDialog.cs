using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using WNTable.Controls;
using WNTable.Core;

namespace WNTable.Views;

/// <summary>Small dialogs built in code: SQL preview and the full-value cell editor.</summary>
public static class TextDialog
{
    public static void ShowSql(Window? owner, string title, string sql)
    {
        var editor = new TextEditor { Text = sql };
        SqlEditor.Setup(editor, readOnly: true);
        var copy = new Button { Content = "Sao chép", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Clipboard.SetText(sql);
        var close = new Button { Content = "Đóng", MinWidth = 90, IsCancel = true, IsDefault = true };
        var window = Build(owner, title, editor, copy, close);
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    /// <summary>Returns the new value (string or DBNull), or null when cancelled / read-only.</summary>
    public static object? EditValue(Window? owner, string column, object value, bool canEdit)
    {
        var box = new TextBox
        {
            Style = (Style)Application.Current.FindResource("TextArea"),
            Text = value is DBNull ? "" : ValueFormatter.Format(value),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("CodeFont"),
            IsReadOnly = !canEdit,
        };
        var isNull = new CheckBox
        {
            Content = "NULL",
            IsChecked = value is DBNull,
            IsEnabled = canEdit,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        isNull.Checked += (_, _) => box.IsEnabled = false;
        isNull.Unchecked += (_, _) => box.IsEnabled = true;
        box.IsEnabled = value is not DBNull;

        object? result = null;
        var ok = new Button { Content = canEdit ? "Áp dụng" : "Đóng", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Huỷ", MinWidth = 90, IsCancel = true, Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed };
        var window = Build(owner, column, box, isNull, ok, cancel);
        ok.Click += (_, _) =>
        {
            if (canEdit) result = isNull.IsChecked == true ? DBNull.Value : box.Text;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                e.Handled = true;
            }
        };
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        window.ShowDialog();
        return result;
    }

    private static Window Build(Window? owner, string title, UIElement content, params UIElement[] buttons)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons) bar.Children.Add(b);
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        if (content is TextBox)
        {
            root.Children.Add(content);
        }
        else
        {
            // The SQL editor has no border of its own.
            var frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(1), Child = content };
            frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            root.Children.Add(frame);
        }
        return new Window
        {
            Style = (Style)Application.Current.FindResource("AppWindow"),
            Title = title,
            Owner = owner,
            Width = 720,
            Height = 460,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = root,
        };
    }
}
