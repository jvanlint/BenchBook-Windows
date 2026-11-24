using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using BenchBook.Core.Projects;
using BenchBook.Core.Store;

namespace BenchBook.Core.Tests;

/// <summary>When a project was last updated (behaviour.md, stored).</summary>
public class ProjectActivityTests
{
    private static readonly DateTimeOffset Old = new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static Project MakeOld(LibraryStore store, string name)
    {
        var project = ProjectService.Create(store, new ProjectDraft { Name = name });
        project.UpdatedAt = Old;
        store.UpdateProject(project);
        return project;
    }

    private static void SetOld(LibraryStore store, params Project[] projects)
    {
        foreach (var project in projects)
        {
            var stored = store.Project(project.Id)!;
            stored.UpdatedAt = Old;
            store.UpdateProject(stored);
        }
    }

    private static bool Touched(LibraryStore store, Project project) => store.Project(project.Id)!.UpdatedAt > Old;

    [Fact]
    public void EditingAnythingInAProjectUpdatesIt()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = MakeOld(store, "F-16C");

        var note = ReferenceService.CreateNote(store, project.Id, "Wheels", "Gloss white.", "");
        Assert.True(Touched(store, project));

        SetOld(store, project);
        ReferenceService.UpdateNote(store, store.Reference(note.Id)!, "Wheels", "Gloss white, then a wash.", "");
        Assert.True(Touched(store, project));

        SetOld(store, project);
        store.AddTags("cockpit", [note.Id]);
        Assert.True(Touched(store, project));

        SetOld(store, project);
        var row = PaintService.AddColour(store, project.Id, "FS 36270", "Upper surfaces");
        Assert.True(Touched(store, project));

        SetOld(store, project);
        store.DeleteProjectPaints([row.Id]);
        Assert.True(Touched(store, project));

        SetOld(store, project);
        ReferenceService.Delete(store, library.Location, [note.Id]);
        Assert.True(Touched(store, project));
    }

    [Fact]
    public void MovingAReferenceUpdatesBothProjects()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var from = MakeOld(store, "F-16C");
        var note = ReferenceService.CreateNote(store, from.Id, "Wheels", "Gloss white.", "");
        var to = MakeOld(store, "Spitfire");
        SetOld(store, from);

        ReferenceTransfer.Transfer(store, library.Location, [note.Id], to.Id, TransferMode.Move);
        Assert.True(Touched(store, from));
        Assert.True(Touched(store, to));
    }

    [Fact]
    public void BackgroundWorkAndTagChangesLeaveTheDateAlone()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = MakeOld(store, "F-16C");
        var note = ReferenceService.CreateNote(store, project.Id, "Wheels", "Gloss white.", "cockpit");
        SetOld(store, project);

        // As fetching link details does.
        var reference = store.Reference(note.Id)!;
        reference.LinkSiteName = "Fetched";
        store.UpdateReference(reference, userEdit: false);

        var tag = store.TagNamed("cockpit")!;
        store.RenameTag(tag.Id, "Cockpit Detail");
        store.SetTagColor([tag.Id], "red");
        store.DeleteTags([tag.Id]);
        Assert.False(Touched(store, project));
    }

    [Fact]
    public void ANewProjectsUpdatedDateIsItsCreatedDate()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "F-16C" });
        var stored = store.Project(project.Id)!;
        Assert.Equal(stored.CreatedAt, stored.UpdatedAt);
    }

    [Fact]
    public void TheSidebarSortsByNameDateCreatedOrLastUpdated()
    {
        Project Make(string name, int created, int updated) => new()
        {
            Id = Ids.New(), Name = name, Subject = "",
            CreatedAt = Old.AddDays(created), UpdatedAt = Old.AddDays(updated),
        };
        var projects = new[] { Make("Spitfire 10", 1, 5), Make("spitfire 9", 3, 5), Make("Avenger", 2, 1) };

        Assert.Equal(["Avenger", "spitfire 9", "Spitfire 10"], ProjectSort.Sorted(projects, ProjectSort.Name).Select(p => p.Name));
        Assert.Equal(["spitfire 9", "Avenger", "Spitfire 10"], ProjectSort.Sorted(projects, ProjectSort.DateCreated).Select(p => p.Name));
        // The same date falls back to name.
        Assert.Equal(["spitfire 9", "Spitfire 10", "Avenger"], ProjectSort.Sorted(projects, ProjectSort.LastUpdated).Select(p => p.Name));
        Assert.Equal(ProjectSort.Sorted(projects, ProjectSort.Name), ProjectSort.Sorted(projects, "unknown"));
    }
}
