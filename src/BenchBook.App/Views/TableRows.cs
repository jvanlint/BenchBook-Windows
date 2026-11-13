using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>
/// Tables written in code: a virtualised list whose rows are built only when
/// they scroll into view, with column headers that sort.
/// </summary>
internal static class TableRows
{
    private const string RowTemplate =
        "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid /></DataTemplate>";

    /// <summary>A list that calls <paramref name="fill"/> to build each visible row's cells.</summary>
    public static ListView List(Action<Grid, object> fill, ListViewSelectionMode selection = ListViewSelectionMode.Extended)
    {
        var list = new ListView
        {
            SelectionMode = selection,
            ItemTemplate = (DataTemplate)XamlReader.Load(RowTemplate),
        };
        list.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue || args.ItemContainer.ContentTemplateRoot is not Grid grid) return;
            grid.Children.Clear();
            grid.ColumnDefinitions.Clear();
            grid.Padding = new Thickness(0, 4, 0, 4);
            fill(grid, args.Item);
            args.Handled = true;
        };
        return list;
    }

    /// <summary>Columns in a row, from widths: a number is pixels, "2*" is a share of what's left.</summary>
    public static void Columns(Grid grid, params string[] widths)
    {
        grid.ColumnSpacing = 12;
        foreach (var width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = width.EndsWith('*')
                    ? new GridLength(width.Length == 1 ? 1 : double.Parse(width[..^1], System.Globalization.CultureInfo.InvariantCulture), GridUnitType.Star)
                    : new GridLength(double.Parse(width, System.Globalization.CultureInfo.InvariantCulture)),
            });
    }

    public static T Cell<T>(Grid grid, T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        element.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(element);
        return element;
    }

    public static TextBlock Text(string text, bool secondary = false) => new()
    {
        Text = text,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = secondary ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] : null,
    };

    /// <summary>Two lines: the main text, and smaller secondary text under it.</summary>
    public static StackPanel TwoLines(string first, string second)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(first));
        if (second.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = second,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        return panel;
    }

    /// <summary>A header row. Columns with a sort key get a button that sorts, showing an arrow when it's the current sort.</summary>
    public static Grid Header(string[] widths, (string Title, string? SortKey)[] columns, string? sortKey, bool ascending, Action<string>? sort)
    {
        var grid = new Grid { Padding = new Thickness(12, 0, 12, 4) };
        Columns(grid, widths);
        for (var i = 0; i < columns.Length; i++)
        {
            var (title, key) = columns[i];
            var label = new TextBlock
            {
                Text = key is not null && key == sortKey ? $"{title} {(ascending ? "▲" : "▼")}" : title,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            if (key is null || sort is null)
            {
                Cell(grid, label, i);
                continue;
            }
            var button = new Button
            {
                Content = label,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(button, $"Sort by {title}");
            button.Click += (_, _) => sort(key);
            Cell(grid, button, i);
        }
        return grid;
    }
}
