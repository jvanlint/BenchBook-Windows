using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Backup;

/// <summary>Writes a format 2 backup (see library-format.md, "Writing a backup").</summary>
public static partial class BackupWriter
{
    public const string ManifestName = "manifest.json";
    public const string LibraryJsonName = "library.json";
    public const string LibraryPrefix = "Library/";

    /// <summary>The backed-up folders. Thumbnails are made again when shown.</summary>
    internal static readonly string[] Folders = ["Originals", "Posters"];

    /// <summary>Formats whose bytes are already compressed, so they're stored as they are.</summary>
    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic",
    };

    /// <summary>e.g. "BenchBook Backup 2026-11-13.benchbookbackup".</summary>
    public static string SuggestedFileName(DateTime localDate) =>
        $"BenchBook Backup {localDate:yyyy-MM-dd}.benchbookbackup";

    /// <summary>
    /// Backs up the library to <paramref name="destination"/>. The file appears
    /// only once it's complete; an existing file there is replaced.
    /// </summary>
    public static BackupManifest BackUp(
        LibraryStore store,
        LibraryLocation location,
        string destination,
        string appVersion,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default)
    {
        var staging = Path.Combine(location.AppFolder, "BenchBook Backup Staging");
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            var stagedLibrary = Path.Combine(staging, "Library");

            // 1. Copy the files first, so ones deleted while backing up are still here.
            CopyFolders(location.LibraryRoot, stagedLibrary, cancellation);

            // 2. The records, in one consistent read.
            var snapshot = store.ReadSnapshot();
            snapshot.Tidy();
            var libraryJson = Path.Combine(staging, LibraryJsonName);
            using (var file = File.Create(libraryJson))
                JsonSerializer.Serialize(file, snapshot, BackupJson.Options);

            // 3. Files added while the records were read.
            CopyFolders(location.LibraryRoot, stagedLibrary, cancellation);

            // 4. Only the files the records name.
            var included = new List<string> { LibraryJsonName };
            foreach (var asset in snapshot.Assets)
            {
                var path = LibraryPrefix + asset.OriginalRelativePath;
                if (!File.Exists(StagedPath(staging, path))) throw BackupException.MissingFile(asset.OriginalRelativePath);
                included.Add(path);
            }
            foreach (var reference in snapshot.References)
            {
                var path = $"{LibraryPrefix}Posters/{reference.Id}.jpg";
                if (File.Exists(StagedPath(staging, path))) included.Add(path);
            }
            included = included.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // 5. Hash, describe and zip.
            var totalBytes = included.Sum(p => new FileInfo(StagedPath(staging, p)).Length);
            var done = 0L;
            void Advance(long bytes)
            {
                done += bytes;
                progress?.Report(totalBytes == 0 ? 1 : 0.5 * done / totalBytes);
            }

            var files = new List<BackupFile>();
            foreach (var path in included)
            {
                cancellation.ThrowIfCancellationRequested();
                var full = StagedPath(staging, path);
                var size = new FileInfo(full).Length;
                files.Add(new BackupFile { Path = path, Size = size, Sha256 = HashFile(full) });
                Advance(size);
            }

            var manifest = new BackupManifest
            {
                Format = BackupManifest.CurrentFormat,
                CreatedAt = IsoDate.Now(),
                AppVersion = appVersion,
                Platform = "windows",
                SchemaVersion = snapshot.SchemaVersion,
                Counts = new BackupCounts
                {
                    Projects = snapshot.Projects.Count,
                    References = snapshot.References.Count,
                    Paints = snapshot.Paints.Count,
                },
                Files = files,
            };

            WriteArchive(staging, manifest, destination, Advance, cancellation);
            progress?.Report(1);
            return manifest;
        }
        finally
        {
            TryDeleteFolder(staging);
        }
    }

    private static string StagedPath(string staging, string entryPath) =>
        Path.Combine(staging, entryPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Links or copies Originals and Posters, skipping hidden and partial files and ones already there.</summary>
    private static void CopyFolders(string libraryRoot, string stagedLibrary, CancellationToken cancellation)
    {
        foreach (var folder in Folders)
        {
            var source = Path.Combine(libraryRoot, folder);
            var target = Path.Combine(stagedLibrary, folder);
            Directory.CreateDirectory(target);
            if (!Directory.Exists(source)) continue;
            foreach (var file in Directory.EnumerateFiles(source))
            {
                cancellation.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (LibraryPaths.IsHiddenOrPartial(name)) continue;
                var copy = Path.Combine(target, name);
                if (File.Exists(copy)) continue;
                try
                {
                    LinkOrCopy(file, copy);
                }
                catch (FileNotFoundException)
                {
                    // Deleted since it was listed. Step 4 finds out if it was needed.
                }
            }
        }
    }

    /// <summary>
    /// Originals are never changed once written, so a hard link is as good as a
    /// copy and takes no time or space. Falls back to copying.
    /// </summary>
    private static void LinkOrCopy(string source, string destination)
    {
        if (OperatingSystem.IsWindows() && CreateHardLink(destination, source, IntPtr.Zero)) return;
        File.Copy(source, destination);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    private static void WriteArchive(
        string staging, BackupManifest manifest, string destination, Action<long> advance, CancellationToken cancellation)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var temporary = Path.Combine(folder, $"{LibraryPaths.PartialPrefix}{Ids.New()}");
        try
        {
            // A seekable file lets each entry's header be filled in after its data,
            // so there are no data descriptors.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: Encoding.UTF8))
            {
                var manifestEntry = archive.CreateEntry(ManifestName, CompressionLevel.Optimal);
                manifestEntry.LastWriteTime = manifest.CreatedAt;
                using (var output = manifestEntry.Open())
                    JsonSerializer.Serialize(output, manifest, BackupJson.IndentedOptions);

                foreach (var file in manifest.Files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var full = StagedPath(staging, file.Path);
                    var level = CompressedExtensions.Contains(Path.GetExtension(file.Path))
                        ? CompressionLevel.NoCompression
                        : CompressionLevel.Optimal;
                    var entry = archive.CreateEntry(file.Path, level);
                    entry.LastWriteTime = File.GetLastWriteTimeUtc(full);
                    using var input = File.OpenRead(full);
                    using var output = entry.Open();
                    input.CopyTo(output);
                    advance(file.Size);
                }
            }
            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(temporary);
            throw BackupException.ArchiveFailed(error);
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
