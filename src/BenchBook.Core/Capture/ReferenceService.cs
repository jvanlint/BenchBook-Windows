using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Capture;

/// <summary>Display text for references, shared by every screen.</summary>
public static class ReferenceText
{
    /// <summary>Line feed, carriage return, line and paragraph separators, next line, vertical tab and form feed.</summary>
    private static readonly char[] LineBreaks = [(char)0x0A, (char)0x0D, (char)0x2028, (char)0x2029, (char)0x85, (char)0x0B, (char)0x0C];

    public static string KindName(string kind) => kind switch
    {
        ReferenceKind.Image => "Image",
        ReferenceKind.Pdf => "PDF",
        ReferenceKind.Markdown => "Markdown",
        ReferenceKind.Webpage => "Webpage",
        ReferenceKind.VideoLink => "Video",
        ReferenceKind.TextSnippet => "Text",
        ReferenceKind.Note => "Note",
        _ => kind,
    };

    public static string KindPlural(string kind) => kind switch
    {
        ReferenceKind.Image => "Images",
        ReferenceKind.Pdf => "PDFs",
        ReferenceKind.Markdown => "Markdown Files",
        ReferenceKind.Webpage => "Webpages",
        ReferenceKind.VideoLink => "Videos",
        ReferenceKind.TextSnippet => "Text Snippets",
        ReferenceKind.Note => "Notes",
        _ => kind,
    };

    /// <summary>Title; file name; first line of text (120 characters); source host; kind name.</summary>
    public static string FallbackTitle(ReferenceEntry entry)
    {
        var reference = entry.Reference;
        if (!string.IsNullOrEmpty(reference.Title)) return reference.Title;
        if (!string.IsNullOrEmpty(entry.Asset?.OriginalFilename)) return entry.Asset.OriginalFilename;
        if (!string.IsNullOrEmpty(reference.Text))
        {
            var line = reference.Text.Split(LineBreaks)[0];
            return ReferenceImporter.Cut(line, 120);
        }
        if (Uri.TryCreate(reference.SourcePageUrl, UriKind.Absolute, out var url) && url.Host.Length > 0) return url.Host;
        return KindName(reference.Kind);
    }

    /// <summary>
    /// The line under a tile's title, as the Mac builds it: "Image · 5 notes",
    /// "PDF · 4 markups", "Video · Channel" (or its first moment, "Video · 1:30"),
    /// "Webpage · JetPhotos" (or the host), "Text · example.com".
    /// </summary>
    public static string Details(ReferenceEntry entry)
    {
        var reference = entry.Reference;
        var kind = KindName(reference.Kind);
        string Count(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";
        string? Host() => Uri.TryCreate(reference.SourcePageUrl, UriKind.Absolute, out var url) && url.Host.Length > 0 ? url.Host : null;
        var detail = reference.Kind switch
        {
            ReferenceKind.Image when entry.MarkupCount > 0 => Count(entry.MarkupCount, "note", "notes"),
            ReferenceKind.Pdf when entry.MarkupCount > 0 => Count(entry.MarkupCount, "markup", "markups"),
            ReferenceKind.VideoLink => reference.VideoChannel
                ?? (entry.Moments.Count > 0 ? VideoLink.Format(entry.Moments[0].OffsetSeconds) : null),
            ReferenceKind.Webpage => reference.LinkSiteName ?? Host(),
            ReferenceKind.TextSnippet => Host(),
            _ => null,
        };
        return detail is null ? kind : $"{kind} · {detail}";
    }
}

/// <summary>Reference edits, notes and deleting, with clean-up of stored files.</summary>
public static class ReferenceService
{
    /// <summary>A note the user writes. Needs a title or some text; the body keeps its line breaks and indentation.</summary>
    public static Reference CreateNote(LibraryStore store, string projectId, string title, string text, string tagsText)
    {
        if (ReferenceImporter.Trimmed(title) is null && ReferenceImporter.Trimmed(text) is null) throw CaptureException.EmptyNote();
        if (store.Project(projectId) is null) throw CaptureException.ProjectNotFound();
        var reference = ReferenceImporter.NewReference(projectId, ReferenceKind.Note, IsoDate.Now());
        FillNote(reference, title, text);
        store.InsertReference(reference, null, [], TagNames.Parse(tagsText));
        return reference;
    }

    public static void UpdateNote(LibraryStore store, Reference reference, string title, string text, string tagsText)
    {
        if (ReferenceImporter.Trimmed(title) is null && ReferenceImporter.Trimmed(text) is null) throw CaptureException.EmptyNote();
        FillNote(reference, title, text);
        reference.UpdatedAt = IsoDate.Now();
        Save(store, reference, TagNames.Parse(tagsText));
    }

    /// <summary>Title, note and tags from the details dialog.</summary>
    public static void UpdateDetails(LibraryStore store, Reference reference, string title, string note, string tagsText)
    {
        reference.Title = ReferenceImporter.Trimmed(title);
        reference.Note = ReferenceImporter.Trimmed(note);
        reference.UpdatedAt = IsoDate.Now();
        Save(store, reference, TagNames.Parse(tagsText));
    }

    /// <summary>Deletes references with their tag links, moments, markups and posters, then any files nothing else uses.</summary>
    public static void Delete(LibraryStore store, LibraryLocation location, IReadOnlyCollection<string> referenceIds)
    {
        var assets = store.DeleteReferences(referenceIds);
        foreach (var id in referenceIds) TryDelete(location.PosterPath(id));
        foreach (var assetId in assets) RemoveAssetIfUnused(store, location, assetId);
    }

    public static void RemoveAssetIfUnused(LibraryStore store, LibraryLocation location, string assetId)
    {
        var asset = store.Asset(assetId);
        if (asset is null || !store.DeleteAssetIfUnused(assetId)) return;
        if (location.FullPath(asset.OriginalRelativePath) is { } original) TryDelete(original);
        TryDelete(location.ThumbnailPath(assetId));
    }

    private static void FillNote(Reference reference, string title, string text)
    {
        reference.Title = ReferenceImporter.Trimmed(title);
        // Keep the writer's line breaks and indentation; trim only the ends.
        var body = ReferenceImporter.Trimmed(text);
        reference.Text = body is null ? null : ReferenceImporter.Cut(body, ReferenceImporter.MaxTextLength);
    }

    private static void Save(LibraryStore store, Reference reference, IEnumerable<string> tags)
    {
        if (!store.UpdateReference(reference, tags)) throw new CaptureException("This reference no longer exists.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
