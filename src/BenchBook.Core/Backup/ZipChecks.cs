using System.IO.Compression;
using System.Security.Cryptography;

namespace BenchBook.Core.Backup;

/// <summary>
/// Checks shared by restoring a backup and importing a project file, which use
/// the same ZIP layout (library-format.md, "Format 2 backup file" and "Project file").
/// Each takes the error to throw, so each file type keeps its own messages.
/// </summary>
internal static class ZipChecks
{
    // Unix file types in the high half of a ZIP entry's external attributes.
    private const int UnixTypeMask = 0xF000;
    private const int UnixDirectory = 0x4000;
    private const int UnixRegular = 0x8000;

    public static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>
    /// Every entry must be a plain file, or one of the library's folders, with a
    /// safe name, not encrypted, and no two may have the same name ignoring case.
    /// Returns the files by name.
    /// </summary>
    public static Dictionary<string, ZipArchiveEntry> Files(ZipArchive archive, Func<Exception> bad)
    {
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            var unixType = (entry.ExternalAttributes >> 16) & UnixTypeMask;
            var isFolder = name.EndsWith('/');
            if (unixType != 0 && unixType != (isFolder ? UnixDirectory : UnixRegular)) throw bad();
            if (entry.IsEncrypted) throw bad();

            var path = isFolder ? name[..^1] : name;
            if (!LibraryPaths.IsSafeRelativePath(path)) throw bad();
            if (isFolder)
            {
                if (path is not ("Library" or "Library/Originals" or "Library/Posters") || !folders.Add(path)) throw bad();
                continue;
            }
            if (!files.TryAdd(name, entry)) throw bad();
        }
        return files;
    }

    /// <summary>library.json, or one file directly in Library/Originals or Library/Posters.</summary>
    public static bool IsFilePath(string path)
    {
        if (path == BackupWriter.LibraryJsonName) return true;
        if (!LibraryPaths.IsSafeRelativePath(path)) return false;
        foreach (var folder in new[] { "Library/Originals/", "Library/Posters/" })
        {
            if (!path.StartsWith(folder, StringComparison.Ordinal)) continue;
            var name = path[folder.Length..];
            return name.Length > 0 && !name.Contains('/') && !name.StartsWith('.');
        }
        return false;
    }

    public static bool IsSha256(string? text) =>
        text is { Length: 64 } && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>A small entry such as manifest.json, refused if it's longer than <paramref name="limit"/>.</summary>
    public static byte[] Read(ZipArchiveEntry entry, long limit, Func<Exception> bad)
    {
        if (entry.Length > limit) throw bad();
        try
        {
            using var input = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(chunk)) > 0)
            {
                total += read;
                if (total > limit) throw bad();
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (InvalidDataException)
        {
            throw bad();
        }
    }

    /// <summary>Unpacks one file, stopping as soon as it's longer than the manifest says, and checks its hash.</summary>
    public static void Extract(ZipArchiveEntry entry, BackupFile file, string destination, Func<string, Exception> damaged)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                total += read;
                if (total > file.Size) throw damaged(file.Path);
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }
            if (total != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                throw damaged(file.Path);
        }
        catch (InvalidDataException)
        {
            // Bad compressed data, or a method other than stored and Deflate.
            throw damaged(file.Path);
        }
    }
}
