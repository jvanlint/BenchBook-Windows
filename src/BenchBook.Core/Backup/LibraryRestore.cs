using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Backup;

/// <summary>A backup that has been checked and unpacked, ready to swap in at the next launch.</summary>
public sealed record PreparedRestore(BackupManifest Manifest, bool SchemaIsNewer);

/// <summary>What the launch that finished a restore tells the user.</summary>
public abstract record RestoreOutcome
{
    /// <summary>Restored. <paramref name="PreviousLibrary"/> is null when there was no library before.</summary>
    public sealed record Restored(DateTimeOffset BackupDate, string? PreviousLibrary) : RestoreOutcome;

    public sealed record Failed(string Message) : RestoreOutcome;
}

/// <summary>
/// Restoring replaces the whole library, so it happens in two parts: the backup
/// is unpacked and checked while BenchBook runs (<see cref="Prepare"/>), then the
/// libraries are swapped at the next launch, before the store opens
/// (<see cref="FinishPending"/>). See library-format.md, "Restoring a backup".
/// </summary>
public static class LibraryRestore
{
    private const long ManifestLimit = 64L * 1024 * 1024;
    private const string PreviousPrefix = "BenchBook Before Restore ";

    // Unix file types in the high half of a ZIP entry's external attributes.
    private const int UnixTypeMask = 0xF000;
    private const int UnixDirectory = 0x4000;
    private const int UnixRegular = 0x8000;

    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    public static string StagingFolder(LibraryLocation location) => Path.Combine(location.AppFolder, "BenchBook Restore");

    private static string PendingPath(LibraryLocation location) => Path.Combine(StagingFolder(location), "pending.json");

    // MARK: Check

    /// <summary>
    /// Checks the backup and unpacks it beside the store, building a new store
    /// from its records. The current library isn't touched. Throws a
    /// <see cref="BackupException"/> saying what's wrong.
    /// </summary>
    public static PreparedRestore Prepare(string archivePath, LibraryLocation location, CancellationToken cancellation = default)
    {
        var staging = StagingFolder(location);
        BackupWriter.TryDeleteFolder(staging);
        try
        {
            Directory.CreateDirectory(staging);
            var prepared = Unpack(archivePath, staging, cancellation);
            return prepared;
        }
        catch
        {
            BackupWriter.TryDeleteFolder(staging);
            throw;
        }
    }

    private static PreparedRestore Unpack(string archivePath, string staging, CancellationToken cancellation)
    {
        using var stream = OpenBackup(archivePath);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException error)
        {
            throw BackupException.NotABackup(error);
        }

        using (archive)
        {
            var entries = CheckEntries(archive);
            if (!entries.TryGetValue(BackupWriter.ManifestName, out var manifestEntry)) throw BackupException.NotABackup();
            var manifest = ReadManifest(manifestEntry);

            // The files must be exactly the ones the manifest lists.
            var listed = new Dictionary<string, BackupFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                if (!IsBackupFilePath(file.Path) || file.Size < 0 || !IsSha256(file.Sha256) || !listed.TryAdd(file.Path, file))
                    throw BackupException.NotABackup();
            }
            if (!listed.ContainsKey(BackupWriter.LibraryJsonName)) throw BackupException.NotABackup();
            foreach (var name in entries.Keys)
                if (name != BackupWriter.ManifestName && !listed.ContainsKey(name)) throw BackupException.Damaged(name);

            foreach (var file in manifest.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(file.Path, out var entry)) throw BackupException.Damaged(file.Path);
                Extract(entry, file, Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            }

            var snapshot = ReadLibrary(Path.Combine(staging, BackupWriter.LibraryJsonName));
            var schemaIsNewer = CheckSchemaVersion(snapshot.SchemaVersion);
            snapshot.Tidy();
            var problems = snapshot.FindProblems();
            if (problems.Count > 0)
                throw new BackupException($"This backup can't be restored because its records don't fit together: {problems[0]} Your library hasn't been changed.");
            foreach (var asset in snapshot.Assets)
                if (!listed.ContainsKey(BackupWriter.LibraryPrefix + asset.OriginalRelativePath))
                    throw BackupException.Damaged(BackupWriter.LibraryPrefix + asset.OriginalRelativePath);

            Directory.CreateDirectory(Path.Combine(staging, "Library", "Originals"));
            Directory.CreateDirectory(Path.Combine(staging, "Library", "Posters"));
            try
            {
                LibraryStore.Create(Path.Combine(staging, LibraryLocation.StoreFileName), snapshot);
            }
            catch (Exception error) when (error is SqliteException or StoreOpenException)
            {
                throw new BackupException($"BenchBook couldn't build a library from the backup ({error.Message}). Your library hasn't been changed.", error);
            }
            return new PreparedRestore(manifest, schemaIsNewer);
        }
    }

    private static FileStream OpenBackup(string archivePath)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new BackupException($"The backup couldn't be opened ({error.Message}).", error);
        }

        // A ZIP file starts with "PK\3\4". Anything else is a format 1 backup from a Mac.
        Span<byte> start = stackalloc byte[4];
        var read = stream.ReadAtLeast(start, 4, throwOnEndOfStream: false);
        stream.Position = 0;
        if (read == 4 && start.SequenceEqual(ZipSignature)) return stream;
        stream.Dispose();
        throw read == 0 ? BackupException.NotABackup() : BackupException.FormatOne();
    }

    /// <summary>
    /// Every entry must be a plain file or folder with a safe name, and no two
    /// may have the same name ignoring case. Returns the files by name.
    /// </summary>
    private static Dictionary<string, ZipArchiveEntry> CheckEntries(ZipArchive archive)
    {
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            var unixType = (entry.ExternalAttributes >> 16) & UnixTypeMask;
            var isFolder = name.EndsWith('/');
            if (unixType != 0 && unixType != (isFolder ? UnixDirectory : UnixRegular)) throw BackupException.NotABackup();

            var path = isFolder ? name[..^1] : name;
            if (!LibraryPaths.IsSafeRelativePath(path)) throw BackupException.NotABackup();
            if (isFolder)
            {
                if (path is not ("Library" or "Library/Originals" or "Library/Posters") || !folders.Add(path))
                    throw BackupException.NotABackup();
                continue;
            }
            if (!files.TryAdd(name, entry)) throw BackupException.NotABackup();
        }
        return files;
    }

    /// <summary>library.json, or one file directly in Library/Originals or Library/Posters.</summary>
    private static bool IsBackupFilePath(string path)
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

    private static bool IsSha256(string text) =>
        text.Length == 64 && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static BackupManifest ReadManifest(ZipArchiveEntry entry)
    {
        if (entry.Length > ManifestLimit) throw BackupException.NotABackup();
        byte[] bytes;
        using (var input = entry.Open())
        using (var buffer = new MemoryStream())
        {
            CopyAtMost(input, buffer, ManifestLimit);
            bytes = buffer.ToArray();
        }

        // Read the format on its own first, so a newer layout gets the right message.
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("format", out var format) || !format.TryGetInt32(out var number) || number < 1)
                throw BackupException.NotABackup();
            if (number > BackupManifest.CurrentFormat) throw BackupException.NewerFormat();
            return JsonSerializer.Deserialize<BackupManifest>(bytes, BackupJson.Options) ?? throw BackupException.NotABackup();
        }
        catch (JsonException error)
        {
            throw BackupException.NotABackup(error);
        }
    }

    /// <summary>Unpacks one file, stopping as soon as it's longer than the manifest says, and checks its hash.</summary>
    private static void Extract(ZipArchiveEntry entry, BackupFile file, string destination)
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
                if (total > file.Size) throw BackupException.Damaged(file.Path);
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }
            if (total != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                throw BackupException.Damaged(file.Path);
        }
        catch (InvalidDataException error)
        {
            throw new BackupException(BackupException.Damaged(file.Path).Message, error);
        }
    }

    /// <summary>
    /// The records, with an older schema brought up to date first, e.g. a 1.0.0
    /// project's kit details moved into its notes (library-format.md, "Compatibility").
    /// </summary>
    internal static LibrarySnapshot ReadLibrary(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            if (System.Text.Json.Nodes.JsonNode.Parse(input) is not System.Text.Json.Nodes.JsonObject library) throw BackupException.NotABackup();
            SchemaUpgrades.Upgrade(library);
            return library.Deserialize<LibrarySnapshot>(BackupJson.Options) ?? throw BackupException.NotABackup();
        }
        catch (JsonException error)
        {
            throw new BackupException($"This backup's records couldn't be read ({error.Message}). Your library hasn't been changed.", error);
        }
    }

    /// <summary>True if the backup's data model is newer than this app's, which may lose fields it doesn't know.</summary>
    internal static bool CheckSchemaVersion(string? version)
    {
        if (!TryParseVersion(version, out var theirs)) throw BackupException.NotABackup();
        TryParseVersion(LibrarySnapshot.CurrentSchemaVersion, out var ours);
        if (theirs.Major > ours.Major) throw BackupException.NewerFormat();
        return theirs > ours;
    }

    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null) return false;
        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    private static void CopyAtMost(Stream input, Stream output, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            total += read;
            if (total > limit) throw BackupException.NotABackup();
            output.Write(buffer, 0, read);
        }
    }

    // MARK: Schedule

    private sealed record Pending(int ProcessId, DateTimeOffset BackupDate);

    /// <summary>Restores the prepared backup at the next launch, once process <paramref name="processId"/> has quit.</summary>
    public static void Schedule(LibraryLocation location, BackupManifest manifest, int processId)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Pending(processId, manifest.CreatedAt), BackupJson.Options);
        File.WriteAllBytes(PendingPath(location), json);
    }

    /// <summary>Forgets a prepared backup that won't be restored.</summary>
    public static void Discard(LibraryLocation location) => BackupWriter.TryDeleteFolder(StagingFolder(location));

    // MARK: Swap

    /// <summary>
    /// At launch, before the store opens: swaps in a scheduled backup. The
    /// current library is kept beside it, or left as it was if any step fails.
    /// <paramref name="waitForExit"/> waits for a process id to quit and says whether it did.
    /// </summary>
    public static RestoreOutcome? FinishPending(LibraryLocation location, Func<int, bool> waitForExit, DateTime? now = null)
    {
        var staging = StagingFolder(location);
        if (!Directory.Exists(staging)) return null;

        Pending? pending = null;
        try
        {
            pending = JsonSerializer.Deserialize<Pending>(File.ReadAllBytes(PendingPath(location)), BackupJson.Options);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        if (pending is null)
        {
            // Unpacked but never confirmed, e.g. BenchBook closed while asking.
            Discard(location);
            return null;
        }

        // Never swap under a BenchBook that still has the library open; try again next launch.
        if (!waitForExit(pending.ProcessId))
            return new RestoreOutcome.Failed("Another BenchBook was still open, so the backup hasn't been restored yet. Close BenchBook and open it again to finish.");

        var previous = PreviousLibraryPath(location, now ?? DateTime.Now);
        try
        {
            Swap(location, staging, previous);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(previous) && !Directory.EnumerateFileSystemEntries(previous).Any())
                BackupWriter.TryDeleteFolder(previous);
            Discard(location);
            return new RestoreOutcome.Failed($"The backup couldn't be restored, so your library hasn't been changed. {error.Message}");
        }

        Discard(location);
        var kept = Directory.Exists(previous) ? previous : null;
        return new RestoreOutcome.Restored(pending.BackupDate, kept);
    }

    /// <summary>
    /// Moves the current store and library into <paramref name="previous"/>, then
    /// the staged ones into place. If any move fails, every move so far is undone, in reverse.
    /// </summary>
    internal static void Swap(LibraryLocation location, string staging, string previous, Action<string, string>? moveForTests = null)
    {
        var move = moveForTests ?? MoveEntry;
        var done = new Stack<(string From, string To)>();
        void Move(string from, string to)
        {
            move(from, to);
            done.Push((from, to));
        }

        var stagedStore = Path.Combine(staging, LibraryLocation.StoreFileName);
        var stagedLibrary = Path.Combine(staging, "Library");
        if (!File.Exists(stagedStore) || !Directory.Exists(stagedLibrary))
            throw new IOException("The unpacked backup is incomplete.");

        var current = location.StoreFiles.Where(File.Exists).ToList();
        var hasLibrary = Directory.Exists(location.LibraryRoot);
        try
        {
            if (current.Count > 0 || hasLibrary) Directory.CreateDirectory(previous);
            foreach (var file in current) Move(file, Path.Combine(previous, Path.GetFileName(file)));
            if (hasLibrary) Move(location.LibraryRoot, Path.Combine(previous, "Library"));
            Move(stagedStore, location.StorePath);
            Move(stagedLibrary, location.LibraryRoot);
        }
        catch
        {
            while (done.Count > 0)
            {
                var (from, to) = done.Pop();
                try { MoveEntry(to, from); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    // MARK: Earlier libraries

    /// <summary>e.g. "BenchBook Before Restore 2026-11-10 15.30.12", beside the store.</summary>
    public static string PreviousLibraryPath(LibraryLocation location, DateTime localNow) =>
        Path.Combine(location.AppFolder, PreviousPrefix + localNow.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture));

    /// <summary>Libraries kept from before a restore, newest first.</summary>
    public static List<string> PreviousLibraries(LibraryLocation location)
    {
        if (!Directory.Exists(location.AppFolder)) return [];
        return Directory.EnumerateDirectories(location.AppFolder, PreviousPrefix + "*")
            .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();
    }
}
