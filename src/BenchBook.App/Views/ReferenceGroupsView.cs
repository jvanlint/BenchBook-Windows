using BenchBook.App.Services;
using BenchBook.App.ViewModels;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>One part of a reference that matched a search, e.g. "Box 2: …discolouration…".</summary>
public sealed record MatchLine(string Label, string Excerpt, IReadOnlyList<(int Start, int Length)> Bold);

/// <summary>A reference in a list of results, with what matched.</summary>
public sealed record ResultRow(ReferenceEntry Entry, IReadOnlyList<MatchLine> Matches);

/// <summary>References grouped by project: "F-16C — 3 references", then a row for each.</summary>
internal sealed class ReferenceGroupsView : UserControl
{
    private readonly StackPanel _groups = new() { Spacing = 20 };

    public ReferenceGroupsView()
    {
        Content = _groups;
    }

    public AppServices Services { get; set; } = null!;

    /// <summary>Runs after a reference was edited from the list, so the owner can reload.</summary>
    public Func<Task>? Changed { get; set; }

    public void Show(IReadOnlyList<ResultRow> rows)
    {
        _groups.Children.Clear();
        var projects = Services.WithStore(s => s.Projects()).ToDictionary(p => p.Id, Ids.Comparer);
        var byProject = rows.GroupBy(r => r.Entry.Reference.ProjectId, Ids.Comparer)
            .OrderBy(g => projects.TryGetValue(g.Key, out var p) ? p.Name : "", NaturalComparer.Instance);
        foreach (var group in byProject)
        {
            var name = projects.TryGetValue(group.Key, out var project) ? project.Name : "Project";
            var count = group.Count();
            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(new TextBlock
            {
                Text = $"{name} — {count:N0} {(count == 1 ? "reference" : "references")}",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            });
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
            foreach (var row in group) list.Items.Add(Row(row));
            list.ItemClick += async (_, e) =>
            {
                if (e.ClickedItem is FrameworkElement { Tag: ResultRow row } && await ReferenceActions.Open(XamlRoot, Services, row.Entry) && Changed is not null)
                    await Changed();
            };
            section.Children.Add(list);
            _groups.Children.Add(section);
        }
    }

    private FrameworkElement Row(ResultRow row)
    {
        var tile = new ReferenceTile(row.Entry);
        _ = tile.LoadPictureAsync(Services);

        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Tag = row };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var picture = new Grid { Width = 64, Height = 64, CornerRadius = new CornerRadius(6), Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Top };
        var image = new Image { Stretch = Stretch.UniformToFill };
        var glyph = new FontIcon { Glyph = tile.Glyph, FontSize = 22, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        void UpdatePicture()
        {
            image.Source = tile.Picture;
            image.Stretch = tile.IsSiteIcon ? Stretch.Uniform : Stretch.UniformToFill;
            glyph.Visibility = tile.Picture is null ? Visibility.Visible : Visibility.Collapsed;
        }
        tile.PropertyChanged += (_, _) => UpdatePicture();
        UpdatePicture();
        picture.Children.Add(image);
        picture.Children.Add(glyph);
        grid.Children.Add(picture);

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = tile.Title, TextTrimming = TextTrimming.CharacterEllipsis });
        var details = tile.Details + (tile.Tags.Count > 0 ? " · " + string.Join(", ", tile.Tags.Select(t => t.Name)) : "");
        text.Children.Add(new TextBlock
        {
            Text = details,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        foreach (var match in row.Matches.Take(6)) text.Children.Add(MatchText(match));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"{tile.Title}, {details}");
        return grid;
    }

    private static TextBlock MatchText(MatchLine match)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        block.Inlines.Add(new Run { Text = match.Label + ": ", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        var position = 0;
        foreach (var (start, length) in match.Bold.OrderBy(b => b.Start))
        {
            if (start < position || start + length > match.Excerpt.Length) continue;
            if (start > position) block.Inlines.Add(new Run { Text = match.Excerpt[position..start] });
            block.Inlines.Add(new Run { Text = match.Excerpt.Substring(start, length), FontWeight = FontWeights.Bold });
            position = start + length;
        }
        if (position < match.Excerpt.Length) block.Inlines.Add(new Run { Text = match.Excerpt[position..] });
        return block;
    }
}
