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
        _title.Text = $"Search · {count:N0} {(count == 1 ? "reference" : "references")}";

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
            foreach (var project in results.Projects)
            {
                var button = new HyperlinkButton { Content = project.Name, Padding = new Thickness(0, 2, 0, 2) };
                var id = project.Id;
                button.Click += (_, _) => _services.ShowProject?.Invoke(id);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(button);
                row.Children.Add(Secondary(Words.ProjectSummary(project)));
                list.Children.Add(row);
            }
        }
        if (results.Paints.Count > 0)
        {
            var list = Section("Paints");
            foreach (var paint in results.Paints)
                list.Children.Add(new TextBlock { Text = $"{paint.Brand} {paint.Code} {paint.Name}".Trim() + (paint.StandardCode.Length > 0 ? $" · {paint.StandardCode}" : "") });
        }
        if (results.AftermarketItems.Count > 0)
        {
            var list = Section("Aftermarket");
            foreach (var item in results.AftermarketItems)
                list.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { item.ProductName, item.Manufacturer, LibrarySearch.AftermarketTypeName(item.Type) }.Where(s => s.Length > 0)) });
        }

        _groups.Show(results.ReferenceGroups.SelectMany(g => g.Hits).Select(hit => new ResultRow(hit.Entry,
            hit.Matches.Where(m => m.ShownAsLine).Select(m =>
            {
                var (excerpt, bold) = SearchSnippet.Excerpt(m.Text, query);
                return new MatchLine(m.Label, excerpt, bold, m.MomentSeconds);
            }).ToList())).ToList());
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
