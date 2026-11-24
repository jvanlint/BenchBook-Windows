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

/// <summary>A project's Paint tab (screens.md, "Paint tab").</summary>
internal sealed partial class ProjectPaintView : UserControl
{
    private static readonly string[] Widths = ["24", "2*", "1*", "1.5*", "60"];

    private readonly AppServices _services;
    private readonly Project _project;
    private readonly StackPanel _summary = new() { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListView _list;
    private readonly FrameworkElement _header;
    private readonly StackPanel _empty = new() { Spacing = 10, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private Dictionary<string, Paint> _paints = [];
    private HashSet<string> _owned = [];

    public ProjectPaintView(AppServices services, Project project)
    {
        _services = services;
        _project = project;
        _list = TableRows.List(FillRow);
        _list.ContextRequested += List_ContextRequested;
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Delete && Selected().Count > 0) { e.Handled = true; await Remove(Selected()); }
        };
        _header = TableRows.Header(Widths, [("", null), ("Paint", null), ("Colour", null), ("Used For", null), ("Owned", null)], null, true, null);

        var addColour = new Button { Content = "Add Colour…" };
        ToolTipService.SetToolTip(addColour, "Add a colour from the painting guide, such as FS 36270");
        addColour.Click += async (_, _) => await AddColour();
        var addPaint = new Button { Content = "Add Paint…" };
        ToolTipService.SetToolTip(addPaint, "Add paints from your catalogue");
        addPaint.Click += async (_, _) => await AddPaints();
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_summary);
        Grid.SetColumn(addColour, 1);
        Grid.SetColumn(addPaint, 2);
        top.Children.Add(addColour);
        top.Children.Add(addPaint);

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

    private void Load(string? select = null)
    {
        List<ProjectPaint> rows;
        (rows, _paints, _owned) = _services.WithStore(s => (s.ProjectPaints(_project.Id), s.Paints().ToDictionary(p => p.Id, Ids.Comparer), s.OwnedPaintIds()));
        var sorted = PaintService.SortedRows(rows, _paints);
        _list.ItemsSource = sorted;
        if (select is not null && sorted.FirstOrDefault(r => Ids.Comparer.Equals(r.Id, select)) is { } row) _list.ScrollIntoView(row);
        ShowSummary(sorted);
        ShowEmpty(sorted.Count == 0);
    }

    private void Changed(string? select = null)
    {
        Load(select);
        _services.NotifyLibraryChanged();
    }

    /// <summary>"4 paints · 2 to buy · 1 colour without paint". "To buy" and "without paint" only show when there are any.</summary>
    private void ShowSummary(List<ProjectPaint> rows)
    {
        _summary.Children.Clear();
        var chosen = rows.Where(r => r.PaintId is not null && _paints.ContainsKey(r.PaintId)).ToList();
        var toBuy = chosen.Count(r => !_owned.Contains(r.PaintId!));
        var unchosen = rows.Count - chosen.Count;
        _summary.Children.Add(TableRows.Text($"{chosen.Count:N0} {(chosen.Count == 1 ? "paint" : "paints")}", secondary: true));
        if (toBuy > 0)
        {
            var orange = new SolidColorBrush(NamedColors.Color("orange")!.Value);
            var buy = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            buy.Children.Add(new FontIcon { Glyph = "\uE7BF", FontSize = 12, Foreground = orange });
            buy.Children.Add(new TextBlock { Text = $"{toBuy:N0} to buy", Foreground = orange });
            _summary.Children.Add(buy);
        }
        if (unchosen > 0)
        {
            var without = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            without.Children.Add(PaintSwatch.Make(null, 12));
            without.Children.Add(TableRows.Text($"{unchosen:N0} {(unchosen == 1 ? "colour" : "colours")} without paint", secondary: true));
            _summary.Children.Add(without);
        }
    }

    private void ShowEmpty(bool empty)
    {
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _header.Visibility = _list.Visibility;
        _summary.Visibility = _list.Visibility;
        if (!empty || _empty.Children.Count > 0) return;
        _empty.Children.Add(new TextBlock { Text = "No Paint", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock
        {
            Text = "Add the colours from the painting guide, such as FS 36270, then choose a paint for each. Or add paints from your catalogue directly.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var addColour = new Button { Content = "Add Colour…" };
        addColour.Click += async (_, _) => await AddColour();
        var addPaint = new Button { Content = "Add Paint…" };
        addPaint.Click += async (_, _) => await AddPaints();
        buttons.Children.Add(addColour);
        buttons.Children.Add(addPaint);
        _empty.Children.Add(buttons);
    }

    // MARK: Rows

    private void FillRow(Grid grid, object item)
    {
        var row = (ProjectPaint)item;
        grid.DataContext = row;
        TableRows.Columns(grid, Widths);
        var paint = row.PaintId is { } id && _paints.TryGetValue(id, out var p) ? p : null;
        TableRows.Cell(grid, PaintSwatch.Make(paint?.SwatchHex), 0);
        if (paint is not null)
        {
            TableRows.Cell(grid, TableRows.TwoLines(PaintText.DisplayName(paint), PaintText.SecondaryLine(paint)), 1);
        }
        else
        {
            var choose = new Button { Content = "Choose Paint…" };
            ToolTipService.SetToolTip(choose, "Choose a paint from your catalogue for this colour");
            choose.Click += async (_, _) => await Choose(row);
            TableRows.Cell(grid, choose, 1);
        }

        // The colour and "used for" are edited in place and saved when you leave the field.
        var standard = new TextBox { Text = row.GuideStandard?.DisplayText ?? "", PlaceholderText = "e.g. FS 36270" };
        var usedFor = new TextBox { Text = row.UsedFor ?? "", PlaceholderText = "e.g. upper surfaces" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(standard, "Colour");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(usedFor, "Used For");
        void Save()
        {
            try { _services.WithStore(s => { PaintService.SetText(s, row, standard.Text, usedFor.Text); return 0; }); }
            catch (SqliteException) { }
        }
        standard.LostFocus += (_, _) => Save();
        usedFor.LostFocus += (_, _) => Save();
        TableRows.Cell(grid, standard, 2);
        TableRows.Cell(grid, usedFor, 3);

        if (paint is not null)
        {
            var isOwned = _owned.Contains(paint.Id);
            var owned = new CheckBox { IsChecked = isOwned, MinWidth = 0, Padding = new Thickness(0) };
            ToolTipService.SetToolTip(owned, isOwned ? "You own this paint" : "You don't own this paint yet");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(owned, $"Owned: {PaintText.DisplayName(paint)}");
            owned.Click += (_, _) =>
            {
                _services.WithStore(s => { s.SetOwned(paint.Id, owned.IsChecked == true); return 0; });
                Changed();
            };
            TableRows.Cell(grid, owned, 4);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, paint is null ? $"{row.GuideStandard?.DisplayText ?? "Colour"}, no paint chosen" : PaintText.DisplayName(paint));
    }

    private List<ProjectPaint> Selected() => _list.SelectedItems.OfType<ProjectPaint>().ToList();

    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var element = args.OriginalSource as FrameworkElement;
        while (element is not null && element.DataContext is not ProjectPaint) element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        if (element?.DataContext is not ProjectPaint clicked) return;
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
            var choose = new MenuFlyoutItem { Text = clicked.PaintId is null ? "Choose Paint…" : "Change Paint…" };
            choose.Click += async (_, _) => await Choose(clicked);
            menu.Items.Add(choose);
        }
        var remove = new MenuFlyoutItem { Text = chosen.Count == 1 ? "Remove from Project" : $"Remove {chosen.Count:N0} Rows from Project", Icon = new SymbolIcon(Symbol.Remove) };
        remove.Click += async (_, _) => await Remove(chosen);
        menu.Items.Add(remove);
        if (args.TryGetPosition(_list, out var point)) menu.ShowAt(_list, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(element);
    }

    // MARK: Commands

    public async Task AddColour()
    {
        var standard = new TextBox { Header = "Colour", PlaceholderText = "e.g. FS 36270 or RLM 74" };
        var usedFor = new TextBox { Header = "Used For", PlaceholderText = "e.g. upper surfaces" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Add Colour",
            Content = new StackPanel { Spacing = 12, MinWidth = 380, Children = { standard, usedFor } },
            PrimaryButtonText = "Add Colour", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false,
        };
        standard.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = standard.Text.Trim().Length > 0;
        dialog.Opened += (_, _) => standard.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var row = _services.WithStore(s => PaintService.AddColour(s, _project.Id, standard.Text, usedFor.Text));
            Changed(row.Id);
        }
        catch (SqliteException error)
        {
            await ReferenceActions.Message(XamlRoot, "Couldn't Add Colour", error.Message);
        }
    }

    public async Task AddPaints()
    {
        var picker = new PaintPickerDialog(_services, _project, null, forColour: false) { XamlRoot = XamlRoot };
        await picker.ShowAsync();
        var ids = picker.Result switch
        {
            PaintPickerDialog.PickerResult.Picked => picker.Chosen.Select(p => p.Id).ToList(),
            PaintPickerDialog.PickerResult.NewPaint => await NewPaint(null) is { } paint ? [paint.Id] : [],
            _ => [],
        };
        if (ids.Count == 0) return;
        try
        {
            _services.WithStore(s => PaintService.AddPaints(s, _project.Id, ids));
        }
        catch (SqliteException error)
        {
            await ReferenceActions.Message(XamlRoot, "Couldn't Add Paint", error.Message);
        }
        Changed();
    }

    private async Task Choose(ProjectPaint row)
    {
        var picker = new PaintPickerDialog(_services, _project, row.GuideStandard, forColour: true) { XamlRoot = XamlRoot };
        await picker.ShowAsync();
        var paint = picker.Result switch
        {
            PaintPickerDialog.PickerResult.Picked => picker.Chosen.FirstOrDefault(),
            PaintPickerDialog.PickerResult.NewPaint => await NewPaint(row.GuideStandard?.DisplayText),
            _ => null,
        };
        if (paint is null) return;
        _services.WithStore(s => { PaintService.Choose(s, row, paint.Id); return 0; });
        Changed(row.Id);
    }

    /// <summary>"New Paint…" in the picker: the editor, with the colour's standard filled in when choosing for one.</summary>
    private async Task<Paint?> NewPaint(string? standard)
    {
        var editor = new PaintEditorDialog(_services, null, false, standard) { XamlRoot = XamlRoot };
        return await editor.ShowAsync() == ContentDialogResult.Primary ? editor.Saved : null;
    }

    private async Task Remove(List<ProjectPaint> rows)
    {
        try
        {
            _services.WithStore(s => { s.DeleteProjectPaints(rows.Select(r => r.Id)); return 0; });
        }
        catch (SqliteException error)
        {
            await ReferenceActions.Message(XamlRoot, "Something Went Wrong", error.Message);
        }
        Changed();
    }
}
