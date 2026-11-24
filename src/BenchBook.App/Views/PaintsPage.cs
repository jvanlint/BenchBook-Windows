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
    private static readonly string[] Widths = ["24", "1*", "1*", "90", "1.5*", "1*", "60", "1*"];

    private readonly AppServices _services;
    private readonly TextBlock _count = new() { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
    private readonly DropDownButton _brand = new();
    private readonly DropDownButton _range = new();
    private readonly ToggleButton _ownedOnly = new() { Content = "Owned Only" };
    private readonly ContentControl _header = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _list;
    private readonly StackPanel _empty = new() { Spacing = 8, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };

    private List<Paint> _paints = [];
    private HashSet<string> _owned = [];
    private Dictionary<string, List<string>> _uses = [];
    private string? _brandKey, _rangeKey;
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

        var import = new AppBarButton { Label = "Import CSV…", Icon = new FontIcon { Glyph = "\uE8B5" } };
        ToolTipService.SetToolTip(import, "Import paints from a CSV file");
        import.Click += async (_, _) => await Import();
        var export = new AppBarButton { Label = "Export CSV…", Icon = new FontIcon { Glyph = "\uEDE1" } };
        ToolTipService.SetToolTip(export, "Save the catalogue as a CSV file that another BenchBook can import");
        export.Click += async (_, _) => await Export();
        var add = new AppBarButton { Label = "Add Paint", Icon = new SymbolIcon(Symbol.Add) };
        ToolTipService.SetToolTip(add, "Add a paint to the catalogue");
        add.Click += async (_, _) => await Edit(null);
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        commands.PrimaryCommands.Add(import);
        commands.PrimaryCommands.Add(export);
        commands.PrimaryCommands.Add(add);

        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock { Text = "Paints", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        Grid.SetColumn(commands, 1);
        title.Children.Add(commands);

        ToolTipService.SetToolTip(_brand, "Show one brand's paints");
        ToolTipService.SetToolTip(_range, "Show one range's paints");
        ToolTipService.SetToolTip(_ownedOnly, "Show only paints you own");
        _ownedOnly.Click += (_, _) => Apply();
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filters.Children.Add(_brand);
        filters.Children.Add(_range);
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
        (_paints, _owned, _uses) = _services.WithStore(s => (s.Paints(), s.OwnedPaintIds(), s.PaintProjects()));
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

        var visible = inBrand
            .Where(p => _rangeKey is null || PaintIdentity.Words(p.Range) == _rangeKey)
            .Where(p => _ownedOnly.IsChecked != true || _owned.Contains(p.Id));
        var sorted = Sort(visible).ToList();
        _list.ItemsSource = sorted;

        var owned = _paints.Count(p => _owned.Contains(p.Id));
        var filtered = _brandKey is not null || _rangeKey is not null || _ownedOnly.IsChecked == true;
        _count.Text = filtered
            ? $"{sorted.Count:N0} of {_paints.Count:N0} paints · {owned:N0} owned"
            : $"{_paints.Count:N0} {(_paints.Count == 1 ? "paint" : "paints")} · {owned:N0} owned";

        _header.Content = TableRows.Header(Widths,
            [("", null), ("Brand", "brand"), ("Range", "range"), ("Code", "code"), ("Name", "name"), ("FS / RLM", "standard"), ("Owned", null), ("Projects", null)],
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

    private IEnumerable<Paint> Sort(IEnumerable<Paint> paints)
    {
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
            message = "Add the paints you use, or import a CSV file with Brand, Range, Code, Name, FS/RLM, Colour and Owned columns.";
        }
        else if (_ownedOnly.IsChecked == true && _brandKey is null && _rangeKey is null)
        {
            title = "No Owned Paints";
            message = "Mark the paints you have as owned, in the table or when editing a paint.";
        }
        else
        {
            title = "No Matching Paints";
            message = "No paints match the chosen brand, range and ownership.";
        }
        _empty.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        if (filtered)
        {
            var showAll = new Button { Content = "Show All Paints", HorizontalAlignment = HorizontalAlignment.Center };
            showAll.Click += (_, _) => { _brandKey = null; _rangeKey = null; _ownedOnly.IsChecked = false; Apply(); };
            _empty.Children.Add(showAll);
        }
    }

    // MARK: Rows

    private void FillRow(Grid grid, object item)
    {
        var paint = (Paint)item;
        grid.DataContext = paint;
        TableRows.Columns(grid, Widths);
        TableRows.Cell(grid, PaintSwatch.Make(paint.SwatchHex), 0);
        TableRows.Cell(grid, TableRows.Text(paint.Brand), 1);
        TableRows.Cell(grid, TableRows.Text(paint.Range, secondary: true), 2);
        TableRows.Cell(grid, TableRows.Text(paint.Code), 3);
        TableRows.Cell(grid, TableRows.Text(paint.Name), 4);
        TableRows.Cell(grid, TableRows.Text(paint.Standard?.DisplayText ?? "", secondary: true), 5);
        var owned = new CheckBox { IsChecked = _owned.Contains(paint.Id), MinWidth = 0, Padding = new Thickness(0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(owned, $"Owned: {PaintText.DisplayName(paint)}");
        ToolTipService.SetToolTip(owned, _owned.Contains(paint.Id) ? "You own this paint" : "You don't own this paint yet");
        owned.Click += (_, _) => SetOwned([paint], owned.IsChecked == true);
        TableRows.Cell(grid, owned, 6);
        var projects = _uses.GetValueOrDefault(paint.Id) ?? [];
        TableRows.Cell(grid, TableRows.Text(string.Join(", ", projects), secondary: true), 7);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"{PaintText.DisplayName(paint)}, {PaintText.SecondaryLine(paint)}");
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
            var edit = new MenuFlyoutItem { Text = "Edit Paint…", Icon = new SymbolIcon(Symbol.Edit) };
            edit.Click += async (_, _) => await Edit(clicked);
            menu.Items.Add(edit);
        }
        var allOwned = chosen.All(p => _owned.Contains(p.Id));
        var own = new MenuFlyoutItem { Text = allOwned ? "Mark as Not Owned" : "Mark as Owned" };
        own.Click += (_, _) => SetOwned(chosen, !allOwned);
        menu.Items.Add(own);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = chosen.Count == 1 ? "Delete Paint…" : $"Delete {chosen.Count:N0} Paints…", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await Delete(chosen);
        menu.Items.Add(delete);
        if (args.TryGetPosition(_list, out var point)) menu.ShowAt(_list, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(element);
    }

    // MARK: Commands

    private void SetOwned(List<Paint> paints, bool owned)
    {
        _services.WithStore(s => { foreach (var paint in paints) s.SetOwned(paint.Id, owned); return 0; });
        Changed();
    }

    private async Task Edit(Paint? paint)
    {
        var dialog = new PaintEditorDialog(_services, paint, paint is not null && _owned.Contains(paint.Id)) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Saved is { } saved) Changed(saved.Id);
    }

    private async Task Delete(List<Paint> paints)
    {
        if (paints.Count == 0) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = paints.Count == 1 ? $"Delete “{PaintText.DisplayName(paints[0])}”?" : $"Delete {paints.Count:N0} Paints?",
            Content = new TextBlock { Text = "Paints used in a project can't be deleted until they're removed from it.", TextWrapping = TextWrapping.Wrap },
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

    /// <summary>UTF-8, or Latin-1 when the file isn't valid UTF-8 (some spreadsheets save that way).</summary>
    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private async Task Export()
    {
        var ownership = new CheckBox { Content = "Include whether you own each paint" };
        var notes = new CheckBox { Content = "Include notes" };
        var panel = new StackPanel { Spacing = 12, MaxWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = $"All {_paints.Count:N0} paints are saved with their brand, range, code, name, FS / RLM and colour, in a CSV file another BenchBook can import. Leave ownership and notes out when sharing.",
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
            var csv = PaintCsvExporter.Csv(_paints, _owned, ownership.IsChecked == true, notes.IsChecked == true);
            await File.WriteAllTextAsync(file.Path, csv, new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await ReferenceActions.Message(XamlRoot, "Couldn't Export Paints", error.Message);
        }
    }
}
