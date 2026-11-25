using BenchBook.Core.Capture;
using BenchBook.Core.Links;
using BenchBook.Core.Markups;
using BenchBook.Core.Model;
using BenchBook.Core.Projects;

namespace BenchBook.Core.Tests;

/// <summary>Link previews only fetch what's allowed (behaviour.md, "Webpages"; #15).</summary>
public class WebAddressTests
{
    [Theory]
    [InlineData("https://www.example.com/a.jpg", true)]
    [InlineData("http://8.8.8.8/", true)]
    [InlineData("https://[2606:4700::1111]/", true)]
    [InlineData("ftp://example.com/", false)]
    [InlineData("http://localhost/", false)]
    [InlineData("http://router/", false)]
    [InlineData("http://printer.local/", false)]
    [InlineData("http://nas.lan/", false)]
    [InlineData("http://box.home.arpa/", false)]
    [InlineData("http://a.internal/", false)]
    [InlineData("http://x.localhost/", false)]
    [InlineData("http://10.0.0.1/", false)]
    [InlineData("http://172.16.4.2/", false)]
    [InlineData("http://172.32.0.1/", true)]
    [InlineData("http://192.168.1.1/", false)]
    [InlineData("http://192.0.0.8/", false)]
    [InlineData("http://169.254.169.254/latest", false)]
    [InlineData("http://100.64.0.1/", false)]
    [InlineData("http://127.0.0.1/", false)]
    [InlineData("http://0.0.0.0/", false)]
    [InlineData("http://198.18.0.1/", false)]
    [InlineData("http://224.0.0.1/", false)]
    [InlineData("http://[::1]/", false)]
    [InlineData("http://[fd00::1]/", false)]
    [InlineData("http://[fe80::1]/", false)]
    [InlineData("http://[ff02::1]/", false)]
    [InlineData("http://[::ffff:10.0.0.1]/", false)]
    [InlineData("http://[::ffff:8.8.8.8]/", true)]
    [InlineData("http://127.1/", false)]
    [InlineData("http://2130706433/", false)]
    [InlineData("http://0x7f.0.0.1/", false)]
    [InlineData("http://example.com./", true)]
    public void OnlyPublicAddressesArePublic(string url, bool expected) =>
        Assert.Equal(expected, WebAddresses.IsPublic(new Uri(url)));

    [Fact]
    public void TheRulesMatchTheMac()
    {
        Assert.Equal((1_000_000, false), (FetchRules.Page.MaxBytes, FetchRules.Page.PublicOnly));
        Assert.Equal((10_000_000, true), (FetchRules.PageImage.MaxBytes, FetchRules.PageImage.PublicOnly));
        Assert.Equal((5_000_000, true), (FetchRules.Video.MaxBytes, FetchRules.Video.PublicOnly));
    }

    [Fact]
    public async Task RedirectsAreFollowedOnlyToPublicAddresses()
    {
        var fetcher = new FakeFetcher();
        // The user's own link may be on their network; a redirect off it may not go to another.
        fetcher.Redirects["http://192.168.1.5/page"] = "https://www.example.com/moved";
        fetcher.Add("https://www.example.com/moved", "<title>Moved</title>");
        var moved = await fetcher.Get(new Uri("http://192.168.1.5/page"), "text/html", FetchRules.Page);
        Assert.Equal((200, "https://www.example.com/moved"), (moved.Status, moved.FinalUrl.AbsoluteUri));

        fetcher.Redirects["https://www.example.com/go"] = "http://10.0.0.1/admin";
        var stopped = await fetcher.Get(new Uri("https://www.example.com/go"), "text/html", FetchRules.Page);
        Assert.Equal(302, stopped.Status);
        Assert.DoesNotContain("http://10.0.0.1/admin", fetcher.Requested);

        // A relative redirect resolves against the address it came from.
        fetcher.Redirects["https://www.example.com/a"] = "/b";
        fetcher.Add("https://www.example.com/b", "ok");
        Assert.Equal(200, (await fetcher.Get(new Uri("https://www.example.com/a"), "text/html", FetchRules.Page)).Status);
    }

    [Fact]
    public async Task ARedirectLoopStops()
    {
        var fetcher = new FakeFetcher();
        fetcher.Redirects["https://www.example.com/1"] = "https://www.example.com/2";
        fetcher.Redirects["https://www.example.com/2"] = "https://www.example.com/1";
        var response = await fetcher.Get(new Uri("https://www.example.com/1"), "text/html", FetchRules.Page);
        Assert.Equal(302, response.Status);
        Assert.Equal(LinkFetcher.MaxRedirects + 1, fetcher.Requested.Count);
    }

    [Fact]
    public async Task APageCantPointPreviewsAtTheLocalNetwork()
    {
        var fetcher = new FakeFetcher();
        fetcher.Add("http://192.168.1.10/camera.jpg", "IMG 1200x900", "image/jpeg");
        fetcher.Add("https://www.example.com/a.jpg", "IMG 1200x900", "image/jpeg");
        var info = new PageInfo { ImageUrls = [new Uri("http://192.168.1.10/camera.jpg"), new Uri("https://www.example.com/a.jpg")] };
        var preview = await PageDetails.Preview(fetcher, new FakeImages(), info);
        Assert.NotNull(preview);
        Assert.DoesNotContain("http://192.168.1.10/camera.jpg", fetcher.Requested);
        Assert.Equal(["https://www.example.com/a.jpg"], fetcher.Requested);
    }

    [Fact]
    public async Task ReadingStopsAtTheLimit()
    {
        var fetcher = new FakeFetcher();
        fetcher.Add("https://www.example.com/big", new string('x', 1_200_000));
        var response = await fetcher.Get(new Uri("https://www.example.com/big"), "text/html", FetchRules.Page);
        Assert.Equal(1_000_000, response.Body.Length);
    }
}

/// <summary>The capture inbox ignores what the share window wouldn't write (behaviour.md, "Browser extension"; #15).</summary>
public class InboxSafetyTests
{
    [Fact]
    public void AnOversizedEnvelopeIsNotRead()
    {
        using var library = new TestLibrary();
        var inbox = new CaptureInbox(library.Location.InboxFolder);
        var folder = Path.Combine(inbox.Folder, Ids.New());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, CaptureEnvelope.FileName), "{\"id\":\"x\"," + new string(' ', 1_000_001) + "}");
        Assert.Null(CaptureInbox.Read(folder));
    }

    [Fact]
    public void FileNamesThatLeaveTheFolderAreIgnored()
    {
        var envelope = new CaptureEnvelope
        {
            Id = Ids.New(), CreatedAt = DateTimeOffset.UtcNow, ProjectId = Ids.New(),
            Items = [new EnvelopeItem("file", Name: ".."), new EnvelopeItem("file", Name: "a/b.png"), new EnvelopeItem("file", Name: ""), new EnvelopeItem("file", Name: "..\\b.png"), new EnvelopeItem("file", Name: "ok.png")],
        };
        var items = envelope.CapturedItems(Path.GetTempPath());
        Assert.Equal([true, true, true, true, false], items.Select(i => i is null));
    }

    [Fact]
    public void LinksInTheInboxAreSkipped()
    {
        using var library = new TestLibrary();
        var inbox = new CaptureInbox(library.Location.InboxFolder);
        Directory.CreateDirectory(inbox.Folder);
        var elsewhere = Path.Combine(library.Root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "secret.png"), "not yours");
        var envelopeFolder = Path.Combine(inbox.Folder, Ids.New());
        var linkedFolder = Path.Combine(inbox.Folder, Ids.New());
        Directory.CreateDirectory(envelopeFolder);
        try
        {
            Directory.CreateSymbolicLink(linkedFolder, elsewhere);
            File.CreateSymbolicLink(Path.Combine(envelopeFolder, "linked.png"), Path.Combine(elsewhere, "secret.png"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Making links on Windows needs Developer Mode or administrator rights.
            Assert.Skip($"This PC can't make symbolic links here ({error.Message}).");
        }
        Assert.Equal([envelopeFolder], inbox.PendingFolders());
        var envelope = new CaptureEnvelope { Id = Ids.New(), CreatedAt = DateTimeOffset.UtcNow, ProjectId = Ids.New(), Items = [new EnvelopeItem("file", Name: "linked.png")] };
        Assert.Null(Assert.Single(envelope.CapturedItems(envelopeFolder)));
    }
}

/// <summary>One link per project, and a file's source page (behaviour.md, "Capture"; #7).</summary>
public class CaptureLinkTests
{
    [Fact]
    public void ExactlyTheSameLinkIsADuplicateButAnotherTimestampIsNot()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "F-16C" });
        var other = ProjectService.Create(store, new ProjectDraft { Name = "Spitfire" });
        var importer = new ReferenceImporter(new DirectStore(store), library.Location, _ => true);

        Assert.IsType<CaptureOutcome.Added>(Assert.Single(importer.Import([new CapturedItem.Link("https://www.example.com/walkaround", "Walkaround")], project.Id)));
        var again = Assert.IsType<CaptureOutcome.Duplicate>(Assert.Single(importer.Import([new CapturedItem.Link("https://www.example.com/walkaround")], project.Id)));
        Assert.Equal("Walkaround", again.ExistingTitle);
        Assert.Equal("Already Saved to This Project", ReferenceImporter.Report([again])!.Title);
        // Another project can have it too.
        Assert.IsType<CaptureOutcome.Added>(Assert.Single(importer.Import([new CapturedItem.Link("https://www.example.com/walkaround")], other.Id)));

        Assert.IsType<CaptureOutcome.Added>(Assert.Single(importer.Import([new CapturedItem.Link("https://youtu.be/abc123?t=90")], project.Id)));
        Assert.IsType<CaptureOutcome.Added>(Assert.Single(importer.Import([new CapturedItem.Link("https://youtu.be/abc123?t=120")], project.Id)));
        Assert.IsType<CaptureOutcome.Duplicate>(Assert.Single(importer.Import([new CapturedItem.Link("https://youtu.be/abc123?t=120")], project.Id)));
    }

    [Fact]
    public void AFileKeepsThePageItCameFrom()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "F-16C" });
        var path = Path.Combine(library.Root, "gear.png");
        File.WriteAllBytes(path, Images.Png(40, 30));
        var importer = new ReferenceImporter(new DirectStore(store), library.Location, _ => true);
        var added = Assert.IsType<CaptureOutcome.Added>(Assert.Single(importer.Import(
            [new CapturedItem.File(path, "gear.png", "https://cdn.example.com/gear.png", "https://www.example.com/walkaround")], project.Id)));
        var reference = store.Reference(added.Reference.Id)!;
        Assert.Equal("https://www.example.com/walkaround", reference.SourcePageUrl);
        Assert.Equal("https://cdn.example.com/gear.png", reference.OriginalResourceUrl);
    }

    [Fact]
    public void AnEnvelopesPageGoesToItsFiles()
    {
        var envelope = new CaptureEnvelope
        {
            Id = Ids.New(), CreatedAt = DateTimeOffset.UtcNow, ProjectId = Ids.New(), PageUrl = "https://www.example.com/page",
            Items = [new EnvelopeItem("file", Name: "a.png"), new EnvelopeItem("link", Url: "https://www.example.com/other")],
        };
        var file = Assert.IsType<CapturedItem.File>(envelope.CapturedItems(Path.GetTempPath())[0]);
        Assert.Equal("https://www.example.com/page", file.PageUrl);
    }
}

/// <summary>A PDF markup's badge stays wholly on the page (behaviour.md, "PDF markups"; #39).</summary>
public class BadgeTests
{
    private static Markup Note(double x, double y) => new()
    {
        Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, ReferenceId = "r", PageIndex = 0, Kind = MarkupKinds.Note, Color = "yellow",
        X = x, Y = y, Width = 180, Height = 32, Points = [], Text = "",
    };

    [Fact]
    public void ABadgeAtTheEdgeMovesInwardAndTheMarkupDoesnt()
    {
        var page = new PageRect(0, 0, 612, 792);
        // A note at the top-left corner of the page: its badge would be cut in half.
        var note = Note(0, 792 - 32);
        Assert.Equal(new PagePoint(9, 783), MarkupGeometry.BadgeCentre(note, page));
        Assert.Equal((0.0, 760.0), (note.X, note.Y));
        // Away from the edges it stays at the corner.
        Assert.Equal(new PagePoint(100, 532), MarkupGeometry.BadgeCentre(Note(100, 500), page));
    }

    [Fact]
    public void ACropBoxThatDoesntStartAtZeroIsRespectedAndATinyPageCentresIt()
    {
        var cropped = new PageRect(50, 50, 500, 700);
        var arrow = new Markup
        {
            Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, ReferenceId = "r", PageIndex = 0, Kind = MarkupKinds.Arrow, Color = "red",
            X = 0, Y = 0, Width = 0, Height = 0, Points = [548, 52, 300, 300], Text = "",
        };
        Assert.Equal(new PagePoint(541, 59), MarkupGeometry.BadgeCentre(arrow, cropped));
        Assert.Equal(8, MarkupGeometry.BadgeCentre(Note(0, 768), new PageRect(0, 0, 16, 800)).X);
    }
}
