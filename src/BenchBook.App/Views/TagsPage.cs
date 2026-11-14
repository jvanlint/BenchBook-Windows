using BenchBook.App.Services;
using BenchBook.Core.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>The tag manager (screens.md, "Tags"): colour, name, references and projects for every tag.</summary>
internal sealed partial class TagsPage : Page
{
    private readonly AppServices _services;
    private readonly Action<string> _showTag;
    private readonly TextBlock _count = new() { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended };
    private readonly AppBarButton _removeUnused = new() { Label = "Remove Unused Tags", Icon = new FontIcon { Glyph = "\uE74D" } };
    private readonly StackPanel _empty = new() { Spacing = 8, MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };
    private List<TagUsage> _tags = [];

    public TagsPage(AppServices services, Action<string> showTag)
    {
        _services = services;
        _showTag = showTag;

        var newTag = new AppBarButton { Label = "New Tag", Icon = new SymbolIcon(Symbol.Add) };
        newTag.Click += async (_, _) => { if (await TagCommands.NewTag(XamlRoot, _services)) Changed(); };
        _removeUnused.Click += (_, _) =>
        {
            _services.WithStore(s => s.RemoveUnusedTags());
            Changed();
        };
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        commands.PrimaryCommands.Add(newTag);
        commands.PrimaryCommands.Add(_removeUnused);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = "Tags", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        titles.Children.Add(_count);
        header.Children.Add(titles);
        Grid.SetColumn(commands, 1);
        header.Children.Add(commands);

        _list.Header = Row("Colour", "Name", "References", "Projects", header: true);
        _list.DoubleTapped += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is TagUsage usage) _showTag(usage.Tag.Id);
        };
        _list.ContextRequested += List_ContextRequested;
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Delete && Selected().Count > 0)
            {
                e.Handled = true;
                if (await TagCommands.Delete(XamlRoot, _services, Selected())) Changed();
            }
        };

        _empty.Children.Add(new TextBlock { Text = "No Tags", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock
        {
            Text = "Tags you add to references appear here, where you can rename, colour, merge and delete them.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var root = new Grid { Padding = new Thickness(32, 20, 32, 12), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        Grid.SetRow(_list, 1);
        Grid.SetRow(_empty, 1);
        root.Children.Add(_list);
        root.Children.Add(_empty);
        Content = root;
        Load();
    }

    private void Changed()
    {
        Load();
        _services.NotifyLibraryChanged();
    }

    private void Load()
    {
        _tags = _services.WithStore(s => s.TagsByUsage());
        _count.Text = $"{_tags.Count:N0} {(_tags.Count == 1 ? "tag" : "tags")}";
        _list.Items.Clear();
        foreach (var usage in _tags)
        {
            var row = Row(null, usage.Tag.Name, usage.References.ToString("N0"), usage.Projects.ToString("N0"), header: false, color: usage.Tag.Color);
            row.DataContext = usage;
            _list.Items.Add(row);
        }
        var unused = _tags.Count(t => t.References == 0);
        _removeUnused.IsEnabled = unused > 0;
        ToolTipService.SetToolTip(_removeUnused, unused > 0 ? $"Delete the {unused:N0} tags no reference uses" : "Every tag is in use");
        _empty.Visibility = _tags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = _tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Grid Row(string? colour, string name, string references, string projects, bool header, string? color = null)
    {
        var grid = new Grid { ColumnSpacing = 16, Padding = new Thickness(header ? 12 : 0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        UIElement first = colour is not null ? Cell(colour, header) : TagCommands.Dot(color, 14);
        if (first is FrameworkElement element) element.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(first);
        var cells = new[] { Cell(name, header), Cell(references, header), Cell(projects, header) };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i + 1);
            grid.Children.Add(cells[i]);
        }
        if (!header) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"{name}, {references} references, {projects} projects");
        return grid;
    }

    private static TextBlock Cell(string text, bool header)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        // Only set for headers: a null Foreground draws the text with no brush at all.
        if (header)
        {
            block.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
            block.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
        return block;
    }

    private List<TagUsage> Selected() =>
        _list.SelectedItems.OfType<FrameworkElement>().Select(e => e.DataContext).OfType<TagUsage>().ToList();

    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var element = args.OriginalSource as FrameworkElement;
        while (element is not null && element.DataContext is not TagUsage) element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        if (element?.DataContext is not TagUsage clicked) return;
        args.Handled = true;
        var row = _list.Items.OfType<FrameworkElement>().First(r => ReferenceEquals(r.DataContext, clicked));
        if (!_list.SelectedItems.Contains(row))
        {
            _list.SelectedItems.Clear();
            _list.SelectedItems.Add(row);
        }
        var chosen = Selected();

        var menu = new MenuFlyout();
        if (chosen.Count == 1)
        {
            var show = new MenuFlyoutItem { Text = "Show References" };
            show.Click += (_, _) => _showTag(clicked.Tag.Id);
            menu.Items.Add(show);
            var rename = new MenuFlyoutItem { Text = "Rename…" };
            rename.Click += async (_, _) => { if (await TagCommands.Rename(XamlRoot, _services, clicked.Tag)) Changed(); };
            menu.Items.Add(rename);
        }
        menu.Items.Add(TagCommands.ColourMenu(_services, chosen.Select(c => c.Tag).ToList(), () => { Changed(); return Task.CompletedTask; }));
        if (chosen.Count > 1)
        {
            var merge = new MenuFlyoutItem { Text = $"Merge {chosen.Count:N0} Tags…" };
            merge.Click += async (_, _) => { if (await TagCommands.Merge(XamlRoot, _services, chosen)) Changed(); };
            menu.Items.Add(merge);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = chosen.Count == 1 ? "Delete Tag…" : $"Delete {chosen.Count:N0} Tags…", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => { if (await TagCommands.Delete(XamlRoot, _services, chosen)) Changed(); };
        menu.Items.Add(delete);

        if (args.TryGetPosition(_list, out var point)) menu.ShowAt(_list, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(row);
    }
}
