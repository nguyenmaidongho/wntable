using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace WNTable.Controls;

/// <summary>Placeholder text for a TextBox: <c>controls:Hint.Text="Tìm bảng…"</c>.</summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Hint), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        tb.Loaded -= Update;
        tb.Loaded += Update;
        tb.TextChanged -= Update;
        tb.TextChanged += Update;
        tb.IsVisibleChanged -= OnVisibleChanged;
        tb.IsVisibleChanged += OnVisibleChanged;
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update(sender, null!);

    private static void Update(object sender, RoutedEventArgs e)
    {
        var tb = (TextBox)sender;
        var layer = AdornerLayer.GetAdornerLayer(tb);
        if (layer is null) return;
        var existing = layer.GetAdorners(tb)?.OfType<HintAdorner>().FirstOrDefault();
        // The adorner layer belongs to the window, so it would keep drawing for a hidden TextBox.
        if (string.IsNullOrEmpty(tb.Text) && tb.IsVisible)
        {
            if (existing is null) layer.Add(new HintAdorner(tb, GetText(tb) ?? ""));
        }
        else if (existing is not null)
        {
            layer.Remove(existing);
        }
    }

    private sealed class HintAdorner : Adorner
    {
        private readonly string _text;

        public HintAdorner(TextBox box, string text) : base(box)
        {
            _text = text;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var box = (TextBox)AdornedElement;
            var ft = new FormattedText(_text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(box.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                box.FontSize, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var x = box.Padding.Left + box.BorderThickness.Left + 3;
            var y = (box.ActualHeight - ft.Height) / 2;
            dc.DrawText(ft, new Point(x, y));
        }
    }
}
