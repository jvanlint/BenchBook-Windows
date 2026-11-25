using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Backup;

/// <summary>A project file that can't be written or read. The message is for the user.</summary>
public sealed class ProjectFileException(string message, Exception? inner = null) : Exception(message, inner)
{
    public static ProjectFileException NotAProjectFile(Exception? inner = null) =>
        new("This isn't a BenchBook project file, or it's damaged.", inner);

    public static ProjectFileException NewerFormat() =>
        new("This project was exported by a newer version of BenchBook. Update BenchBook, then import it.");

    public static ProjectFileException Damaged(string path) =>
        new($"This project file is damaged: “{path}” doesn't match what was exported. Nothing was imported.");

    public static ProjectFileException MissingFile(string name) =>
        new($"“{name}” is missing from your library, so the project couldn't be exported.");

    public static ProjectFileException WriteFailed(Exception inner) =>
        new($"The project file couldn't be written. Check there's enough space where you're saving it. ({inner.Message})", inner);
}

/// <summary>Describes a project file: manifest.json (library-format.md, "Project file").</summary>
public sealed class ProjectFileManifest
{
    public const string ProjectKind = "project";

    public required int Format { get; set; }
    /// <summary>Always "project". A backup has none.</summary>
    public required string Kind { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string AppVersion { get; set; }
    /// <summary>"mac" or "windows". For messages only, never for logic.</summary>
    public required string Platform { get; set; }
    public required string SchemaVersion { get; set; }
    /// <summary>Whether kit prices and paint ownership are in the file.</summary>
    public required bool IncludesPersonalDetails { get; set; }
    public required ProjectFileCounts Counts { get; set; }
    public required List<BackupFile> Files { get; set; }
}

/// <summary>For the import summary only.</summary>
public sealed class ProjectFileCounts
{
    public long Projects { get; set; } = 1;
    public long References { get; set; }
    public long BuildPhotos { get; set; }
    public long Paints { get; set; }
    public long AftermarketItems { get; set; }
    public long Kits { get; set; }
}

/// <summary>
/// Saves one project as a .benchbookproject file: a ZIP of stored entries with
/// manifest.json, library.json (format 2 records) and the project's files, which
/// BenchBook on a Mac or a Windows PC can import.
/// </summary>
public static class ProjectExporter
{
    public const string Extension = ".benchbookproject";

    /// <summary>"F-16C Viper.benchbookproject", with / \ : * ? " &lt; &gt; | and line breaks replaced by "-".</summary>
    public static string SuggestedFileName(string projectName)
    {
        var cleaned = string.Concat(projectName.Select(c => "/\\:*?\"<>|".Contains(c) || IsLineBreak(c) ? '-' : c));
        var name = cleaned.Trim().Trim('.').Trim();
        return (name.Length == 0 ? "Project" : name) + Extension;
    }

    private static bool IsLineBreak(char c) =>
        c < 0x20 || c == 0x85 || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// Writes the project to <paramref name="destination"/>, which appears only
    /// once it's complete. Kit prices, for sale and paint ownership are left out
    /// unless <paramref name="includePersonalDetails"/>.
    /// </summary>
    public static ProjectFileManifest Export(
        LibraryStore store,
        LibraryLocation location,
        string projectId,
        bool includePersonalDetails,
        string destination,
        string appVersion,
        CancellationToken cancellation = default) =>
        Export(store.ReadSnapshot(), location, projectId, includePersonalDetails, destination, appVersion, cancellation);

    /// <summary>As above, from records already read, so the store needn't stay open while the file is written.</summary>
    public static ProjectFileManifest Export(
        LibrarySnapshot library,
        LibraryLocation location,
        string projectId,
        bool includePersonalDetails,
        string destination,
        string appVersion,
        CancellationToken cancellation = default)
    {
        library.Tidy();
        var (records, files) = Package(library, location, projectId, includePersonalDetails);
        var manifest = new ProjectFileManifest
        {
            Format = BackupManifest.CurrentFormat,
            Kind = ProjectFileManifest.ProjectKind,
            CreatedAt = IsoDate.Now(),
            AppVersion = appVersion,
            Platform = "windows",
            SchemaVersion = records.SchemaVersion,
            IncludesPersonalDetails = includePersonalDetails,
            Counts = new ProjectFileCounts
            {
                References = records.References.Count,
                BuildPhotos = records.BuildPhotos.Count,
                Paints = records.Paints.Count,
                AftermarketItems = records.AftermarketItems.Count,
                Kits = records.Kits.Count,
            },
            Files = [],
        };
        Write(records, files, manifest, destination, cancellation);
        return manifest;
    }

    /// <summary>The project's records, and the files to put beside them: entry name and where to read it.</summary>
    internal static (LibrarySnapshot Records, List<(string Path, string Source)> Files) Package(
        LibrarySnapshot library, LibraryLocation location, string projectId, bool includePersonalDetails)
    {
        var project = library.Projects.FirstOrDefault(p => Ids.Comparer.Equals(p.Id, projectId))
            ?? throw new ProjectFileException("This project no longer exists.");
        var records = new LibrarySnapshot { SchemaVersion = library.SchemaVersion };
        var files = new List<(string, string)>();
        var assetIds = new HashSet<string>(Ids.Comparer);
        var tagIds = new HashSet<string>(Ids.Comparer);

        bool Mine(string? id) => Ids.Comparer.Equals(id, project.Id);
        ILookup<string, T> By<T>(IEnumerable<T> items, Func<T, string?> key) where T : LibraryRecord =>
            InOrder(items).Where(i => key(i) is not null).ToLookup(i => key(i)!, Ids.Comparer);

        var photos = InOrder(library.BuildPhotos.Where(p => Mine(p.ProjectId))).ToList();
        var photoIds = new HashSet<string>(photos.Select(p => p.Id), Ids.Comparer);
        records.Projects.Add(project);
        if (project.ProjectPhotoId is not null && !photoIds.Contains(project.ProjectPhotoId)) project.ProjectPhotoId = null;

        var referenceTags = By(library.ReferenceTags, t => t.ReferenceId);
        var moments = By(library.VideoMoments, m => m.ReferenceId);
        var referenceMarkups = By(library.Markups, m => m.ReferenceId);
        foreach (var reference in InOrder(library.References.Where(r => Mine(r.ProjectId))))
        {
            if (reference.AssetId is not null) assetIds.Add(reference.AssetId);
            records.References.Add(reference);
            foreach (var link in referenceTags[reference.Id])
            {
                tagIds.Add(link.TagId);
                records.ReferenceTags.Add(link);
            }
            records.VideoMoments.AddRange(moments[reference.Id]);
            records.Markups.AddRange(referenceMarkups[reference.Id]);
            var poster = location.PosterPath(reference.Id);
            if (File.Exists(poster)) files.Add(($"{BackupWriter.LibraryPrefix}Posters/{reference.Id}.jpg", poster));
        }

        var photoTags = By(library.BuildPhotoTags, t => t.BuildPhotoId);
        var photoMarkups = By(library.Markups, m => m.BuildPhotoId);
        foreach (var photo in photos)
        {
            assetIds.Add(photo.AssetId);
            records.BuildPhotos.Add(photo);
            foreach (var link in photoTags[photo.Id])
            {
                tagIds.Add(link.TagId);
                records.BuildPhotoTags.Add(link);
            }
            records.Markups.AddRange(photoMarkups[photo.Id]);
        }

        // The paints chosen, and for mixes the paints in their recipes.
        var paintsById = library.Paints.ToDictionary(p => p.Id, Ids.Comparer);
        var recipes = library.PaintMixParts.ToLookup(p => p.MixId, Ids.Comparer);
        var paintIds = new HashSet<string>(Ids.Comparer);
        foreach (var row in InOrder(library.ProjectPaints.Where(p => Mine(p.ProjectId))))
        {
            if (row.PaintId is not null && paintsById.TryGetValue(row.PaintId, out var paint))
            {
                paintIds.Add(paint.Id);
                foreach (var part in recipes[paint.Id])
                    if (paintsById.ContainsKey(part.PaintId)) paintIds.Add(part.PaintId);
            }
            if (row.StandardSystem.Length == 0) row.Standard = null;
            records.ProjectPaints.Add(row);
        }
        var owned = new HashSet<string>(library.OwnedPaints.Select(o => o.PaintId), Ids.Comparer);
        foreach (var paint in paintIds.Select(id => paintsById[id]).OrderBy(p => p.IdentityKey, StringComparer.Ordinal))
        {
            records.Paints.Add(paint);
            if (includePersonalDetails && !paint.IsMix && owned.Contains(paint.Id))
                records.OwnedPaints.Add(library.OwnedPaints.First(o => Ids.Comparer.Equals(o.PaintId, paint.Id)));
            records.PaintMixParts.AddRange(recipes[paint.Id]
                .Where(p => paintsById.ContainsKey(p.PaintId))
                .OrderBy(p => p.Position).ThenBy(p => p.Id, StringComparer.Ordinal));
        }

        foreach (var item in InOrder(library.AftermarketItems.Where(a => Mine(a.ProjectId))))
        {
            if (item.ImageAssetId is not null) assetIds.Add(item.ImageAssetId);
            records.AftermarketItems.Add(item);
        }

        foreach (var kit in InOrder(library.Kits.Where(k => Mine(k.ProjectId))))
        {
            if (kit.PictureAssetId is not null) assetIds.Add(kit.PictureAssetId);
            if (!includePersonalDetails)
            {
                kit.PricePaid = null;
                kit.SalePrice = null;
                kit.ForSale = false;
            }
            records.Kits.Add(kit);
        }

        records.Tags.AddRange(library.Tags.Where(t => tagIds.Contains(t.Id)).OrderBy(t => t.NormalizedName, StringComparer.Ordinal));

        foreach (var asset in library.Assets.Where(a => assetIds.Contains(a.Id)).OrderBy(a => a.OriginalRelativePath, StringComparer.Ordinal))
        {
            var source = LibraryPaths.IsSafeOriginalPath(asset.OriginalRelativePath) ? location.FullPath(asset.OriginalRelativePath) : null;
            if (source is null || !File.Exists(source))
                throw ProjectFileException.MissingFile(asset.OriginalFilename ?? asset.OriginalRelativePath);
            records.Assets.Add(asset);
            files.Add((BackupWriter.LibraryPrefix + asset.OriginalRelativePath, source));
        }
        return (records, files);
    }

    /// <summary>Oldest first, then by id, so the same project always gives the same file.</summary>
    private static IEnumerable<T> InOrder<T>(IEnumerable<T> records) where T : LibraryRecord =>
        records.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal);

    /// <summary>
    /// Every entry is stored, not compressed: the files are photos and PDFs that
    /// are compressed already. Each file is hashed as it's copied, and the
    /// manifest goes last, so it always matches what's in the file.
    /// </summary>
    private static void Write(
        LibrarySnapshot records, List<(string Path, string Source)> files, ProjectFileManifest manifest,
        string destination, CancellationToken cancellation)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var temporary = Path.Combine(folder, $"{LibraryPaths.PartialPrefix}{Ids.New()}");
        var date = manifest.CreatedAt;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: Encoding.UTF8))
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(records, BackupJson.Options);
                AddEntry(archive, BackupWriter.LibraryJsonName, date, output => output.Write(json));
                manifest.Files.Add(new BackupFile { Path = BackupWriter.LibraryJsonName, Size = json.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(json)) });

                foreach (var (path, source) in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var input = File.OpenRead(source);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long size = 0;
                    AddEntry(archive, path, date, output =>
                    {
                        var buffer = new byte[1 << 20];
                        int read;
                        while ((read = input.Read(buffer)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            output.Write(buffer, 0, read);
                            size += read;
                        }
                    });
                    manifest.Files.Add(new BackupFile { Path = path, Size = size, Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()) });
                }

                var manifestJson = JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJson.IndentedOptions);
                AddEntry(archive, BackupWriter.ManifestName, date, output => output.Write(manifestJson));
            }
            File.Move(temporary, destination, overwrite: true);
        }
        catch (FileNotFoundException error)
        {
            BackupWriter.TryDeleteFile(temporary);
            throw ProjectFileException.MissingFile(Path.GetFileName(error.FileName ?? ""));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            BackupWriter.TryDeleteFile(temporary);
            throw ProjectFileException.WriteFailed(error);
        }
        catch
        {
            BackupWriter.TryDeleteFile(temporary);
            throw;
        }
    }

    private static void AddEntry(ZipArchive archive, string name, DateTimeOffset date, Action<Stream> write)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = date;
        using var output = entry.Open();
        write(output);
    }
}

/// <summary>A project file unpacked and checked, ready to import. Remove it with <see cref="Discard"/>.</summary>
public sealed class ProjectFile
{
    private const long ManifestLimit = 64L * 1024 * 1024;
    private static readonly byte[] EmptyZipSignature = [0x50, 0x4B, 0x05, 0x06];

    private ProjectFile(string folder, ProjectFileManifest manifest, LibrarySnapshot records)
    {
        Folder = folder;
        Manifest = manifest;
        Records = records;
    }

    /// <summary>Where it's unpacked: manifest.json, library.json and Library\.</summary>
    public string Folder { get; }
    public ProjectFileManifest Manifest { get; }
    public LibrarySnapshot Records { get; }
    public Project Project => Records.Projects[0];

    /// <summary>The folder project files are unpacked in, beside the library so files move quickly.</summary>
    public static string StagingFolder(LibraryLocation location) => Path.Combine(location.AppFolder, "BenchBook Import");

    /// <summary>A file in the unpacked project, e.g. "Library/Posters/….jpg".</summary>
    internal string PathOf(string entryPath) => Path.Combine(Folder, entryPath.Replace('/', Path.DirectorySeparatorChar));

    public void Discard() => BackupWriter.TryDeleteFolder(Folder);

    /// <summary>
    /// Unpacks the file into a new folder and checks it: only safe names, every
    /// file listed in the manifest with the right size and checksum, nothing
    /// else, and exactly one project whose records point only at records in the
    /// file. Nothing in the library changes. Throws a <see cref="ProjectFileException"/> saying what's wrong.
    /// </summary>
    public static ProjectFile Open(string path, LibraryLocation location, CancellationToken cancellation = default)
    {
        // One import at a time; anything left here is from one that was interrupted.
        var staging = StagingFolder(location);
        BackupWriter.TryDeleteFolder(staging);
        var folder = Path.Combine(staging, "Import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            return Unpack(path, folder, cancellation);
        }
        catch
        {
            BackupWriter.TryDeleteFolder(folder);
            throw;
        }
    }

    private static ProjectFile Unpack(string path, string folder, CancellationToken cancellation)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ProjectFileException($"The project file couldn't be opened ({error.Message}).", error);
        }

        using (stream)
        {
            Span<byte> start = stackalloc byte[4];
            var read = stream.ReadAtLeast(start, 4, throwOnEndOfStream: false);
            stream.Position = 0;
            if (read != 4 || !(start.SequenceEqual(ZipChecks.ZipSignature) || start.SequenceEqual(EmptyZipSignature)))
                throw ProjectFileException.NotAProjectFile();

            ZipArchive archive;
            try
            {
                archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (InvalidDataException error)
            {
                throw ProjectFileException.NotAProjectFile(error);
            }

            using (archive)
            {
                var entries = ZipChecks.Files(archive, () => ProjectFileException.NotAProjectFile());
                if (!entries.TryGetValue(BackupWriter.ManifestName, out var manifestEntry)) throw ProjectFileException.NotAProjectFile();
                var manifest = ReadManifest(manifestEntry);

                // Exactly the files the manifest lists.
                var listed = new Dictionary<string, BackupFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in manifest.Files)
                {
                    if (!ZipChecks.IsFilePath(file.Path) || file.Size < 0 || !ZipChecks.IsSha256(file.Sha256) || !listed.TryAdd(file.Path, file))
                        throw ProjectFileException.NotAProjectFile();
                }
                if (!listed.ContainsKey(BackupWriter.LibraryJsonName)) throw ProjectFileException.NotAProjectFile();
                if (entries.Count != listed.Count + 1 || !listed.Keys.All(entries.ContainsKey)) throw ProjectFileException.NotAProjectFile();

                foreach (var file in manifest.Files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    ZipChecks.Extract(entries[file.Path], file, Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar)), ProjectFileException.Damaged);
                }

                var records = ReadRecords(Path.Combine(folder, BackupWriter.LibraryJsonName));
                if (!IsComplete(records, new HashSet<string>(listed.Keys, StringComparer.OrdinalIgnoreCase)))
                    throw ProjectFileException.NotAProjectFile();
                return new ProjectFile(folder, manifest, records);
            }
        }
    }

    private static ProjectFileManifest ReadManifest(ZipArchiveEntry entry)
    {
        var bytes = ZipChecks.Read(entry, ManifestLimit, () => ProjectFileException.NotAProjectFile());
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || !format.TryGetInt32(out var number))
                throw ProjectFileException.NotAProjectFile();
            if (number > BackupManifest.CurrentFormat) throw ProjectFileException.NewerFormat();
            if (number != BackupManifest.CurrentFormat
                || !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != ProjectFileManifest.ProjectKind)
                throw ProjectFileException.NotAProjectFile();
            return JsonSerializer.Deserialize<ProjectFileManifest>(bytes, BackupJson.Options) ?? throw ProjectFileException.NotAProjectFile();
        }
        catch (JsonException error)
        {
            throw ProjectFileException.NotAProjectFile(error);
        }
    }

    /// <summary>The records, with an older schema brought up to date first, as for a backup.</summary>
    private static LibrarySnapshot ReadRecords(string path)
    {
        LibrarySnapshot records;
        try
        {
            using var input = File.OpenRead(path);
            if (JsonNode.Parse(input) is not JsonObject library) throw ProjectFileException.NotAProjectFile();
            var version = library["schemaVersion"]?.GetValueKind() == JsonValueKind.String ? library["schemaVersion"]!.GetValue<string>() : null;
            if (!LibraryRestore.TryParseVersion(version, out var theirs)) throw ProjectFileException.NotAProjectFile();
            LibraryRestore.TryParseVersion(LibrarySnapshot.CurrentSchemaVersion, out var ours);
            // A newer major version may mean records this app can't keep; a newer minor only adds fields.
            if (theirs.Major > ours.Major) throw ProjectFileException.NewerFormat();
            SchemaUpgrades.Upgrade(library);
            records = library.Deserialize<LibrarySnapshot>(BackupJson.Options) ?? throw ProjectFileException.NotAProjectFile();
        }
        catch (JsonException error)
        {
            throw ProjectFileException.NotAProjectFile(error);
        }
        catch (InvalidOperationException error)
        {
            throw ProjectFileException.NotAProjectFile(error);
        }
        records.Tidy();
        return records;
    }

    /// <summary>One named project, and every id a record points at is in the file.</summary>
    internal static bool IsComplete(LibrarySnapshot records, ISet<string> files)
    {
        if (records.Projects.Count != 1 || string.IsNullOrWhiteSpace(records.Projects[0].Name)) return false;
        if (records.FindProblems().Count > 0) return false;
        var mixes = new HashSet<string>(records.Paints.Where(p => p.IsMix).Select(p => p.Id), Ids.Comparer);
        return records.Assets.All(a => LibraryPaths.IsSafeOriginalPath(a.OriginalRelativePath) && files.Contains(BackupWriter.LibraryPrefix + a.OriginalRelativePath))
            && records.OwnedPaints.All(o => !mixes.Contains(o.PaintId))
            && records.Tags.All(t => TagNames.Normalize(t.Name).Length > 0);
    }
}
