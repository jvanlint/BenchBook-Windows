using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BenchBook.Core.Backup;
using BenchBook.Core.Model;

namespace BenchBook.Core.Tests;

public class BackupTests
{
    [Fact]
    public void ABackupRestoresToTheSameRecordsAndFiles()
    {
        using var from = new TestLibrary();
        var original = Sample.Install(from);
        var backup = Path.Combine(from.Root, BackupWriter.SuggestedFileName(new DateTime(2026, 11, 13)));
        BackupManifest manifest;
        using (var store = from.OpenStore())
            manifest = BackupWriter.BackUp(store, from.Location, backup, "0.1.0");

        Assert.Equal(1, manifest.Counts.Projects);
        Assert.Equal(4, manifest.Counts.References);
        Assert.Equal(3, manifest.Counts.Paints);
        Assert.Equal("windows", manifest.Platform);

        using var to = new TestLibrary();
        var prepared = LibraryRestore.Prepare(backup, to.Location);
        Assert.False(prepared.SchemaIsNewer);
        LibraryRestore.Schedule(to.Location, prepared.Manifest, processId: 0);
        var outcome = LibraryRestore.FinishPending(to.Location, _ => true);

        var restored = Assert.IsType<RestoreOutcome.Restored>(outcome);
        Assert.Equal(manifest.CreatedAt, restored.BackupDate);
        Assert.Null(restored.PreviousLibrary); // There was no library before.
        Assert.False(Directory.Exists(LibraryRestore.StagingFolder(to.Location)));

        using (var store = to.OpenStore())
            Assert.Equal(StoreTests.Json(original), StoreTests.Json(store.ReadSnapshot()));
        var paths = original.Assets.Select(a => a.OriginalRelativePath).Append($"Posters/{original.References[2].Id}.jpg");
        foreach (var path in paths)
            Assert.Equal(File.ReadAllBytes(from.Location.FullPath(path)!), File.ReadAllBytes(to.Location.FullPath(path)!));
    }

    [Fact]
    public void RestoringKeepsTheCurrentLibraryAside()
    {
        using var from = new TestLibrary();
        Sample.Install(from);
        var backup = BackUp(from);

        using var to = new TestLibrary();
        Sample.Install(to);
        var thumbnail = to.Location.ThumbnailPath(Ids.New());
        Directory.CreateDirectory(to.Location.ThumbnailsFolder);
        File.WriteAllBytes(thumbnail, [1]);
        var before = File.ReadAllBytes(to.Location.StorePath);

        var prepared = LibraryRestore.Prepare(backup, to.Location);
        LibraryRestore.Schedule(to.Location, prepared.Manifest, 0);
        var now = new DateTime(2026, 11, 13, 15, 30, 12);
        var outcome = Assert.IsType<RestoreOutcome.Restored>(LibraryRestore.FinishPending(to.Location, _ => true, now));

        Assert.Equal(Path.Combine(to.Location.AppFolder, "BenchBook Before Restore 2026-11-13 15.30.12"), outcome.PreviousLibrary);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(outcome.PreviousLibrary!, "BenchBook.db")));
        Assert.True(File.Exists(Path.Combine(outcome.PreviousLibrary!, "Library", "Thumbnails", Path.GetFileName(thumbnail))));
        Assert.Equal([outcome.PreviousLibrary!], LibraryRestore.PreviousLibraries(to.Location));
    }

    [Fact]
    public void TheZipUsesOnlyWhatTheFormatAllows()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        // A file no record names, and a hidden one, are left out.
        File.WriteAllBytes(Path.Combine(library.Location.OriginalsFolder, Ids.New() + ".jpg"), [1, 2]);
        File.WriteAllBytes(Path.Combine(library.Location.OriginalsFolder, ".partial-x"), [1]);
        var backup = BackUp(library);

        var entries = CentralDirectory(File.ReadAllBytes(backup));
        Assert.Equal(BackupWriter.ManifestName, entries[0].Name);
        var expected = new[] { "manifest.json", "library.json" }
            .Concat(sample.Assets.Select(a => "Library/" + a.OriginalRelativePath))
            .Append($"Library/Posters/{sample.References[2].Id}.jpg")
            .Order();
        Assert.Equal(expected, entries.Select(e => e.Name).Order());
        foreach (var entry in entries)
        {
            Assert.True(entry.Method is 0 or 8, $"{entry.Name} uses method {entry.Method}");
            Assert.Equal(0, entry.Flags & 0x0008); // No data descriptors.
            Assert.Equal(0, entry.Flags & 0x0001); // Not encrypted.
            Assert.DoesNotContain('\\', entry.Name);
        }
        Assert.All(entries.Where(e => e.Name.EndsWith(".jpg")), e => Assert.Equal(0, e.Method)); // Already-compressed images are stored.
        Assert.Equal(8, entries.Single(e => e.Name == "library.json").Method);
    }

    [Fact]
    public void TheManifestDescribesEveryFile()
    {
        using var library = new TestLibrary();
        Sample.Install(library);
        var backup = BackUp(library);
        using var zip = ZipFile.OpenRead(backup);
        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var root = manifest.RootElement;
        Assert.Equal(2, root.GetProperty("format").GetInt32());
        Assert.Equal("6.0.0", root.GetProperty("schemaVersion").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", root.GetProperty("createdAt").GetString());
        var files = root.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(zip.Entries.Count - 1, files.Count);
        foreach (var file in files)
        {
            var entry = zip.GetEntry(file.GetProperty("path").GetString()!)!;
            using var data = new MemoryStream();
            using (var input = entry.Open()) input.CopyTo(data);
            Assert.Equal(file.GetProperty("size").GetInt64(), data.Length);
            Assert.Equal(file.GetProperty("sha256").GetString(), Sample.Hash(data.ToArray()));
        }
    }

    [Fact]
    public void LibraryJsonLeavesOutEmptyOptionalFields()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var backup = BackUp(library);
        using var zip = ZipFile.OpenRead(backup);
        using var input = zip.GetEntry("library.json")!.Open();
        using var json = JsonDocument.Parse(input);
        var project = json.RootElement.GetProperty("projects")[0];
        Assert.False(project.TryGetProperty("markingsScheme", out _));
        Assert.Equal(sample.Projects[0].Id, project.GetProperty("id").GetString());
        Assert.Equal("inProgress", project.GetProperty("status").GetString());
        var pdf = json.RootElement.GetProperty("references")[1];
        Assert.False(pdf.TryGetProperty("title", out _));
        Assert.Equal("hologram", json.RootElement.GetProperty("references")[3].GetProperty("kind").GetString());
        var markup = json.RootElement.GetProperty("markups")[1];
        Assert.Equal("", markup.GetProperty("text").GetString());
        Assert.Equal(0, markup.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public void ABackupWithAMissingOriginalIsntMade()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        File.Delete(library.Location.FullPath(sample.Assets[1].OriginalRelativePath)!);
        var destination = Path.Combine(library.Root, "b.benchbookbackup");
        using var store = library.OpenStore();
        var error = Assert.Throws<BackupException>(() => BackupWriter.BackUp(store, library.Location, destination, "0.1.0"));
        Assert.Contains(sample.Assets[1].OriginalRelativePath, error.Message);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(library.Root, ".partial-*"));
    }

    [Fact]
    public void ABackupFromAMacReadsWithUnknownFieldsAndLowercaseIds()
    {
        using var library = new TestLibrary();
        var (snapshot, files) = Sample.Make();
        var json = JsonSerializer.SerializeToNode(snapshot, BackupJson.Options)!.AsObject();
        json["somethingNew"] = "ignored";
        json["projects"]![0]!["futureField"] = 42;
        var projectId = snapshot.Projects[0].Id;
        foreach (var reference in json["references"]!.AsArray()) reference!["projectId"] = projectId.ToLowerInvariant();
        var backup = MakeZip(library, json.ToJsonString(), files);

        var prepared = LibraryRestore.Prepare(backup, library.Location);
        Assert.Equal("mac", prepared.Manifest.Platform);
        LibraryRestore.Schedule(library.Location, prepared.Manifest, 0);
        LibraryRestore.FinishPending(library.Location, _ => true);
        using var store = library.OpenStore();
        Assert.All(store.ReadSnapshot().References, r => Assert.Equal(projectId, r.ProjectId));
    }

    [Fact]
    public void ANewerMinorSchemaIsFlaggedAndANewerMajorIsRefused()
    {
        Assert.False(LibraryRestore.CheckSchemaVersion("1.0.0"));
        Assert.False(LibraryRestore.CheckSchemaVersion("6.0.0"));
        Assert.True(LibraryRestore.CheckSchemaVersion("6.1.0"));
        Assert.Contains("newer version", Assert.Throws<BackupException>(() => LibraryRestore.CheckSchemaVersion("7.0.0")).Message);
        Assert.Throws<BackupException>(() => LibraryRestore.CheckSchemaVersion("one"));
    }

    // MARK: Refusals

    [Fact]
    public void AFormatOneBackupSaysItCameFromAnOlderMac()
    {
        using var library = new TestLibrary();
        var path = Path.Combine(library.Root, "old.benchbookbackup");
        File.WriteAllBytes(path, "AA01 not a zip"u8.ToArray());
        Assert.Contains("older BenchBook for Mac", Refuse(library, path));
    }

    [Fact]
    public void AnEmptyFileIsntABackup()
    {
        using var library = new TestLibrary();
        var path = Path.Combine(library.Root, "empty.benchbookbackup");
        File.WriteAllBytes(path, []);
        Assert.Equal(BackupException.NotABackup().Message, Refuse(library, path));
    }

    [Fact]
    public void ANewerFormatIsRefused()
    {
        using var library = new TestLibrary();
        var (snapshot, files) = Sample.Make();
        var backup = MakeZip(library, LibraryJson(snapshot), files, editManifest: m => m["format"] = 3);
        Assert.Equal(BackupException.NewerFormat().Message, Refuse(library, backup));
    }

    [Fact]
    public void AnExtraFileIsRefused()
    {
        using var library = new TestLibrary();
        var (snapshot, files) = Sample.Make();
        var backup = MakeZip(library, LibraryJson(snapshot), files, extra: [("Library/Posters/stray.jpg", [1, 2, 3], 0)]);
        Assert.Equal(BackupException.Damaged("Library/Posters/stray.jpg").Message, Refuse(library, backup));
    }

    [Fact]
    public void AChangedFileIsRefused()
    {
        using var library = new TestLibrary();
        var (snapshot, files) = Sample.Make();
        var path = snapshot.Assets[0].OriginalRelativePath;
        var backup = MakeZip(library, LibraryJson(snapshot), files, editManifest: m =>
        {
            foreach (var file in m["files"]!.AsArray())
                if ((string)file!["path"]! == "Library/" + path) file["sha256"] = new string('0', 64);
        });
        Assert.Equal(BackupException.Damaged("Library/" + path).Message, Refuse(library, backup));
    }

    [Fact]
    public void AFileLongerThanTheManifestSaysIsRefused()
    {
        using var library = new TestLibrary();
        var (snapshot, files) = Sample.Make();
        var path = snapshot.Assets[0].OriginalRelativePath;
        var backup = MakeZip(library, LibraryJson(snapshot), files, editManifest: m =>
        {
            foreach (var file in m["files"]!.AsArray())
                if ((string)file!["path"]! == "Library/" + path) file["size"] = 3;
        });
        Assert.Equal(BackupException.Damaged("Library/" + path).Message, Refuse(library, backup));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("Library/../../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("Library\\Originals\\evil.jpg")]
    [InlineData("Library/Originals/CON.jpg")]
    [InlineData("Library/Originals/evil.jpg:hidden")]
    public void UnsafeNamesAreRefused(string name)
    {
        using var library = new TestLibrary();
        var (snapshot, files) = Sample.Make();
        var backup = MakeZip(library, LibraryJson(snapshot), files, extra: [(name, [1], 0)]);
        Assert.Equal(BackupException.NotABackup().Message, Refuse(library, backup));
        Assert.False(File.Exists(Path.Combine(library.Root, "evil.txt")));
    }

    [Fact]
    public void LinksAreRefused()
    {
        using var library = new TestLibrary();
        var (snapshot, files) = Sample.Make();
        const int symlink = 0xA1FF << 16;
        var backup = MakeZip(library, LibraryJson(snapshot), files, extra: [("Library/Posters/link.jpg", "/etc/passwd"u8.ToArray(), symlink)]);
        Assert.Equal(BackupException.NotABackup().Message, Refuse(library, backup));
    }

    [Fact]
    public void NamesThatDifferOnlyInCaseAreRefused()
    {
        using var library = new TestLibrary();
        var (snapshot, files) = Sample.Make();
        var path = "Library/" + snapshot.Assets[0].OriginalRelativePath;
        var backup = MakeZip(library, LibraryJson(snapshot), files, extra: [(path.ToLowerInvariant().Replace("library/", "Library/"), [1], 0)]);
        Assert.Equal(BackupException.NotABackup().Message, Refuse(library, backup));
    }

    [Fact]
    public void RecordsThatPointNowhereAreRefused()
    {
        using var library = new TestLibrary();
        var (snapshot, files) = Sample.Make();
        snapshot.References[0].ProjectId = Ids.New();
        var backup = MakeZip(library, LibraryJson(snapshot), files);
        Assert.Contains("records don't fit together", Refuse(library, backup));
    }

    [Fact]
    public void AnAssetWhoseFileIsMissingIsRefused()
    {
        using var library = new TestLibrary();
        var (snapshot, files) = Sample.Make();
        files.Remove(snapshot.Assets[1].OriginalRelativePath);
        var backup = MakeZip(library, LibraryJson(snapshot), files);
        Assert.Equal(BackupException.Damaged("Library/" + snapshot.Assets[1].OriginalRelativePath).Message, Refuse(library, backup));
    }

    [Fact]
    public void ARefusedBackupLeavesTheLibraryAsItWas()
    {
        using var library = new TestLibrary();
        Sample.Install(library);
        var before = File.ReadAllBytes(library.Location.StorePath);
        var path = Path.Combine(library.Root, "bad.benchbookbackup");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04, 0, 0, 0]);
        Refuse(library, path);
        Assert.Equal(before, File.ReadAllBytes(library.Location.StorePath));
        Assert.False(Directory.Exists(LibraryRestore.StagingFolder(library.Location)));
    }

    // MARK: The swap

    [Fact]
    public void AFailedSwapPutsEverythingBack()
    {
        using var from = new TestLibrary();
        Sample.Install(from);
        var backup = BackUp(from);
        using var to = new TestLibrary();
        Sample.Install(to);
        var before = File.ReadAllBytes(to.Location.StorePath);
        var originals = Directory.GetFiles(to.Location.OriginalsFolder).Order().ToArray();
        LibraryRestore.Prepare(backup, to.Location);

        var moves = 0;
        var previous = LibraryRestore.PreviousLibraryPath(to.Location, DateTime.Now);
        Assert.ThrowsAny<IOException>(() => LibraryRestore.Swap(to.Location, LibraryRestore.StagingFolder(to.Location), previous, (a, b) =>
        {
            // The current library moves aside, then putting the backup's library in place fails.
            if (++moves == 4) throw new IOException("Disk full");
            if (Directory.Exists(a)) Directory.Move(a, b); else File.Move(a, b);
        }));

        Assert.Equal(before, File.ReadAllBytes(to.Location.StorePath));
        Assert.Equal(originals, Directory.GetFiles(to.Location.OriginalsFolder).Order().ToArray());
        Assert.True(File.Exists(Path.Combine(LibraryRestore.StagingFolder(to.Location), "BenchBook.db")));
    }

    [Fact]
    public void ARestoreWaitsWhileAnotherBenchBookIsOpen()
    {
        using var from = new TestLibrary();
        Sample.Install(from);
        var backup = BackUp(from);
        using var to = new TestLibrary();
        var prepared = LibraryRestore.Prepare(backup, to.Location);
        LibraryRestore.Schedule(to.Location, prepared.Manifest, 1234);

        Assert.IsType<RestoreOutcome.Failed>(LibraryRestore.FinishPending(to.Location, _ => false));
        Assert.True(Directory.Exists(LibraryRestore.StagingFolder(to.Location)));
        Assert.IsType<RestoreOutcome.Restored>(LibraryRestore.FinishPending(to.Location, _ => true));
    }

    [Fact]
    public void AnUnconfirmedRestoreIsForgotten()
    {
        using var from = new TestLibrary();
        Sample.Install(from);
        var backup = BackUp(from);
        using var to = new TestLibrary();
        LibraryRestore.Prepare(backup, to.Location);
        Assert.Null(LibraryRestore.FinishPending(to.Location, _ => true));
        Assert.False(Directory.Exists(LibraryRestore.StagingFolder(to.Location)));
        Assert.False(File.Exists(to.Location.StorePath));
    }

    // MARK: Helpers

    private static string BackUp(TestLibrary library)
    {
        var path = Path.Combine(library.Root, $"{Guid.NewGuid():N}.benchbookbackup");
        using var store = library.OpenStore();
        BackupWriter.BackUp(store, library.Location, path, "0.1.0");
        return path;
    }

    private static string Refuse(TestLibrary library, string backup) =>
        Assert.Throws<BackupException>(() => LibraryRestore.Prepare(backup, library.Location)).Message;

    private static string LibraryJson(LibrarySnapshot snapshot) => JsonSerializer.Serialize(snapshot, BackupJson.Options);

    /// <summary>Builds a backup by hand, as another app might, so its parts can be broken on purpose.</summary>
    private static string MakeZip(
        TestLibrary library,
        string libraryJson,
        Dictionary<string, byte[]> files,
        Action<System.Text.Json.Nodes.JsonObject>? editManifest = null,
        (string Name, byte[] Bytes, int Attributes)[]? extra = null)
    {
        var contents = new List<(string Name, byte[] Bytes)> { ("library.json", Encoding.UTF8.GetBytes(libraryJson)) };
        contents.AddRange(files.Select(f => ("Library/" + f.Key, f.Value)));
        var manifest = new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = 2,
            ["createdAt"] = "2026-11-12T18:02:00Z",
            ["appVersion"] = "1.1.0",
            ["platform"] = "mac",
            ["schemaVersion"] = "1.0.0",
            ["counts"] = new System.Text.Json.Nodes.JsonObject { ["projects"] = 1, ["references"] = 4, ["paints"] = 1 },
            ["files"] = new System.Text.Json.Nodes.JsonArray(contents.Select(c => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = c.Name, ["size"] = c.Bytes.Length, ["sha256"] = Sample.Hash(c.Bytes),
            }).ToArray()),
        };
        editManifest?.Invoke(manifest);

        var path = Path.Combine(library.Root, $"{Guid.NewGuid():N}.benchbookbackup");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] bytes, int attributes)
            {
                var entry = zip.CreateEntry(name);
                entry.ExternalAttributes = attributes;
                using var output = entry.Open();
                output.Write(bytes);
            }
            Add("manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()), 0);
            foreach (var (name, bytes) in contents) Add(name, bytes, 0);
            foreach (var (name, bytes, attributes) in extra ?? []) Add(name, bytes, attributes);
        }
        return path;
    }

    private sealed record CentralEntry(string Name, int Flags, int Method);

    /// <summary>Reads the ZIP's central directory straight from its bytes.</summary>
    private static List<CentralEntry> CentralDirectory(byte[] zip)
    {
        var end = zip.Length - 22;
        while (end >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end)) != 0x06054B50) end--;
        Assert.True(end >= 0, "No end of central directory record");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10));
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16));
        var entries = new List<CentralEntry>();
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(0x02014B50u, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(offset)));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 8));
            var method = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 10));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 32));
            var localOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(offset + 42));
            var name = Encoding.UTF8.GetString(zip, offset + 46, nameLength);
            // The local header must agree on the flags.
            Assert.Equal(flags, BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(localOffset + 6)));
            entries.Add(new CentralEntry(name, flags, method));
            offset += 46 + nameLength + extraLength + commentLength;
        }
        return entries;
    }
}
