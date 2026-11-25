using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchBook.Core.Backup;
using BenchBook.Core.Model;
using BenchBook.Core.Projects;
using BenchBook.Core.Store;

namespace BenchBook.Core.Tests;

/// <summary>Exporting and importing a project (library-format.md, "Project file").</summary>
public class ProjectTransferTests
{
    private static string Export(TestLibrary library, LibrarySnapshot sample, bool personal = false)
    {
        var path = Path.Combine(library.Root, ProjectExporter.SuggestedFileName(sample.Projects[0].Name));
        using var store = library.OpenStore();
        ProjectExporter.Export(store, library.Location, sample.Projects[0].Id, personal, path, "1.3.0");
        return path;
    }

    private static ProjectImportSummary Import(TestLibrary library, string path, ProjectImportMode mode = ProjectImportMode.Add)
    {
        var file = ProjectFile.Open(path, library.Location);
        try
        {
            using var store = library.OpenStore();
            return ProjectImporter.Import(store, library.Location, file, mode);
        }
        finally
        {
            file.Discard();
        }
    }

    private static string Refuse(TestLibrary library, string path) =>
        Assert.Throws<ProjectFileException>(() => ProjectFile.Open(path, library.Location)).Message;

    private static LibrarySnapshot Read(TestLibrary library)
    {
        using var store = library.OpenStore();
        return store.ReadSnapshot();
    }

    [Fact]
    public void AProjectMovesToAnotherLibraryAsItWas()
    {
        using var from = new TestLibrary();
        var sample = Sample.Install(from);
        var path = Export(from, sample, personal: true);

        using var to = new TestLibrary();
        var summary = Import(to, path);
        Assert.Equal(sample.Projects[0].Id, summary.ProjectId);
        Assert.Equal(3, summary.PaintsAdded);
        Assert.Equal(1, summary.TagsAdded);
        Assert.Equal(1, summary.KitsAdded);

        // The same records, ids and dates, apart from the kit that's in no project.
        var expected = Read(from);
        expected.Kits.RemoveAll(k => k.ProjectId is null);
        var imported = Read(to);
        foreach (var list in new Func<LibrarySnapshot, System.Collections.IList>[] { s => s.Projects, s => s.References, s => s.Assets, s => s.Tags, s => s.Paints, s => s.Kits, s => s.Markups, s => s.PaintMixParts, s => s.OwnedPaints })
        {
            static List<LibraryRecord> Sorted(System.Collections.IList items) => items.Cast<LibraryRecord>().OrderBy(r => r.Id).ToList();
            Assert.Equal(
                JsonSerializer.Serialize(Sorted(list(expected)).Cast<object>(), BackupJson.Options),
                JsonSerializer.Serialize(Sorted(list(imported)).Cast<object>(), BackupJson.Options));
        }
        Assert.Equal(Sample.Date, imported.Projects[0].UpdatedAt); // Importing isn't editing.

        foreach (var asset in sample.Assets)
            Assert.Equal(File.ReadAllBytes(from.Location.FullPath(asset.OriginalRelativePath)!), File.ReadAllBytes(to.Location.FullPath(asset.OriginalRelativePath)!));
        Assert.Equal(Sample.PosterBytes, File.ReadAllBytes(to.Location.PosterPath(sample.References[2].Id)));
        Assert.False(Directory.EnumerateFileSystemEntries(ProjectFile.StagingFolder(to.Location)).Any());
    }

    [Fact]
    public void PricesAndOwnershipAreLeftOutUnlessAsked()
    {
        using var from = new TestLibrary();
        var sample = Sample.Install(from);
        var without = ProjectFile.Open(Export(from, sample), from.Location);
        try
        {
            Assert.False(without.Manifest.IncludesPersonalDetails);
            var kit = Assert.Single(without.Records.Kits);
            Assert.Null(kit.PricePaid);
            Assert.Null(kit.SalePrice);
            Assert.False(kit.ForSale);
            Assert.Empty(without.Records.OwnedPaints);
        }
        finally
        {
            without.Discard();
        }

        var with = ProjectFile.Open(Export(from, sample, personal: true), from.Location);
        try
        {
            Assert.True(with.Manifest.IncludesPersonalDetails);
            Assert.Equal(89.95m, with.Records.Kits[0].PricePaid);
            Assert.Single(with.Records.OwnedPaints);
        }
        finally
        {
            with.Discard();
        }
    }

    [Fact]
    public void TheFileHoldsOnlyTheProjectAndWhatItUses()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        // Another project, which mustn't come along.
        using (var store = library.OpenStore())
            store.InsertProject(new Project { Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, Name = "Other", Subject = "" });
        var file = ProjectFile.Open(Export(library, sample), library.Location);
        try
        {
            var records = file.Records;
            Assert.Single(records.Projects);
            Assert.Equal(4, records.References.Count);
            Assert.Single(records.Kits); // Not the kit that's in no project.
            Assert.Equal(3, records.Paints.Count); // The chosen paint, the mix, and the mix's white.
            Assert.Equal(2, records.PaintMixParts.Count);
            Assert.Equal(sample.Assets.Count, records.Assets.Count);
            Assert.Equal(sample.Projects[0].ProjectPhotoId, records.Projects[0].ProjectPhotoId);

            var manifest = file.Manifest;
            Assert.Equal(2, manifest.Format);
            Assert.Equal("project", manifest.Kind);
            Assert.Equal("windows", manifest.Platform);
            Assert.Equal(LibrarySnapshot.CurrentSchemaVersion, manifest.SchemaVersion);
            Assert.Equal(4, manifest.Counts.References);
            Assert.Equal(1, manifest.Counts.BuildPhotos);
            Assert.Equal(1, manifest.Counts.AftermarketItems);
            Assert.Equal(1, manifest.Counts.Kits);
        }
        finally
        {
            file.Discard();
        }
    }

    [Fact]
    public void EveryEntryIsStoredWithUtf8Names()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var path = Export(library, sample);
        var entries = BackupTests.CentralDirectory(File.ReadAllBytes(path));
        Assert.Contains(entries, e => e.Name == "manifest.json");
        Assert.Contains(entries, e => e.Name == "library.json");
        foreach (var entry in entries)
        {
            Assert.Equal(0, entry.Method);
            Assert.Equal(0x0800, entry.Flags & 0x0800);
            Assert.Equal(0, entry.Flags & 0x0008);
        }
        // Writing the same project again gives the same records.
        var again = Path.Combine(library.Root, "again.benchbookproject");
        using (var store = library.OpenStore())
            ProjectExporter.Export(store, library.Location, sample.Projects[0].Id, false, again, "1.3.0");
        string Records(string file)
        {
            using var zip = ZipFile.OpenRead(file);
            using var reader = new StreamReader(zip.GetEntry("library.json")!.Open());
            return reader.ReadToEnd();
        }
        Assert.Equal(Records(path), Records(again));
    }

    [Fact]
    public void ExportingWithAMissingFileSaysWhichOne()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        File.Delete(library.Location.FullPath(sample.Assets[0].OriginalRelativePath)!);
        var path = Path.Combine(library.Root, "x.benchbookproject");
        using var store = library.OpenStore();
        var error = Assert.Throws<ProjectFileException>(() => ProjectExporter.Export(store, library.Location, sample.Projects[0].Id, false, path, "1.3.0"));
        Assert.Equal(ProjectFileException.MissingFile("wheel-well é.jpg").Message, error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void KeepBothAddsACopyWithNewIdsSharingTheFiles()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var path = Export(library, sample);
        var originals = Directory.GetFiles(library.Location.OriginalsFolder).Length;

        var file = ProjectFile.Open(path, library.Location);
        ProjectImportSummary summary;
        try
        {
            using var store = library.OpenStore();
            Assert.NotNull(ProjectImporter.ExistingProject(store, file));
            summary = ProjectImporter.Import(store, library.Location, file, ProjectImportMode.KeepBoth);
        }
        finally
        {
            file.Discard();
        }

        Assert.NotEqual(sample.Projects[0].Id, summary.ProjectId);
        Assert.Equal("F-16CJ 50th Anniversary Copy", summary.ProjectName);
        Assert.Equal(0, summary.PaintsAdded);
        Assert.Equal(3, summary.PaintsMatched);
        Assert.Equal(0, summary.TagsAdded);
        // The kit is one box, so it stays in the project building it.
        Assert.Equal(new[] { "Tamiya 1:48 F-16CJ Fighting Falcon (61098)" }, summary.KitsInOtherProjects);
        Assert.Contains("Tamiya 1:48 F-16CJ Fighting Falcon (61098) stayed in the project already building them.", summary.Message(new ProjectFileCounts()));

        var after = Read(library);
        Assert.Equal(2, after.Projects.Count);
        Assert.Equal(8, after.References.Count);
        Assert.Equal(sample.Assets.Count, after.Assets.Count);
        Assert.Equal(originals, Directory.GetFiles(library.Location.OriginalsFolder).Length);
        Assert.Equal(3, after.Paints.Count);
        Assert.Single(after.Tags);
        var copy = after.Projects.Single(p => p.Id == summary.ProjectId);
        var copyPhoto = after.BuildPhotos.Single(p => p.ProjectId == copy.Id);
        Assert.Equal(copyPhoto.Id, copy.ProjectPhotoId);
        Assert.Equal(2, after.BuildPhotoTags.Count);
        Assert.Equal(2, after.ReferenceTags.Count);
        // A poster under the copy's reference id.
        var video = after.References.Single(r => r.ProjectId == copy.Id && r.Kind == ReferenceKind.VideoLink);
        Assert.True(File.Exists(library.Location.PosterPath(video.Id)));
    }

    [Fact]
    public void ReplaceGoesBackToTheFilesVersion()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var path = Export(library, sample);

        // Changes after exporting: a new reference with its own file, and a renamed project.
        var asset = new Asset
        {
            Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, MimeType = "image/jpeg",
            ByteCount = 3, Sha256 = Sample.Hash([7, 7, 7]), OriginalRelativePath = "",
        };
        asset.OriginalRelativePath = $"Originals/{asset.Id}.jpg";
        library.WriteOriginal(asset, [7, 7, 7]);
        using (var store = library.OpenStore())
        {
            store.InsertSnapshot(new LibrarySnapshot
            {
                Assets = [asset],
                References = [new Reference { Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, ProjectId = sample.Projects[0].Id, Kind = ReferenceKind.Image, CapturedAt = Sample.Date, AssetId = asset.Id }],
            });
            var project = store.Project(sample.Projects[0].Id)!;
            project.Name = "Renamed";
            store.UpdateProject(project);
        }

        var summary = Import(library, path, ProjectImportMode.Replace);
        Assert.Equal(sample.Projects[0].Id, summary.ProjectId);
        Assert.Equal(1, summary.KitsMatched);
        Assert.Empty(summary.KitsInOtherProjects);

        var after = Read(library);
        Assert.Equal("F-16CJ 50th Anniversary", Assert.Single(after.Projects).Name);
        Assert.Equal(4, after.References.Count);
        Assert.Equal(sample.References.Select(r => r.Id).Order(), after.References.Select(r => r.Id).Order());
        Assert.Equal(sample.Projects[0].Id, after.Kits.Single(k => k.Brand == "Tamiya").ProjectId);
        // The new reference's file went with it.
        Assert.DoesNotContain(after.Assets, a => a.Id == asset.Id);
        Assert.False(File.Exists(library.Location.FullPath(asset.OriginalRelativePath)));
        Assert.True(File.Exists(library.Location.PosterPath(sample.References[2].Id)));
        // Kit prices weren't in the file, so the kit in the library keeps its own.
        Assert.Equal(89.95m, after.Kits.Single(k => k.Brand == "Tamiya").PricePaid);
    }

    [Fact]
    public void PaintsAndTagsAlreadyThereAreUsedUnchanged()
    {
        using var from = new TestLibrary();
        var sample = Sample.Install(from);
        var path = Export(from, sample);

        using var to = new TestLibrary();
        var paint = new Paint
        {
            Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, Brand = "TAMIYA", Range = "Acrylic  XF", Code = "XF 54",
            Name = "My Grey", StandardCode = "", IdentityKey = "tamiya|acrylic xf|xf54",
        };
        var tag = new Tag { Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, Name = "wheel  well", NormalizedName = "wheel well", Color = "red" };
        using (var store = to.OpenStore()) store.InsertSnapshot(new LibrarySnapshot { Paints = [paint], Tags = [tag] });

        var summary = Import(to, path);
        Assert.Equal(1, summary.PaintsMatched);
        Assert.Equal(2, summary.PaintsAdded);
        Assert.Equal(0, summary.TagsAdded);

        var after = Read(to);
        var mine = after.Paints.Single(p => p.Id == paint.Id);
        Assert.Equal("My Grey", mine.Name);
        Assert.Empty(after.OwnedPaints);
        Assert.Contains(after.ProjectPaints, r => r.PaintId == paint.Id);
        Assert.Contains(after.PaintMixParts, p => p.PaintId == paint.Id);
        Assert.Equal("red", Assert.Single(after.Tags).Color);
        Assert.All(after.ReferenceTags.Concat<LibraryRecord>(after.BuildPhotoTags), l => Assert.Equal(tag.Id, l switch { ReferenceTag r => r.TagId, BuildPhotoTag b => b.TagId, _ => "" }));
    }

    [Fact]
    public void AKitInNoProjectJoinsTheImportedOne()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var path = Export(library, sample);
        using (var store = library.OpenStore())
            ProjectService.Delete(store, library.Location, store.Project(sample.Projects[0].Id)!);

        var summary = Import(library, path);
        Assert.Equal(1, summary.KitsMatched);
        Assert.Equal(0, summary.KitsAdded);
        var after = Read(library);
        Assert.Equal(2, after.Kits.Count);
        Assert.Equal(sample.Projects[0].Id, after.Kits.Single(k => k.Brand == "Tamiya").ProjectId);
        // Its files came back from the file.
        foreach (var asset in sample.Assets)
            Assert.True(File.Exists(library.Location.FullPath(asset.OriginalRelativePath)), asset.OriginalRelativePath);
    }

    [Fact]
    public void AnIdTakenByAnotherRecordGetsANewOne()
    {
        using var from = new TestLibrary();
        var sample = Sample.Install(from);
        var path = Export(from, sample);

        using var to = new TestLibrary();
        // A project in this library happens to use the file's reference id.
        var clash = new Project { Id = sample.References[0].Id, CreatedAt = Sample.Date, UpdatedAt = Sample.Date, Name = "Clash", Subject = "" };
        using (var store = to.OpenStore()) store.InsertProject(clash);
        Import(to, path);
        var after = Read(to);
        Assert.Equal(2, after.Projects.Count);
        Assert.DoesNotContain(after.References, r => r.Id == clash.Id);
        Assert.Equal(4, after.References.Count);
    }

    // MARK: Files that aren't right

    [Fact]
    public void AChangedFileIsDamaged()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var path = Export(library, sample);
        var entry = "Library/" + sample.Assets[1].OriginalRelativePath;
        Rewrite(path, (name, bytes) => name == entry ? [.. bytes, 0] : bytes);
        Assert.Equal(ProjectFileException.Damaged(entry).Message, Refuse(library, path));
        Assert.False(Directory.EnumerateFileSystemEntries(ProjectFile.StagingFolder(library.Location)).Any());
    }

    [Fact]
    public void AFileTheManifestDoesntListIsRefused()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var path = Export(library, sample);
        Rewrite(path, (_, bytes) => bytes, extra: ("Library/Originals/extra.jpg", [1, 2, 3]));
        Assert.Equal(ProjectFileException.NotAProjectFile().Message, Refuse(library, path));
    }

    [Fact]
    public void ABackupIsntAProjectFileAndAProjectFileIsntABackup()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var backup = Path.Combine(library.Root, "b.benchbookbackup");
        using (var store = library.OpenStore()) BackupWriter.BackUp(store, library.Location, backup, "1.3.0");
        Assert.Equal(ProjectFileException.NotAProjectFile().Message, Refuse(library, backup));

        var project = Export(library, sample);
        var error = Assert.Throws<BackupException>(() => LibraryRestore.Prepare(project, library.Location));
        Assert.Equal(BackupException.ProjectFile().Message, error.Message);
    }

    [Fact]
    public void ANewerFormatIsRefused()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var path = Export(library, sample);
        Rewrite(path, (name, bytes) =>
        {
            if (name != "manifest.json") return bytes;
            var manifest = JsonNode.Parse(bytes)!.AsObject();
            manifest["format"] = 3;
            return Encoding.UTF8.GetBytes(manifest.ToJsonString());
        });
        Assert.Equal(ProjectFileException.NewerFormat().Message, Refuse(library, path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("PK")]
    [InlineData("Not a ZIP at all")]
    public void SomethingElseIsntAProjectFile(string text)
    {
        using var library = new TestLibrary();
        var path = Path.Combine(library.Root, "x.benchbookproject");
        File.WriteAllText(path, text);
        Assert.Equal(ProjectFileException.NotAProjectFile().Message, Refuse(library, path));
    }

    [Fact]
    public void TwoProjectsArentAProjectFile()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var path = Export(library, sample);
        Rewrite(path, (name, bytes) =>
        {
            if (name != "library.json") return bytes;
            var records = JsonNode.Parse(bytes)!.AsObject();
            records["projects"]!.AsArray().Add(new JsonObject
            {
                ["id"] = Ids.New(), ["createdAt"] = "2026-11-13T09:41:07.250Z", ["updatedAt"] = "2026-11-13T09:41:07.250Z",
                ["name"] = "Second", ["subject"] = "",
            });
            return Encoding.UTF8.GetBytes(records.ToJsonString());
        }, fixManifest: true);
        Assert.Equal(ProjectFileException.NotAProjectFile().Message, Refuse(library, path));
    }

    /// <summary>
    /// As another tool might write it: Deflate entries, folder entries, lowercase
    /// ids, dates without milliseconds and fields this version doesn't know.
    /// </summary>
    [Fact]
    public void AFileFromTheMacImports()
    {
        using var from = new TestLibrary();
        var sample = Sample.Install(from);
        var path = Export(from, sample);
        var rewritten = Path.Combine(from.Root, "mac.benchbookproject");
        var contents = new List<(string Name, byte[] Bytes)>();
        using (var zip = ZipFile.OpenRead(path))
            foreach (var entry in zip.Entries)
            {
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                contents.Add((entry.FullName, buffer.ToArray()));
            }
        var library = contents.Single(c => c.Name == "library.json");
        var json = Encoding.UTF8.GetString(library.Bytes);
        var records = JsonNode.Parse(json)!.AsObject();
        records["projects"]![0]!["id"] = sample.Projects[0].Id.ToLowerInvariant();
        records["projects"]![0]!["createdAt"] = "2026-11-13T09:41:07Z";
        records["projects"]![0]!["futureField"] = "kept out";
        foreach (var reference in records["references"]!.AsArray()) reference!["projectId"] = sample.Projects[0].Id.ToLowerInvariant();
        var newJson = Encoding.UTF8.GetBytes(records.ToJsonString());
        var manifest = JsonNode.Parse(contents.Single(c => c.Name == "manifest.json").Bytes)!.AsObject();
        manifest["platform"] = "mac";
        foreach (var file in manifest["files"]!.AsArray())
            if (file!["path"]!.GetValue<string>() == "library.json")
            {
                file["size"] = newJson.Length;
                file["sha256"] = Sample.Hash(newJson);
            }
        using (var zip = ZipFile.Open(rewritten, ZipArchiveMode.Create))
        {
            zip.CreateEntry("Library/");
            zip.CreateEntry("Library/Originals/");
            void Add(string name, byte[] bytes)
            {
                using var output = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                output.Write(bytes);
            }
            Add("manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            Add("library.json", newJson);
            foreach (var (name, bytes) in contents.Where(c => c.Name.StartsWith("Library/"))) Add(name, bytes);
        }

        using var to = new TestLibrary();
        var summary = Import(to, rewritten);
        Assert.Equal(sample.Projects[0].Id, summary.ProjectId);
        var after = Read(to);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 9, 41, 7, TimeSpan.Zero), after.Projects[0].CreatedAt);
        Assert.Equal(4, after.References.Count);
    }

    [Theory]
    [InlineData("F-16C Viper", "F-16C Viper.benchbookproject")]
    [InlineData("A/B: \"Test\" <1>|*?", "A-B- -Test- -1----.benchbookproject")]
    [InlineData("Line one\nLine two", "Line one-Line two.benchbookproject")]
    [InlineData(" ... ", "Project.benchbookproject")]
    public void TheSuggestedNameIsSafeOnBothSystems(string name, string expected) =>
        Assert.Equal(expected, ProjectExporter.SuggestedFileName(name));

    [Fact]
    public void TheSummarySaysWhatCameIn()
    {
        var summary = new ProjectImportSummary("id", "F-16C Viper", 3, 27, 2, 1, 0, ["Tamiya F-16C", "Italeri F-16C"]);
        var counts = new ProjectFileCounts { References = 212, BuildPhotos = 14, AftermarketItems = 4 };
        Assert.Equal(
            "“F-16C Viper” has 212 references, 14 build photos and 4 aftermarket parts.\n" +
            "Paints: 3 added to your catalogue, 27 already there.\n" +
            "2 new tags.\n" +
            "Kits: 1 added to your catalogue, 0 already there.\n" +
            "Tamiya F-16C and Italeri F-16C stayed in the project already building them.",
            summary.Message(counts));
        Assert.Equal("“X” has 1 reference, 0 build photos and 1 aftermarket part.",
            new ProjectImportSummary("id", "X", 0, 0, 0, 0, 0, []).Message(new ProjectFileCounts { References = 1, AftermarketItems = 1 }));
    }

    /// <summary>
    /// Rewrites a project file entry by entry. The manifest stays as it was, so
    /// it no longer matches a changed file, unless <paramref name="fixManifest"/>.
    /// </summary>
    private static void Rewrite(string path, Func<string, byte[], byte[]> edit, (string Name, byte[] Bytes)? extra = null, bool fixManifest = false)
    {
        var contents = new List<(string Name, byte[] Bytes)>();
        using (var zip = ZipFile.OpenRead(path))
            foreach (var entry in zip.Entries)
            {
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                contents.Add((entry.FullName, edit(entry.FullName, buffer.ToArray())));
            }
        if (extra is { } more) contents.Add(more);
        if (fixManifest)
        {
            var index = contents.FindIndex(c => c.Name == "manifest.json");
            var manifest = JsonNode.Parse(contents[index].Bytes)!.AsObject();
            foreach (var file in manifest["files"]!.AsArray())
            {
                var bytes = contents.Single(c => c.Name == file!["path"]!.GetValue<string>()).Bytes;
                file!["size"] = bytes.Length;
                file["sha256"] = Sample.Hash(bytes);
            }
            contents[index] = ("manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        }
        File.Delete(path);
        using var output = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in contents)
        {
            using var stream = output.CreateEntry(name, CompressionLevel.NoCompression).Open();
            stream.Write(bytes);
        }
    }
}
