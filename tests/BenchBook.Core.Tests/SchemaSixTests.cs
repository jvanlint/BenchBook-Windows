using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchBook.Core.Backup;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using BenchBook.Core.Projects;
using BenchBook.Core.Store;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Tests;

/// <summary>The cases in the Mac's PaintStandardTests.swift, so both apps read standards the same way.</summary>
public class PaintStandardTests
{
    private static string[]? Parsed(string text) =>
        PaintStandardParser.Parse(text) is { } s ? [s.SystemRaw, s.Code] : null;

    [Theory]
    [InlineData("FS 36270", "fs595", "36270")]
    [InlineData("FS36270", "fs595", "36270")]
    [InlineData("fs-36270", "fs595", "36270")]
    [InlineData("FS595 36270", "fs595", "36270")]
    [InlineData("FS 595 36270", "fs595", "36270")]
    [InlineData("AMS-STD-595 36270", "fs595", "36270")]
    [InlineData("RAL 7021", "ral", "7021")]
    [InlineData("RLM76", "rlm", "76")]
    [InlineData("BS 640", "bs381c", "640")]
    [InlineData("BSC 637", "bs381c", "637")]
    [InlineData("BS 381C 637", "bs381c", "637")]
    [InlineData("ANA 613", "ana", "613")]
    [InlineData("MAP Sky", "map", "Sky")]
    [InlineData("  RLM 02 ", "rlm", "02")]
    [InlineData("RLM", "rlm", "")]
    [InlineData("IJN J3", "ijn", "IJN J3")]
    [InlineData("IJA #7", "ijn", "IJA #7")]
    [InlineData("FS 36375 / BS 637", "multiple", "FS 36375 / BS 637")]
    [InlineData("RLM 74, RLM 75", "multiple", "RLM 74, RLM 75")]
    [InlineData("Humbrol 28", "custom", "Humbrol 28")]
    [InlineData("Maple", "custom", "Maple")]
    [InlineData("Ralph's grey", "custom", "Ralph's grey")]
    [InlineData("36270", "custom", "36270")]
    public void TextIsSplitIntoASystemAndACode(string text, string system, string code) =>
        Assert.Equal([system, code], Parsed(text)!);

    [Fact]
    public void EmptyTextIsNoStandard() => Assert.Null(Parsed("  "));

    [Fact]
    public void StandardsShowWithTheirPrefix()
    {
        Assert.Equal("FS 36270", new PaintStandard(PaintStandardSystem.Fs595, "36270").DisplayText);
        Assert.Equal("BS 640", new PaintStandard(PaintStandardSystem.Bs381c, "640").DisplayText);
        Assert.Equal("5-N", new PaintStandard(PaintStandardSystem.Ships2, "5-N").DisplayText);
        Assert.Equal("FS 36375 / BS 637", new PaintStandard(PaintStandardSystem.Multiple, "FS 36375 / BS 637").DisplayText);
        Assert.Equal("RLM", new PaintStandard(PaintStandardSystem.Rlm, " ").DisplayText);
        Assert.Equal("K3 195", new PaintStandard(PaintStandardSystem.RaafK3, "195").DisplayText);
        // An unknown stored system still shows its code.
        Assert.Equal("X1", PaintStandard.From("future", "X1")?.DisplayText);
        Assert.Null(PaintStandard.From("", "36270"));
    }

    [Fact]
    public void StandardsMatchWhateverTheSpelling()
    {
        var fs = new PaintStandard(PaintStandardSystem.Fs595, "36270");
        Assert.Equal(fs.MatchKey, new PaintStandard(PaintStandardSystem.Fs595, "36 270").MatchKey);
        Assert.Equal(new PaintStandard(PaintStandardSystem.Custom, "FS-36270").MatchKey, new PaintStandard(PaintStandardSystem.Custom, "fs 36270").MatchKey);
        Assert.NotEqual(fs.MatchKey, new PaintStandard(PaintStandardSystem.Fs595, "36375").MatchKey);
        Assert.NotEqual(fs.MatchKey, new PaintStandard(PaintStandardSystem.Rlm, "36270").MatchKey);
        Assert.Equal("FS 36270 FS 595", fs.SearchText);
    }

    [Fact]
    public void MatchAndTypeNamesAreRead()
    {
        Assert.Equal(PaintMatch.Exact, PaintMatch.FromText("Exact match"));
        Assert.Equal(PaintMatch.Closest, PaintMatch.FromText("closest"));
        Assert.Equal(PaintMatch.None, PaintMatch.FromText("No Match"));
        Assert.Equal(PaintMatch.None, PaintMatch.FromText("none"));
        Assert.Null(PaintMatch.FromText("Nearly"));
        Assert.Equal(PaintType.Lacquer, PaintType.FromText("lacquer"));
        Assert.Equal(PaintType.AlcoholAcrylic, PaintType.FromText("Alcohol-compatible acrylic"));
        Assert.Equal(PaintType.WaterAcrylic, PaintType.FromText("waterAcrylic"));
        Assert.Equal(PaintType.EnamelWash, PaintType.FromText("Enamel wash"));
        Assert.Equal(PaintType.Pigment, PaintType.FromText("pigment"));
        Assert.Equal(PaintType.AcrylicWash, PaintType.FromText("Acrylic Wash"));
        Assert.Equal(PaintType.Marker, PaintType.FromText("marker"));
        Assert.Null(PaintType.FromText("Gouache"));
    }
}

public class SchemaUpgradeTests
{
    [Fact]
    public void KitDetailsGoToTheEndOfTheNotesOnce()
    {
        Assert.Equal("Kit: Italeri · 1234 · 1:72", KitDetailsNote.Line(" Italeri", "1234", "1:72 "));
        Assert.Equal("Kit: Tamiya · 1:48", KitDetailsNote.Line("Tamiya", null, "1:48"));
        Assert.Null(KitDetailsNote.Line("", "  ", ""));
        Assert.Equal("Kit: Tamiya", KitDetailsNote.Notes(null, "Kit: Tamiya"));
        Assert.Equal("Wheels first.\n\nKit: Tamiya", KitDetailsNote.Notes("Wheels first.\n", "Kit: Tamiya"));
        // Already the last line: not added again.
        Assert.Equal("Wheels first.\n\nKit: Tamiya", KitDetailsNote.Notes("Wheels first.\n\nKit: Tamiya", "Kit: Tamiya"));
        Assert.Equal("Keep", KitDetailsNote.Notes("Keep", null));
    }

    [Fact]
    public void AnOlderLibraryJsonIsBroughtUpToDate()
    {
        var library = JsonNode.Parse("""
            {
              "schemaVersion": "1.0.0",
              "projects": [
                { "id": "A", "name": "F-16C", "manufacturer": "Tamiya", "subject": "", "scale": "1:48", "kitNumber": "61098", "notes": "Gear first." },
                { "id": "B", "name": "Empty", "manufacturer": "", "subject": "", "scale": "" }
              ],
              "paints": [
                { "id": "P1", "standardCode": "FS 36320" },
                { "id": "P2", "standardCode": "FS 34102 / RAL 6003" },
                { "id": "P3", "standardCode": "" },
                { "id": "P4", "standardCode": "IJN J3" }
              ],
              "projectPaints": [
                { "id": "R1", "standard": "FS36320" },
                { "id": "R2", "standard": "RLM" },
                { "id": "R3" }
              ]
            }
            """)!.AsObject();

        SchemaUpgrades.Upgrade(library);

        Assert.Equal("6.0.0", (string?)library["schemaVersion"]);
        var projects = library["projects"]!.AsArray();
        Assert.Equal("Gear first.\n\nKit: Tamiya · 61098 · 1:48", (string?)projects[0]!["notes"]);
        Assert.False(projects[0]!.AsObject().ContainsKey("manufacturer"));
        Assert.False(projects[1]!.AsObject().ContainsKey("notes"));
        string Pair(JsonNode? node, string code) => $"{(string?)node!["standardSystem"]}|{(string?)node[code]}";
        var paints = library["paints"]!.AsArray();
        Assert.Equal(["fs595|36320", "multiple|FS 34102 / RAL 6003", "|", "ijn|IJN J3"], paints.Select(p => Pair(p, "standardCode")));
        var rows = library["projectPaints"]!.AsArray();
        Assert.Equal(["fs595|36320", "rlm|", "|"], rows.Select(r => Pair(r, "standard")));
        // A system with no code has no stored code at all.
        Assert.False(rows[1]!.AsObject().ContainsKey("standard"));
    }

    [Fact]
    public void ACurrentOrNewerLibraryIsLeftAlone()
    {
        var library = JsonNode.Parse("""{ "schemaVersion": "6.1.0", "paints": [ { "standardCode": "FS 36320" } ] }""")!.AsObject();
        SchemaUpgrades.Upgrade(library);
        Assert.Equal("6.1.0", (string?)library["schemaVersion"]);
        Assert.Equal("FS 36320", (string?)library["paints"]![0]!["standardCode"]);
    }

    /// <summary>A store as BenchBook for Windows 0.2 made it, at schema 1.0.0.</summary>
    private static void MakeOldStore(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Migrations.Steps[0].Sql + """
            ;
            PRAGMA user_version = 1;
            INSERT INTO projects VALUES ('6A1F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C', '2026-11-13T09:41:07.250Z', '2026-11-13T09:41:07.250Z',
                'Cobra', 'Italeri', 'AH-1W', '1:72', '1234', NULL, 'Rotor first.', 'inProgress');
            INSERT INTO assets VALUES ('BF6F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C', '2026-11-13T09:41:07.250Z', '2026-11-13T09:41:07.250Z',
                'Originals/BF6F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C.pdf', 'Cobra.pdf', 'application/pdf', 10, 'ab', NULL, NULL);
            INSERT INTO reference_items (id, createdAt, updatedAt, projectId, kind, capturedAt, assetId)
                VALUES ('7B2F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C', '2026-11-13T09:41:07.250Z', '2026-11-13T09:41:07.250Z',
                '6A1F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C', 'pdf', '2026-11-13T09:41:07.250Z', 'BF6F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C');
            INSERT INTO markups VALUES ('8C3F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C', '2026-11-13T09:41:07.250Z', '2026-11-13T09:41:07.250Z',
                '7B2F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C', 2, 'note', 'yellow', 72, 640, 180, 46, '[]', 'Fit the seat');
            INSERT INTO paints VALUES ('9D4F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C', '2026-11-13T09:41:07.250Z', '2026-11-13T09:41:07.250Z',
                'Mr. Color', 'Lacquer', 'C307', 'Gray', NULL, NULL, 'FS 36320', 'mr. color|lacquer|c307');
            INSERT INTO project_paints VALUES ('AE5F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C', '2026-11-13T09:41:07.250Z', '2026-11-13T09:41:07.250Z',
                '6A1F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C', '9D4F0C5E-0C9B-4C5E-9B1E-1F0C5E0C9B4C', 'FS36320', 'upper', NULL);
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void AnOldStoreIsCopiedThenUpgradedWithNothingLost()
    {
        using var library = new TestLibrary();
        Directory.CreateDirectory(library.Location.AppFolder);
        MakeOldStore(library.Location.StorePath);

        using (var store = library.OpenStore())
        {
            Assert.Equal(Migrations.Latest, store.SchemaStep);
            var snapshot = store.ReadSnapshot();
            var project = Assert.Single(snapshot.Projects);
            Assert.Equal("Rotor first.\n\nKit: Italeri · 1234 · 1:72", project.Notes);
            Assert.Equal("AH-1W", project.Subject);
            var paint = Assert.Single(snapshot.Paints);
            Assert.Equal(("fs595", "36320", "", "", false), (paint.StandardSystem, paint.StandardCode, paint.Match, paint.Type, paint.IsMix));
            var row = Assert.Single(snapshot.ProjectPaints);
            Assert.Equal(("fs595", "36320"), (row.StandardSystem, row.Standard));
            var markup = Assert.Single(snapshot.Markups);
            Assert.Equal(("Fit the seat", 2, (string?)null), (markup.Text, markup.PageIndex, markup.BuildPhotoId));
            Assert.Empty(snapshot.Kits); // No kits are made: the user adds them.
            Assert.Empty(snapshot.FindProblems());
        }

        // The copy is the old store, untouched.
        var copy = LibraryStore.UpgradeCopyPath(library.Location.StorePath);
        Assert.EndsWith("BenchBook Before 6.0.0.db", copy);
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            old.Open();
            using var command = old.CreateCommand();
            command.CommandText = "SELECT manufacturer FROM projects; PRAGMA user_version;";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Italeri", reader.GetString(0));
        }
        Assert.Empty(Directory.GetDirectories(library.Location.AppFolder, ".upgrade-*"));
    }

    [Fact]
    public void ABackupFromSchemaOneRestoresUpgraded()
    {
        using var from = new TestLibrary();
        Sample.Install(from);
        var backup = Path.Combine(from.Root, "old.benchbookbackup");
        using (var store = from.OpenStore()) BackupWriter.BackUp(store, from.Location, backup, "0.2.0");

        // Rewrite its library.json as BenchBook 0.2 wrote it: schema 1.0.0, with a project's kit details.
        var unpacked = Path.Combine(from.Root, "unpacked");
        ZipFile.ExtractToDirectory(backup, unpacked);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(unpacked, "library.json")))!.AsObject();
        json["schemaVersion"] = "1.0.0";
        var project = json["projects"]![0]!.AsObject();
        project["manufacturer"] = "Tamiya";
        project["scale"] = "1:48";
        project["kitNumber"] = "61098";
        foreach (var name in new[] { "buildPhotos", "buildPhotoTags", "kits", "paintMixParts" }) json.Remove(name);
        json["markups"] = new JsonArray(json["markups"]!.AsArray().Where(m => m!["buildPhotoId"] is null).Select(m => m!.DeepClone()).ToArray());
        json["paints"] = new JsonArray(json["paints"]!.AsArray().Where(p => p!["isMix"]?.GetValue<bool>() != true).Select(p => p!.DeepClone()).ToArray());
        json["projectPaints"] = new JsonArray(json["projectPaints"]!.AsArray().Where(r => r!["paintId"] is null || json["paints"]!.AsArray().Any(p => (string?)p!["id"] == (string?)r["paintId"])).Select(r => r!.DeepClone()).ToArray());
        foreach (var paint in json["paints"]!.AsArray())
        {
            var text = PaintStandard.From((string?)paint!["standardSystem"], (string?)paint["standardCode"])?.DisplayText ?? "";
            foreach (var field in new[] { "standardSystem", "match", "type", "isMix" }) paint.AsObject().Remove(field);
            paint["standardCode"] = text;
        }
        project.Remove("projectPhotoId");
        var library = BackupReading.ReadLibraryJson(json);

        Assert.Equal("6.0.0", library.SchemaVersion);
        Assert.EndsWith("Kit: Tamiya · 61098 · 1:48", library.Projects[0].Notes);
        var grey = library.Paints.Single(p => p.Code == "XF-54");
        Assert.Equal(("bs381c", "640"), (grey.StandardSystem, grey.StandardCode));
        Assert.Empty(library.FindProblems());
    }
}

/// <summary>Reads a library.json object as a restore does.</summary>
internal static class BackupReading
{
    public static LibrarySnapshot ReadLibraryJson(JsonObject json)
    {
        var path = Path.Combine(Path.GetTempPath(), "BenchBook Tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, json.ToJsonString());
            return LibraryRestore.ReadLibrary(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class SchemaSixRecordTests
{
    [Fact]
    public void PricesAreWrittenAsTextAndEveryNewRecordSurvivesABackup()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var backup = Path.Combine(library.Root, "b.benchbookbackup");
        using (var store = library.OpenStore()) BackupWriter.BackUp(store, library.Location, backup, "0.3.0");
        using var zip = ZipFile.OpenRead(backup);
        using var reader = new StreamReader(zip.GetEntry("library.json")!.Open());
        var json = JsonNode.Parse(reader.ReadToEnd())!;

        var kit = json["kits"]!.AsArray().Single(k => (string?)k!["brand"] == "Tamiya")!;
        Assert.Equal("89.95", (string?)kit["pricePaid"]);
        Assert.Equal("120.00", (string?)kit["salePrice"]);
        Assert.True((bool)kit["owned"]!);
        // An unknown status is kept as it is.
        Assert.Equal("mint", (string?)json["kits"]!.AsArray().Single(k => (string?)k!["brand"] == "Italeri")!["status"]);
        var mix = json["paints"]!.AsArray().Single(p => (bool)p!["isMix"]!)!;
        Assert.Equal("mix|custom|faded sea grey", (string?)mix["identityKey"]);
        Assert.Equal([3.0, 1.5], json["paintMixParts"]!.AsArray().Select(p => (double)p!["parts"]!));
        Assert.Equal(sample.BuildPhotos[0].Id, (string?)json["projects"]![0]!["projectPhotoId"]);
        // A build photo's note has no referenceId.
        var photoNote = json["markups"]!.AsArray().Single(m => m!["buildPhotoId"] is not null)!;
        Assert.Null(photoNote["referenceId"]);
        // Computed properties aren't written.
        Assert.Null(json["paints"]![0]!["standard"]);

        var read = BackupReading.ReadLibraryJson(json.AsObject());
        Assert.Equal(89.95m, read.Kits.Single(k => k.Brand == "Tamiya").PricePaid);
        Assert.Equal(sample.BuildPhotos[0].TakenAt, read.BuildPhotos[0].TakenAt);
        Assert.Empty(read.FindProblems());
    }

    [Fact]
    public void AMarkupMustBelongToExactlyOneThing()
    {
        var (snapshot, _) = Sample.Make();
        snapshot.Markups[0].BuildPhotoId = snapshot.BuildPhotos[0].Id;
        Assert.Contains(snapshot.FindProblems(), p => p.Contains("must belong to one reference or one build photo"));
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        Assert.Throws<SqliteException>(() => store.InsertSnapshot(snapshot));
        Assert.Empty(store.ReadSnapshot().Projects); // All or nothing.
    }

    [Fact]
    public void AMixIsKnownByBrandAndNameAndAPaintInARecipeCantBeDeleted()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        Assert.Equal("mix|custom|faded sky grey", new PaintDraft { Brand = " Custom", Name = "Faded  Sky Grey", IsMix = true }.IdentityKey);
        Assert.False(new PaintDraft { Brand = "Custom", Name = "Faded", IsMix = true }.MissingRequiredField);
        Assert.True(new PaintDraft { Brand = "Custom", Code = "M1", IsMix = true }.MissingRequiredField);

        var white = sample.Paints.Single(p => p.Code == "XF-2");
        var refused = PaintService.Delete(store, [white]);
        Assert.Equal(["“Tamiya XF-2 Flat White” is in the recipe for Custom Faded Sea Grey. Take it out of those mixes first."], refused);
        Assert.NotNull(store.Paint(white.Id));
    }

    [Fact]
    public void ATagOnlyBuildPhotosUseIsInUse()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        using (var command = store.Connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM reference_tags";
            command.ExecuteNonQuery();
        }
        var usage = Assert.Single(store.TagsByUsage());
        Assert.Equal((0, 1, 1), (usage.References, usage.BuildPhotos, usage.Projects));
        Assert.False(usage.IsUnused);
        Assert.Equal(0, store.RemoveUnusedTags());
        Assert.Single(store.ReadSnapshot().Tags);
    }

    [Fact]
    public void AProjectsSummaryComesFromItsKits()
    {
        var project = new Project { Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, Name = "Cobra", Subject = "AH-1W" };
        Kit Kit(string brand, string? scale) => new()
        {
            Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, Brand = brand, Name = "Kit", Scale = scale,
            Owned = true, Status = KitStatus.Bnib, ForSale = false,
        };
        Assert.Equal("Italeri · 1:72 · AH-1W", ProjectService.Summary(project, [Kit("Italeri", "1:72")]));
        Assert.Equal("2 kits · AH-1W", ProjectService.Summary(project, [Kit("A", null), Kit("B", null)]));
        Assert.Equal("AH-1W", ProjectService.Summary(project, []));
        project.Subject = "";
        Assert.Equal("Italeri", ProjectService.Summary(project, [Kit("Italeri", null)]));
    }
}

/// <summary>The cases in the Mac's PaintStandardCSVTests and the re-import rules (#24).</summary>
public class PaintCsvSchemaSixTests
{
    [Fact]
    public void BenchBooksOwnColumnsAreRead()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var csv = string.Join("\n",
            "Brand,Code,Standard,Standard Code,Match,Type",
            "Mr. Color,C307,FS 595,36320,Exact match,Lacquer",
            "Tamiya,XF-19,fs595,36173,closest,alcoholAcrylic",
            "AK,RC1,FS,34102,,",
            "Vallejo,70.1,Humbrol,28,,",
            "Revell,32,,,Exact match,Enamel",
            "Mr. Hobby,H1,RLM,76,Nearly,Gouache");
        var summary = PaintCsvImporter.Import(store, csv);
        Assert.Equal(6, summary.Added);
        Assert.Equal(2, summary.Problems.Count);

        var paints = store.Paints().ToDictionary(p => p.Code);
        Assert.Equal(("FS 36320", "exact", "lacquer"), (paints["C307"].Standard?.DisplayText, paints["C307"].Match, paints["C307"].Type));
        Assert.Equal(("fs595", "closest", "alcoholAcrylic"), (paints["XF-19"].StandardSystem, paints["XF-19"].Match, paints["XF-19"].Type));
        Assert.Equal("FS 34102", paints["RC1"].Standard?.DisplayText);
        Assert.Equal(("custom", "Humbrol 28"), (paints["70.1"].StandardSystem, paints["70.1"].StandardCode));
        // A match needs a standard.
        Assert.Equal(("", "", "enamel"), (paints["32"].StandardSystem, paints["32"].Match, paints["32"].Type));
        Assert.Equal(("", ""), (paints["H1"].Match, paints["H1"].Type));
    }

    [Fact]
    public void TheOldStandardColumnIsSplit()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        PaintCsvImporter.Import(store, "Brand,Code,Standard\nMr. Color,C307,FS 36320\nAK,RC1,RLM 74 / RLM 75");
        var paints = store.Paints().ToDictionary(p => p.Code);
        Assert.Equal(("fs595", "36320"), (paints["C307"].StandardSystem, paints["C307"].StandardCode));
        Assert.Equal("multiple", paints["RC1"].StandardSystem);
    }

    [Fact]
    public void ReimportingUpdatesOnlyFromFilledCells()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var grey = PaintService.Create(store, new PaintDraft
        {
            Brand = "Tamiya", Range = "Acrylic XF", Code = "XF-54", Name = "Dark Sea Grey", Notes = "Thin 1:1", SwatchHex = "#6B7378", IsOwned = true,
        }.WithStandardText("BS 640"));

        var csv = string.Join("\n",
            "Brand,Range,Code,Name,Colour,Owned,Notes,Type",
            // Blank name, colour, owned and notes keep what's there; the type is added.
            "TAMIYA,acrylic xf,XF 54,,,,,Alcohol-compatible acrylic",
            "Tamiya,Acrylic XF,XF-1,Flat Black,,maybe,,");
        var summary = PaintCsvImporter.Import(store, csv);

        Assert.Equal((1, 1, 0, 0), (summary.Added, summary.Updated, summary.UpToDate, summary.Repeated));
        Assert.Equal(["Line 3: “maybe” isn't yes or no, so ownership wasn't changed."], summary.Problems);
        var updated = store.Paint(grey.Id)!;
        // Brand, range and code keep the catalogue's spelling.
        Assert.Equal(("Tamiya", "XF-54", "Dark Sea Grey", "#6B7378", "Thin 1:1", "alcoholAcrylic", "640"),
            (updated.Brand, updated.Code, updated.Name, updated.SwatchHex, updated.Notes, updated.Type, updated.StandardCode));
        Assert.Contains(grey.Id, store.OwnedPaintIds());

        // It takes an explicit no to un-own a paint.
        var again = PaintCsvImporter.Import(store, "Brand,Range,Code,Owned\nTamiya,Acrylic XF,XF-54,no\nTamiya,Acrylic XF,XF-1,");
        Assert.Equal((0, 1, 1), (again.Added, again.Updated, again.UpToDate));
        Assert.DoesNotContain(grey.Id, store.OwnedPaintIds());
    }
}
