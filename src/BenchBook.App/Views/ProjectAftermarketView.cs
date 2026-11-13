using BenchBook.App.Services;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BenchBook.App.Views;

/// <summary>A project's Aftermarket tab (screens.md, "Aftermarket tab").</summary>
internal sealed partial class ProjectAftermarketView : UserControl
{
    private static readonly string[] Widths = ["48", "2*", "1*", "1*", "150", "44"];

    private readonly AppServices _services;
    private readonly Project _project;
    private readonly TextBlock _summary = new() { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
    private readonly ListView _list;
    private readonly FrameworkElement _header;
    private readonly StackPanel _empty = new() { Spacing = 10, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private Dictionary<string, Asset> _assets = [];

    public ProjectAftermarketView(AppServices services, Project project)
    {
        _services = services;
        _project = project;
        _list = TableRows.List(FillRow);
        _list.DoubleTapped += async (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is AftermarketItem item) await Edit(item);
        };
        _list.ContextRequested += List_ContextRequested;
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Delete && Selected().Count > 0) { e.Handled = true; await Delete(Selected()); }
        };
        _header = TableRows.Header(Widths, [("Picture", null), ("Part", null), ("Type", null), ("Scale or Fits", null), ("Status", null), ("Link", null)], null, true, null);

        var add = new Button { Content = "Add Part…" };
        ToolTipService.SetToolTip(add, "Add decals, resin or other parts for this build");
        add.Click += async (_, _) => await Edit(null);
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_summary);
        Grid.SetColumn(add, 1);
        top.Children.Add(add);

        _empty.Children.Add(new TextBlock { Text = "No Aftermarket Parts", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock
        {
            Text = "Keep track of decals, resin, photo-etch and other parts you're using or considering for this build.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var emptyAdd = new Button { Content = "Add Part…", HorizontalAlignment = HorizontalAlignment.Center };
        emptyAdd.Click += async (_, _) => await Edit(null);
        _empty.Children.Add(emptyAdd);

        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(top);
        Grid.SetRow(_header, 1);
        root.Children.Add(_header);
        Grid.SetRow(_list, 2);
        root.Children.Add(_list);
        Grid.SetRow(_empty, 2);
        root.Children.Add(_empty);
        Content = root;
        Load();
    }

    public Task AddPart() => Edit(null);

    private void Load()
    {
        var (items, assets) = _services.WithStore(s => (s.AftermarketItems(_project.Id), s.ReadSnapshot().Assets));
        _assets = assets.ToDictionary(a => a.Id, Ids.Comparer);
        var sorted = AftermarketKinds.Sorted(items);
        _list.ItemsSource = sorted;

        // "3 parts · 1 considering · 2 in stash".
        var parts = new List<string> { $"{sorted.Count:N0} {(sorted.Count == 1 ? "part" : "parts")}" };
        parts.AddRange(sorted.GroupBy(i => i.Status).OrderBy(g => AftermarketKinds.StatusRank(g.Key))
            .Select(g => $"{g.Count():N0} {AftermarketKinds.StatusName(g.Key).ToLowerInvariant()}"));
        _summary.Text = string.Join(" · ", parts);

        var empty = sorted.Count == 0;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _header.Visibility = _list.Visibility;
        _summary.Visibility = _list.Visibility;
    }

    private void Changed()
    {
        Load();
        _services.NotifyLibraryChanged();
    }

    private void FillRow(Grid grid, object value)
    {
        var item = (AftermarketItem)value;
        grid.DataContext = item;
        TableRows.Columns(grid, Widths);

        var picture = new Grid { Width = 40, Height = 40, CornerRadius = new CornerRadius(4), Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] };
        picture.Children.Add(new FontIcon { Glyph = "\uE7B8", FontSize = 16, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
        if (item.ImageAssetId is { } assetId && _assets.TryGetValue(assetId, out var asset)) _ = LoadPicture(picture, asset);
        TableRows.Cell(grid, picture, 0);

        TableRows.Cell(grid, TableRows.TwoLines(item.ProductName, AftermarketKinds.SecondaryLine(item)), 1);
        TableRows.Cell(grid, TableRows.Text(AftermarketKinds.TypeName(item.Type), secondary: true), 2);
        TableRows.Cell(grid, TableRows.Text(item.ScaleCompatibility ?? "", secondary: true), 3);

        var status = new ComboBox { MinWidth = 140 };
        foreach (var name in AftermarketKinds.Statuses) status.Items.Add(AftermarketKinds.StatusName(name));
        var known = AftermarketKinds.Statuses.ToList().IndexOf(item.Status);
        if (known < 0) status.Items.Add(item.Status); // A status from a newer BenchBook is shown as it is.
        status.SelectedIndex = known >= 0 ? known : status.Items.Count - 1;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(status, $"Status of {item.ProductName}");
        status.SelectionChanged += (_, _) =>
        {
            if (status.SelectedIndex < 0 || status.SelectedIndex >= AftermarketKinds.Statuses.Count) return;
            _services.WithStore(s => { AftermarketService.SetStatus(s, new[] { item }, AftermarketKinds.Statuses[status.SelectedIndex]); return 0; });
            Changed();
        };
        TableRows.Cell(grid, status, 4);

        if (item.ProductUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var page))
        {
            var link = new Button { Content = new FontIcon { Glyph = "\uE71B", FontSize = 14 }, Padding = new Thickness(6) };
            ToolTipService.SetToolTip(link, $"Open {page.Host}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, "Open Product Page");
            link.Click += async (_, _) => await Opener.OpenLink(url);
            TableRows.Cell(grid, link, 5);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"{item.ProductName}, {AftermarketKinds.TypeName(item.Type)}, {AftermarketKinds.StatusName(item.Status)}");
    }

    private async Task LoadPicture(Grid holder, Asset asset)
    {
        if (await _services.Thumbnails.PathFor(asset) is not { } path) return;
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 80 };
            using (var stream = File.OpenRead(path)) await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            holder.Children.Clear();
            holder.Children.Add(new Image { Source = bitmap, Stretch = Stretch.UniformToFill });
        }
        catch (Exception)
        {
            // The icon stays.
        }
    }

    private List<AftermarketItem> Selected() => _list.SelectedItems.OfType<AftermarketItem>().ToList();

    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var element = args.OriginalSource as FrameworkElement;
        while (element is not null && element.DataContext is not AftermarketItem) element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        if (element?.DataContext is not AftermarketItem clicked) return;
        args.Handled = true;
        if (!_list.SelectedItems.Contains(clicked))
        {
            _list.SelectedItems.Clear();
            _list.SelectedItems.Add(clicked);
        }
        var chosen = Selected();
        var menu = new MenuFlyout();
        if (chosen.Count == 1)
        {
            var edit = new MenuFlyoutItem { Text = "Edit…", Icon = new SymbolIcon(Symbol.Edit) };
            edit.Click += async (_, _) => await Edit(clicked);
            menu.Items.Add(edit);
            if (clicked.ProductUrl is { } url)
            {
                var open = new MenuFlyoutItem { Text = "Open Product Page", Icon = new SymbolIcon(Symbol.World) };
                open.Click += async (_, _) => await Opener.OpenLink(url);
                menu.Items.Add(open);
            }
        }
        var status = new MenuFlyoutSubItem { Text = "Status" };
        foreach (var name in AftermarketKinds.Statuses)
        {
            var option = new RadioMenuFlyoutItem { Text = AftermarketKinds.StatusName(name), GroupName = "partStatus", IsChecked = chosen.All(i => i.Status == name) };
            option.Click += (_, _) =>
            {
                _services.WithStore(s => { AftermarketService.SetStatus(s, chosen, name); return 0; });
                Changed();
            };
            status.Items.Add(option);
        }
        menu.Items.Add(status);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = chosen.Count == 1 ? "Delete Part…" : $"Delete {chosen.Count:N0} Parts…", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await Delete(chosen);
        menu.Items.Add(delete);
        if (args.TryGetPosition(_list, out var point)) menu.ShowAt(_list, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(element);
    }

    private async Task Edit(AftermarketItem? item)
    {
        var dialog = new AftermarketEditorDialog(_services, _project, item) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) Changed();
    }

    private async Task Delete(List<AftermarketItem> items)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = items.Count == 1 ? $"Delete “{items[0].ProductName}”?" : $"Delete {items.Count:N0} Parts?",
            Content = new TextBlock { Text = "Their details and pictures are removed from this project.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            _services.WithStore(s => { AftermarketService.Delete(s, _services.Location, items.Select(i => i.Id).ToList()); return 0; });
        }
        catch (SqliteException error)
        {
            await ReferenceActions.Message(XamlRoot, "Something Went Wrong", error.Message);
        }
        Changed();
    }
}
