using BenchBook.App.Services;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>
/// "Add Paint to F-16C", or "Choose Paint for FS 36270" (screens.md, "Paint
/// picker"). Paints already in the project are left out.
/// </summary>
internal sealed class PaintPickerDialog : ContentDialog
{
    private static readonly string[] Widths = ["20", "1*", "110"];

    private readonly List<Paint> _paints;
    private readonly PaintCatalogue _catalogue;
    private readonly PaintStandard? _guide;
    private readonly bool _choosing;
    private readonly TextBox _search = new() { PlaceholderText = "Search brand, code, name or standard" };
    private readonly ComboBox _type = new() { MinWidth = 170 };
    private readonly ListView _list;
    private readonly StackPanel _empty = new() { Spacing = 6, Margin = new Thickness(0, 24, 0, 24) };

    /// <param name="guide">When choosing for a colour, its standard, if it has one.</param>
    /// <param name="forColour">Choosing for a colour: only one paint can be chosen.</param>
    public PaintPickerDialog(AppServices services, Project project, PaintStandard? guide, bool forColour)
    {
        _choosing = forColour;
        _guide = guide;
        var (catalogue, rows) = services.WithStore(s => (PaintCatalogue.Load(s), s.ProjectPaints(project.Id)));
        _catalogue = catalogue;
        var paints = catalogue.ById.Values.ToList();
        var inProject = rows.Where(r => r.PaintId is not null).Select(r => r.PaintId!).ToHashSet(Ids.Comparer);
        // When choosing for a colour, paints with its standard come first, best
        // match first, then paints you own; brand and code order within each
        // (PaintPickerView.swift). OrderBy keeps the earlier order for ties.
        (int, int) Rank(Paint paint)
        {
            var follows = guide is not null && paint.Standard?.MatchKey == guide.MatchKey;
            var match = PaintMatch.FromRaw(paint.Match) is { } m ? PaintMatch.All.Count - 1 - PaintMatch.All.ToList().IndexOf(m) : 3;
            return (follows ? match : 4, catalogue.IsOwned(paint) ? 0 : 1);
        }
        _paints = PaintService.Sorted(paints.Where(p => !inProject.Contains(p.Id))).ToList();
        if (forColour) _paints = _paints.OrderBy(Rank).ToList();
        _catalogueEmpty = paints.Count == 0;

        var standard = guide?.DisplayText;
        Title = forColour ? $"Choose Paint for {standard ?? "This Colour"}" : $"Add Paint to {project.Name}";
        PrimaryButtonText = forColour ? "Choose Paint" : "Add Paint";
        SecondaryButtonText = "New Paint…";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        _list = TableRows.List(FillRow, forColour ? ListViewSelectionMode.Single : ListViewSelectionMode.Multiple);
        _list.Height = 360;
        _list.SelectionChanged += (_, _) => UpdateButton();
        _list.DoubleTapped += (_, e) =>
        {
            if (_choosing && (e.OriginalSource as FrameworkElement)?.DataContext is Paint paint)
            {
                _list.SelectedItem = paint;
                Chosen = [paint];
                Result = PickerResult.Picked;
                Hide();
            }
        };
        // Choosing for a colour starts with its standard in the search, as on the Mac
        // (PaintPickerView.swift), so only matching paints are listed.
        if (forColour && !string.IsNullOrWhiteSpace(standard)) _search.Text = standard;
        _search.TextChanged += (_, _) => Filter();
        // All Types, each type, Not Set; no counts here (behaviour.md, "Filters").
        _type.Items.Add(new ComboBoxItem { Content = "All Types", Tag = null });
        foreach (var type in PaintType.All)
        {
            var item = new ComboBoxItem { Content = type.Shown, Tag = type.Raw };
            ToolTipService.SetToolTip(item, type.Hint);
            _type.Items.Add(item);
        }
        _type.Items.Add(new ComboBoxItem { Content = "Not Set", Tag = "" });
        _type.SelectedIndex = 0;
        ToolTipService.SetToolTip(_type, "Show one type of paint, such as lacquers");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_type, "Type");
        _type.SelectionChanged += (_, _) => Filter();
        PrimaryButtonClick += (_, _) => { Chosen = _list.SelectedItems.OfType<Paint>().ToList(); Result = PickerResult.Picked; };
        SecondaryButtonClick += (_, _) => Result = PickerResult.NewPaint;

        var panel = new StackPanel { Spacing = 10, MinWidth = 520 };
        var top = new Grid { ColumnSpacing = 8 };
        TableRows.Columns(top, "1*", "Auto");
        TableRows.Cell(top, _search, 0);
        TableRows.Cell(top, _type, 1);
        panel.Children.Add(top);
        panel.Children.Add(_list);
        panel.Children.Add(_empty);
        Content = panel;
        Opened += (_, _) =>
        {
            _search.Focus(FocusState.Programmatic);
            _search.SelectAll();
        };
        Filter();
    }

    private readonly bool _catalogueEmpty;

    public enum PickerResult { Cancelled, Picked, NewPaint }

    public PickerResult Result { get; private set; }

    public List<Paint> Chosen { get; private set; } = [];

    private void Filter()
    {
        var selected = _list.SelectedItems.OfType<Paint>().ToHashSet();
        var type = (_type.SelectedItem as ComboBoxItem)?.Tag as string;
        var visible = _paints
            .Where(p => PaintIdentity.Matches(_search.Text, p))
            .Where(p => type is null || (PaintType.FromRaw(p.Type)?.Raw ?? "") == type)
            .ToList();
        _list.ItemsSource = visible;
        foreach (var paint in visible.Where(selected.Contains)) _list.SelectedItems.Add(paint);

        _empty.Children.Clear();
        var empty = visible.Count == 0;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty)
        {
            var (title, message) = _catalogueEmpty
                ? ("No Paints in Catalogue", "Create a paint here, or add many at once from Paints in the sidebar.")
                : _paints.Count == 0
                    ? ("No Paints to Add", "Every paint is already in this project.")
                    : ("No Matching Paints", "No paint in your catalogue matches the search and type, or every match is already in this project.");
            _empty.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            _empty.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        }
        UpdateButton();
    }

    private void UpdateButton()
    {
        var count = _list.SelectedItems.Count;
        IsPrimaryButtonEnabled = count > 0;
        if (!_choosing) PrimaryButtonText = count > 1 ? $"Add {count:N0} Paints" : "Add Paint";
    }

    private void FillRow(Grid grid, object item)
    {
        var paint = (Paint)item;
        grid.DataContext = paint;
        TableRows.Columns(grid, Widths);
        TableRows.Cell(grid, PaintSwatch.Make(paint), 0);
        TableRows.Cell(grid, TableRows.TwoLines(PaintText.DisplayName(paint), _catalogue.SecondaryLine(paint)), 1);
        // Choosing for a colour, paints with its standard show their match ("Same Standard" when it isn't set).
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        string? match = null;
        if (_choosing && _guide is not null && paint.Standard?.MatchKey == _guide.MatchKey)
            match = PaintMatch.FromRaw(paint.Match)?.Shown ?? "Same Standard";
        if (match is not null) right.Children.Add(TableRows.Text(match, secondary: true));
        var owned = _catalogue.IsOwned(paint);
        if (owned) right.Children.Add(TableRows.Text("Owned", secondary: true));
        TableRows.Cell(grid, right, 2);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid,
            string.Join(", ", new[] { PaintText.DisplayName(paint), match, owned ? "owned" : null }.OfType<string>()));
    }
}
