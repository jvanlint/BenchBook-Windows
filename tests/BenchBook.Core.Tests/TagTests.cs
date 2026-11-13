using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Projects;
using BenchBook.Core.Store;

namespace BenchBook.Core.Tests;

public class TagTests
{
    private static (LibraryStore Store, string A, string B, string C) Setup(TestLibrary library)
    {
        var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "P" });
        string Note(string tags) => ReferenceService.CreateNote(store, project.Id, "n", "t", tags).Id;
        return (store, Note("Cockpit, decals"), Note("cockpit"), Note("Wheel Well"));
    }

    private static List<string> TagsOf(LibraryStore store, string referenceId) =>
        store.ReferenceEntries([referenceId]).Single().Tags.Select(t => t.Name).ToList();

    [Fact]
    public void TagsSortByUseThenName()
    {
        using var library = new TestLibrary();
        var (store, _, _, _) = Setup(library);
        using (store)
            Assert.Equal(["Cockpit", "decals", "Wheel Well"], store.TagsByUsage().Select(u => u.Tag.Name));
    }

    [Fact]
    public void RenamingToAnotherTagsNameMergesThemWithTheNewSpelling()
    {
        using var library = new TestLibrary();
        var (store, a, b, c) = Setup(library);
        using (store)
        {
            var decals = store.TagNamed("decals")!;
            store.SetTagColor([decals.Id], "teal");
            var wheel = store.TagNamed("wheel well")!;
            var kept = store.RenameTag(wheel.Id, "  DECALS ");

            Assert.Equal(decals.Id, kept.Id);
            Assert.Equal("DECALS", store.Tag(decals.Id)!.Name);
            Assert.Equal("teal", store.Tag(decals.Id)!.Color);
            Assert.Null(store.Tag(wheel.Id));
            Assert.Equal(["Cockpit", "DECALS"], TagsOf(store, a));
            Assert.Equal(["DECALS"], TagsOf(store, c));
            Assert.Equal(2, store.TagsByUsage().Count);
        }
    }

    [Fact]
    public void RenamingChangesOnlyTheSpellingWhenTheNameIsFree()
    {
        using var library = new TestLibrary();
        var (store, _, _, _) = Setup(library);
        using (store)
        {
            var tag = store.TagNamed("wheel well")!;
            var renamed = store.RenameTag(tag.Id, "Main  Gear");
            Assert.Equal(tag.Id, renamed.Id);
            Assert.Equal(("Main Gear", "main gear"), (renamed.Name, renamed.NormalizedName));
            Assert.Throws<TagException>(() => store.RenameTag(tag.Id, " , "));
        }
    }

    [Fact]
    public void MergingTakesTheFirstColourWhenTheTargetHasNone()
    {
        using var library = new TestLibrary();
        var (store, a, b, c) = Setup(library);
        using (store)
        {
            var cockpit = store.TagNamed("cockpit")!;
            var decals = store.TagNamed("decals")!;
            var wheel = store.TagNamed("wheel well")!;
            store.SetTagColor([wheel.Id], "red");
            store.MergeTags([decals.Id, wheel.Id], cockpit.Id);
            Assert.Equal("red", store.Tag(cockpit.Id)!.Color);
            Assert.Equal(["Cockpit"], TagsOf(store, a));
            Assert.Equal(["Cockpit"], TagsOf(store, c));
            Assert.Single(store.TagsByUsage());
        }
    }

    [Fact]
    public void DeletingATagKeepsItsReferences()
    {
        using var library = new TestLibrary();
        var (store, a, b, _) = Setup(library);
        using (store)
        {
            store.DeleteTags([store.TagNamed("cockpit")!.Id]);
            Assert.Equal(["decals"], TagsOf(store, a));
            Assert.Empty(TagsOf(store, b));
            Assert.NotNull(store.Reference(b));
        }
    }

    [Fact]
    public void TaggingManyReferencesAddsOnlyWhereMissing()
    {
        using var library = new TestLibrary();
        var (store, a, b, c) = Setup(library);
        using (store)
        {
            store.AddTags("Cockpit, new one", [a, b, c]);
            Assert.Equal(["Cockpit", "decals", "new one"], TagsOf(store, a));
            Assert.Equal(["Cockpit", "new one", "Wheel Well"], TagsOf(store, c));
            store.RemoveTag(store.TagNamed("new one")!.Id, [a, c]);
            Assert.Equal(["Cockpit", "new one"], TagsOf(store, b));
            Assert.Equal(["Cockpit", "Wheel Well"], TagsOf(store, c));
        }
    }

    [Fact]
    public void RemoveUnusedTagsDeletesOnlyTagsWithNoLinks()
    {
        using var library = new TestLibrary();
        var (store, _, _, c) = Setup(library);
        using (store)
        {
            store.CreateTag("Spare");
            Assert.Equal(store.TagNamed("cockpit")!.Id, store.CreateTag("COCKPIT").Id);
            ReferenceService.Delete(store, library.Location, [c]);
            Assert.Equal(2, store.RemoveUnusedTags());
            Assert.Equal(["Cockpit", "decals"], store.TagsByUsage().Select(u => u.Tag.Name));
        }
    }
}
