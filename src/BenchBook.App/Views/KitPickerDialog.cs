using BenchBook.App.Services;
using BenchBook.Core.Kits;
using BenchBook.Core.Model;
using BenchBook.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>
/// "Add Kits to Cobra" (screens.md, "Project Kits tab"): kits that aren't in a
/// project, each with a tick box. New Kit… adds one to the catalogue and ticks it.
/// </summary>
internal sealed class KitPickerDialog : ContentDialog
{
    private static readonly string[] Widths = ["40", "1*", "90"];

    private readonly AppServices _services;
    private readonly HashSet<string> _exclude;
    private readonly TextBox _search = new() { PlaceholderText = "Search brand, name, scale or number" };
    private readonly ListView _list;
    private readonly StackPanel _empty = new() { Spacing = 6, Margin = new Thickness(0, 24, 0, 24) };
    private List<Kit> _kits = [];

    /// <param name="exclude">Kits already chosen, such as in the new-project sheet.</param>
    public KitPickerDialog(AppServices services, string projectName, IEnumerable<string>? exclude = null)
    {
        _services = services;
        _exclude = (exclude ?? []).ToHashSet(Ids.Comparer);
        Title = $"Add Kits to {projectName}";
        PrimaryButtonText = "Add Kit";
        SecondaryButtonText = "New Kit…";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        _list = TableRows.List(FillRow, ListViewSelectionMode.Multiple);
        _list.Height = 360;
        _list.SelectionChanged += (_, _) => UpdateButton();
        _search.TextChanged += (_, _) => Filter();
        PrimaryButtonClick += (_, _) => Chosen = _list.SelectedItems.OfType<Kit>().ToList();

        var panel = new StackPanel { Spacing = 10, MinWidth = 480 };
        panel.Children.Add(_search);
        panel.Children.Add(_list);
        panel.Children.Add(_empty);
        Content = panel;
        Opened += (_, _) => _search.Focus(FocusState.Programmatic);
        Load();
    }

    public List<Kit> Chosen { get; private set; } = [];

    private void Load()
    {
        _kits = KitText.Sorted(_services.WithStore(s => s.Kits()).Where(k => k.ProjectId is null && !_exclude.Contains(k.Id)));
        Filter();
    }

    private void Filter()
    {
        var selected = _list.SelectedItems.OfType<Kit>().Select(k => k.Id).ToHashSet(Ids.Comparer);
        var query = new SearchQuery(_search.Text);
        var visible = _kits.Where(k => query.Words.Count == 0
            || query.MatchesEveryWord(new[] { k.Brand, k.Name, k.Scale, k.Number }.OfType<string>().Select(SearchField.Of).ToList())).ToList();
        _list.ItemsSource = visible;
        foreach (var kit in visible.Where(k => selected.Contains(k.Id))) _list.SelectedItems.Add(kit);

        _empty.Children.Clear();
        var empty = visible.Count == 0;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty)
        {
            var (title, message) = _kits.Count == 0
                ? ("No Kits to Add", "Every kit in your catalogue is already in a project. Add a new one here, or import your stash from Kits in the sidebar.")
                : ("No Matching Kits", $"No kit that isn't already in a project matches “{_search.Text.Trim()}”.");
            _empty.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            _empty.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        }
        UpdateButton();
    }

    private void UpdateButton()
    {
        var count = _list.SelectedItems.Count;
        IsPrimaryButtonEnabled = count > 0;
        PrimaryButtonText = count > 1 ? $"Add {count:N0} Kits" : "Add Kit";
    }

    private void FillRow(Grid grid, object item)
    {
        var kit = (Kit)item;
        grid.DataContext = kit;
        TableRows.Columns(grid, Widths);
        TableRows.Cell(grid, KitViews.Picture(_services, kit.PictureAssetId, 36), 0);
        TableRows.Cell(grid, TableRows.TwoLines(kit.Name, KitText.Details(kit)), 1);
        TableRows.Cell(grid, TableRows.Text(KitText.StatusOrNotOwned(kit), secondary: true), 2);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"{KitText.DisplayName(kit)}, {KitText.StatusOrNotOwned(kit)}");
    }

    /// <summary>
    /// Shows the picker until the user adds kits or cancels. New Kit… closes it
    /// (only one dialog can be open), opens the editor, then shows the picker
    /// again with the new kit ticked.
    /// </summary>
    public async Task<List<Kit>> PickAsync()
    {
        var root = XamlRoot;
        while (true)
        {
            var result = await ShowAsync();
            if (result == ContentDialogResult.Primary) return Chosen;
            if (result != ContentDialogResult.Secondary) return [];
            var ticked = _list.SelectedItems.OfType<Kit>().Select(k => k.Id).ToHashSet(Ids.Comparer);
            if (await KitViews.Edit(root, _services, null) is { } saved) ticked.Add(saved.Id);
            Load();
            foreach (var kit in ((IEnumerable<Kit>)_list.ItemsSource).Where(k => ticked.Contains(k.Id))) _list.SelectedItems.Add(kit);
            UpdateButton();
        }
    }
}
