using BenchBook.App.Services;
using BenchBook.Core.BuildPhotos;
using BenchBook.Core.Capture;
using BenchBook.Core.Markups;
using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.App.Viewers;

/// <summary>
/// What the image viewer and the comparison show: an image reference or a
/// build photo. Each keeps its own box notes, and steps through its own
/// neighbours (ViewerImage.swift on the Mac).
/// </summary>
internal sealed record ViewerImage(ReferenceEntry? Reference, BuildPhotoEntry? Photo)
{
    public static implicit operator ViewerImage(ReferenceEntry entry) => new(entry, null);

    public static implicit operator ViewerImage(BuildPhotoEntry entry) => new(null, entry);

    public bool IsBuildPhoto => Photo is not null;

    public string Id => Reference?.Reference.Id ?? Photo!.Photo.Id;

    public string ProjectId => Reference?.Reference.ProjectId ?? Photo!.Photo.ProjectId;

    public Asset? Asset => Reference is not null ? Reference.Asset : Photo!.Asset;

    public IReadOnlyList<Tag> Tags => Reference?.Tags ?? Photo!.Tags;

    /// <summary>A reference's title; a build photo's caption, or the date it was taken.</summary>
    public string Title => Reference is { } entry ? ReferenceText.FallbackTitle(entry) : Words.BuildPhotoTitle(Photo!.Photo);

    /// <summary>When it was added to BenchBook.</summary>
    public DateTimeOffset Added => Reference?.Reference.CapturedAt ?? Photo!.Photo.CreatedAt;

    /// <summary>The page an image reference came from, if known.</summary>
    public string? PageUrl => Reference?.Reference.SourcePageUrl ?? Reference?.Reference.OriginalResourceUrl;

    public List<Markup> Notes(LibraryStore store) =>
        ImageNotes.Sorted(IsBuildPhoto ? store.BuildPhotoMarkups(Id) : store.Markups(Id));

    public Markup? AddNote(LibraryStore store, PixelRect drawn, double width, double height) =>
        IsBuildPhoto ? ImageNotes.Add(store, null, drawn, width, height, buildPhotoId: Id) : ImageNotes.Add(store, Id, drawn, width, height);

    /// <summary>Previous and next step through the project's image references, or its build photos in grid order.</summary>
    public List<ViewerImage> Siblings(LibraryStore store) => IsBuildPhoto
        ? BuildPhotoText.Groups(store.ProjectBuildPhotos(ProjectId)).SelectMany(g => g.Entries).Select(e => (ViewerImage)e).ToList()
        : store.ProjectReferences(ProjectId).Where(e => e.Reference.Kind == ReferenceKind.Image).Select(e => (ViewerImage)e).ToList();

    public bool SameAs(ViewerImage other) => Ids.Comparer.Equals(Id, other.Id);
}
