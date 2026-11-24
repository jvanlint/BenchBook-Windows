using BenchBook.Core.Kits;
using BenchBook.Core.Model;
using BenchBook.Core.Projects;
using BenchBook.Core.Store;

namespace BenchBook.Core.Tests;

public class KitTests
{
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "BenchBook", "spec", "fixtures"));

    private static Kit Make(string brand, string name, string? scale = null, string? number = null, string? link = null, bool owned = true, string status = KitStatus.Bnib) => new()
    {
        Id = Ids.New(), CreatedAt = IsoDate.Now(), UpdatedAt = IsoDate.Now(),
        Brand = brand, Name = name, Scale = scale, Number = number, Link = link, Owned = owned, Status = status, ForSale = false,
    };

    [Theory]
    [InlineData("25", "25")]
    [InlineData("25.00", "25")]
    [InlineData("25,00", "25")]
    [InlineData("$18.50", "18.5")]
    [InlineData("1,250.00", "1250")]
    [InlineData("1.250,00", "1250")]
    [InlineData("1,250", "1250")]
    [InlineData("1,250,000", "1250000")]
    [InlineData("0.250", "0.25")]
    [InlineData("AU$ 12", "12")]
    public void PricesReadAsTheMacReadsThem(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), KitPrice.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("-5")]
    [InlineData("free")]
    public void SomePricesAreUnreadable(string text) => Assert.Null(KitPrice.Parse(text));

    [Fact]
    public void PricesAreWrittenPlain() => Assert.Equal("18.50", KitPrice.Plain(18.5m));

    [Fact]
    public void TheSameKitIsFoundByScalematesAddressOrBrandNumberAndScale()
    {
        Assert.Equal("scalemates.com/kits/italeri-1234--100", KitIdentity.ScalematesKey("https://www.scalemates.com/kits/Italeri-1234--100/?x=1"));
        Assert.Null(KitIdentity.ScalematesKey("https://example.com/kits/1"));

        var cobra = Make("Italeri", "Super Cobra", "1:72", "1234");
        Assert.True(KitIdentity.IsSameKit("ital eri", " 1234", "1: 72", null, cobra));
        Assert.False(KitIdentity.IsSameKit("Italeri", "1234", "1:48", null, cobra));
        // Without a number, brand and scale alone aren't enough.
        Assert.False(KitIdentity.IsSameKit(Make("Italeri", "A", "1:72"), Make("Italeri", "B", "1:72")));
        Assert.True(KitIdentity.IsSameKit(
            Make("A", "x", link: "https://www.scalemates.com/kits/a--1"),
            Make("B", "y", link: "https://scalemates.com/kits/A--1/")));
    }

    [Fact]
    public void KitStatusFollowsTheProjectForwardOnly()
    {
        var now = IsoDate.Now();
        var bnib = Make("A", "a");
        Assert.True(KitRules.Follow(bnib, ProjectStatus.InProgress, now));
        Assert.Equal(KitStatus.Wip, bnib.Status);

        var built = Make("A", "a", status: KitStatus.Built);
        Assert.False(KitRules.Follow(built, ProjectStatus.InProgress, now));

        var unknown = Make("A", "a", status: "mint");
        Assert.False(KitRules.Follow(unknown, ProjectStatus.InProgress, now));
        Assert.True(KitRules.Follow(unknown, ProjectStatus.Completed, now));
        Assert.Equal(KitStatus.Built, unknown.Status);

        var wanted = Make("A", "a", owned: false);
        Assert.False(KitRules.Follow(wanted, ProjectStatus.Completed, now));
        Assert.False(KitRules.Follow(Make("A", "a"), ProjectStatus.Paused, now));
    }

    [Fact]
    public void KitsInAProjectFollowItsStatusAndMovingOneUpdatesBothProjects()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var cobra = ProjectService.Create(store, new ProjectDraft { Name = "Cobra", Status = ProjectStatus.Researching });
        var kit = KitService.Create(store, new KitDraft { Brand = "Italeri", Name = "Super Cobra", Scale = "1:72", Number = "1234" }, cobra);
        Assert.Equal(cobra.Id, store.Kit(kit.Id)!.ProjectId);
        Assert.Equal(KitStatus.Bnib, store.Kit(kit.Id)!.Status);

        ProjectService.SetStatus(store, store.Project(cobra.Id)!, ProjectStatus.InProgress);
        Assert.Equal(KitStatus.Wip, store.Kit(kit.Id)!.Status);
        ProjectService.SetStatus(store, store.Project(cobra.Id)!, ProjectStatus.Completed);
        Assert.Equal(KitStatus.Built, store.Kit(kit.Id)!.Status);
        // Out of Completed, the kit stays Built.
        ProjectService.SetStatus(store, store.Project(cobra.Id)!, ProjectStatus.Paused);
        Assert.Equal(KitStatus.Built, store.Kit(kit.Id)!.Status);

        var old = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var spitfire = ProjectService.Create(store, new ProjectDraft { Name = "Spitfire" });
        foreach (var id in new[] { cobra.Id, spitfire.Id })
        {
            var p = store.Project(id)!;
            p.UpdatedAt = old;
            store.UpdateProject(p);
        }
        KitService.Add(store, [store.Kit(kit.Id)!], store.Project(spitfire.Id)!);
        Assert.Equal(spitfire.Id, store.Kit(kit.Id)!.ProjectId);
        Assert.True(store.Project(cobra.Id)!.UpdatedAt > old);
        Assert.True(store.Project(spitfire.Id)!.UpdatedAt > old);

        KitService.Remove(store, [store.Kit(kit.Id)!]);
        Assert.Null(store.Kit(kit.Id)!.ProjectId);
        Assert.Equal(KitStatus.Built, store.Kit(kit.Id)!.Status);
    }

    [Fact]
    public void EditingAKitLeavesItsProjectAlone()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "Cobra" });
        var kit = KitService.Create(store, new KitDraft { Brand = "Italeri", Name = "Super Cobra" }, project);
        var old = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var stored = store.Project(project.Id)!;
        stored.UpdatedAt = old;
        store.UpdateProject(stored);

        KitService.Update(store, kit, KitDraft.From(kit) with { Notes = "Spare decals" });
        KitService.SetStatus(store, [kit], KitStatus.Opened);
        Assert.Equal(old, store.Project(project.Id)!.UpdatedAt);
    }

    [Fact]
    public void ANewProjectCanStartWithKits()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var kit = KitService.Create(store, new KitDraft { Brand = "Italeri", Name = "Super Cobra" });
        var project = ProjectService.Create(store, new ProjectDraft { Name = "Cobra", Status = ProjectStatus.InProgress }, [kit]);
        var stored = store.Kit(kit.Id)!;
        Assert.Equal(project.Id, stored.ProjectId);
        Assert.Equal(KitStatus.Wip, stored.Status);
        Assert.Equal(store.Project(project.Id)!.CreatedAt, store.Project(project.Id)!.UpdatedAt);
    }

    [Fact]
    public void AnotherCopyIsOwnedBnibWithoutPersonalDetails()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var kit = KitService.Create(store, new KitDraft
        {
            Brand = "Italeri", Name = "Super Cobra", Scale = "1:72", Number = "1234", Link = "scalemates.com/kits/italeri-1234--100",
            Owned = false, Stars = 4, PricePaid = "18.50", ForSale = true, SalePrice = "20", Notes = "Spare decals",
        });
        var copy = KitService.AddCopy(store, kit);
        var stored = store.Kit(copy.Id)!;
        Assert.Equal(("Italeri", "Super Cobra", "1:72", "1234"), (stored.Brand, stored.Name, stored.Scale, stored.Number));
        Assert.Equal("https://scalemates.com/kits/italeri-1234--100", stored.Link);
        Assert.True(stored.Owned);
        Assert.Equal(KitStatus.Bnib, stored.Status);
        Assert.Null(stored.Rating);
        Assert.Null(stored.PricePaid);
        Assert.False(stored.ForSale);
        Assert.Null(stored.Notes);

        var copies = KitService.Copies(store.Kits(), KitDraft.From(kit));
        Assert.Equal(2, copies.Count);
        Assert.Equal("You already have 2 of this kit. This adds another.", KitService.CopiesMessage(2, editing: false));
        Assert.Equal("You have 1 more of this kit.", KitService.CopiesMessage(KitService.Copies(store.Kits(), KitDraft.From(kit), kit).Count, editing: true));
    }

    [Fact]
    public void AnUnknownStatusIsKeptUntilTheUserPicksOne()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var kit = KitService.Create(store, new KitDraft { Brand = "Italeri", Name = "Super Cobra" });
        kit.Status = "mint";
        store.SaveKits([kit]);
        Assert.Equal("BNIB", KitText.StatusName(kit.Status));

        KitService.Update(store, kit, KitDraft.From(kit) with { Notes = "Box damaged" });
        Assert.Equal("mint", store.Kit(kit.Id)!.Status);
        KitService.Update(store, kit, KitDraft.From(kit) with { Status = KitStatus.Opened });
        Assert.Equal(KitStatus.Opened, store.Kit(kit.Id)!.Status);
    }

    [Fact]
    public void TheScalematesFixtureImportsOnceAndAgainAddsNothing()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var text = File.ReadAllText(Path.Combine(Fixtures, "kits-scalemates.csv"));

        var first = KitCsvImporter.Import(store, text);
        Assert.Equal(4, first.Added);
        Assert.Equal(0, first.Updated);
        Assert.Equal(3, first.SkippedCount);
        Assert.Equal([("Markings (Decals)", 2), ("Detail and Conversion sets", 1)], first.Skipped);
        Assert.Equal("Added 4 Kits", first.Title);

        var kits = store.Kits();
        var cobras = kits.Where(k => k.Number == "1234").ToList();
        Assert.Equal(2, cobras.Count);
        // " 0" means not entered; "18,50" is 18.50.
        Assert.Equal([18.50m], cobras.Select(k => k.PricePaid).OfType<decimal>());
        Assert.Equal("F-16C \"Viper\" Block 50", kits.Single(k => k.Number == "61098").Name);

        var again = KitCsvImporter.Import(store, text);
        Assert.Equal(0, again.Added);
        Assert.Equal(4, again.Unchanged);
        Assert.Equal("No Kits Added", again.Title);
        Assert.Equal(4, store.Kits().Count);
    }

    [Fact]
    public void AScalematesRowOwnsAWantedEntryBeforeAddingOne()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var wanted = KitService.Create(store, new KitDraft { Brand = "Italeri", Name = "Super Cobra", Scale = "1:72", Number = "1234", Owned = false });

        var summary = KitCsvImporter.Import(store, "Brand,Title,Scale,Number,Type\r\nItaleri,AH-1W Super Cobra,1:72,1234,Static model kits\r\n");
        Assert.Equal(0, summary.Added);
        Assert.Equal(1, summary.Updated);
        var stored = store.Kit(wanted.Id)!;
        Assert.True(stored.Owned);
        Assert.Equal("AH-1W Super Cobra", stored.Name);
    }

    [Fact]
    public void BenchBookExportsReadBackUnchanged()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        KitService.Create(store, new KitDraft
        {
            Brand = "Italeri", Name = "Super Cobra, \"AH-1W\"", Scale = "1:72", Number = "1234", Stars = 3,
            PricePaid = "18.5", ForSale = true, SalePrice = "25", Notes = "Spare decals", Link = "https://www.scalemates.com/kits/x--1",
        });
        KitService.Create(store, new KitDraft { Brand = "Airfix", Name = "Spitfire", Owned = false, Status = KitStatus.Opened });
        var kit = store.Kits().Single(k => k.Brand == "Airfix");
        kit.Status = "mint";
        store.SaveKits([kit]);

        var csv = KitCsvExporter.Csv(store.Kits(), includeNotes: true);
        var lines = csv.Split("\r\n");
        Assert.Equal("BenchBook ID,Brand,Title,Scale,Number,Type,Price,Link,Owned,Status,Rating,For Sale,Sale Price,Notes", lines[0]);
        Assert.StartsWith($"{kit.Id},Airfix,Spitfire,,,Static model kits,,,,mint,,,,", lines[1]);
        Assert.EndsWith(",Italeri,\"Super Cobra, \"\"AH-1W\"\"\",1:72,1234,Static model kits,18.50,https://www.scalemates.com/kits/x--1,Yes,BNIB,3,Yes,25.00,Spare decals", lines[2]);

        var summary = KitCsvImporter.Import(store, csv);
        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Updated);
        Assert.Equal(2, summary.Unchanged);
        // The unknown status is reported, and kept.
        Assert.Single(summary.Problems);
        Assert.Equal("mint", store.Kit(kit.Id)!.Status);
    }

    [Fact]
    public void BenchBookRowsUpdateByIdAndAddTheRest()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var kit = KitService.Create(store, new KitDraft { Brand = "Italeri", Name = "Super Cobra", Number = "1234", PricePaid = "10" });
        var newId = Ids.New();
        var csv = "BenchBook ID,Brand,Title,Number,Price,Owned,Status,Rating\r\n"
            + $"{kit.Id.ToLowerInvariant()},Italeri,Super Cobra,,,no,wip,6\r\n"
            + $"{newId},Airfix,Spitfire,,,,,\r\n";
        var summary = KitCsvImporter.Import(store, csv);
        Assert.Equal("Added 1 Kit and Updated 1", summary.Title);
        var stored = store.Kit(kit.Id)!;
        Assert.False(stored.Owned);
        Assert.Equal(KitStatus.Wip, stored.Status);
        // Empty number and price never clear one.
        Assert.Equal("1234", stored.Number);
        Assert.Equal(10m, stored.PricePaid);
        Assert.Equal(["Line 2: “6” isn't a rating from 1 to 5."], summary.Problems);
        Assert.NotNull(store.Kit(newId));
        Assert.False(store.Kit(newId)!.Owned);
    }

    [Fact]
    public void DeletingAKitRemovesItsPictureUnlessACopyUsesIt()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var (snapshot, files) = Sample.Make();
        store.InsertSnapshot(snapshot);
        var withArt = store.Kits().Single(k => k.PictureAssetId is not null);
        var copy = KitService.AddCopy(store, withArt);
        KitService.Delete(store, library.Location, [withArt]);
        Assert.NotNull(store.Asset(copy.PictureAssetId!));
        KitService.Delete(store, library.Location, [store.Kit(copy.Id)!]);
        Assert.Null(store.Asset(copy.PictureAssetId!));
    }

    [Fact]
    public void TheDeletionMessageNamesTheProjects()
    {
        var cobra = new Project { Id = Ids.New(), CreatedAt = IsoDate.Now(), UpdatedAt = IsoDate.Now(), Name = "Cobra", Subject = "" };
        var projects = new Dictionary<string, Project>(Ids.Comparer) { [cobra.Id] = cobra };
        var kit = Make("Italeri", "Super Cobra", "1:72", "1234");
        kit.ProjectId = cobra.Id;
        Assert.Equal(("Delete “Italeri 1:72 Super Cobra (1234)”?", "It's used by the project “Cobra”. This removes the kit and its picture from BenchBook. This can't be undone."),
            KitService.DeletionMessage([kit], projects));
        Assert.Equal("Delete 2 Kits?", KitService.DeletionMessage([kit, Make("A", "b")], projects).Title);
    }

    [Fact]
    public void FiltersCombine()
    {
        var project = new Project { Id = Ids.New(), CreatedAt = IsoDate.Now(), UpdatedAt = IsoDate.Now(), Name = "Cobra", Subject = "", Status = ProjectStatus.InProgress };
        var projects = new Dictionary<string, Project>(Ids.Comparer) { [project.Id] = project };
        var wanted = Make("Italeri", "a", "1:72", owned: false);
        wanted.ProjectId = project.Id;
        var stash = Make("Airfix", "b", "1:48", status: KitStatus.Opened);

        Assert.True(new KitFilter { Ownership = KitFilter.Needed }.Includes(wanted, projects));
        Assert.False(new KitFilter { Ownership = KitFilter.Needed }.Includes(stash, projects));
        Assert.True(new KitFilter { Ownership = KitFilter.Unallocated }.Includes(stash, projects));
        Assert.True(new KitFilter { Brand = "airfix", Status = KitStatus.Opened }.Includes(stash, projects));
        Assert.False(new KitFilter { Status = KitStatus.Bnib }.Includes(wanted, projects));
    }
}

public class ShoppingListKitTests
{
    [Fact]
    public void NeededKitsComeFirstAndMixesListTheirRecipePaints()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var (snapshot, _) = Sample.Make();
        store.InsertSnapshot(snapshot);
        var project = store.Projects().Single();
        var cobra = store.Kits().Single(k => k.Brand == "Italeri");
        cobra.Scale = "1:72";
        cobra.Number = "1234";
        store.SaveKits([cobra]);
        KitService.Add(store, [cobra], project);

        var list = new BenchBook.Core.Paints.ShoppingList(store);
        Assert.Equal("Italeri Super Cobra · 1:72 · 1234", Assert.Single(list.Kits).Title);
        // XF-54 is owned; XF-2 is in the recipe of the chosen mix. The mix itself is never listed.
        var item = Assert.Single(list.Items);
        Assert.Equal("XF-2", item.Paint.Code);
        Assert.Equal("F-16CJ 50th Anniversary (Faded panels, for Faded Sea Grey)", item.UsesLine);
        Assert.Equal(2, list.BadgeCount);
        Assert.Equal("1 kit · 1 paint for 1 project · 1 colour without paint", list.Summary);
        var text = list.Text("13 November 2026");
        Assert.StartsWith("Shopping List\n13 November 2026 · 1 kit · 1 paint for 1 project · 1 colour without paint\n\nKits\n☐ Italeri Super Cobra · 1:72 · 1234\n    F-16CJ 50th Anniversary\n", text);

        KitService.MarkBought(store, [store.Kit(cobra.Id)!]);
        var bought = store.Kit(cobra.Id)!;
        Assert.True(bought.Owned);
        Assert.Equal(KitStatus.Bnib, bought.Status);
        Assert.Empty(new BenchBook.Core.Paints.ShoppingList(store).Kits);
    }
}
