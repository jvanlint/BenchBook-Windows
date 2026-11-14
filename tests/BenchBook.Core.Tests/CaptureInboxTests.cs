using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Projects;

namespace BenchBook.Core.Tests;

public class CaptureInboxTests
{
    private static CaptureEnvelope Envelope(string projectId, IReadOnlyList<EnvelopeItem> items, IReadOnlyList<string>? tags = null, string? note = null) => new()
    {
        Id = Ids.New(),
        CreatedAt = DateTimeOffset.UtcNow,
        ProjectId = projectId,
        Tags = tags ?? [],
        Note = note,
        Items = items,
    };

    [Fact]
    public void ACommittedEnvelopeIsImportedWithItsTagsAndNoteThenRemoved()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "F-16C" });
        var inbox = new CaptureInbox(library.Location.InboxFolder);

        var staging = inbox.MakeStagingFolder();
        var file = Path.Combine(staging, Ids.New() + ".png");
        File.WriteAllBytes(file, Images.Png(40, 30));
        CapturedItem[] items =
        [
            new CapturedItem.File(file, "Wheel well.png", "https://example.com/wheel.png"),
            new CapturedItem.Link("https://example.com/walkaround", "Walkaround"),
            new CapturedItem.Text("Paint the gear legs white.", "https://example.com/guide", "Guide"),
        ];
        var envelope = Envelope(project.Id, items.Select(CaptureEnvelope.ItemFor).ToList(), ["Wheel Well", "cockpit"], "  From the forum  ");
        // Not in the inbox until it's committed.
        Assert.Empty(inbox.PendingFolders());
        inbox.Commit(envelope, staging);
        Assert.False(Directory.Exists(staging));
        Assert.Single(inbox.PendingFolders());

        var results = inbox.ImportPending(new ReferenceImporter(new DirectStore(store), library.Location, _ => true));

        Assert.Equal(3, results.Count(r => r.Outcome is CaptureOutcome.Added));
        Assert.Empty(inbox.PendingFolders());
        var references = store.ProjectReferences(project.Id).Select(e => e.Reference).ToList();
        Assert.Equal(3, references.Count);
        Assert.All(references, r => Assert.Equal("From the forum", r.Note));
        var image = Assert.Single(references, r => r.Kind == ReferenceKind.Image);
        Assert.Equal("https://example.com/wheel.png", image.OriginalResourceUrl);
        var tags = store.TagsByUsage().Select(t => t.Tag.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(["cockpit", "Wheel Well"], tags);
    }

    [Fact]
    public void AnEnvelopeForAProjectThatsGoneReportsItsItemsAndIsRemoved()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var inbox = new CaptureInbox(library.Location.InboxFolder);
        inbox.Commit(Envelope(Ids.New(), [new EnvelopeItem("link", Url: "https://example.com")]), inbox.MakeStagingFolder());

        var result = Assert.Single(inbox.ImportPending(new ReferenceImporter(new DirectStore(store), library.Location, _ => true)));

        Assert.Equal("The project these items were sent to no longer exists.", Assert.IsType<CaptureOutcome.Failed>(result.Outcome).Message);
        Assert.Empty(inbox.PendingFolders());
    }

    [Fact]
    public void UnreadableAndNewerEnvelopesWaitAndStagingIsIgnored()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "P" });
        var inbox = new CaptureInbox(library.Location.InboxFolder);
        inbox.Commit(Envelope(project.Id, [new EnvelopeItem("text", Text: "later")]) with { Version = CaptureEnvelope.CurrentVersion + 1 }, inbox.MakeStagingFolder());
        var damaged = Path.Combine(inbox.Folder, Ids.New());
        Directory.CreateDirectory(damaged);
        File.WriteAllText(Path.Combine(damaged, CaptureEnvelope.FileName), "{ not json");
        Directory.CreateDirectory(Path.Combine(inbox.Folder, "Not an envelope"));
        inbox.MakeStagingFolder();

        Assert.Empty(inbox.ImportPending(new ReferenceImporter(new DirectStore(store), library.Location, _ => true)));
        Assert.Equal(2, inbox.PendingFolders().Count);
        Assert.Empty(store.ProjectReferences(project.Id));
    }

    [Fact]
    public void FileNamesThatLeaveTheFolderAreDropped()
    {
        var envelope = Envelope(Ids.New(),
        [
            new EnvelopeItem("file", Name: "..\\..\\BenchBook.db"),
            new EnvelopeItem("file", Name: ".."),
            new EnvelopeItem("file", Name: "C:\\Windows\\win.ini"),
            new EnvelopeItem("file", Name: "a/b.png"),
            new EnvelopeItem("file"),
            new EnvelopeItem("something new"),
            new EnvelopeItem("file", Name: "photo.png", OriginalFilename: "Photo.png"),
        ]);
        var items = envelope.CapturedItems(@"C:\Inbox\X");
        Assert.Equal(6, items.Count(i => i is null));
        var file = Assert.IsType<CapturedItem.File>(items[^1]);
        Assert.Equal((@"C:\Inbox\X\photo.png", "Photo.png"), (file.Path, file.OriginalFilename));
    }

    [Fact]
    public void OldStagingFoldersAreRemoved()
    {
        using var library = new TestLibrary();
        var inbox = new CaptureInbox(library.Location.InboxFolder);
        var old = inbox.MakeStagingFolder();
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        var recent = inbox.MakeStagingFolder();
        inbox.RemoveStaleStagingFolders(DateTimeOffset.UtcNow);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void TheShareDirectoryRoundTripsAndChoosesTheActiveProject()
    {
        using var library = new TestLibrary();
        var path = library.Location.ShareProjectsPath;
        Assert.Empty(ShareDirectory.Load(path).Projects);
        var a = new ShareProject(Ids.New(), "F-16C", "Tamiya · 1:48");
        var b = new ShareProject(Ids.New(), "Spitfire", "");
        var directory = new ShareDirectory { Projects = [a, b], ActiveProjectId = b.Id.ToLowerInvariant() };
        directory.Save(path);
        var loaded = ShareDirectory.Load(path);
        Assert.True(loaded.Matches(directory));
        Assert.Equal(b.Id, loaded.InitialProjectId);
        Assert.Equal(a.Id, (loaded with { ActiveProjectId = Ids.New() }).InitialProjectId);
        File.WriteAllText(path, "damaged");
        Assert.Empty(ShareDirectory.Load(path).Projects);
    }

    [Fact]
    public void AQuickLookRecognisesFilesBenchBookSaves()
    {
        using var library = new TestLibrary();
        string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(library.Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        Assert.True(FileInspector.LooksSaveable(Write("x.tmp", Images.Png(2, 2)), "Photo"));
        Assert.True(FileInspector.LooksSaveable(Write("kit", "%PDF-1.7\n"u8.ToArray()), "kit"));
        Assert.True(FileInspector.LooksSaveable(Write("a.tmp", "# Notes"u8.ToArray()), "notes.md"));
        Assert.False(FileInspector.LooksSaveable(Write("list.txt", "plain"u8.ToArray()), "list.txt"));
        Assert.False(FileInspector.LooksSaveable(Path.Combine(library.Root, "missing.png"), "missing.png"));
    }
}
