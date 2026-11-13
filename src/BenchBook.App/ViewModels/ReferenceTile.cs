using BenchBook.App.Services;
using BenchBook.Core;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BenchBook.App.ViewModels;

/// <summary>A tag chip on a tile or in the filter bar.</summary>
public sealed class TagChip(string name, Windows.UI.Color color)
{
    public string Name { get; } = name;
    public SolidColorBrush Fill { get; } = new(Windows.UI.Color.FromArgb(48, color.R, color.G, color.B));
    public SolidColorBrush Stroke { get; } = new(color);
}

/// <summary>One reference in the gallery: picture, title, kind line and tags.</summary>
public sealed partial class ReferenceTile : ObservableObject
{
    public ReferenceTile(ReferenceEntry entry)
    {
        Entry = entry;
        Title = ReferenceText.FallbackTitle(entry);
        Details = ReferenceText.Details(entry);
        Glyph = KindGlyph(entry.Reference.Kind);
        Tags = entry.Tags.Select(t => new TagChip(t.Name, NamedColors.TagColor(t.Color))).ToList();
        var text = entry.Reference.Kind is ReferenceKind.TextSnippet or ReferenceKind.Note ? entry.Reference.Text : null;
        TextPreview = text is null ? "" : ReferenceImporter.Cut(text, 600);
        Duration = entry.Reference.VideoDurationSeconds is { } seconds ? VideoLink.Format(seconds) : "";
        SiteName = entry.Reference.LinkSiteName
            ?? (Uri.TryCreate(entry.Reference.SourcePageUrl, UriKind.Absolute, out var url) ? url.Host : "");
        UpdatePicture();
    }

    public ReferenceEntry Entry { get; }
    public Reference Reference => Entry.Reference;
    public string Title { get; }
    public string Details { get; }
    public string Glyph { get; }
    public IReadOnlyList<TagChip> Tags { get; }
    public bool HasTags => Tags.Count > 0;
    public string TextPreview { get; }
    public string Duration { get; }
    public string SiteName { get; }

    /// <summary>The thumbnail, poster or preview, once loaded.</summary>
    [ObservableProperty] public partial ImageSource? Picture { get; set; }

    /// <summary>A small square site icon, shown at its own size rather than filling the tile.</summary>
    [ObservableProperty] public partial bool IsSiteIcon { get; set; }

    [ObservableProperty] public partial string? PictureNote { get; set; }

    public Visibility PictureVisibility => Picture is not null && !IsSiteIcon ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => IsSiteIcon ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => Picture is null && TextPreview.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Picture is null && TextPreview.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DurationVisibility => Picture is not null && Duration.Length > 0 && !IsSiteIcon ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TagsVisibility => HasTags ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PictureNoteVisibility => PictureNote is null ? Visibility.Collapsed : Visibility.Visible;

    partial void OnPictureChanged(ImageSource? value) => UpdatePicture();
    partial void OnIsSiteIconChanged(bool value) => UpdatePicture();
    partial void OnPictureNoteChanged(string? value) => OnPropertyChanged(nameof(PictureNoteVisibility));

    private void UpdatePicture()
    {
        OnPropertyChanged(nameof(PictureVisibility));
        OnPropertyChanged(nameof(IconVisibility));
        OnPropertyChanged(nameof(TextVisibility));
        OnPropertyChanged(nameof(GlyphVisibility));
        OnPropertyChanged(nameof(DurationVisibility));
    }

    /// <summary>Loads the picture: a thumbnail for files, the poster or preview for links.</summary>
    public async Task LoadPictureAsync(AppServices services)
    {
        string? path = null;
        if (Entry.Asset is { } asset)
        {
            path = await services.Thumbnails.PathFor(asset);
            if (path is null && asset.MimeType == "image/heic")
                PictureNote = "HEIC preview needs the HEIF Image Extensions from the Microsoft Store";
        }
        else if (Reference.Kind is ReferenceKind.VideoLink or ReferenceKind.Webpage)
        {
            var poster = services.Location.PosterPath(Reference.Id);
            if (File.Exists(poster)) path = poster;
        }
        if (path is null) return;

        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 480, DecodePixelType = DecodePixelType.Physical };
            using (var stream = File.OpenRead(path))
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            IsSiteIcon = Reference.Kind == ReferenceKind.Webpage && bitmap.PixelWidth == bitmap.PixelHeight && bitmap.PixelWidth <= 256;
            Picture = bitmap;
        }
        catch (Exception)
        {
            // A damaged thumbnail or poster: the tile keeps its icon.
        }
    }

    public static string KindGlyph(string kind) => kind switch
    {
        ReferenceKind.Image => "\uE91B",       // Photo
        ReferenceKind.Pdf => "\uEA90",         // PDF
        ReferenceKind.Markdown => "\uE8A5",    // Document
        ReferenceKind.Webpage => "\uE774",     // Globe
        ReferenceKind.VideoLink => "\uE714",   // Video
        ReferenceKind.TextSnippet => "\uE7BC", // ReadingList
        ReferenceKind.Note => "\uE70B",        // QuickNote
        _ => "\uE9CE",                         // Unknown
    };
}
