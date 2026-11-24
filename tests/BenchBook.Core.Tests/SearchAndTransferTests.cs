using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Projects;
using BenchBook.Core.Search;

namespace BenchBook.Core.Tests;

public class SearchTests
{
    [Fact]
    public void FoldingIgnoresCaseAccentsAndWidth()
    {
        Assert.Equal("decals", SearchField.Fold("DÉCALS"));
        Assert.Equal("f-16", SearchField.Fold("Ｆ-１６"));
        Assert.Equal("f16", SearchField.Of("F-16").Compact);
    }

    [Theory]
    [InlineData("f16", true)]
    [InlineData("F-16CJ", true)]
    [InlineData("xf54", true)]
    [InlineData("tamiya 1:48", true)]
    [InlineData("tamiya spitfire", false)]
    [InlineData("WHEEL", true)]
    [InlineData("quoted", true)]
    [InlineData("check", true)]
    public void EveryWordMustBeFoundSomewhere(string query, bool found)
    {
        using var library = new TestLibrary();
        Sample.Install(library);
        using var store = library.OpenStore();
        var results = new LibrarySearch(store).Search(new SearchQuery(query));
        Assert.Equal(found, !results.IsEmpty);
    }

    [Fact]
    public void ReferencesListWhatMatchedWithLabels()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var results = new LibrarySearch(store).Search(new SearchQuery("check"));
        var hit = Assert.Single(Assert.Single(results.ReferenceGroups).Hits);
        Assert.Equal(sample.References[1].Id, hit.Entry.Reference.Id);
        var match = Assert.Single(hit.Matches);
        Assert.Equal(("Highlight 1", "Check this"), (match.Label, match.Text));
        Assert.Equal(sample.Markups[0].Id, match.MarkupId); // So the result can open the PDF at it.
        Assert.Empty(results.Projects);
    }

    [Fact]
    public void ProjectsAndPaintsAreFoundTooButATagTokenLeavesOnlyReferences()
    {
        using var library = new TestLibrary();
        Sample.Install(library);
        using var store = library.OpenStore();
        var search = new LibrarySearch(store);
        // The project is found by its kit's brand; Tamiya's two paints are found, the Custom mix isn't.
        var results = search.Search(new SearchQuery("tamiya"));
        Assert.Single(results.Projects);
        Assert.Equal(2, results.Paints.Count);
        Assert.Single(search.Search(new SearchQuery("61098")).Projects);

        var tagged = search.Search(new SearchQuery("", ["wheel well"]));
        Assert.Empty(tagged.Projects);
        Assert.Equal("Main Gear and Bay", ReferenceText.FallbackTitle(Assert.Single(Assert.Single(tagged.ReferenceGroups).Hits).Entry));
    }

    [Fact]
    public void TitleMatchesComeFirstThenNewest()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "P" });
        ReferenceService.CreateNote(store, project.Id, "Other", "about canopy masks", "");
        Thread.Sleep(5);
        ReferenceService.CreateNote(store, project.Id, "", "more canopy", "");
        Thread.Sleep(5);
        ReferenceService.CreateNote(store, project.Id, "Canopy", "", "");
        var hits = Assert.Single(new LibrarySearch(store).Search(new SearchQuery("canopy")).ReferenceGroups).Hits;
        Assert.Equal(["Canopy", "more canopy", "Other"], hits.Select(h => ReferenceText.FallbackTitle(h.Entry)));
    }

    [Fact]
    public void ExcerptsStartNearTheMatchAndMarkEveryOccurrence()
    {
        var text = string.Join(" ", Enumerable.Repeat("filler", 30)) + " the Discoloration on the canopy, discoloration again";
        var (excerpt, bold) = SearchSnippet.Excerpt(text, new SearchQuery("discoloration"));
        Assert.StartsWith("…", excerpt);
        Assert.Equal(2, bold.Count);
        Assert.All(bold, b => Assert.Equal("discoloration", excerpt.Substring(b.Start, b.Length).ToLowerInvariant()));
    }
}

public class TransferTests
{
    [Fact]
    public void MovingChangesTheProjectAndKeepsEverything()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var target = ProjectService.Create(store, new ProjectDraft { Name = "Target" });
        var image = sample.References[0];

        var result = ReferenceTransfer.Transfer(store, library.Location, [image.Id], target.Id, TransferMode.Move);
        Assert.Equal("Moved “Main Gear and Bay” to Target.", result.Notice(TransferMode.Move, "Target"));
        var moved = Assert.Single(store.ProjectReferences(target.Id));
        Assert.Equal(image.Id, moved.Reference.Id);
        Assert.Equal(["Wheel Well"], moved.Tags.Select(t => t.Name));
        Assert.Equal(1, moved.MarkupCount);
    }

    [Fact]
    public void CopyingSharesTheFileAndCopiesTagsMomentsMarkupsAndPoster()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var target = ProjectService.Create(store, new ProjectDraft { Name = "Target" });
        var ids = sample.References.Select(r => r.Id).ToList();

        var result = ReferenceTransfer.Transfer(store, library.Location, ids, target.Id, TransferMode.Copy);
        Assert.Equal(4, result.Transferred.Count);
        Assert.Equal("Copied 4 references to Target.", result.Notice(TransferMode.Copy, "Target"));
        Assert.Equal(4, store.ProjectReferences(sample.Projects[0].Id).Count);

        var copies = store.ProjectReferences(target.Id);
        var image = copies.Single(c => c.Reference.Kind == ReferenceKind.Image);
        Assert.NotEqual(sample.References[0].Id, image.Reference.Id);
        Assert.Equal(sample.Assets[0].Id, image.Asset!.Id);
        Assert.Equal(sample.References[0].CapturedAt, image.Reference.CapturedAt);
        Assert.Equal(["Wheel Well"], image.Tags.Select(t => t.Name));
        Assert.Equal(1, image.MarkupCount);
        var video = copies.Single(c => c.Reference.Kind == ReferenceKind.VideoLink);
        Assert.Equal(90, Assert.Single(video.Moments).OffsetSeconds);
        Assert.Equal(Sample.PosterBytes, File.ReadAllBytes(library.Location.PosterPath(video.Reference.Id)));
        Assert.Equal(sample.Assets.Count, store.ReadSnapshot().Assets.Count); // No new files.

        // Copying again finds the files already there.
        var again = ReferenceTransfer.Transfer(store, library.Location, [ids[0], ids[1]], target.Id, TransferMode.Copy);
        Assert.Empty(again.Transferred);
        Assert.Equal("Target already has 2 of these files, so they were left as they are.", again.Notice(TransferMode.Copy, "Target"));
    }
}
