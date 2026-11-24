using BenchBook.App.Services;
using BenchBook.Core.Model;
using BenchBook.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>Search results for the whole library (screens.md, "Search").</summary>
internal sealed partial class SearchPage : Page
{
    private readonly AppServices _services;
    private readonly Func<LibrarySearch> _index;
    private readonly Action<string> _removeTag;
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] };
    private readonly StackPanel _tokens = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _sections = new() { Spacing = 24 };
    private readonly ReferenceGroupsView _groups = new();
    private string _query = "";
    private IReadOnlyList<Tag> _tags = [];

    /// <param name="removeTag">Takes a tag token out of the search, by tag id.</param>
    public SearchPage(AppServices services, Func<LibrarySearch> index, Action<string> removeTag)
    {
        _services = services;
        _index = index;
        _removeTag = removeTag;
        _groups.Services = services;
        _groups.Changed = () => { services.NotifyLibraryChanged(); Show(_query, _tags); return Task.CompletedTask; };
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(32, 20, 32, 24) };
        panel.Children.Add(_title);
        panel.Children.Add(_tokens);
        panel.Children.Add(_sections);
        panel.Children.Add(_groups);
        Content = new ScrollViewer { Content = panel };
    }

    /// <summary>Shows what matches the words and every chosen tag.</summary>
    public void Show(string text, IReadOnlyList<Tag> tags)
    {
        _query = text;
        _tags = tags;
        var query = new SearchQuery(text, tags.Select(t => t.NormalizedName));
        var results = _index().Search(query);
        var count = results.ReferenceCount;
        // "Search · 4 references", adding "2 build photos" when there are any.
        _title.Text = $"Search · {count:N0} {(count == 1 ? "reference" : "references")}"
            + (results.BuildPhotoCount > 0 ? $", {Words.Count(results.BuildPhotoCount, "build photo")}" : "");

        // Tag tokens: each narrows the results to references with every chosen tag.
        _tokens.Children.Clear();
        _tokens.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tag in tags)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(TagCommands.Dot(tag.Color));
            content.Children.Add(new TextBlock { Text = tag.Name });
            content.Children.Add(new FontIcon { Glyph = "\uE711", FontSize = 10 });
            var chip = new Button { Content = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3) };
            ToolTipService.SetToolTip(chip, $"Stop searching for “{tag.Name}”");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Remove {tag.Name} from the search");
            var id = tag.Id;
            chip.Click += (_, _) => _removeTag(id);
            _tokens.Children.Add(chip);
        }

        _sections.Children.Clear();
        if (results.IsEmpty)
        {
            var words = text.Trim();
            var what = words.Length > 0 && tags.Count > 0 ? $"“{words}” with these tags"
                : words.Length > 0 ? $"“{words}”"
                : tags.Count == 1 ? "this tag" : "all these tags";
            _sections.Children.Add(Secondary($"Nothing in your library matches {what}."));
        }

        if (results.Projects.Count > 0)
        {
            var list = Section("Projects");
            var kits = Words.KitsByProject(_services);
            foreach (var project in results.Projects)
            {
                var button = new HyperlinkButton { Content = project.Name, Padding = new Thickness(0, 2, 0, 2) };
                var id = project.Id;
                button.Click += (_, _) => _services.ShowProject?.Invoke(id);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(button);
                row.Children.Add(Secondary(Words.ProjectSummary(project, kits[project.Id].ToList())));
                list.Children.Add(row);
            }
        }
        if (results.Kits.Count > 0)
        {
            var list = Section("Kits");
            foreach (var kit in results.Kits) list.Children.Add(KitRow(kit));
        }
        if (results.Paints.Count > 0)
        {
            var list = Section("Paints");
            var catalogue = _services.WithStore(BenchBook.Core.Paints.PaintCatalogue.Load);
            foreach (var paint in results.Paints) list.Children.Add(PaintRow(paint, catalogue));
        }
        if (results.AftermarketItems.Count > 0)
        {
            var list = Section("Aftermarket");
            foreach (var item in results.AftermarketItems) list.Children.Add(PartRow(item));
        }

        List<MatchLine> Lines(IEnumerable<SearchMatch> matches) => matches.Where(m => m.ShownAsLine).Select(m =>
        {
            var (excerpt, bold) = SearchSnippet.Excerpt(m.Text, query);
            return new MatchLine(m.Label, excerpt, bold, m.MomentSeconds, m.MarkupId);
        }).ToList();
        _groups.Show(
            results.ReferenceGroups.SelectMany(g => g.Hits).Select(hit => new ResultRow(hit.Entry, Lines(hit.Matches))).ToList(),
            results.ReferenceGroups.SelectMany(g => g.Photos).Select(hit => new PhotoResultRow(hit.Entry, Lines(hit.Matches))).ToList());
    }

    /// <summary>Box art, brand and name, scale, number and notes, and its status or "Not Owned".</summary>
    private Grid KitRow(BenchBook.Core.Model.Kit kit)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 2, 0, 2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        TableRows.Columns(row, "40", "1*");
        TableRows.Cell(row, KitViews.Picture(_services, kit.PictureAssetId, 36), 0);
        var details = string.Join(" · ", new[] { kit.Scale, kit.Number, BenchBook.Core.Kits.KitText.StatusOrNotOwned(kit) }.Where(t => !string.IsNullOrWhiteSpace(t)));
        var text = TableRows.TwoLines($"{kit.Brand} {kit.Name}", details);
        if (kit.Notes is { } notes) text.Children.Add(Secondary(notes));
        TableRows.Cell(row, text, 1);

        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "Edit Kit…", Icon = new SymbolIcon(Symbol.Edit) };
        edit.Click += async (_, _) =>
        {
            if (await KitViews.Edit(XamlRoot, _services, kit) is not null) _services.NotifyLibraryChanged();
        };
        menu.Items.Add(edit);
        var show = new MenuFlyoutItem { Text = "Show in Kits" };
        show.Click += (_, _) => _services.ShowKit?.Invoke(kit.Id);
        menu.Items.Add(show);
        row.ContextFlyout = menu;
        row.DoubleTapped += (_, _) => _services.ShowKit?.Invoke(kit.Id);
        return row;
    }

    /// <summary>A paint: its swatch, name, and range and standard. Click shows it in Paints; right-click: Edit Paint…, Show in Paints.</summary>
    private Grid PaintRow(BenchBook.Core.Model.Paint paint, BenchBook.Core.Paints.PaintCatalogue catalogue)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 2, 0, 2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        TableRows.Columns(row, "20", "Auto", "1*");
        row.ColumnSpacing = 8;
        TableRows.Cell(row, PaintSwatch.Make(paint), 0);
        var name = BenchBook.Core.Paints.PaintText.DisplayName(paint);
        var link = new HyperlinkButton { Content = name, Padding = new Thickness(0, 2, 0, 2) };
        ToolTipService.SetToolTip(link, "Show in Paints");
        link.Click += (_, _) => _services.ShowPaint?.Invoke(paint.Id);
        TableRows.Cell(row, link, 1);
        TableRows.Cell(row, Secondary(catalogue.SecondaryLine(paint)), 2);

        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = paint.IsMix ? "Edit Mix…" : "Edit Paint…", Icon = new SymbolIcon(Symbol.Edit) };
        edit.Click += async (_, _) =>
        {
            var dialog = new PaintEditorDialog(_services, paint, catalogue.OwnedIds.Contains(paint.Id)) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Saved is not null)
            {
                _services.NotifyLibraryChanged();
                Show(_query, _tags);
            }
        };
        menu.Items.Add(edit);
        var show = new MenuFlyoutItem { Text = "Show in Paints" };
        show.Click += (_, _) => _services.ShowPaint?.Invoke(paint.Id);
        menu.Items.Add(show);
        row.ContextFlyout = menu;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, $"{name}, {catalogue.SecondaryLine(paint)}");
        return row;
    }

    /// <summary>An aftermarket part. Click shows it in its project; right-click: Edit Part…, Show in Project.</summary>
    private Grid PartRow(BenchBook.Core.Model.AftermarketItem item)
    {
        var row = new Grid { Padding = new Thickness(0, 2, 0, 2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        TableRows.Columns(row, "Auto", "1*");
        row.ColumnSpacing = 8;
        var link = new HyperlinkButton { Content = item.ProductName, Padding = new Thickness(0, 2, 0, 2) };
        ToolTipService.SetToolTip(link, "Show in Project");
        link.Click += (_, _) => _services.ShowAftermarket?.Invoke(item.ProjectId);
        TableRows.Cell(row, link, 0);
        var details = string.Join(" · ", new[] { item.Manufacturer, LibrarySearch.AftermarketTypeName(item.Type) }.Where(s => !string.IsNullOrEmpty(s)));
        TableRows.Cell(row, Secondary(details), 1);

        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "Edit Part…", Icon = new SymbolIcon(Symbol.Edit) };
        edit.Click += async (_, _) =>
        {
            if (_services.WithStore(s => s.Project(item.ProjectId)) is not { } project) return;
            var dialog = new AftermarketEditorDialog(_services, project, item) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _services.NotifyLibraryChanged();
                Show(_query, _tags);
            }
        };
        menu.Items.Add(edit);
        var show = new MenuFlyoutItem { Text = "Show in Project" };
        show.Click += (_, _) => _services.ShowAftermarket?.Invoke(item.ProjectId);
        menu.Items.Add(show);
        row.ContextFlyout = menu;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, $"{item.ProductName}, {details}");
        return row;
    }

    private StackPanel Section(string title)
    {
        var section = new StackPanel { Spacing = 4 };
        section.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var list = new StackPanel { Spacing = 2 };
        section.Children.Add(list);
        _sections.Children.Add(section);
        return list;
    }

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };
}
