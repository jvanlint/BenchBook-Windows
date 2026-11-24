using System.Globalization;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.BuildPhotos;

/// <summary>What happened to one file being added to a project's build photos.</summary>
public abstract record BuildPhotoOutcome
{
    public sealed record Added(BuildPhoto Photo) : BuildPhotoOutcome;

    /// <summary>The same file is already one of this project's build photos.</summary>
    public sealed record Duplicate(BuildPhoto Existing, string Filename) : BuildPhotoOutcome;

    public sealed record Failed(string Filename, string Message) : BuildPhotoOutcome;
}

public sealed class BuildPhotoException(string message) : Exception(message)
{
    public static BuildPhotoException NotAPhoto(string name) =>
        new($"“{name}” isn't a photo. Build photos can be JPEG, PNG, WebP or HEIC images.");

    public static BuildPhotoException ExportFailed(string name) => new($"“{name}” couldn't be exported.");

    public static BuildPhotoException Missing() => new("The saved copy of this photo is missing from the library.");
}

/// <summary>Captions, stages and the order photos are shown in (BuildPhoto+Display.swift).</summary>
public static class BuildPhotoText
{
    /// <summary>Unknown values, e.g. from a newer version, read as WIP.</summary>
    public static bool IsCompleted(BuildPhoto photo) => photo.Stage == BuildPhotoStage.Completed;

    public static string StageName(BuildPhoto photo) => IsCompleted(photo) ? "Completed" : "WIP";

    /// <summary>The first non-empty line of the notes, if there are any.</summary>
    public static string? Caption(BuildPhoto photo) =>
        photo.Notes?.Split('\n', '\r').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    /// <summary>Completed photos first, then newest taken first. Ties: the one added later first.</summary>
    public static IOrderedEnumerable<T> GalleryOrder<T>(IEnumerable<T> items, Func<T, BuildPhoto> photo) =>
        items.OrderBy(i => IsCompleted(photo(i)) ? 0 : 1)
            .ThenByDescending(i => photo(i).TakenAt)
            .ThenByDescending(i => photo(i).CreatedAt);

    /// <summary>
    /// Completed photos first, then work in progress grouped by the local day it
    /// was taken, newest day first. Within a group, newest first.
    /// </summary>
    public static List<BuildPhotoGroup> Groups(IEnumerable<BuildPhotoEntry> entries, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        zone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        var sorted = GalleryOrder(entries, e => e.Photo).ToList();
        var groups = new List<BuildPhotoGroup>();
        var completed = sorted.Where(e => IsCompleted(e.Photo)).ToList();
        if (completed.Count > 0) groups.Add(new BuildPhotoGroup("completed", "Completed", completed));
        foreach (var day in sorted.Where(e => !IsCompleted(e.Photo)).GroupBy(e => TimeZoneInfo.ConvertTime(e.Photo.TakenAt, zone).Date))
            groups.Add(new BuildPhotoGroup(day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), day.Key.ToString("D", culture), day.ToList()));
        return groups;
    }

    /// <summary>
    /// e.g. "2026-11-19 Cockpit painted", with the date in this PC's time zone.
    /// Characters other systems can't use in names, and line breaks, become
    /// spaces; at most 80 characters of caption.
    /// </summary>
    public static string ExportBaseName(BuildPhoto photo, TimeZoneInfo? zone = null)
    {
        var date = TimeZoneInfo.ConvertTime(photo.TakenAt, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Caption(photo) is not { } caption) return date;
        var spaced = new string(caption.Select(c => c is '/' or ':' or '\\' or '?' or '%' or '*' or '|' or '"' or '<' or '>' || char.IsWhiteSpace(c) ? ' ' : c).ToArray());
        var cleaned = string.Join(" ", spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length == 0 ? date : $"{date} {Prefix(cleaned, 80)}";
    }

    /// <summary>The first characters as a reader counts them, so an emoji or accent isn't split.</summary>
    private static string Prefix(string text, int count)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= count ? text : info.SubstringByTextElements(0, count);
    }
}

/// <summary>Every completed photo, or the work in progress from one day.</summary>
/// <param name="Id">"completed", or the day as "2026-11-19".</param>
/// <param name="Title">"Completed", or e.g. "Thursday, 19 November 2026".</param>
public sealed record BuildPhotoGroup(string Id, string Title, IReadOnlyList<BuildPhotoEntry> Entries);

/// <summary>
/// Build photos: adding them, editing their notes, date, stage and tags,
/// choosing the project photo, deleting them, and exporting copies
/// (behaviour.md, "Build photos"; BuildPhotoService.swift).
/// </summary>
public static class BuildPhotoService
{
    // MARK: Adding

    /// <summary>
    /// Adds image files to the project's build photos. Each original is checked,
    /// copied byte for byte into the library and saved before it counts as
    /// added. A file the same as one added earlier in this call is left out
    /// without a report: some apps offer one dragged photo twice.
    /// </summary>
    public static List<BuildPhotoOutcome> Add(IStoreAccess store, LibraryLocation location, IEnumerable<(string Path, string? Filename)> files, string projectId)
    {
        var outcomes = new List<BuildPhotoOutcome>();
        var addedNow = new HashSet<string>(Ids.Comparer);
        foreach (var (path, filename) in files)
        {
            var name = filename ?? Path.GetFileName(path);
            try
            {
                var outcome = AddOne(store, location, path, name, projectId);
                if (outcome is BuildPhotoOutcome.Added added) addedNow.Add(added.Photo.Id);
                else if (outcome is BuildPhotoOutcome.Duplicate duplicate && addedNow.Contains(duplicate.Existing.Id)) continue;
                outcomes.Add(outcome);
            }
            catch (Exception error) when (error is BuildPhotoException or CaptureException)
            {
                outcomes.Add(new BuildPhotoOutcome.Failed(name, error.Message));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                outcomes.Add(new BuildPhotoOutcome.Failed(name, $"“{name}” couldn't be saved ({error.Message})."));
            }
        }
        return outcomes;
    }

    private static BuildPhotoOutcome AddOne(IStoreAccess store, LibraryLocation location, string path, string name, string projectId)
    {
        if (store.With(s => s.Project(projectId)) is null) throw CaptureException.ProjectNotFound();
        InspectedFile inspected;
        try
        {
            // Only images are photos, so a PDF's pages never need opening.
            inspected = FileInspector.Inspect(path, name, _ => false);
        }
        catch (CaptureException) when (!LooksLikeImage(path))
        {
            throw BuildPhotoException.NotAPhoto(name);
        }
        if (inspected.Kind != ReferenceKind.Image) throw BuildPhotoException.NotAPhoto(name);
        if (store.With(s => s.BuildPhotoWithFile(projectId, inspected.Sha256)) is { } existing)
            return new BuildPhotoOutcome.Duplicate(existing, name);

        var taken = PhotoMetadata.DateTakenLocal(File.ReadAllBytes(path));
        var now = IsoDate.Now();
        var assetId = Ids.New();
        var relativePath = $"Originals/{assetId}.{inspected.Extension}";
        var destination = location.FullPath(relativePath)!;
        CopyOriginal(path, destination);
        var asset = new Asset
        {
            Id = assetId, CreatedAt = now, UpdatedAt = now, OriginalRelativePath = relativePath,
            OriginalFilename = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            MimeType = inspected.MimeType, ByteCount = inspected.ByteCount, Sha256 = inspected.Sha256,
            PixelWidth = inspected.PixelWidth, PixelHeight = inspected.PixelHeight,
        };
        var photo = new BuildPhoto
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ProjectId = projectId, AssetId = assetId,
            TakenAt = taken ?? now, Stage = BuildPhotoStage.Wip,
        };
        try
        {
            store.With(s => { s.InsertBuildPhoto(photo, asset); return 0; });
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
        return new BuildPhotoOutcome.Added(photo);
    }

    /// <summary>A file that's an image by its signature, though it couldn't be saved (too big, unreadable).</summary>
    private static bool LooksLikeImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[32];
            return ImageHeaders.Recognise(header.AsSpan(0, stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false))) is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// What to tell the user after adding: nothing when every photo was added;
    /// otherwise the failures and duplicates (ProjectWorkspaceView.swift).
    /// </summary>
    /// <param name="skipped">Names of things dropped that aren't files, e.g. folders.</param>
    public static CaptureReport? Report(IReadOnlyList<BuildPhotoOutcome> outcomes, IReadOnlyList<string>? skipped = null)
    {
        var failures = (skipped ?? []).Select(s => $"“{s}” isn't a photo.")
            .Concat(outcomes.OfType<BuildPhotoOutcome.Failed>().Select(f => f.Message)).ToList();
        var duplicates = outcomes.OfType<BuildPhotoOutcome.Duplicate>().Select(d => d.Filename).ToList();
        if (failures.Count > 0)
            return new CaptureReport(
                failures.Count == 1 ? "A Photo Couldn't Be Added" : $"{failures.Count:N0} Photos Couldn't Be Added",
                string.Join("\n", failures.Concat(duplicates.Select(d => $"“{d}” is already in this project's build photos."))));
        if (duplicates.Count > 0)
            return new CaptureReport("Already in Build Photos", string.Join("\n", duplicates.Select(d => $"“{d}”")));
        return null;
    }

    // MARK: Editing

    /// <summary>Saves the details: notes, date taken, stage and tags.</summary>
    public static void Update(LibraryStore store, BuildPhoto photo, string notes, DateTimeOffset takenAt, string stage, string tagsText)
    {
        photo.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim().Replace("\r\n", "\n").Replace('\r', '\n');
        photo.TakenAt = IsoDate.RoundToMilliseconds(takenAt);
        photo.Stage = stage;
        photo.UpdatedAt = IsoDate.Now();
        if (!store.UpdateBuildPhoto(photo, TagNames.Parse(tagsText))) throw new BuildPhotoException("This photo no longer exists.");
    }

    /// <summary>Deletes the photos, then their stored files if nothing else uses them.</summary>
    public static void Delete(LibraryStore store, LibraryLocation location, IReadOnlyCollection<string> photoIds)
    {
        foreach (var asset in store.DeleteBuildPhotos(photoIds)) ReferenceService.RemoveAssetIfUnused(store, location, asset);
    }

    // MARK: Exporting

    /// <summary>
    /// Saves copies of the originals into <paramref name="folder"/>, named by date
    /// and caption, adding " 2", " 3" and so on rather than overwriting. With
    /// <paramref name="removeLocation"/>, each copy leaves out GPS data; the
    /// pixels are copied as they are. The originals are never changed. Returns
    /// the files written.
    /// </summary>
    public static List<string> Export(IEnumerable<BuildPhotoEntry> entries, LibraryLocation location, string folder, bool removeLocation)
    {
        var written = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OrderBy(e => e.Photo.TakenAt))
        {
            var name = BuildPhotoText.Caption(entry.Photo) ?? entry.Asset?.OriginalFilename ?? "Photo";
            if (entry.Asset is not { } asset || location.FullPath(asset.OriginalRelativePath) is not { } source || !File.Exists(source))
                throw BuildPhotoException.ExportFailed(name);
            var extension = Path.GetExtension(asset.OriginalRelativePath).TrimStart('.');
            var destination = Path.Combine(folder, UniqueName(BuildPhotoText.ExportBaseName(entry.Photo), extension, used, folder));
            try
            {
                if (removeLocation) File.WriteAllBytes(destination, PhotoMetadata.WithoutLocation(File.ReadAllBytes(source)));
                else File.Copy(source, destination);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PhotoMetadataException)
            {
                throw BuildPhotoException.ExportFailed(name);
            }
            written.Add(destination);
        }
        return written;
    }

    private static string UniqueName(string baseName, string extension, HashSet<string> used, string folder)
    {
        var name = $"{baseName}.{extension}";
        for (var number = 2; used.Contains(name) || File.Exists(Path.Combine(folder, name)); number++)
            name = $"{baseName} {number}.{extension}";
        used.Add(name);
        return name;
    }

    /// <summary>Copies byte for byte to a hidden partial file, then renames it into place once complete.</summary>
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
