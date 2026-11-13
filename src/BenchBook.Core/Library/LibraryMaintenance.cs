using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core;

/// <summary>Space the library's files take, by folder.</summary>
public readonly record struct LibraryUsage(long Originals, long Thumbnails, long Posters)
{
    public long Total => Originals + Thumbnails + Posters;
}

/// <summary>Clean Up, Clear Thumbnails and Space Used (see behaviour.md, "Deleting").</summary>
public static class LibraryMaintenance
{
    /// <summary>
    /// Removes leftovers from interrupted work: unused assets with their files,
    /// files in Originals or Thumbnails with no asset, leftover partial files,
    /// and posters with no reference. Files whose names aren't ids are left
    /// alone, except partial files. Returns how many records and files were removed.
    /// </summary>
    public static int CleanUp(LibraryStore store, LibraryLocation location)
    {
        var removed = 0;
        foreach (var asset in store.UnusedAssets())
        {
            if (!store.DeleteAssetIfUnused(asset.Id)) continue;
            removed++;
            if (location.FullPath(asset.OriginalRelativePath) is { } original) TryDelete(original);
            TryDelete(location.ThumbnailPath(asset.Id));
        }

        var assetIds = store.AssetIds();
        foreach (var folder in new[] { location.OriginalsFolder, location.ThumbnailsFolder })
            foreach (var file in Files(folder))
            {
                var name = Path.GetFileName(file);
                var orphan = name.StartsWith(LibraryPaths.PartialPrefix, StringComparison.Ordinal)
                    || (Ids.Normalize(Stem(name)) is { } id && !assetIds.Contains(id));
                if (orphan && TryDelete(file)) removed++;
            }

        var referenceIds = store.ReferenceIds();
        foreach (var file in Files(location.PostersFolder))
        {
            var id = Ids.Normalize(Stem(Path.GetFileName(file)));
            if ((id is null || !referenceIds.Contains(id)) && TryDelete(file)) removed++;
        }
        return removed;
    }

    /// <summary>Deletes the gallery's thumbnails. Each is made again from its file when it's next shown.</summary>
    public static void ClearThumbnails(LibraryLocation location)
    {
        foreach (var file in Files(location.ThumbnailsFolder)) TryDelete(file);
    }

    /// <summary>Reads file sizes, so call it off the UI thread.</summary>
    public static LibraryUsage MeasureUsage(LibraryLocation location) =>
        new(Size(location.OriginalsFolder), Size(location.ThumbnailsFolder), Size(location.PostersFolder));

    /// <summary>The name before its first dot, so ".partial-x" gives "" and "A.jpg" gives "A".</summary>
    private static string Stem(string name)
    {
        var dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    private static IEnumerable<string> Files(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder).ToList() : [];

    private static long Size(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        long total = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { total += file.Length; }
            catch (IOException) { }
        }
        return total;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
