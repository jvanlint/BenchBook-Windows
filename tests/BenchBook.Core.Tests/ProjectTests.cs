using BenchBook.Core.Model;
using BenchBook.Core.Projects;
using BenchBook.Core.Store;

namespace BenchBook.Core.Tests;

public class ProjectTests
{
    [Fact]
    public void CreatingTrimsTheFieldsAndLeavesEmptyOnesOut()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft
        {
            Name = "  Spitfire Mk IX ", Subject = " Mk IXc ", MarkingsScheme = "   ",
            Notes = "\n  Weathering: light.\n", Status = ProjectStatus.Paused,
        });

        var stored = store.Project(project.Id)!;
        Assert.Equal("Spitfire Mk IX", stored.Name);
        Assert.Equal("Mk IXc", stored.Subject);
        Assert.Null(stored.MarkingsScheme);
        Assert.Equal("Weathering: light.", stored.Notes);
        Assert.Equal(ProjectStatus.Paused, stored.Status);
        Assert.Equal(stored.CreatedAt, stored.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void AProjectNeedsAName(string name)
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        Assert.False(new ProjectDraft { Name = name }.HasValidName);
        Assert.Throws<ProjectException>(() => ProjectService.Create(store, new ProjectDraft { Name = name }));
        Assert.Empty(store.Projects());
    }

    [Fact]
    public void SavingAnUnchangedDraftChangesNothing()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "F-16C" });
        var updated = project.UpdatedAt;
        ProjectService.Update(store, project, ProjectDraft.From(project) with { Name = " F-16C " });
        Assert.Equal(updated, store.Project(project.Id)!.UpdatedAt);

        Thread.Sleep(5);
        ProjectService.SetStatus(store, project, ProjectStatus.Completed);
        var stored = store.Project(project.Id)!;
        Assert.Equal(ProjectStatus.Completed, stored.Status);
        Assert.True(stored.UpdatedAt > updated);
    }

    [Fact]
    public void AProjectWithNoStatusIsResearching()
    {
        var draft = ProjectDraft.From(new Project
        {
            Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, Name = "Old", Subject = "",
        });
        Assert.Equal(ProjectStatus.Researching, draft.Status);
    }

    [Fact]
    public void DeletingAProjectRemovesItsFilesButKeepsSharedOnesAndTheCatalogue()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var location = library.Location;
        using var store = library.OpenStore();

        // A second project shares the PDF's asset, as a copied reference would.
        var other = ProjectService.Create(store, new ProjectDraft { Name = "Other" });
        var pdf = sample.References[1];
        store.InsertSnapshot(new LibrarySnapshot
        {
            References = [new Reference { Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, ProjectId = other.Id, Kind = ReferenceKind.Pdf, CapturedAt = Sample.Date, AssetId = pdf.AssetId }],
        });
        Directory.CreateDirectory(location.ThumbnailsFolder);
        File.WriteAllBytes(location.ThumbnailPath(sample.Assets[0].Id), [1]);

        ProjectService.Delete(store, location, sample.Projects[0]);

        Assert.Null(store.Project(sample.Projects[0].Id));
        var after = store.ReadSnapshot();
        Assert.Single(after.References);
        // The PDF another project uses stays, and so does the box art of the kit, which stays in the catalogue.
        var boxArt = sample.Kits[0].PictureAssetId!;
        Assert.Equal(new[] { pdf.AssetId!, boxArt }.Order(), after.Assets.Select(a => a.Id).Order());
        Assert.Equal(sample.Paints.Count, after.Paints.Count);
        Assert.Single(after.OwnedPaints);
        Assert.Single(after.Tags);
        Assert.Empty(after.BuildPhotos);
        Assert.Equal(2, after.Kits.Count);
        Assert.All(after.Kits, k => Assert.Null(k.ProjectId));
        Assert.False(File.Exists(location.FullPath(sample.Assets.Single(a => a.Id == sample.BuildPhotos[0].AssetId).OriginalRelativePath)));

        Assert.False(File.Exists(location.FullPath(sample.Assets[0].OriginalRelativePath)));
        Assert.False(File.Exists(location.ThumbnailPath(sample.Assets[0].Id)));
        Assert.False(File.Exists(location.FullPath(sample.Assets[2].OriginalRelativePath)));
        Assert.True(File.Exists(location.FullPath(sample.Assets[1].OriginalRelativePath)));
        Assert.False(File.Exists(location.PosterPath(sample.References[2].Id)));
    }

    [Fact]
    public void TheDeletionMessageCountsWhatGoes()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var contents = store.ProjectContents(sample.Projects[0].Id);
        Assert.Equal(new ProjectContents(4, 1, 1, 3, 1, 1), contents);
        Assert.Equal(
            "Its 4 references, 1 image note, 1 PDF markup, 3 paints from its paint list, 1 aftermarket part and 1 build photo will be deleted. Paints in your catalogue and your tags are kept. This can't be undone.",
            ProjectService.DeletionMessage(contents));
        Assert.Equal(
            "Its 12 references and 2 aftermarket parts will be deleted. Paints in your catalogue and your tags are kept. This can't be undone.",
            ProjectService.DeletionMessage(new ProjectContents(12, 0, 0, 0, 2)));
        Assert.Equal(
            "The project is empty. Paints in your catalogue and your tags are kept. This can't be undone.",
            ProjectService.DeletionMessage(new ProjectContents(0, 0, 0, 0, 0)));
    }
}
