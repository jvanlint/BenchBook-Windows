using BenchBook.App.Services;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BenchBook.App.Views;

/// <summary>Details of any reference except notes (screens.md, "Reference details").</summary>
public sealed partial class ReferenceDetailsDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly ReferenceEntry _entry;

    public ReferenceDetailsDialog(AppServices services, ReferenceEntry entry)
    {
        _services = services;
        _entry = entry;
        InitializeComponent();

        var reference = entry.Reference;
        TitleBox.Text = reference.Title ?? "";
        TitleBox.PlaceholderText = ReferenceText.FallbackTitle(entry with { Reference = WithoutTitle(reference) });
        TagsBox.Text = string.Join(", ", entry.Tags.Select(t => t.Name));
        NoteBox.Text = reference.Note ?? "";

        if (reference.Kind == ReferenceKind.TextSnippet && reference.Text is { } text)
        {
            TextSection.Visibility = Visibility.Visible;
            CapturedText.Text = text;
        }
        if (reference.Kind is ReferenceKind.VideoLink or ReferenceKind.Webpage) ShowLink(reference);
        if (entry.Moments.Count > 0) ShowMoments(reference);
        ShowInformation();
    }

    private static Reference WithoutTitle(Reference reference) => new()
    {
        Id = reference.Id, CreatedAt = reference.CreatedAt, UpdatedAt = reference.UpdatedAt, ProjectId = reference.ProjectId,
        Kind = reference.Kind, CapturedAt = reference.CapturedAt, Text = reference.Text, SourcePageUrl = reference.SourcePageUrl,
    };

    private void ShowLink(Reference reference)
    {
        LinkSection.Visibility = Visibility.Visible;
        var isVideo = reference.Kind == ReferenceKind.VideoLink;
        LinkHeader.Text = isVideo ? "Video" : "Webpage";
        var fetched = isVideo ? reference.VideoInfoFetchedAt : reference.LinkPreviewFetchedAt;
        if (fetched is null)
        {
            LinkStatus.Text = isVideo
                ? "Video details haven't been fetched yet. BenchBook tries again when it's online."
                : "The page's preview hasn't been fetched yet. BenchBook tries again when it's online.";
            LinkStatus.Visibility = Visibility.Visible;
        }

        var poster = _services.Location.PosterPath(reference.Id);
        if (File.Exists(poster)) _ = LoadPoster(poster);
        else if (!isVideo && fetched is not null && reference.LinkSiteName is null && reference.LinkDescription is null)
        {
            LinkStatus.Text = "This page doesn't offer a preview.";
            LinkStatus.Visibility = Visibility.Visible;
        }

        var rows = new List<(string, UIElement)>();
        if (isVideo)
        {
            if (reference.VideoChannel is { } channel)
                rows.Add(("Channel", reference.VideoChannelUrl is { } url ? Link(channel, url) : Text(channel)));
            if (reference.VideoDurationSeconds is { } seconds) rows.Add(("Length", Text(VideoLink.Format(seconds))));
            if (reference.VideoPublishedAt is { } published) rows.Add(("Published", Text(published.ToLocalTime().ToString("d MMMM yyyy"))));
        }
        else if (reference.LinkSiteName is { } site)
        {
            rows.Add(("Site", Text(site)));
        }
        Fill(LinkRows, rows);

        var description = isVideo ? reference.VideoDescription : reference.LinkDescription;
        if (description is not null)
        {
            DescriptionText.Text = description;
            DescriptionExpander.Visibility = Visibility.Visible;
            DescriptionExpander.IsExpanded = !isVideo;
        }
    }

    private async Task LoadPoster(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            using (var stream = File.OpenRead(path)) await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            var icon = bitmap.PixelWidth == bitmap.PixelHeight && bitmap.PixelWidth <= 256;
            Poster.Source = bitmap;
            Poster.MaxHeight = icon ? 64 : 200;
            Poster.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // A damaged poster isn't shown.
        }
    }

    private void ShowMoments(Reference reference)
    {
        MomentsSection.Visibility = Visibility.Visible;
        foreach (var moment in _entry.Moments)
        {
            var label = VideoLink.Format(moment.OffsetSeconds) + (moment.Title is { } title ? $" · {title}" : "");
            MomentLinks.Children.Add(Link(label, VideoLink.PlaybackUrl(reference.SourcePageUrl ?? "", moment.OffsetSeconds)));
        }
    }

    private void ShowInformation()
    {
        var reference = _entry.Reference;
        var asset = _entry.Asset;
        var rows = new List<(string, UIElement)>
        {
            ("Type", Text(ReferenceText.KindName(reference.Kind))),
            ("Added", Text(Words.DateAndTime(reference.CapturedAt))),
        };
        if (Uri.TryCreate(reference.SourcePageUrl, UriKind.Absolute, out var source))
            rows.Add(("Source", Link(source.Host, source.AbsoluteUri)));
        if (Uri.TryCreate(reference.OriginalResourceUrl, UriKind.Absolute, out var original))
            rows.Add(("Original File", Link(Uri.UnescapeDataString(original.Segments.LastOrDefault()?.Trim('/') ?? original.Host), original.AbsoluteUri)));
        if (asset?.OriginalFilename is { } name) rows.Add(("File Name", Text(name)));
        if (asset is not null) rows.Add(("Size", Text(Shell.FormatBytes(asset.ByteCount))));
        if (asset is { PixelWidth: { } w, PixelHeight: { } h }) rows.Add(("Dimensions", Text($"{w:N0} × {h:N0}")));
        Fill(InfoRows, rows);
    }

    private static void Fill(Grid grid, List<(string Label, UIElement Value)> rows)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
            Grid.SetRow(label, i);
            Grid.SetRow((FrameworkElement)rows[i].Value, i);
            Grid.SetColumn((FrameworkElement)rows[i].Value, 1);
            grid.Children.Add(label);
            grid.Children.Add(rows[i].Value);
        }
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    private static HyperlinkButton Link(string text, string url) => new()
    {
        Content = text,
        NavigateUri = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null,
        Padding = new Thickness(0),
    };

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            _services.WithStore(s =>
            {
                ReferenceService.UpdateDetails(s, _entry.Reference, TitleBox.Text, NoteBox.Text.Replace("\r\n", "\n").Replace('\r', '\n'), TagsBox.Text);
                return 0;
            });
        }
        catch (Exception error) when (error is CaptureException or SqliteException)
        {
            args.Cancel = true;
            ErrorText.Text = $"Couldn't Save Reference. {error.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
