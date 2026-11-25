using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using BenchBook.Core.Projects;
using BenchBook.Core.Store;

namespace BenchBook.Core.Tests;

/// <summary>Mixes and their recipes (behaviour.md, "Mixes (stored)"; PaintMixTests.swift on the Mac).</summary>
public class MixTests
{
    private static Paint Paint(LibraryStore store, string code, string name, bool owned = false) =>
        PaintService.Create(store, new PaintDraft { Brand = "Tamiya", Range = "Acrylic XF", Code = code, Name = name, IsOwned = owned });

    private static Paint Mix(LibraryStore store, string name, params (Paint Paint, double Parts)[] recipe) =>
        PaintService.Create(store, new PaintDraft
        {
            Brand = "Custom", Name = name, IsMix = true,
            Recipe = recipe.Select(r => new MixPartDraft(r.Paint.Id, r.Parts)).ToList(),
        });

    [Fact]
    public void AMixIsKnownByBrandAndNameAndHasNoRange()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var mix = PaintService.Create(store, new PaintDraft { Brand = "Custom", Range = "Ignored", Name = "Faded Sky Grey", IsMix = true });
        Assert.Equal("mix|custom|faded sky grey", mix.IdentityKey);
        Assert.Equal("", mix.Range);
        Assert.Equal("A mix needs a brand and a name.",
            Assert.Throws<PaintException>(() => PaintService.Create(store, new PaintDraft { Brand = "Custom", Code = "1", IsMix = true })).Message);
        Assert.Equal("“Custom Faded Sky Grey” is already in your catalogue.",
            Assert.Throws<PaintException>(() => PaintService.Create(store, new PaintDraft { Brand = "custom ", Name = "faded  sky grey", IsMix = true })).Message);
    }

    [Fact]
    public void TheRecipeIsSavedInOrderAndShownWithParts()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var white = Paint(store, "XF-2", "Flat White");
        var grey = Paint(store, "XF-19", "Sky Grey");
        var mix = Mix(store, "Faded Sky Grey", (white, 3), (grey, 1));

        var parts = store.MixParts(mix.Id);
        Assert.Equal([(white.Id, 3.0, 0), (grey.Id, 1.0, 1)], parts.Select(p => (p.PaintId, p.Parts, p.Position)));
        var catalogue = PaintCatalogue.Load(store);
        Assert.Equal("3 parts XF-2, 1 part XF-19", catalogue.RecipeSummary(mix));
        Assert.Equal("Mix: 3 parts XF-2, 1 part XF-19", catalogue.SecondaryLine(mix));
    }

    [Fact]
    public void PartsAreWrittenWithUpToTwoDecimals()
    {
        Assert.Equal("1 part XF-19", MixAmount.Line(1, "XF-19"));
        Assert.Equal($"{1.5:0.##} parts XF-2", MixAmount.Line(1.5, "XF-2"));
        Assert.Equal($"{0.33:0.##} parts XF-2", MixAmount.Line(1.0 / 3, "XF-2"));
        Assert.Equal("Mix", MixAmount.RecipeLine(""));
    }

    [Fact]
    public void ARecipePaintWithNoCodeShowsItsName()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var named = PaintService.Create(store, new PaintDraft { Brand = "Custom", Name = "Base", IsMix = true });
        // A mix can't be in a recipe; a paint always has a code, so its name only shows when the code is blank.
        Assert.Equal("Base", PaintText.ShortName(named));
    }

    [Fact]
    public void BadRecipesAreRefused()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var white = Paint(store, "XF-2", "Flat White");
        var other = Mix(store, "Other", (white, 1));

        Assert.Equal("Each paint in a recipe needs a number of parts more than zero.",
            Assert.Throws<PaintException>(() => Mix(store, "Zero", (white, 0))).Message);
        var bad = "A recipe can only use paints from your catalogue that aren't mixes, each once.";
        Assert.Equal(bad, Assert.Throws<PaintException>(() => Mix(store, "Twice", (white, 1), (white, 2))).Message);
        Assert.Equal(bad, Assert.Throws<PaintException>(() => Mix(store, "Of a Mix", (other, 1))).Message);
        var self = PaintDraft.From(other, false, store.MixParts(other.Id)) with { Recipe = [new MixPartDraft(other.Id, 1)] };
        Assert.Equal(bad, Assert.Throws<PaintException>(() => PaintService.Update(store, other, false, self)).Message);
    }

    [Fact]
    public void AMixIsOwnedWhenEveryRecipePaintIs()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var white = Paint(store, "XF-2", "Flat White", owned: true);
        var grey = Paint(store, "XF-19", "Sky Grey");
        var mix = Mix(store, "Faded Sky Grey", (white, 3), (grey, 1));
        var empty = Mix(store, "Empty");

        var catalogue = PaintCatalogue.Load(store);
        Assert.False(catalogue.IsOwned(mix));
        Assert.False(catalogue.IsOwned(empty));
        Assert.Equal("Not owned yet: Tamiya XF-19 Sky Grey", catalogue.MixOwnedHelp(mix));
        Assert.Equal("Add a recipe to this mix. It's owned when you own every paint in it.", catalogue.MixOwnedHelp(empty));

        // Marking a mix owned does nothing: it has no ownership of its own.
        PaintService.SetOwned(store, [mix, grey], true);
        catalogue = PaintCatalogue.Load(store);
        Assert.True(catalogue.IsOwned(mix));
        Assert.DoesNotContain(mix.Id, store.OwnedPaintIds());
        Assert.Equal("You own every paint in this mix", catalogue.MixOwnedHelp(mix));
    }

    [Fact]
    public void EditingTheRecipeReplacesItAndAnUnchangedOneIsLeftAlone()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var white = Paint(store, "XF-2", "Flat White");
        var grey = Paint(store, "XF-19", "Sky Grey");
        var blue = Paint(store, "XF-23", "Light Blue");
        var mix = Mix(store, "Faded Sky Grey", (white, 3), (grey, 1));
        var before = store.MixParts(mix.Id).Select(p => p.Id).ToList();
        var updated = mix.UpdatedAt;

        // Nothing changed: nothing is written.
        PaintService.Update(store, mix, false, PaintDraft.From(mix, false, store.MixParts(mix.Id)));
        Assert.Equal(before, store.MixParts(mix.Id).Select(p => p.Id));
        Assert.Equal(updated, store.Paint(mix.Id)!.UpdatedAt);

        var draft = PaintDraft.From(mix, false, store.MixParts(mix.Id)).MoveRecipePaint(grey.Id, -1);
        draft = draft with { Recipe = [.. draft.Recipe, new MixPartDraft(blue.Id, 1.5)] };
        PaintService.Update(store, mix, false, draft);
        Assert.Equal([(grey.Id, 1.0), (white.Id, 3.0), (blue.Id, 1.5)], store.MixParts(mix.Id).Select(p => (p.PaintId, p.Parts)));
    }

    [Fact]
    public void MovingAtTheEndsDoesNothing()
    {
        var draft = new PaintDraft { IsMix = true, Recipe = [new("a", 1), new("b", 2)] };
        Assert.Same(draft, draft.MoveRecipePaint("a", -1));
        Assert.Same(draft, draft.MoveRecipePaint("b", 1));
        Assert.Equal(["b", "a"], draft.MoveRecipePaint("a", 1).Recipe.Select(p => p.PaintId));
    }

    [Fact]
    public void RecipePaintsCantBeDeletedButAMixCan()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var white = Paint(store, "XF-2", "Flat White");
        var mix = Mix(store, "Faded Sky Grey", (white, 3));

        var refused = PaintService.Delete(store, [white]);
        Assert.Equal(["“Tamiya XF-2 Flat White” is in the recipe for Custom Faded Sky Grey. Take it out of those mixes first."], refused);
        Assert.NotNull(store.Paint(white.Id));

        // Deleting the mix deletes its recipe, never the paints in it.
        Assert.Empty(PaintService.Delete(store, [mix]));
        Assert.Empty(store.AllMixParts());
        Assert.NotNull(store.Paint(white.Id));
        Assert.Empty(PaintService.Delete(store, [white]));
    }

    [Fact]
    public void AMixsRecipePaintsAreBoughtForTheMix()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var white = Paint(store, "XF-2", "Flat White", owned: true);
        var grey = Paint(store, "XF-19", "Sky Grey");
        var mix = Mix(store, "Faded Sky Grey", (white, 3), (grey, 1));
        var project = ProjectService.Create(store, new ProjectDraft { Name = "Spitfire" });
        PaintService.AddPaints(store, project.Id, [mix.Id]);

        var list = new ShoppingList(store);
        var item = Assert.Single(list.Groups.SelectMany(g => g.Items));
        Assert.Equal(grey.Id, item.Paint.Id);
        Assert.Equal("for Faded Sky Grey", Assert.Single(item.Uses).UsedFor);
    }

    [Theory]
    [InlineData("Clear", true)]
    [InlineData("Flat clear", true)]
    [InlineData("Clear Red", true)]
    [InlineData("Semi-Gloss Super Clear", true)]
    [InlineData("CLEAR", true)]
    [InlineData("Cléar", true)]
    [InlineData("Unclear", false)]
    [InlineData("Clearcoat", false)]
    [InlineData("", false)]
    public void ClearsAreFoundByTheWordClear(string name, bool clear) => Assert.Equal(clear, PaintClarity.IsClear(name));

    [Fact]
    public void ADecomposedAccentStaysInTheWord() => Assert.True(PaintClarity.IsClear("Cléar Blue"));
}

/// <summary>A project's Paint tab: the guide colour's system and code, and notes (screens.md, "Paint tab").</summary>
public class ProjectPaintRowTests
{
    [Fact]
    public void NoSystemClearsTheCodeAndNotesAreTidied()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "F-16C" });
        var row = PaintService.AddColour(store, project.Id, new PaintStandard(PaintStandardSystem.Fs595, " 36270 "), " upper surfaces ");
        Assert.Equal(("fs595", "36270", "upper surfaces"), (row.StandardSystem, row.Standard, row.UsedFor));

        PaintService.SetNotes(store, row, " lightened 20%\r\nfor fading ");
        Assert.Equal("lightened 20%\nfor fading", store.ProjectPaints(project.Id).Single().Notes);

        PaintService.SetStandardSystem(store, row, "");
        var saved = store.ProjectPaints(project.Id).Single();
        Assert.Equal(("", null), (saved.StandardSystem, saved.Standard));
        // With no system, a code isn't kept.
        PaintService.SetCodeAndUse(store, saved, "36270", "upper surfaces");
        Assert.Null(store.ProjectPaints(project.Id).Single().Standard);
    }
}

/// <summary>Import from Scalemates… on the Paints screen (behaviour.md, "Scalemates import"; ScalematesPaintImportTests.swift).</summary>
public class ScalematesPaintTests
{
    private static readonly string Fixtures = SpecFixtures.Folder;

    private static string Stash => File.ReadAllText(Path.Combine(Fixtures, "paints-scalemates.csv"));

    private static Paint Find(LibraryStore store, string brand, string code) =>
        store.Paints().Single(p => p.Brand == brand && p.Code == code && !p.IsMix);

    [Fact]
    public void AddsNewPaintsOwnedWithTheTableAndNotes()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var summary = ScalematesPaintImporter.Import(store, Stash);
        Assert.Equal((6, 1, 0), (summary.Added, summary.SameRows, summary.MarkedOwned));
        Assert.Equal(["Example Paint Co"], summary.UnknownCompanies);
        Assert.Single(summary.Problems);
        var owned = store.OwnedPaintIds();
        Assert.All(store.Paints(), p => Assert.Contains(p.Id, owned));

        var sky = Find(store, "Vallejo", "71.901");
        Assert.Equal(("Model Air", "Example Sky", "#A8B59B", "waterAcrylic"), (sky.Range, sky.Name, sky.SwatchHex, sky.Type));
        Assert.Equal("Matt · 17ml · https://www.scalemates.com/colors/example-sky--900001", sky.Notes);
    }

    [Fact]
    public void MapsTypesAndRanges()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        ScalematesPaintImporter.Import(store, Stash);
        Assert.Equal("enamel", Find(store, "AK Interactive", "AK 901").Type);
        Assert.Equal(("lacquer", "Real Color"), (Find(store, "AK Interactive", "RC901").Type, Find(store, "AK Interactive", "RC901").Range));
        Assert.Equal("polyurethane", Find(store, "Vallejo", "70.901").Type);
        Assert.Equal("pigment", Find(store, "Ammo by Mig", "A.MIG-9901").Type);
        var blue = Find(store, "Example Paint Co", "EX 1");
        Assert.Equal(("", ""), (blue.Range, blue.Type));
    }

    [Fact]
    public void NoColourAndBadColourLeaveNoSwatch()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var summary = ScalematesPaintImporter.Import(store, Stash);
        Assert.Null(Find(store, "AK Interactive", "AK 901").SwatchHex);
        Assert.Null(Find(store, "Example Paint Co", "EX 1").SwatchHex);
        Assert.Equal("Line 8: “10;20;300” isn't a colour like 168;181;155, so no swatch was set.", summary.Problems[0]);
    }

    [Fact]
    public void AnExistingPaintIsOnlyMarkedOwned()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var existing = PaintService.Create(store, new PaintDraft { Brand = "Vallejo", Range = "Air", Code = "71901", Name = "My Sky", Notes = "Mine" });
        var summary = ScalematesPaintImporter.Import(store, Stash);
        Assert.Equal((1, 5), (summary.MarkedOwned, summary.Added));
        var after = store.Paint(existing.Id)!;
        Assert.Contains(existing.Id, store.OwnedPaintIds());
        Assert.Equal(("My Sky", "Mine", "Air", null, ""), (after.Name, after.Notes, after.Range, after.SwatchHex, after.Type));
    }

    [Fact]
    public void CountsAlreadyOwnedAndKeepsPaintsNotInTheFile()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        PaintService.Create(store, new PaintDraft { Brand = "Vallejo", Range = "Model Air", Code = "71.901", IsOwned = true });
        var other = PaintService.Create(store, new PaintDraft { Brand = "Tamiya", Code = "XF-1", IsOwned = true });
        var summary = ScalematesPaintImporter.Import(store, Stash);
        Assert.Equal((1, 0), (summary.AlreadyOwned, summary.MarkedOwned));
        Assert.Contains(other.Id, store.OwnedPaintIds());
    }

    [Fact]
    public void SeveralMatchesPickTheTablesRangeOrNone()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var air = PaintService.Create(store, new PaintDraft { Brand = "Vallejo", Range = "Model Air", Code = "71.901" });
        var color = PaintService.Create(store, new PaintDraft { Brand = "Vallejo", Range = "Model Color", Code = "71.901" });
        var one = PaintService.Create(store, new PaintDraft { Brand = "AK Interactive", Range = "Enamel", Code = "AK 901" });
        var two = PaintService.Create(store, new PaintDraft { Brand = "AK Interactive", Range = "Wash", Code = "AK901" });
        var summary = ScalematesPaintImporter.Import(store, Stash);
        var owned = store.OwnedPaintIds();
        Assert.Contains(air.Id, owned);
        Assert.DoesNotContain(color.Id, owned);
        Assert.DoesNotContain(one.Id, owned);
        Assert.DoesNotContain(two.Id, owned);
        Assert.Contains(summary.Problems, p => p.Contains("AK 901", StringComparison.Ordinal) && p.EndsWith("so none of them was marked owned.", StringComparison.Ordinal));
    }

    [Fact]
    public void MixesAreNeverMatched()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var mix = PaintService.Create(store, new PaintDraft { Brand = "Vallejo", Code = "71.901", Name = "Sky Mix", IsMix = true });
        var summary = ScalematesPaintImporter.Import(store, Stash);
        Assert.Equal(6, summary.Added);
        Assert.DoesNotContain(mix.Id, store.OwnedPaintIds());
    }

    [Fact]
    public void SkipsRowsWithoutCompanyOrNumberAndRefusesOtherFiles()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var summary = ScalematesPaintImporter.Import(store, "Color,Company,Number\nSky,,71.901\n");
        Assert.Equal(0, summary.Added);
        Assert.Equal(["Line 2: a paint needs a company and a number, so it was skipped."], summary.Problems);
        Assert.Equal("This isn't a Scalemates paint stash export.", Assert.Throws<PaintImportException>(() => ScalematesPaintImporter.Import(store, "Brand,Code\nTamiya,XF-1\n")).Message);
        Assert.Equal("The file has no paints to import.", Assert.Throws<PaintImportException>(() => ScalematesPaintImporter.Import(store, "Color,Company,Number\n")).Message);
    }

    [Fact]
    public void TheSummaryReadsAsOnTheMac()
    {
        var summary = new ScalematesPaintSummary { MarkedOwned = 3, Added = 1, SameRows = 2, UnknownCompanies = ["Example Paint Co"] };
        Assert.Equal("""
            3 paints marked owned.
            1 new paint added, marked owned.
            2 rows repeated a paint earlier in the file.
            BenchBook doesn't know how to split Example Paint Co into brand and range, so the whole name is the brand.
            """.ReplaceLineEndings("\n"), summary.Message);
    }
}
