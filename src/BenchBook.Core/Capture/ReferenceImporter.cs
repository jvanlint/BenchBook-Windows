using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Capture;

/// <summary>Runs work on the store one piece at a time. The app shares one connection between threads.</summary>
public interface IStoreAccess
{
    T With<T>(Func<LibraryStore, T> work);
}

/// <summary>Something being added: a file, a link or some text.</summary>
public abstract record CapturedItem
{
    /// <param name="OriginalFilename">The name the user would recognise. The file at Path may be a temporary copy.</param>
    /// <param name="ResourceUrl">The web address the file came from, when known.</param>
    public sealed record File(string Path, string? OriginalFilename = null, string? ResourceUrl = null) : CapturedItem;

    public sealed record Link(string Url, string? Title = null) : CapturedItem;

    public sealed record Text(string Content, string? SourceUrl = null, string? Title = null) : CapturedItem;
}

public abstract record CaptureOutcome
{
    public sealed record Added(Reference Reference) : CaptureOutcome;

    /// <summary>The same file is already in the project.</summary>
    public sealed record Duplicate(Reference Existing, string ExistingTitle) : CaptureOutcome;

    public sealed record Failed(CapturedItem Item, string Message) : CaptureOutcome;
}

/// <summary>What to tell the user after a capture, if anything.</summary>
public sealed record CaptureReport(string Title, string Message);

/// <summary>
/// The one import path for drops, pastes, Add Files and the Share target
/// (behaviour.md, "Capture"). Each item is checked, its file copied into the
/// library and its reference saved before it counts as added.
/// </summary>
public sealed class ReferenceImporter(IStoreAccess store, LibraryLocation location, Func<string, bool> pdfHasPages)
{
    public const int MaxTextLength = 100_000;

    /// <summary>Imports the items one by one. Ones that fail don't stop the rest.</summary>
    public List<CaptureOutcome> Import(IEnumerable<CapturedItem> items, string projectId, IReadOnlyList<string>? tags = null, string? note = null)
    {
        var outcomes = new List<CaptureOutcome>();
        foreach (var item in items)
        {
            try
            {
                if (store.With(s => s.Project(projectId)) is null) throw CaptureException.ProjectNotFound();
                outcomes.Add(item switch
                {
                    CapturedItem.File file => ImportFile(file, projectId, tags ?? [], note),
                    CapturedItem.Link link => ImportLink(link, projectId, tags ?? [], note),
                    CapturedItem.Text text => ImportText(text, projectId, tags ?? [], note),
                    _ => throw new ArgumentException("Unknown item."),
                });
            }
            catch (CaptureException error)
            {
                outcomes.Add(new CaptureOutcome.Failed(item, error.Message));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                outcomes.Add(new CaptureOutcome.Failed(item, $"“{Name(item)}” couldn't be saved ({error.Message})."));
            }
        }
        return outcomes;
    }

    private CaptureOutcome ImportFile(CapturedItem.File item, string projectId, IReadOnlyList<string> tags, string? note)
    {
        var name = Name(item);
        // Checking and hashing can take a while, so they happen outside the store.
        var inspected = FileInspector.Inspect(item.Path, name, pdfHasPages);
        if (store.With(s => s.ReferenceWithFile(projectId, inspected.Sha256)) is { } existing)
        {
            var entry = store.With(s => s.ReferenceEntries([existing.Id])).Single();
            return new CaptureOutcome.Duplicate(existing, ReferenceText.FallbackTitle(entry));
        }

        var now = IsoDate.Now();
        var assetId = Ids.New();
        var relativePath = $"Originals/{assetId}.{inspected.Extension}";
        var destination = location.FullPath(relativePath)!;
        CopyOriginal(item.Path, destination);

        var asset = new Asset
        {
            Id = assetId, CreatedAt = now, UpdatedAt = now,
            OriginalRelativePath = relativePath,
            OriginalFilename = Trimmed(item.OriginalFilename ?? Path.GetFileName(item.Path)),
            MimeType = inspected.MimeType,
            ByteCount = inspected.ByteCount,
            Sha256 = inspected.Sha256,
            PixelWidth = inspected.PixelWidth,
            PixelHeight = inspected.PixelHeight,
        };
        var reference = NewReference(projectId, inspected.Kind, now);
        reference.AssetId = assetId;
        reference.Note = Trimmed(note);
        reference.OriginalResourceUrl = Trimmed(item.ResourceUrl);
        try
        {
            store.With(s => { s.InsertReference(reference, asset, [], tags); return 0; });
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
        return new CaptureOutcome.Added(reference);
    }

    private CaptureOutcome ImportLink(CapturedItem.Link item, string projectId, IReadOnlyList<string> tags, string? note)
    {
        if (!IsWebLink(item.Url, out var url)) throw CaptureException.InvalidLink(item.Url);
        var video = VideoLink.Parse(url);
        var now = IsoDate.Now();
        var reference = NewReference(projectId, video is null ? ReferenceKind.Webpage : ReferenceKind.VideoLink, now);
        reference.Title = Trimmed(item.Title);
        reference.Note = Trimmed(note);
        reference.SourcePageUrl = url.AbsoluteUri;
        var moments = video?.StartSeconds is { } start
            ? new[] { new VideoMoment { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ReferenceId = reference.Id, OffsetSeconds = start } }
            : [];
        store.With(s => { s.InsertReference(reference, null, moments, tags); return 0; });
        return new CaptureOutcome.Added(reference);
    }

    private CaptureOutcome ImportText(CapturedItem.Text item, string projectId, IReadOnlyList<string> tags, string? note)
    {
        var text = item.Content.Trim();
        if (text.Length == 0) throw CaptureException.EmptyText();
        var now = IsoDate.Now();
        var reference = NewReference(projectId, ReferenceKind.TextSnippet, now);
        reference.Title = Trimmed(item.Title);
        reference.Note = Trimmed(note);
        reference.Text = Cut(text, MaxTextLength);
        reference.SourcePageUrl = IsWebLink(item.SourceUrl, out var source) ? source.AbsoluteUri : null;
        store.With(s => { s.InsertReference(reference, null, [], tags); return 0; });
        return new CaptureOutcome.Added(reference);
    }

    /// <summary>
    /// What to tell the user about a batch: nothing when everything was added;
    /// otherwise the failures and duplicates in one message.
    /// </summary>
    public static CaptureReport? Report(IReadOnlyList<CaptureOutcome> outcomes, IReadOnlyList<string>? skipped = null)
    {
        var duplicates = outcomes.OfType<CaptureOutcome.Duplicate>().Select(d => d.ExistingTitle).ToList();
        var failures = (skipped ?? []).Select(s => $"“{s}” isn't a type BenchBook can save.")
            .Concat(outcomes.OfType<CaptureOutcome.Failed>().Select(f => f.Message)).ToList();
        if (failures.Count > 0)
            return new CaptureReport(
                failures.Count == 1 ? "An Item Couldn't Be Added" : $"{failures.Count} Items Couldn't Be Added",
                string.Join("\n", failures.Concat(duplicates.Select(d => $"“{d}” is already saved to this project."))));
        if (duplicates.Count > 0)
            return new CaptureReport("Already Saved to This Project", string.Join("\n", duplicates.Select(d => $"“{d}”")));
        return null;
    }

    /// <summary>http or https, with a host.</summary>
    public static bool IsWebLink(string? text, out Uri url)
    {
        url = null!;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host)) return false;
        url = parsed;
        return true;
    }

    internal static Reference NewReference(string projectId, string kind, DateTimeOffset now) => new()
    {
        Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ProjectId = projectId, Kind = kind, CapturedAt = now,
    };

    /// <summary>Copies byte-for-byte to a hidden partial file, then renames it into place once complete.</summary>
    private static void CopyOriginal(string source, string destination)
    {
        var folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        var partial = Path.Combine(folder, LibraryPaths.PartialPrefix + Ids.New());
        try
        {
            File.Copy(source, partial);
            File.Move(partial, destination);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static string Name(CapturedItem item) => item switch
    {
        CapturedItem.File file => file.OriginalFilename ?? Path.GetFileName(file.Path),
        CapturedItem.Link link => link.Url,
        _ => "Text",
    };

    internal static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Cuts to a length without splitting a surrogate pair.</summary>
    public static string Cut(string text, int length)
    {
        if (text.Length <= length) return text;
        var end = char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
        return text[..end];
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
