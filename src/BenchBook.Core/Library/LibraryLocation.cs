namespace BenchBook.Core;

/// <summary>
/// Where a library lives:
/// <code>
/// &lt;app folder&gt;\BenchBook.db          the store
/// &lt;app folder&gt;\Library\Originals\    the user's files, byte-for-byte
/// &lt;app folder&gt;\Library\Posters\      video posters and webpage previews
/// &lt;app folder&gt;\Library\Thumbnails\   made from originals; can be deleted
/// &lt;app folder&gt;\Inbox\                captures from the share window, until they're imported
/// </code>
/// The app uses %LOCALAPPDATA%\BenchBook. Tests use a temporary folder.
/// </summary>
public sealed class LibraryLocation
{
    public const string StoreFileName = "BenchBook.db";

    public LibraryLocation(string appFolder)
    {
        AppFolder = Path.GetFullPath(appFolder);
    }

    public static LibraryLocation Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenchBook"));

    public string AppFolder { get; }
    public string StorePath => Path.Combine(AppFolder, StoreFileName);
    public string LibraryRoot => Path.Combine(AppFolder, "Library");
    public string OriginalsFolder => Path.Combine(LibraryRoot, "Originals");
    public string PostersFolder => Path.Combine(LibraryRoot, "Posters");
    public string ThumbnailsFolder => Path.Combine(LibraryRoot, "Thumbnails");
    public string SettingsPath => Path.Combine(AppFolder, "Settings.json");

    /// <summary>Captures from the share window wait here until the app imports them.</summary>
    public string InboxFolder => Path.Combine(AppFolder, "Inbox");

    /// <summary>The projects the share window lists, kept up to date by the app.</summary>
    public string ShareProjectsPath => Path.Combine(InboxFolder, "Projects.json");

    /// <summary>The store and the files SQLite keeps beside it.</summary>
    public IEnumerable<string> StoreFiles => [StorePath, StorePath + "-wal", StorePath + "-shm"];

    /// <summary>The full path of a path stored in a record, or null if it would leave the library.</summary>
    public string? FullPath(string relativePath)
    {
        if (!LibraryPaths.IsSafeRelativePath(relativePath)) return null;
        return Path.Combine(LibraryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string PosterPath(string referenceId) => Path.Combine(PostersFolder, $"{referenceId}.jpg");
    public string ThumbnailPath(string assetId) => Path.Combine(ThumbnailsFolder, $"{assetId}.png");
}

/// <summary>Rules for relative paths in records and backups.</summary>
public static class LibraryPaths
{
    public const string PartialPrefix = ".partial-";

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// A path that's safe to create under a folder on Windows: "/" separators,
    /// not absolute, no drive letters, no "." or ".." segments, no device names,
    /// and no characters Windows can't use in a name.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024) return false;
        if (path[0] == '/') return false;
        foreach (var segment in path.Split('/'))
            if (!IsSafeName(segment)) return false;
        return true;
    }

    /// <summary>One file or folder name.</summary>
    public static bool IsSafeName(string name)
    {
        if (name.Length == 0 || name.Length > 255) return false;
        if (name is "." or "..") return false;
        if (name.EndsWith('.') || name.EndsWith(' ')) return false;
        foreach (var c in name)
            if (c < 0x20 || c is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*') return false;
        var stem = name.Split('.')[0].TrimEnd(' ');
        return !DeviceNames.Contains(stem);
    }

    /// <summary>An asset's path: one file directly in Originals, not hidden.</summary>
    public static bool IsSafeOriginalPath(string? path) =>
        IsSafeRelativePath(path)
        && path!.StartsWith("Originals/", StringComparison.Ordinal)
        && path.Count(c => c == '/') == 1
        && !path["Originals/".Length..].StartsWith('.');

    public static bool IsHiddenOrPartial(string fileName) => fileName.StartsWith('.');
}
