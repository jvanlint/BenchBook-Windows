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
            Name = "  Spitfire Mk IX ", Manufacturer = " Eduard", Scale = "1:48 ", KitNumber = "   ",
            Notes = "\n  Weathering: light.\n", Status = ProjectStatus.Paused,
        });

        var stored = store.Project(project.Id)!;
        Assert.Equal("Spitfire Mk IX", stored.Name);
        Assert.Equal("Eduard", stored.Manufacturer);
        Assert.Equal("", stored.Subject);
        Assert.Equal("1:48", stored.Scale);
        Assert.Null(stored.KitNumber);
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
            Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, Name = "Old", Manufacturer = "", Subject = "", Scale = "",
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
        Assert.Equal(pdf.AssetId, Assert.Single(after.Assets).Id);
        Assert.Single(after.Paints);
        Assert.Single(after.OwnedPaints);
        Assert.Single(after.Tags);

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
        Assert.Equal(new ProjectContents(4, 1, 1, 2, 1), contents);
        Assert.Equal(
            "Its 4 references, 1 image note, 1 PDF markup, 2 paints from its paint list and 1 aftermarket part will be deleted. Paints in your catalogue and your tags are kept. This can't be undone.",
            ProjectService.DeletionMessage(contents));
        Assert.Equal(
            "Its 12 references and 2 aftermarket parts will be deleted. Paints in your catalogue and your tags are kept. This can't be undone.",
            ProjectService.DeletionMessage(new ProjectContents(12, 0, 0, 0, 2)));
        Assert.Equal(
            "The project is empty. Paints in your catalogue and your tags are kept. This can't be undone.",
            ProjectService.DeletionMessage(new ProjectContents(0, 0, 0, 0, 0)));
    }
}
