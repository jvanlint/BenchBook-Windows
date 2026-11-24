using System.Text;
using BenchBook.App.Services;
using BenchBook.Core.Kits;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>The Kits catalogue (screens.md, "Kits catalogue"): one row per physical kit.</summary>
internal sealed partial class KitsPage : Page
{
    /// <summary>A column: its key, heading, width, whether it sorts, and whether it can be hidden.</summary>
    private sealed record Column(string Key, string Title, string Width, bool Sorts = true, bool Hideable = true);

    private static readonly Column[] Columns =
    [
        new("picture", "Picture", "48", Sorts: false),
        new("brand", "Brand", "1*", Hideable: false),
        new("name", "Name", "1.6*", Hideable: false),
        new("scale", "Scale", "60"),
        new("number", "Number", "80"),
        new("status", "Status", "120"),
        new("rating", "Rating", "80"),
        new("project", "Project", "1*"),
        new("pricePaid", "Price Paid", "84"),
        new("forSale", "For Sale", "60"),
        new("salePrice", "Sale Price", "84"),
        new("link", "Link", "44", Sorts: false),
    ];

    private readonly AppServices _services;
    private readonly TextBlock _count = new() { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
    private readonly DropDownButton _brand = new();
    private readonly DropDownButton _scale = new();
    private readonly DropDownButton _status = new();
    private readonly DropDownButton _show = new();
    private readonly ContentControl _header = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _list;
    private readonly StackPanel _empty = new() { Spacing = 8, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };

    private List<Kit> _kits = [];
    private Dictionary<string, Project> _projects = [];
    private KitFilter _filter = new();
    private string _sortKey = "brand";
    private bool _ascending = true;

    public KitsPage(AppServices services, string? selectKitId = null)
    {
        _services = services;
        _list = TableRows.List(FillRow);
        _list.DoubleTapped += async (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is Kit kit) await Edit(kit);
        };
        _list.ContextRequested += List_ContextRequested;
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Delete && Selected().Count > 0) { e.Handled = true; await Delete(Selected()); }
            else if (e.Key == Windows.System.VirtualKey.Enter && Selected().Count == 1) { e.Handled = true; await Edit(Selected()[0]); }
        };

        var import = new AppBarButton { Label = "Import CSV…", Icon = new FontIcon { Glyph = "" } };
        ToolTipService.SetToolTip(import, "Import a Scalemates stash export, or a BenchBook kits file");
        import.Click += async (_, _) => await Import();
        var export = new AppBarButton { Label = "Export CSV…", Icon = new FontIcon { Glyph = "" } };
        ToolTipService.SetToolTip(export, "Save the catalogue as a CSV file BenchBook can import");
        export.Click += async (_, _) => await Export();
        var add = new AppBarButton { Label = "Add Kit", Icon = new SymbolIcon(Symbol.Add) };
        ToolTipService.SetToolTip(add, "Add a kit to the catalogue");
        add.Click += async (_, _) => await Edit(null);
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        commands.PrimaryCommands.Add(import);
        commands.PrimaryCommands.Add(export);
        commands.PrimaryCommands.Add(add);

        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock { Text = "Kits", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        Grid.SetColumn(commands, 1);
        title.Children.Add(commands);

        ToolTipService.SetToolTip(_brand, "Show one brand's kits");
        ToolTipService.SetToolTip(_scale, "Show one scale's kits");
        ToolTipService.SetToolTip(_status, "Show owned kits with one status");
        ToolTipService.SetToolTip(_show, "Show kits you own, want, are selling or need for a build");
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in new UIElement[] { _brand, _scale, _status, _show, _count }) filters.Children.Add(child);

        var root = new Grid { Padding = new Thickness(32, 20, 32, 12), RowSpacing = 10 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        root.Children.Add(title);
        Grid.SetRow(filters, 1);
        root.Children.Add(filters);
        Grid.SetRow(_header, 2);
        root.Children.Add(_header);
        Grid.SetRow(_list, 3);
        root.Children.Add(_list);
        Grid.SetRow(_empty, 3);
        root.Children.Add(_empty);
        Content = root;
        Load(selectKitId);
    }

    // MARK: Loading

    private void Load(string? select = null)
    {
        (_kits, var projects) = _services.WithStore(s => (s.Kits(), s.Projects()));
        _projects = projects.ToDictionary(p => p.Id, Ids.Comparer);
        Apply(select);
    }

    private void Changed(string? select = null)
    {
        Load(select);
        _services.NotifyLibraryChanged();
    }

    private Column[] Visible => Columns.Where(c => !c.Hideable || !_services.Settings.HiddenKitColumns.Contains(c.Key)).ToArray();

    /// <summary>Applies the filters and sort, and rebuilds the menus with their counts.</summary>
    private void Apply(string? select = null)
    {
        var brands = PaintFilterOption.For(_kits.Select(k => k.Brand));
        if (_filter.Brand is not null && brands.All(b => b.Key != _filter.Brand)) _filter = _filter with { Brand = null };
        var scales = PaintFilterOption.For(_kits.Select(k => k.Scale ?? ""));
        if (_filter.Scale is not null && scales.All(b => b.Key != _filter.Scale)) _filter = _filter with { Scale = null };

        _brand.Content = Label("All Brands", brands, _filter.Brand);
        _brand.Flyout = FilterMenu("All Brands", brands, _filter.Brand, key => { _filter = _filter with { Brand = key }; Apply(); });
        _scale.Content = Label("All Scales", scales, _filter.Scale);
        _scale.Flyout = FilterMenu("All Scales", scales, _filter.Scale, key => { _filter = _filter with { Scale = key }; Apply(); });
        _scale.Visibility = scales.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Status counts owned kits only.
        var statuses = KitStatus.All
            .Select(s => new PaintFilterOption(s, KitText.StatusName(s), _kits.Count(k => k.Owned && KitText.Shown(k.Status) == s)))
            .Where(o => o.Count > 0 || o.Key == _filter.Status).ToList();
        _status.Content = _filter.Status is null ? "Any Status" : $"{KitText.StatusName(_filter.Status)} ({statuses.FirstOrDefault(o => o.Key == _filter.Status)?.Count ?? 0:N0})";
        _status.Flyout = FilterMenu("Any Status", statuses, _filter.Status, key => { _filter = _filter with { Status = key }; Apply(); }, sortByName: false);

        _show.Content = KitFilter.OwnershipName(_filter.Ownership);
        var show = new MenuFlyout();
        foreach (var ownership in KitFilter.Ownerships)
        {
            var item = new RadioMenuFlyoutItem { Text = KitFilter.OwnershipName(ownership), GroupName = "kitShow", IsChecked = _filter.Ownership == ownership };
            item.Click += (_, _) => { _filter = _filter with { Ownership = ownership }; Apply(); };
            show.Items.Add(item);
            if (ownership == KitFilter.All) show.Items.Add(new MenuFlyoutSeparator());
        }
        _show.Flyout = show;

        var sorted = Sort(_kits.Where(k => _filter.Includes(k, _projects))).ToList();
        _list.ItemsSource = sorted;

        // "116 kits · 4 not owned", or "12 of 116 kits · 4 not owned" while filtered.
        var notOwned = _kits.Count(k => !k.Owned);
        var counted = _filter.IsActive ? $"{sorted.Count:N0} of {KitText.Count(_kits.Count)}" : KitText.Count(_kits.Count);
        _count.Text = notOwned > 0 ? $"{counted} · {notOwned:N0} not owned" : counted;

        var columns = Visible;
        var header = TableRows.Header(columns.Select(c => c.Width).ToArray(), columns.Select(c => (c.Title, c.Sorts ? c.Key : null)).ToArray(),
            _sortKey, _ascending, key =>
            {
                _ascending = _sortKey != key || !_ascending;
                _sortKey = key;
                Apply();
            });
        header.ContextRequested += Header_ContextRequested;
        _header.Content = header;

        ShowEmpty(sorted.Count == 0);
        if (select is not null && sorted.FirstOrDefault(k => Ids.Comparer.Equals(k.Id, select)) is { } chosen)
        {
            _list.SelectedItem = chosen;
            _list.ScrollIntoView(chosen);
        }
    }

    private static string Label(string all, List<PaintFilterOption> options, string? key) =>
        key is null ? all : options.FirstOrDefault(o => o.Key == key) is { } o ? $"{o.Name} ({o.Count:N0})" : all;

    private IEnumerable<Kit> Sort(IEnumerable<Kit> kits)
    {
        var start = KitText.Sorted(kits);
        IOrderedEnumerable<Kit> Text(Func<Kit, string> key) => _ascending ? start.OrderBy(key, NaturalComparer.Instance) : start.OrderByDescending(key, NaturalComparer.Instance);
        IOrderedEnumerable<Kit> By<T>(Func<Kit, T> key) => _ascending ? start.OrderBy(key) : start.OrderByDescending(key);
        // Empty values sort last whichever way: OrderBy keeps the default order for ties.
        return _sortKey switch
        {
            "name" => Text(k => k.Name),
            "scale" => start.OrderBy(k => k.Scale is null).ThenBy(k => k.Scale ?? "", _ascending ? NaturalComparer.Instance : Comparer<string>.Create((a, b) => NaturalComparer.Instance.Compare(b, a))),
            "number" => start.OrderBy(k => k.Number is null).ThenBy(k => k.Number ?? "", _ascending ? NaturalComparer.Instance : Comparer<string>.Create((a, b) => NaturalComparer.Instance.Compare(b, a))),
            "status" => By(KitText.StatusSortKey),
            "rating" => start.OrderBy(k => KitText.Stars(k) is null).ThenBy(k => _ascending ? KitText.RatingSortKey(k) : -KitText.RatingSortKey(k)),
            "project" => start.OrderBy(k => ProjectName(k) is null).ThenBy(k => ProjectName(k) ?? "", _ascending ? NaturalComparer.Instance : Comparer<string>.Create((a, b) => NaturalComparer.Instance.Compare(b, a))),
            "pricePaid" => start.OrderBy(k => k.PricePaid is null).ThenBy(k => _ascending ? k.PricePaid ?? 0 : -(k.PricePaid ?? 0)),
            "forSale" => By(k => k.ForSale ? 0 : 1),
            "salePrice" => start.OrderBy(k => k.SalePrice is null).ThenBy(k => _ascending ? k.SalePrice ?? 0 : -(k.SalePrice ?? 0)),
            "brand" when !_ascending => start.AsEnumerable().Reverse(),
            _ => start,
        };
    }

    private string? ProjectName(Kit kit) => kit.ProjectId is { } id && _projects.TryGetValue(id, out var project) ? project.Name : null;

    private static MenuFlyout FilterMenu(string allTitle, List<PaintFilterOption> options, string? current, Action<string?> choose, bool sortByName = true)
    {
        var menu = new MenuFlyout();
        var all = new RadioMenuFlyoutItem { Text = allTitle, GroupName = allTitle, IsChecked = current is null };
        all.Click += (_, _) => choose(null);
        menu.Items.Add(all);
        if (options.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var option in sortByName ? options : options.AsEnumerable())
        {
            var item = new RadioMenuFlyoutItem { Text = $"{option.Name} ({option.Count:N0})", GroupName = allTitle, IsChecked = current == option.Key };
            var key = option.Key;
            item.Click += (_, _) => choose(key);
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>Right-click a heading to hide columns, except Brand and Name.</summary>
    private void Header_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        var menu = new MenuFlyout();
        foreach (var column in Columns.Where(c => c.Hideable))
        {
            var item = new ToggleMenuFlyoutItem { Text = column.Title, IsChecked = !_services.Settings.HiddenKitColumns.Contains(column.Key) };
            item.Click += (_, _) =>
            {
                var hidden = _services.Settings.HiddenKitColumns;
                if (!hidden.Remove(column.Key)) hidden.Add(column.Key);
                _services.SaveSettings();
                Apply();
            };
            menu.Items.Add(item);
        }
        if (args.TryGetPosition(sender, out var point)) menu.ShowAt(sender, new FlyoutShowOptions { Position = point });
        else menu.ShowAt((FrameworkElement)sender);
    }

    private void ShowEmpty(bool empty)
    {
        _empty.Children.Clear();
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _header.Visibility = _list.Visibility;
        if (!empty) return;
        var (title, message) = _kits.Count == 0
            ? ("No Kits", "Add the kits in your stash, and the ones you want, or import a Scalemates stash export. Each box is its own entry, with its own status.")
            : ("No Matching Kits", "No kits match the chosen brand, scale, status and ownership.");
        _empty.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        if (_kits.Count == 0)
        {
            var add = new Button { Content = "Add Kit" };
            add.Click += async (_, _) => await Edit(null);
            var import = new Button { Content = "Import CSV…" };
            import.Click += async (_, _) => await Import();
            buttons.Children.Add(add);
            buttons.Children.Add(import);
        }
        else
        {
            var showAll = new Button { Content = "Show All Kits" };
            showAll.Click += (_, _) => { _filter = new KitFilter(); Apply(); };
            buttons.Children.Add(showAll);
        }
        _empty.Children.Add(buttons);
    }

    // MARK: Rows

    private void FillRow(Grid grid, object item)
    {
        var kit = (Kit)item;
        grid.DataContext = kit;
        var columns = Visible;
        TableRows.Columns(grid, columns.Select(c => c.Width).ToArray());
        for (var i = 0; i < columns.Length; i++)
            if (Cell(kit, columns[i].Key) is { } cell) TableRows.Cell(grid, cell, i);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"{KitText.DisplayName(kit)}, {KitText.StatusOrNotOwned(kit)}");
    }

    private FrameworkElement? Cell(Kit kit, string key)
    {
        switch (key)
        {
            case "picture": return KitViews.Picture(_services, kit.PictureAssetId);
            case "brand": return TableRows.Text(kit.Brand);
            case "name":
                var name = TableRows.Text(kit.Name);
                if (kit.Notes is { } notes) ToolTipService.SetToolTip(name, notes);
                return name;
            case "scale": return TableRows.Text(kit.Scale ?? "", secondary: true);
            case "number": return TableRows.Text(kit.Number ?? "", secondary: true);
            case "status":
                var status = TableRows.Text(KitText.StatusOrNotOwned(kit), secondary: !kit.Owned);
                if (kit.Owned) ToolTipService.SetToolTip(status, KitText.LongName(kit.Status));
                return status;
            case "rating":
                var stars = TableRows.Text(KitViews.Stars(kit));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stars, KitText.Stars(kit) is { } n ? $"{n} {(n == 1 ? "star" : "stars")}" : "No rating");
                return stars;
            case "project":
                if (ProjectName(kit) is not { } project) return null;
                var text = TableRows.Text(project, secondary: true);
                if (KitRules.IsNeeded(kit, _projects)) ToolTipService.SetToolTip(text, $"Needed for {project}");
                return text;
            case "pricePaid": return TableRows.Text(kit.PricePaid is { } paid ? KitPrice.Display(paid) : "");
            case "forSale":
                var box = new CheckBox { IsChecked = kit.ForSale, MinWidth = 0, Padding = new Thickness(0) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"For sale: {KitText.DisplayName(kit)}");
                box.Click += (_, _) => SetForSale([kit], box.IsChecked == true);
                return box;
            case "salePrice": return TableRows.Text(kit.SalePrice is { } sale ? KitPrice.Display(sale) : "", secondary: !kit.ForSale);
            case "link": return KitViews.LinkButton(kit);
            default: return null;
        }
    }

    private List<Kit> Selected() => _list.SelectedItems.OfType<Kit>().ToList();

    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var element = args.OriginalSource as FrameworkElement;
        while (element is not null && element.DataContext is not Kit) element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        if (element?.DataContext is not Kit clicked) return;
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
            var edit = new MenuFlyoutItem { Text = "Edit Kit…", Icon = new SymbolIcon(Symbol.Edit) };
            edit.Click += async (_, _) => await Edit(clicked);
            menu.Items.Add(edit);
            var copy = new MenuFlyoutItem { Text = "Add Another Copy", Icon = new SymbolIcon(Symbol.Copy) };
            copy.Click += (_, _) =>
            {
                var added = _services.WithStore(s => KitService.AddCopy(s, clicked));
                Changed(added.Id);
            };
            menu.Items.Add(copy);
            if (clicked.Link is { } link)
            {
                var open = new MenuFlyoutItem { Text = "Open Link", Icon = new SymbolIcon(Symbol.World) };
                open.Click += async (_, _) => await Opener.OpenLink(link);
                menu.Items.Add(open);
            }
            if (clicked.ProjectId is { } projectId)
            {
                var showProject = new MenuFlyoutItem { Text = "Show Project" };
                showProject.Click += (_, _) => _services.ShowProject?.Invoke(projectId);
                menu.Items.Add(showProject);
            }
            else
            {
                var newProject = new MenuFlyoutItem { Text = "New Project from Kit…" };
                newProject.Click += (_, _) => _services.NewProjectFromKit?.Invoke(clicked);
                menu.Items.Add(newProject);
            }
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(KitViews.StatusMenu(_services, chosen, () => Changed()));
        var allOwned = chosen.All(k => k.Owned);
        var own = new MenuFlyoutItem { Text = allOwned ? "Mark as Not Owned" : "Mark as Owned" };
        own.Click += (_, _) => { _services.WithStore(s => { KitService.SetOwned(s, chosen, !allOwned); return 0; }); Changed(); };
        menu.Items.Add(own);
        var allForSale = chosen.All(k => k.ForSale);
        var sale = new MenuFlyoutItem { Text = allForSale ? "Mark as Not For Sale" : "Mark as For Sale" };
        sale.Click += (_, _) => SetForSale(chosen, !allForSale);
        menu.Items.Add(sale);

        menu.Items.Add(new MenuFlyoutSeparator());
        var move = new MenuFlyoutSubItem { Text = "Move to Project" };
        foreach (var project in _projects.Values.OrderBy(p => p.Name, NaturalComparer.Instance))
        {
            var item = new MenuFlyoutItem { Text = project.Name };
            // The kit's own project is dimmed.
            item.IsEnabled = !chosen.All(k => Ids.Comparer.Equals(k.ProjectId, project.Id));
            item.Click += (_, _) => { _services.WithStore(s => { KitService.Add(s, chosen, project); return 0; }); Changed(); };
            move.Items.Add(item);
        }
        move.IsEnabled = _projects.Count > 0;
        menu.Items.Add(move);
        var inProjects = chosen.Where(k => k.ProjectId is not null).ToList();
        if (inProjects.Count > 0)
        {
            var remove = new MenuFlyoutItem { Text = inProjects.Count == 1 ? "Remove from Project" : $"Remove {inProjects.Count:N0} Kits from Projects" };
            remove.Click += (_, _) => { _services.WithStore(s => { KitService.Remove(s, inProjects); return 0; }); Changed(); };
            menu.Items.Add(remove);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = chosen.Count == 1 ? "Delete Kit…" : $"Delete {chosen.Count:N0} Kits…", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await Delete(chosen);
        menu.Items.Add(delete);
        if (args.TryGetPosition(_list, out var point)) menu.ShowAt(_list, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(element);
    }

    // MARK: Commands

    private void SetForSale(List<Kit> kits, bool forSale)
    {
        _services.WithStore(s => { KitService.SetForSale(s, kits, forSale); return 0; });
        Changed();
    }

    private async Task Edit(Kit? kit)
    {
        if (await KitViews.Edit(XamlRoot, _services, kit) is { } saved) Changed(saved.Id);
    }

    private async Task Delete(List<Kit> kits)
    {
        if (await KitViews.Delete(XamlRoot, _services, kits)) Changed();
    }

    private async Task Import()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow!.AppWindow.Id);
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".txt");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        KitImportSummary summary;
        try
        {
            var text = await Task.Run(() => ReadText(file.Path));
            summary = await _services.WithStoreAsync(s => KitCsvImporter.Import(s, text));
        }
        catch (Exception error) when (error is KitImportException or IOException or UnauthorizedAccessException or SqliteException)
        {
            await ReferenceActions.Message(XamlRoot, "Couldn't Import Kits", error.Message);
            return;
        }
        Changed();
        await ReferenceActions.Message(XamlRoot, summary.Title, string.Join("\n", summary.Lines()));
    }

    /// <summary>UTF-8, or Latin-1 when the file isn't valid UTF-8 (some spreadsheets save that way).</summary>
    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private async Task Export()
    {
        var notes = new CheckBox { Content = "Include notes" };
        var panel = new StackPanel { Spacing = 12, MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = KitCsvExporter.ExportMessage(_kits.Count), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(notes);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Export Kits", Content = panel,
            PrimaryButtonText = "Export…", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.MainWindow!.AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(KitCsvExporter.FileName(DateTime.Now)),
            DefaultFileExtension = ".csv",
        };
        picker.FileTypeChoices.Add("CSV file", [".csv"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            await File.WriteAllTextAsync(file.Path, KitCsvExporter.Csv(_kits, notes.IsChecked == true), new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await ReferenceActions.Message(XamlRoot, "Couldn't Export Kits", error.Message);
        }
    }
}
