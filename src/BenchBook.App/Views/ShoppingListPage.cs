using System.Globalization;
using System.Text;
using BenchBook.App.Services;
using BenchBook.Core.Paints;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>The shopping list (screens.md, "Shopping list").</summary>
internal sealed partial class ShoppingListPage : Page
{
    private readonly AppServices _services;
    private readonly Action<string> _showPaint;
    private readonly TextBlock _summary = new() { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
    private readonly StackPanel _body = new() { Spacing = 20 };
    private ShoppingList _list = null!;

    /// <param name="showPaint">Shows a paint in the catalogue, by id.</param>
    public ShoppingListPage(AppServices services, Action<string> showPaint)
    {
        _services = services;
        _showPaint = showPaint;
        var export = new AppBarButton { Label = "Export Shopping List…", Icon = new FontIcon { Glyph = "\uEDE1" } };
        ToolTipService.SetToolTip(export, "Save the shopping list as a text file");
        export.Click += async (_, _) => await Export();
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        commands.PrimaryCommands.Add(export);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = "Shopping List", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        titles.Children.Add(_summary);
        header.Children.Add(titles);
        Grid.SetColumn(commands, 1);
        header.Children.Add(commands);

        var panel = new StackPanel { Spacing = 20, Padding = new Thickness(32, 20, 32, 24) };
        panel.Children.Add(header);
        panel.Children.Add(_body);
        Content = new ScrollViewer { Content = panel };
        Load();
    }

    private void Load()
    {
        _list = _services.WithStore(s => new ShoppingList(s));
        _summary.Text = _list.Summary;
        _body.Children.Clear();
        if (_list.IsEmpty)
        {
            var empty = new StackPanel { Spacing = 8, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
            empty.Children.Add(new TextBlock { Text = "Nothing to Buy", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock
            {
                Text = "Paints you don't own that a project needs appear here, with colours that have no paint chosen yet. Completed projects aren't included.",
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            _body.Children.Add(empty);
            return;
        }

        foreach (var group in _list.Groups)
        {
            var section = Section(group.Name);
            foreach (var item in group.Items) section.Children.Add(ItemRow(item));
        }
        if (_list.Unchosen.Count > 0)
        {
            var section = Section(ShoppingList.UnchosenHeading);
            foreach (var colour in _list.Unchosen)
            {
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                TableRows.Columns(row, "24", "1*");
                TableRows.Cell(row, PaintSwatch.Make(null), 0);
                TableRows.Cell(row, TableRows.TwoLines(colour.Standard, colour.Use.ToString()), 1);
                ToolTipService.SetToolTip(row, "Choose a paint for this colour on the project's Paint tab");
                section.Children.Add(row);
            }
        }
    }

    private StackPanel Section(string title)
    {
        var section = new StackPanel { Spacing = 2 };
        section.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 0, 0, 4) });
        _body.Children.Add(section);
        return section;
    }

    private Grid ItemRow(ShoppingList.Item item)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        TableRows.Columns(row, "32", "24", "1*");
        var owned = new CheckBox { MinWidth = 0, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(owned, "Mark as owned and take it off the list");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(owned, $"Mark {item.Title} as Owned");
        owned.Click += (_, _) => MarkOwned(item);
        TableRows.Cell(row, owned, 0);
        TableRows.Cell(row, PaintSwatch.Make(item.Paint.SwatchHex), 1);
        var text = TableRows.TwoLines(item.Title, item.Detail);
        if (item.Uses.Count > 0) text.Children.Add(TableRows.Text(item.UsesLine, secondary: true));
        TableRows.Cell(row, text, 2);

        var menu = new MenuFlyout();
        var mark = new MenuFlyoutItem { Text = "Mark as Owned" };
        mark.Click += (_, _) => MarkOwned(item);
        menu.Items.Add(mark);
        var show = new MenuFlyoutItem { Text = "Show in Paints" };
        show.Click += (_, _) => _showPaint(item.Paint.Id);
        menu.Items.Add(show);
        row.ContextFlyout = menu;
        return row;
    }

    /// <summary>Ticking a paint as bought marks it owned, and it leaves the list.</summary>
    private void MarkOwned(ShoppingList.Item item)
    {
        _services.WithStore(s => { s.SetOwned(item.Paint.Id, true); return 0; });
        _services.NotifyLibraryChanged();
        Load();
    }

    private async Task Export()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.MainWindow!.AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(ShoppingList.FileName(DateTime.Now)),
            DefaultFileExtension = ".txt",
        };
        picker.FileTypeChoices.Add("Text file", [".txt"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            var date = DateTime.Now.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
            await File.WriteAllTextAsync(file.Path, _list.Text(date).Replace("\n", "\r\n"), new UTF8Encoding(true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await ReferenceActions.Message(XamlRoot, "Something Went Wrong", error.Message);
        }
    }
}
