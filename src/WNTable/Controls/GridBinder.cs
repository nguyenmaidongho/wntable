using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WNTable.Core;

namespace WNTable.Controls;

/// <summary>Builds DataGrid columns for a result DataTable (columns c0..cN, real name in Caption).</summary>
public static class GridBinder
{
    private static readonly Style CellTextStyle = CreateCellTextStyle();

    public static void Configure(DataGrid grid)
    {
        grid.AutoGenerateColumns = false;
        grid.EnableRowVirtualization = true;
        grid.EnableColumnVirtualization = true;
        // Colors, row height and grid lines come from the DataGrid style in Theme/Controls.xaml.
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
        grid.HeadersVisibility = DataGridHeadersVisibility.All;
        grid.RowHeaderWidth = 12;
        grid.CanUserAddRows = false;
        grid.ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader;
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        VirtualizingPanel.SetIsVirtualizing(grid, true);
        ScrollViewer.SetCanContentScroll(grid, true);
    }

    /// <summary>Shows <paramref name="table"/>; keeps existing columns (and their widths) when the shape is unchanged.</summary>
    public static void Bind(DataGrid grid, DataTable table, bool readOnly)
    {
        var captions = table.Columns.Cast<DataColumn>().Select(c => c.Caption).ToList();
        var current = grid.Columns.Select(c => (c as CellColumn)?.Caption).ToList();
        if (!captions.SequenceEqual(current))
        {
            grid.Columns.Clear();
            var headerTemplate = (DataTemplate)Application.Current.FindResource("ColumnHeaderTemplate");
            foreach (DataColumn dc in table.Columns)
            {
                var clr = dc.ExtendedProperties["ClrType"] as Type;
                grid.Columns.Add(new CellColumn(dc.ColumnName, dc.Caption)
                {
                    Header = dc.Caption,
                    HeaderTemplate = headerTemplate,
                    IsReadOnly = readOnly || clr == typeof(byte[]),
                    Width = DataGridLength.Auto,
                    MinWidth = 40,
                    MaxWidth = 380,
                    ElementStyle = CellTextStyle,
                    EditingElementStyle = (Style)Application.Current.FindResource("CellEditBox"),
                    SortMemberPath = dc.ColumnName,
                    HeaderStringFormat = null,
                });
            }
        }
        grid.IsReadOnly = readOnly;
        grid.ItemsSource = table.DefaultView;
    }

    private static Style CreateCellTextStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        // Padding (not Margin) so the auto column width includes the gap and text is not clipped.
        style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 0, 6, 0)));
        var nullTrigger = new Trigger { Property = TextBlock.TextProperty, Value = ValueFormatter.NullText };
        nullTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("MutedBrush")));
        nullTrigger.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic));
        style.Triggers.Add(nullTrigger);
        style.Seal();
        return style;
    }

    /// <summary>The DataRowView behind the cell that has keyboard focus / is current.</summary>
    public static (DataRowView Row, CellColumn Column)? CurrentCell(DataGrid grid)
    {
        var cell = grid.CurrentCell;
        if (cell.Item is DataRowView row && cell.Column is CellColumn col) return (row, col);
        if (grid.SelectedCells.Count > 0 && grid.SelectedCells[0].Item is DataRowView r2 && grid.SelectedCells[0].Column is CellColumn c2)
        {
            return (r2, c2);
        }
        return null;
    }

    public static List<DataRowView> SelectedRows(DataGrid grid) =>
        grid.SelectedCells.Select(c => c.Item).OfType<DataRowView>().Distinct().ToList();
}

/// <summary>
/// Text column that displays a short single-line value but edits (and copies) the full value.
/// </summary>
public sealed class CellColumn : DataGridTextColumn
{
    public CellColumn(string columnName, string caption)
    {
        ColumnName = columnName;
        Caption = caption;
        // TwoWay only so DataGrid does not coerce the column to read-only; edits go through the binding below.
        Binding = new Binding($"[{columnName}]") { Converter = CellConverter.Short, Mode = BindingMode.TwoWay };
        ClipboardContentBinding = new Binding($"[{columnName}]") { Converter = CellConverter.Full, Mode = BindingMode.OneWay };
    }

    public string ColumnName { get; }
    public string Caption { get; }

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem)
    {
        var box = (TextBox)base.GenerateEditingElement(cell, dataItem);
        BindingOperations.SetBinding(box, TextBox.TextProperty, new Binding($"[{ColumnName}]")
        {
            Converter = CellConverter.Full,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.Explicit,
        });
        return box;
    }

    protected override bool CommitCellEdit(FrameworkElement editingElement)
    {
        if (editingElement is TextBox box)
        {
            BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
            return true;
        }
        return base.CommitCellEdit(editingElement);
    }
}

public sealed class CellConverter(bool full) : IValueConverter
{
    public static readonly CellConverter Short = new(false);
    public static readonly CellConverter Full = new(true);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        full ? ValueFormatter.Format(value) : ValueFormatter.FormatCell(value);

    /// <summary>Grid edits are stored as text; typing NULL sets SQL NULL.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s != ValueFormatter.NullText ? s : DBNull.Value;
}
