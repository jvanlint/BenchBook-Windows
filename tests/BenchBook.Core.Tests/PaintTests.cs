using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using BenchBook.Core.Projects;
using BenchBook.Core.Store;

namespace BenchBook.Core.Tests;

public class PaintRuleTests
{
    [Theory]
    [InlineData("Tamiya", "Acrylic XF", "XF-54", "tamiya|acrylic xf|xf54")]
    [InlineData(" Tamiya ", "Acrylic  XF", "XF 54", "tamiya|acrylic xf|xf54")]
    [InlineData("TAMIYA", "acrylic xf", "xf54", "tamiya|acrylic xf|xf54")]
    [InlineData("Mr. Hobby", "", "H_335/a.b", "mr. hobby||h335ab")]
    public void IdentityKeysFoldAsTheSpecSays(string brand, string range, string code, string key) =>
        Assert.Equal(key, PaintIdentity.Key(brand, range, code));

    [Theory]
    [InlineData("#4a5a6b", "#4A5A6B")]
    [InlineData("4A5A6B", "#4A5A6B")]
    [InlineData("#abc", "#AABBCC")]
    [InlineData(" #ABC ", "#AABBCC")]
    [InlineData("bluish", null)]
    [InlineData("#12345", null)]
    [InlineData("", null)]
    public void SwatchesAreNormalized(string text, string? hex) => Assert.Equal(hex, Swatch.Normalize(text));

    [Theory]
    [InlineData("xf-54", true)]
    [InlineData("sea grey", true)]
    [InlineData("FS36270", true)]
    [InlineData("fs 36270", true)]
    [InlineData("36270", true)]
    [InlineData("BS 637", true)]
    [InlineData("rlm", false)]
    public void PaintSearchFindsStandardsWithoutPunctuation(string query, bool found)
    {
        var paint = Paint("Tamiya", "Acrylic XF", "XF-54", "Dark Sea Grey", "FS-36270 / BS 637");
        Assert.Equal(found, PaintIdentity.Matches(query, paint));
    }

    [Fact]
    public void FilterOptionsUseTheCommonestSpelling()
    {
        var options = PaintFilterOption.For(["Tamiya", "tamiya ", "Tamiya", "AK", "  "]);
        Assert.Equal([("ak", "AK", 1), ("tamiya", "Tamiya", 3)], options.Select(o => (o.Key, o.Name, o.Count)));
    }

    /// <summary>A paint whose standard is read from text, e.g. "FS 36270".</summary>
    internal static Paint Paint(string brand, string range, string code, string name = "", string standard = "") => new()
    {
        Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, Brand = brand, Range = range, Code = code,
        Name = name, StandardSystem = PaintStandardParser.Parse(standard)?.SystemRaw ?? "", StandardCode = PaintStandardParser.Parse(standard)?.Code ?? "",
        IdentityKey = PaintIdentity.Key(brand, range, code),
    };
}

public class PaintServiceTests
{
    [Fact]
    public void ADuplicateKeyIsRefusedByName()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        PaintService.Create(store, new PaintDraft { Brand = "Tamiya", Range = "Acrylic XF", Code = "XF-54", Name = "Dark Sea Grey", SwatchHex = "#6b7378", IsOwned = true });
        var error = Assert.Throws<PaintException>(() => PaintService.Create(store, new PaintDraft { Brand = "tamiya", Range = "Acrylic XF", Code = "xf 54" }));
        Assert.Equal("“Tamiya XF-54 Dark Sea Grey” is already in your catalogue.", error.Message);
        Assert.Equal("A paint needs a brand and a code.", Assert.Throws<PaintException>(() => PaintService.Create(store, new PaintDraft { Brand = " ", Code = "1" })).Message);

        var paint = Assert.Single(store.Paints());
        Assert.Equal("#6B7378", paint.SwatchHex);
        Assert.Contains(paint.Id, store.OwnedPaintIds());
    }

    [Fact]
    public void APaintInUseIsntDeleted()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "F-16C" });
        var used = PaintService.Create(store, new PaintDraft { Brand = "Tamiya", Code = "XF-54", Name = "Dark Sea Grey" });
        var spare = PaintService.Create(store, new PaintDraft { Brand = "Tamiya", Code = "XF-1", IsOwned = true });
        PaintService.AddPaints(store, project.Id, [used.Id, used.Id]);

        var refused = PaintService.Delete(store, [used, spare]);
        Assert.Equal(["“Tamiya XF-54 Dark Sea Grey” is used in F-16C. Remove it from those projects first."], refused);
        Assert.Equal([used.Id], store.Paints().Select(p => p.Id));
        Assert.Empty(store.OwnedPaintIds());
        Assert.Single(store.ProjectPaints(project.Id));
    }

    [Fact]
    public void ProjectRowsSortColoursFirstThenPaints()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "P" });
        var xf10 = PaintService.Create(store, new PaintDraft { Brand = "Tamiya", Code = "XF-10" });
        var xf2 = PaintService.Create(store, new PaintDraft { Brand = "Tamiya", Code = "XF-2" });
        var ak = PaintService.Create(store, new PaintDraft { Brand = "AK", Code = "RC001" });
        PaintService.AddPaints(store, project.Id, [xf10.Id, xf2.Id, ak.Id]);
        PaintService.AddColour(store, project.Id, "RLM 76", "  undersides ");
        PaintService.AddColour(store, project.Id, "FS 36270", "");

        var paints = store.Paints().ToDictionary(p => p.Id, Ids.Comparer);
        var rows = PaintService.SortedRows(store.ProjectPaints(project.Id), paints);
        Assert.Equal(["FS 36270", "RLM 76", "AK RC001", "Tamiya XF-2", "Tamiya XF-10"],
            rows.Select(r => r.PaintId is null ? r.GuideStandard?.DisplayText : PaintText.DisplayName(paints[r.PaintId])));
        Assert.Equal("undersides", rows[1].UsedFor);
        Assert.Null(rows[0].UsedFor);
    }
}

public class PaintCsvTests
{
    [Fact]
    public void ParsingHandlesQuotesLineBreaksAndEveryLineEnding()
    {
        var rows = CsvParser.Parse("a,\"b, with comma\",\"say \"\"hi\"\"\"\r\n\r\nx,\"two\nlines\",z\ry,,");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["a", "b, with comma", "say \"hi\""], rows[0].Fields);
        Assert.Equal(3, rows[1].Line);
        Assert.Equal(["x", "two\nlines", "z"], rows[1].Fields);
        Assert.Equal(5, rows[2].Line);
        Assert.Equal(["y", "", ""], rows[2].Fields);
    }

    [Fact]
    public void ImportReadsAliasesStandardsAndOwnershipAndReportsProblems()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        PaintService.Create(store, new PaintDraft { Brand = "Tamiya", Range = "Acrylic XF", Code = "XF-1" });
        var csv = string.Join("\r\n",
            "Maker,SERIES,Ref,Colour Name,FS,RLM,Hex,Have,Comments",
            "Tamiya,Acrylic XF,XF-54,Dark Sea Grey,36270,,#6b7378,yes,thin 1:1",
            "Tamiya,Acrylic XF,XF 1,Flat Black,,,,,",
            "Gunze,Aqueous,H-335,Medium Sea Grey,,RLM 76,bluish,✓,",
            ",Acrylic,X-1,No brand,,,,,",
            "tamiya,acrylic xf,xf54,Again,,,,,");

        var summary = PaintCsvImporter.Import(store, csv);
        // "XF 1" is the catalogue's XF-1, which gains its name; the second XF-54 row repeats the first.
        Assert.Equal((2, 1, 0, 1), (summary.Added, summary.Updated, summary.UpToDate, summary.Repeated));
        Assert.Equal("Flat Black", store.Paints().Single(p => p.Code == "XF-1").Name);
        Assert.Equal([
            "Line 4: “bluish” isn't a colour like #4A5A6B, so no swatch was set.",
            "Line 5: a paint needs a brand and a code.",
        ], summary.Problems);

        var paints = store.Paints();
        var grey = paints.Single(p => p.Code == "XF-54");
        Assert.Equal(("Dark Sea Grey", "fs595", "36270", "#6B7378", "thin 1:1"), (grey.Name, grey.StandardSystem, grey.StandardCode, grey.SwatchHex, grey.Notes));
        var gunze = paints.Single(p => p.Brand == "Gunze");
        Assert.Equal(("rlm", "76", "RLM 76"), (gunze.StandardSystem, gunze.StandardCode, gunze.Standard?.DisplayText));
        Assert.Null(gunze.SwatchHex);
        Assert.Equal(new[] { grey.Id, gunze.Id }.Order(), store.OwnedPaintIds().Order());
    }

    [Fact]
    public void AFileWithoutBrandOrCodeColumnsIsRefused()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        Assert.Equal("The file's first row must name the columns. Missing: Brand and Code.",
            Assert.Throws<PaintImportException>(() => PaintCsvImporter.Import(store, "Name,Colour\r\nGrey,#888888")).Message);
        Assert.Equal("The file has no rows to import.", Assert.Throws<PaintImportException>(() => PaintCsvImporter.Import(store, "Brand,Code\r\n")).Message);
        Assert.Empty(store.Paints());
    }

    [Fact]
    public void ExportSortsQuotesAndLeavesPersonalColumnsOutByDefault()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        PaintService.Create(store, new PaintDraft { Brand = "Tamiya", Range = "Acrylic XF", Code = "XF-10", Name = "Flat Brown" });
        PaintService.Create(store, new PaintDraft { Brand = "Tamiya", Range = "Acrylic XF", Code = "XF-2", Name = "Flat White, \"bright\"", IsOwned = true, Notes = "Primer" });
        PaintService.Create(store, new PaintDraft { Brand = "AK", Range = "Real Colors", Code = "RC001", SwatchHex = "#888", Match = "exact", Type = "lacquer" }.WithStandardText("FS 36270"));
        // A mix isn't exported.
        PaintService.Create(store, new PaintDraft { Brand = "Custom", Name = "Faded Grey", IsMix = true });
        var paints = store.Paints();
        var owned = store.OwnedPaintIds();

        Assert.Equal(
            "Brand,Range,Code,Name,Standard,Standard Code,Match,Type,Colour\r\n" +
            "AK,Real Colors,RC001,,FS 595,36270,Exact match,Lacquer,#888888\r\n" +
            "Tamiya,Acrylic XF,XF-2,\"Flat White, \"\"bright\"\"\",,,,,\r\n" +
            "Tamiya,Acrylic XF,XF-10,Flat Brown,,,,,\r\n",
            PaintCsvExporter.Csv(paints, owned));
        Assert.EndsWith("XF-2,\"Flat White, \"\"bright\"\"\",,,,,,yes,Primer\r\nTamiya,Acrylic XF,XF-10,Flat Brown,,,,,,,\r\n",
            PaintCsvExporter.Csv(paints, owned, includeOwnership: true, includeNotes: true));

        // What's exported imports back unchanged.
        using var other = new TestLibrary();
        using var copy = other.OpenStore();
        Assert.Equal(3, PaintCsvImporter.Import(copy, PaintCsvExporter.Csv(paints, owned, true, true)).Added);
        Assert.Equal(PaintCsvExporter.Csv(paints, owned, true, true), PaintCsvExporter.Csv(copy.Paints(), copy.OwnedPaintIds(), true, true));
        // Importing it again changes nothing.
        var again = PaintCsvImporter.Import(copy, PaintCsvExporter.Csv(paints, owned, true, true));
        Assert.Equal((0, 0, 3), (again.Added, again.Updated, again.UpToDate));
    }
}

public class ShoppingListTests
{
    [Fact]
    public void TheListHasUnownedPaintsForUnfinishedProjectsAndColoursWithoutPaint()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var f16 = ProjectService.Create(store, new ProjectDraft { Name = "F-16C" });
        var spitfire = ProjectService.Create(store, new ProjectDraft { Name = "Spitfire" });
        var done = ProjectService.Create(store, new ProjectDraft { Name = "Done", Status = ProjectStatus.Completed });
        var grey = PaintService.Create(store, new PaintDraft { Brand = "Tamiya", Range = "Acrylic XF", Code = "XF-54", Name = "Dark Sea Grey" }.WithStandardText("BS 640"));
        var black = PaintService.Create(store, new PaintDraft { Brand = "tamiya", Code = "XF-1", Name = "Flat Black" });
        var owned = PaintService.Create(store, new PaintDraft { Brand = "AK", Code = "RC001", IsOwned = true });
        var onlyDone = PaintService.Create(store, new PaintDraft { Brand = "Gunze", Code = "H-1" });

        PaintService.AddPaints(store, f16.Id, [grey.Id, owned.Id]);
        var row = store.ProjectPaints(f16.Id).Single(r => r.PaintId == grey.Id);
        PaintService.SetText(store, row, "", "upper surfaces");
        PaintService.AddPaints(store, spitfire.Id, [grey.Id, black.Id]);
        PaintService.AddPaints(store, done.Id, [onlyDone.Id]);
        PaintService.AddColour(store, f16.Id, "FS 36270", "upper surfaces");
        PaintService.AddColour(store, done.Id, "RLM 76", "");

        var list = new ShoppingList(store);
        var group = Assert.Single(list.Groups);
        Assert.Equal("Tamiya", group.Name);
        Assert.Equal(["XF-1 Flat Black", "XF-54 Dark Sea Grey"], group.Items.Select(i => i.Title));
        Assert.Equal("F-16C (upper surfaces), Spitfire", group.Items[1].UsesLine);
        Assert.Equal("2 paints for 2 projects · 1 colour without paint", list.Summary);
        Assert.Equal(
            "Paint Shopping List\n13 November 2026 · 2 paints for 2 projects · 1 colour without paint\n\n" +
            "Tamiya\n☐ XF-1 Flat Black\n    Spitfire\n☐ XF-54 Dark Sea Grey · Acrylic XF · BS 640\n    F-16C (upper surfaces), Spitfire\n\n" +
            "No Paint Chosen\n☐ FS 36270\n    F-16C (upper surfaces)\n",
            list.Text("13 November 2026"));

        // Ticking a paint as bought marks it owned, and it leaves the list.
        store.SetOwned(grey.Id, true);
        Assert.Equal(["XF-1 Flat Black"], new ShoppingList(store).Items.Select(i => i.Title));
    }
}

public class AftermarketTests
{
    [Fact]
    public void PartsNeedANameAndAWebAddressIfAnyAndSortByStatus()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "F-16C" });
        Assert.Equal("", AftermarketDraft.For(project).ScaleCompatibility);
        Assert.Equal("A part needs a name.", Assert.Throws<AftermarketException>(() => AftermarketService.Create(store, project.Id, new AftermarketDraft())).Message);
        Assert.Equal("The product page isn't a web address.",
            Assert.Throws<AftermarketException>(() => AftermarketService.Create(store, project.Id, new AftermarketDraft { ProductName = "Decals", ProductUrl = "not a page" })).Message);

        var decals = AftermarketService.Create(store, project.Id, new AftermarketDraft { ProductName = "50th Anniversary", ProductUrl = "hannants.co.uk/product/x", Status = "inStash" });
        AftermarketService.Create(store, project.Id, new AftermarketDraft { ProductName = "Resin seat", Type = "resin", Status = "considering" });
        AftermarketService.Create(store, project.Id, new AftermarketDraft { ProductName = "Odd", Status = "fromTheFuture" });
        Assert.Equal("https://hannants.co.uk/product/x", store.AftermarketItem(decals.Id)!.ProductUrl);
        Assert.Equal(["Resin seat", "50th Anniversary", "Odd"], AftermarketKinds.Sorted(store.AftermarketItems(project.Id)).Select(i => i.ProductName));
        Assert.Equal("fromTheFuture", AftermarketKinds.StatusName("fromTheFuture"));
    }

    [Fact]
    public void APictureIsCheckedStoredAndTidiedWhenReplacedOrDeleted()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var access = new DirectStore(store);
        var project = ProjectService.Create(store, new ProjectDraft { Name = "P" });
        var item = AftermarketService.Create(store, project.Id, new AftermarketDraft { ProductName = "Decals" });
        var first = FileCheckTests.Write(library, "first.png", Images.Png(300, 200));
        var second = FileCheckTests.Write(library, "second.jpg", Images.Jpeg(640, 480));
        var markdown = FileCheckTests.Write(library, "notes.md", "# Not a picture"u8.ToArray());

        AftermarketService.SetImage(access, library.Location, item, first, "first.png", _ => true);
        var firstAsset = store.Asset(item.ImageAssetId!)!;
        Assert.Equal((300, 200), (firstAsset.PixelWidth, firstAsset.PixelHeight));

        Assert.Equal("“notes.md” isn't an image BenchBook can use.",
            Assert.Throws<AftermarketException>(() => AftermarketService.SetImage(access, library.Location, item, markdown, "notes.md", _ => true)).Message);

        AftermarketService.SetImage(access, library.Location, item, second, "second.jpg", _ => true);
        Assert.Null(store.Asset(firstAsset.Id));
        Assert.False(File.Exists(library.Location.FullPath(firstAsset.OriginalRelativePath)));

        var secondAsset = store.Asset(item.ImageAssetId!)!;
        AftermarketService.Delete(store, library.Location, [item.Id]);
        Assert.Null(store.Asset(secondAsset.Id));
        Assert.Empty(Directory.GetFiles(library.Location.OriginalsFolder));
    }
}
