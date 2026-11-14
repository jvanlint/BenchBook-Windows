using BenchBook.App.Services;
using BenchBook.App.ViewModels;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>One part of a reference that matched a search, e.g. "Box 2: …discolouration…".</summary>
/// <param name="MomentSeconds">For a video moment, its time: clicking the line opens the video there.</param>
/// <param name="MarkupId">For an image box or PDF markup: clicking the line opens the viewer at it.</param>
public sealed record MatchLine(string Label, string Excerpt, IReadOnlyList<(int Start, int Length)> Bold, double? MomentSeconds = null, string? MarkupId = null);

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
                if (e.ClickedItem is FrameworkElement { Tag: ResultRow row }) await Open(row.Entry);
            };
            list.ContextRequested += List_ContextRequested;
            section.Children.Add(list);
            _groups.Children.Add(section);
        }
    }

    private async Task Open(ReferenceEntry entry)
    {
        if (await ReferenceActions.Open(XamlRoot, Services, entry) && Changed is not null) await Changed();
    }

    /// <summary>Right-click: Open, Show in Project, Edit Details… (screens.md, "Search").</summary>
    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var element = args.OriginalSource as DependencyObject;
        while (element is not null && element is not FrameworkElement { Tag: ResultRow }) element = VisualTreeHelper.GetParent(element);
        if (element is not FrameworkElement { Tag: ResultRow row } target) return;
        args.Handled = true;
        var entry = row.Entry;

        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open", Icon = new SymbolIcon(Symbol.OpenFile) };
        open.Click += async (_, _) => await Open(entry);
        menu.Items.Add(open);
        var show = new MenuFlyoutItem { Text = "Show in Project" };
        show.Click += (_, _) => Services.ShowProject?.Invoke(entry.Reference.ProjectId);
        menu.Items.Add(show);
        var edit = new MenuFlyoutItem { Text = entry.Reference.Kind == ReferenceKind.Note ? "Edit Note…" : "Edit Details…", Icon = new SymbolIcon(Symbol.Edit) };
        edit.Click += async (_, _) =>
        {
            if (await ReferenceActions.EditDetails(XamlRoot, Services, entry) && Changed is not null) await Changed();
        };
        menu.Items.Add(edit);
        if (entry.Reference.Kind == ReferenceKind.Pdf)
        {
            var export = new MenuFlyoutItem { Text = "Export Marked-Up Copy…" };
            export.Click += async (_, _) => await Viewers.PdfViewerWindow.ExportMarkedUpCopy(Services, entry, XamlRoot, App.MainWindow!.AppWindow.Id);
            menu.Items.Add(export);
        }

        if (args.TryGetPosition(sender, out var point)) menu.ShowAt(sender, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(target);
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
        foreach (var match in row.Matches.Take(6)) text.Children.Add(MatchView(match, row.Entry));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"{tile.Title}, {details}");
        return grid;
    }

    /// <summary>
    /// A matched line. A video moment's line opens the video at that time; an
    /// image box's or PDF markup's line opens the viewer at it.
    /// </summary>
    private FrameworkElement MatchView(MatchLine match, ReferenceEntry entry)
    {
        var block = MatchText(match);
        if (match.MarkupId is { } markupId && entry.Reference.Kind is ReferenceKind.Image or ReferenceKind.Pdf)
        {
            var open = new HyperlinkButton { Content = block, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left };
            ToolTipService.SetToolTip(open, $"Open at {match.Label}");
            open.Click += (_, _) =>
            {
                if (entry.Reference.Kind == ReferenceKind.Image) Viewers.ImageViewerWindow.Show(Services, entry, markupId);
                else Viewers.PdfViewerWindow.Show(Services, entry, markupId);
            };
            return open;
        }
        if (match.MomentSeconds is not { } seconds || entry.Reference.SourcePageUrl is not { } source) return block;
        var button = new HyperlinkButton { Content = block, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left };
        ToolTipService.SetToolTip(button, $"Open the video at {VideoLink.Format(seconds)}");
        button.Click += async (_, _) => await Opener.OpenLink(VideoLink.PlaybackUrl(source, seconds));
        return button;
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
