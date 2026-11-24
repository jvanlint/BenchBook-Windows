using System.Text;
using BenchBook.App.Services;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>The paint catalogue (screens.md, "Paints catalogue").</summary>
internal sealed partial class PaintsPage : Page
{
    private static readonly string[] Widths = ["24", "1*", "1*", "90", "1.5*", "1*", "0.8*", "1*", "60", "1*"];

    private readonly AppServices _services;
    private readonly TextBlock _count = new() { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
    private readonly DropDownButton _brand = new();
    private readonly DropDownButton _range = new();
    private readonly DropDownButton _type = new();
    private readonly ToggleButton _ownedOnly = new() { Content = "Owned Only" };
    private readonly ContentControl _header = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _list;
    private readonly StackPanel _empty = new() { Spacing = 8, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };

    private List<Paint> _paints = [];
    private PaintCatalogue _catalogue = new([], [], []);
    private Dictionary<string, List<string>> _uses = [];
    private string? _brandKey, _rangeKey;
    /// <summary>Null for All Types, "" for Not Set, else a type's raw value.</summary>
    private string? _typeKey;
    private string _sortKey = "brand";
    private bool _ascending = true;

    public PaintsPage(AppServices services, string? selectPaintId = null)
    {
        _services = services;
        _list = TableRows.List(FillRow);
        _list.DoubleTapped += async (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is Paint paint) await Edit(paint);
        };
        _list.ContextRequested += List_ContextRequested;
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Delete && Selected().Count > 0) { e.Handled = true; await Delete(Selected()); }
            else if (e.Key == Windows.System.VirtualKey.Enter && Selected().Count == 1) { e.Handled = true; await Edit(Selected()[0]); }
        };

        var importMenu = new MenuFlyout();
        var importCsv = new MenuFlyoutItem { Text = "Import CSV…" };
        importCsv.Click += async (_, _) => await Import();
        var importScalemates = new MenuFlyoutItem { Text = "Import from Scalemates…" };
        importScalemates.Click += async (_, _) => await ImportScalemates();
        importMenu.Items.Add(importCsv);
        importMenu.Items.Add(importScalemates);
        var import = new AppBarButton { Label = "Import", Icon = new FontIcon { Glyph = "\uE8B5" }, Flyout = importMenu };
        ToolTipService.SetToolTip(import, "Import paints from a CSV file, or mark the paints in a Scalemates stash export as owned");
        var export = new AppBarButton { Label = "Export CSV…", Icon = new FontIcon { Glyph = "\uEDE1" } };
        ToolTipService.SetToolTip(export, "Save the catalogue as a CSV file that another BenchBook can import");
        export.Click += async (_, _) => await Export();
        var addMix = new AppBarButton { Label = "Add Mix", Icon = new FontIcon { Glyph = "\uE790" } };
        ToolTipService.SetToolTip(addMix, "Add a custom mix, with a recipe of paints from the catalogue");
        addMix.Click += async (_, _) => await Edit(null, newMix: true);
        var add = new AppBarButton { Label = "Add Paint", Icon = new SymbolIcon(Symbol.Add) };
        ToolTipService.SetToolTip(add, "Add a paint to the catalogue");
        add.Click += async (_, _) => await Edit(null);
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        commands.PrimaryCommands.Add(import);
        commands.PrimaryCommands.Add(export);
        commands.PrimaryCommands.Add(addMix);
        commands.PrimaryCommands.Add(add);

        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock { Text = "Paints", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        Grid.SetColumn(commands, 1);
        title.Children.Add(commands);

        ToolTipService.SetToolTip(_brand, "Show one brand's paints");
        ToolTipService.SetToolTip(_range, "Show one range's paints");
        ToolTipService.SetToolTip(_type, "Show one type of paint, such as lacquers");
        ToolTipService.SetToolTip(_ownedOnly, "Show only paints you own");
        _ownedOnly.Click += (_, _) => Apply();
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filters.Children.Add(_brand);
        filters.Children.Add(_range);
        filters.Children.Add(_type);
        filters.Children.Add(_ownedOnly);
        filters.Children.Add(_count);

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
        Load(selectPaintId);
    }

    // MARK: Loading

    private void Load(string? select = null)
    {
        (_catalogue, _uses) = _services.WithStore(s => (PaintCatalogue.Load(s), s.PaintProjects()));
        _paints = _catalogue.ById.Values.ToList();
        Apply(select);
    }

    private void Changed(string? select = null)
    {
        Load(select);
        _services.NotifyLibraryChanged();
    }

    /// <summary>Applies the filters and sort, and rebuilds the menus with their counts.</summary>
    private void Apply(string? select = null)
    {
        var brands = PaintFilterOption.For(_paints.Select(p => p.Brand));
        if (_brandKey is not null && brands.All(b => b.Key != _brandKey)) _brandKey = null;
        var inBrand = _paints.Where(p => _brandKey is null || PaintIdentity.Words(p.Brand) == _brandKey).ToList();
        var ranges = PaintFilterOption.For(inBrand.Select(p => p.Range));
        if (_rangeKey is not null && ranges.All(r => r.Key != _rangeKey)) _rangeKey = null;

        _brand.Content = _brandKey is null ? "All Brands" : brands.First(b => b.Key == _brandKey) is var b ? $"{b.Name} ({b.Count:N0})" : "";
        _brand.Flyout = FilterMenu("All Brands", brands, _brandKey, key => { _brandKey = key; _rangeKey = null; Apply(); });
        _range.Content = _rangeKey is null ? "All Ranges" : ranges.First(r => r.Key == _rangeKey) is var r ? $"{r.Name} ({r.Count:N0})" : "";
        _range.Flyout = FilterMenu("All Ranges", ranges, _rangeKey, key => { _rangeKey = key; Apply(); });
        _range.Visibility = ranges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Each type some paint has, in menu order, then Not Set. An unknown type counts as Not Set.
        static string TypeKey(Paint p) => PaintType.FromRaw(p.Type)?.Raw ?? "";
        var typeCounts = _paints.GroupBy(TypeKey).ToDictionary(g => g.Key, g => g.Count());
        if (_typeKey is not null && !typeCounts.ContainsKey(_typeKey)) _typeKey = null;
        _type.Content = _typeKey switch
        {
            null => "All Types",
            "" => "Type Not Set",
            var key => $"{PaintType.FromRaw(key)!.Shown} ({typeCounts[key]:N0})",
        };
        _type.Flyout = TypeMenu(typeCounts);

        var visible = inBrand
            .Where(p => _rangeKey is null || PaintIdentity.Words(p.Range) == _rangeKey)
            .Where(p => _typeKey is null || TypeKey(p) == _typeKey)
            .Where(p => _ownedOnly.IsChecked != true || _catalogue.IsOwned(p));
        var sorted = Sort(visible).ToList();
        _list.ItemsSource = sorted;

        var owned = _paints.Count(_catalogue.IsOwned);
        var filtered = _brandKey is not null || _rangeKey is not null || _typeKey is not null || _ownedOnly.IsChecked == true;
        _count.Text = filtered
            ? $"{sorted.Count:N0} of {_paints.Count:N0} paints · {owned:N0} owned"
            : $"{_paints.Count:N0} {(_paints.Count == 1 ? "paint" : "paints")} · {owned:N0} owned";

        _header.Content = TableRows.Header(Widths,
            [("", null), ("Brand", "brand"), ("Range", "range"), ("Code", "code"), ("Name", "name"), ("Standard", "standard"), ("Match", "match"), ("Type", "type"), ("Owned", null), ("Projects", null)],
            _sortKey, _ascending, key =>
            {
                _ascending = _sortKey != key || !_ascending;
                _sortKey = key;
                Apply();
            });

        ShowEmpty(sorted.Count == 0, filtered);
        if (select is not null && sorted.FirstOrDefault(p => Ids.Comparer.Equals(p.Id, select)) is { } chosen)
        {
            _list.SelectedItem = chosen;
            _list.ScrollIntoView(chosen);
        }
    }

    /// <summary>Match sorts Exact, Closest, No Match, then Not Set; Type in menu order with Not Set last.</summary>
    private static int MatchSortKey(Paint p) =>
        p.Standard is not null && PaintMatch.FromRaw(p.Match) is { } m ? PaintMatch.All.Count - 1 - PaintMatch.All.ToList().IndexOf(m) : PaintMatch.All.Count;

    private static int TypeSortKey(Paint p) =>
        PaintType.FromRaw(p.Type) is { } t ? PaintType.All.ToList().IndexOf(t) : PaintType.All.Count;

    private IEnumerable<Paint> Sort(IEnumerable<Paint> paints)
    {
        if (_sortKey is "match" or "type")
        {
            Func<Paint, int> rank = _sortKey == "match" ? MatchSortKey : TypeSortKey;
            var byRank = _ascending ? paints.OrderBy(rank) : paints.OrderByDescending(rank);
            return byRank.ThenBy(p => p.Brand, NaturalComparer.Instance).ThenBy(p => p.Code, NaturalComparer.Instance);
        }
        Func<Paint, string> key = _sortKey switch
        {
            "range" => p => p.Range,
            "code" => p => p.Code,
            "name" => p => p.Name,
            "standard" => p => p.Standard?.DisplayText ?? "",
            _ => p => p.Brand,
        };
        // Ties fall back to brand, then code.
        var ordered = _ascending ? paints.OrderBy(key, NaturalComparer.Instance) : paints.OrderByDescending(key, NaturalComparer.Instance);
        return ordered.ThenBy(p => p.Brand, NaturalComparer.Instance).ThenBy(p => p.Code, NaturalComparer.Instance);
    }

    private MenuFlyout TypeMenu(Dictionary<string, int> counts)
    {
        var menu = new MenuFlyout();
        void Add(string text, string? key, string? hint = null)
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "types", IsChecked = _typeKey == key };
            if (hint is not null) ToolTipService.SetToolTip(item, hint);
            item.Click += (_, _) => { _typeKey = key; Apply(); };
            menu.Items.Add(item);
        }
        Add("All Types", null);
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var type in PaintType.All.Where(t => counts.ContainsKey(t.Raw))) Add($"{type.Shown} ({counts[type.Raw]:N0})", type.Raw, type.Hint);
        if (counts.TryGetValue("", out var notSet)) Add($"Not Set ({notSet:N0})", "");
        return menu;
    }

    private static MenuFlyout FilterMenu(string allTitle, List<PaintFilterOption> options, string? current, Action<string?> choose)
    {
        var menu = new MenuFlyout();
        var all = new RadioMenuFlyoutItem { Text = allTitle, GroupName = allTitle, IsChecked = current is null };
        all.Click += (_, _) => choose(null);
        menu.Items.Add(all);
        if (options.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var option in options)
        {
            var item = new RadioMenuFlyoutItem { Text = $"{option.Name} ({option.Count:N0})", GroupName = allTitle, IsChecked = current == option.Key };
            var key = option.Key;
            item.Click += (_, _) => choose(key);
            menu.Items.Add(item);
        }
        return menu;
    }

    private void ShowEmpty(bool empty, bool filtered)
    {
        _empty.Children.Clear();
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _header.Visibility = _list.Visibility;
        if (!empty) return;
        string title, message;
        if (_paints.Count == 0)
        {
            title = "No Paints";
            message = "Add the paints you use, import a CSV file with Brand, Range, Code, Name, Standard, Type, Colour and Owned columns, or import your paint stash from Scalemates.";
        }
        else if (_ownedOnly.IsChecked == true && _brandKey is null && _rangeKey is null && _typeKey is null)
        {
            title = "No Owned Paints";
            message = "Mark the paints you have as owned, in the table or when editing a paint.";
        }
        else
        {
            title = "No Matching Paints";
            message = "No paints match the chosen brand, range, type and ownership.";
        }
        _empty.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        if (filtered)
        {
            var showAll = new Button { Content = "Show All Paints", HorizontalAlignment = HorizontalAlignment.Center };
            showAll.Click += (_, _) => { _brandKey = null; _rangeKey = null; _typeKey = null; _ownedOnly.IsChecked = false; Apply(); };
            _empty.Children.Add(showAll);
        }
        else if (_paints.Count == 0)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            var add = new Button { Content = "Add Paint" };
            add.Click += async (_, _) => await Edit(null);
            var csv = new Button { Content = "Import CSV…" };
            csv.Click += async (_, _) => await Import();
            var scalemates = new Button { Content = "Import from Scalemates…" };
            scalemates.Click += async (_, _) => await ImportScalemates();
            buttons.Children.Add(add);
            buttons.Children.Add(csv);
            buttons.Children.Add(scalemates);
            _empty.Children.Add(buttons);
        }
    }

    // MARK: Rows

    private void FillRow(Grid grid, object item)
    {
        var paint = (Paint)item;
        grid.DataContext = paint;
        TableRows.Columns(grid, Widths);
        TableRows.Cell(grid, PaintSwatch.Make(paint), 0);
        TableRows.Cell(grid, TableRows.Text(paint.Brand), 1);
        TableRows.Cell(grid, TableRows.Text(paint.Range, secondary: true), 2);
        TableRows.Cell(grid, TableRows.Text(paint.Code), 3);
        if (paint.IsMix)
        {
            // A mix's name has "Mix" after it, with its recipe as help.
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            name.Children.Add(TableRows.Text(paint.Name));
            name.Children.Add(TableRows.Text("Mix", secondary: true));
            ToolTipService.SetToolTip(name, _catalogue.SecondaryLine(paint));
            TableRows.Cell(grid, name, 4);
        }
        else TableRows.Cell(grid, TableRows.Text(paint.Name), 4);
        TableRows.Cell(grid, TableRows.Text(paint.Standard?.DisplayText ?? "", secondary: true), 5);
        var match = paint.Standard is null ? null : PaintMatch.FromRaw(paint.Match);
        TableRows.Cell(grid, TableRows.Text(match?.Shown ?? "", secondary: true), 6);
        var type = PaintType.FromRaw(paint.Type);
        var typeText = TableRows.Cell(grid, TableRows.Text(type?.Shown ?? "", secondary: true), 7);
        if (type is not null) ToolTipService.SetToolTip(typeText, type.Hint);

        // A mix's Owned box can't be ticked: it's owned when every recipe paint is.
        var isOwned = _catalogue.IsOwned(paint);
        var owned = new CheckBox { IsChecked = isOwned, MinWidth = 0, Padding = new Thickness(0), IsEnabled = !paint.IsMix };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(owned, $"Owned: {PaintText.DisplayName(paint)}");
        var ownedCell = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { owned } };
        ToolTipService.SetToolTip(ownedCell, _catalogue.OwnedHelp(paint));
        owned.Click += (_, _) => SetOwned([paint], owned.IsChecked == true);
        TableRows.Cell(grid, ownedCell, 8);
        var projects = _uses.GetValueOrDefault(paint.Id) ?? [];
        TableRows.Cell(grid, TableRows.Text(string.Join(", ", projects), secondary: true), 9);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"{PaintText.DisplayName(paint)}, {_catalogue.SecondaryLine(paint)}");
    }

    private List<Paint> Selected() => _list.SelectedItems.OfType<Paint>().ToList();

    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var element = args.OriginalSource as FrameworkElement;
        while (element is not null && element.DataContext is not Paint) element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        if (element?.DataContext is not Paint clicked) return;
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
            var edit = new MenuFlyoutItem { Text = clicked.IsMix ? "Edit Mix…" : "Edit Paint…", Icon = new SymbolIcon(Symbol.Edit) };
            edit.Click += async (_, _) => await Edit(clicked);
            menu.Items.Add(edit);
        }
        // Ownership is for the selected paints that aren't mixes.
        var paints = chosen.Where(p => !p.IsMix).ToList();
        if (paints.Count > 0)
        {
            var allOwned = paints.All(_catalogue.IsOwned);
            var own = new MenuFlyoutItem { Text = allOwned ? "Mark as Not Owned" : "Mark as Owned" };
            own.Click += (_, _) => SetOwned(paints, !allOwned);
            menu.Items.Add(own);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = chosen.Count == 1 ? "Delete Paint…" : $"Delete {chosen.Count:N0} Paints…", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await Delete(chosen);
        menu.Items.Add(delete);
        if (args.TryGetPosition(_list, out var point)) menu.ShowAt(_list, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(element);
    }

    // MARK: Commands

    private void SetOwned(List<Paint> paints, bool owned)
    {
        _services.WithStore(s => { PaintService.SetOwned(s, paints, owned); return 0; });
        Changed();
    }

    private async Task Edit(Paint? paint, bool newMix = false)
    {
        var dialog = new PaintEditorDialog(_services, paint, paint is not null && _catalogue.OwnedIds.Contains(paint.Id), newMix: newMix) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Saved is { } saved) Changed(saved.Id);
    }

    private async Task Delete(List<Paint> paints)
    {
        if (paints.Count == 0) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = paints.Count == 1 ? $"Delete “{PaintText.DisplayName(paints[0])}”?" : $"Delete {paints.Count:N0} Paints?",
            Content = new TextBlock { Text = "Paints used in a project or a mix's recipe can't be deleted until they're removed from it.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        List<string> refused;
        try
        {
            refused = _services.WithStore(s => PaintService.Delete(s, paints));
        }
        catch (SqliteException error)
        {
            refused = [error.Message];
        }
        Changed();
        if (refused.Count > 0) await ReferenceActions.Message(XamlRoot, "Some Paints Weren't Deleted", string.Join("\n", refused));
    }

    private async Task Import()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow!.AppWindow.Id);
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".txt");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        PaintImportSummary summary;
        try
        {
            var text = await Task.Run(() => ReadText(file.Path));
            summary = await _services.WithStoreAsync(s => PaintCsvImporter.Import(s, text));
        }
        catch (Exception error) when (error is PaintImportException or IOException or UnauthorizedAccessException or SqliteException)
        {
            await ReferenceActions.Message(XamlRoot, "Couldn't Import Paints", error.Message);
            return;
        }
        Changed();
        // As on the Mac (screens.md, "Paints catalogue").
        static string Count(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";
        var lines = new List<string>
        {
            $"{Count(summary.Added, "new paint", "new paints")} added.",
            $"{Count(summary.Updated, "paint", "paints")} in your catalogue updated.",
        };
        if (summary.UpToDate > 0) lines.Add($"{Count(summary.UpToDate, "paint", "paints")} already up to date.");
        if (summary.Repeated > 0) lines.Add($"{Count(summary.Repeated, "row", "rows")} repeated a paint earlier in the file and skipped.");
        lines.AddRange(summary.Problems.Take(10));
        if (summary.Problems.Count > 10) lines.Add($"…and {summary.Problems.Count - 10:N0} more problems.");
        await ReferenceActions.Message(XamlRoot, "Imported Paints", string.Join("\n", lines));
    }

    /// <summary>Import from Scalemates…: marks the paints in a Scalemates stash export owned, adding any the catalogue doesn't have.</summary>
    private async Task ImportScalemates()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow!.AppWindow.Id);
        picker.FileTypeFilter.Add(".csv");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        ScalematesPaintSummary summary;
        try
        {
            var text = await Task.Run(() => ReadText(file.Path));
            summary = await _services.WithStoreAsync(s => ScalematesPaintImporter.Import(s, text));
        }
        catch (Exception error) when (error is PaintImportException or IOException or UnauthorizedAccessException or SqliteException)
        {
            await ReferenceActions.Message(XamlRoot, "Couldn't Import Paints", error.Message);
            return;
        }
        Changed();
        await ReferenceActions.Message(XamlRoot, "Imported Your Scalemates Stash", summary.Message);
    }

    /// <summary>UTF-8, or Latin-1 when the file isn't valid UTF-8 (some spreadsheets save that way).</summary>
    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static string Count(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

    private async Task Export()
    {
        var ownership = new CheckBox { Content = "Include whether you own each paint" };
        var notes = new CheckBox { Content = "Include notes" };
        var panel = new StackPanel { Spacing = 12, MaxWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{Count(_paints.Count(p => !p.IsMix), "paint", "paints")} will be saved, with brand, range, code, name, standard, match, type and colour, in a CSV file another BenchBook can import. Mixes aren't included. Leave ownership and notes out when sharing.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(ownership);
        panel.Children.Add(notes);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Export Paints", Content = panel,
            PrimaryButtonText = "Export…", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.MainWindow!.AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(PaintCsvExporter.FileName(DateTime.Now)),
            DefaultFileExtension = ".csv",
        };
        picker.FileTypeChoices.Add("CSV file", [".csv"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            var csv = PaintCsvExporter.Csv(_paints, _catalogue.OwnedIds.ToHashSet(Ids.Comparer), ownership.IsChecked == true, notes.IsChecked == true);
            await File.WriteAllTextAsync(file.Path, csv, new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await ReferenceActions.Message(XamlRoot, "Couldn't Export Paints", error.Message);
        }
    }
}
