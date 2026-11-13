using System.Text;
using BenchBook.Core.Capture;
using BenchBook.Core.Links;
using BenchBook.Core.Model;
using BenchBook.Core.Projects;

namespace BenchBook.Core.Tests;

/// <summary>Answers requests from a table, so tests never go online.</summary>
internal sealed class FakeFetcher : LinkFetcher
{
    public Dictionary<string, (int Status, string? Type, byte[] Body)> Pages { get; } = [];
    public bool Offline { get; set; }
    public List<string> Requested { get; } = [];

    public override Task<FetchedResponse> Get(Uri url, string accept, bool youTubeConsent = false, int limit = 20 * 1024 * 1024, CancellationToken cancellation = default)
    {
        Requested.Add(url.AbsoluteUri);
        if (Offline) throw new LinkUnavailableException();
        return Task.FromResult(Pages.TryGetValue(url.AbsoluteUri, out var page)
            ? new FetchedResponse(page.Status, url, page.Type, page.Body.Take(limit).ToArray())
            : new FetchedResponse(404, url, "text/html", []));
    }

    public void Add(string url, string body, string type = "text/html", int status = 200) => Pages[url] = (status, type, Encoding.UTF8.GetBytes(body));
}

/// <summary>Treats bytes starting "IMG" as an image of the size that follows, e.g. "IMG 1200x630".</summary>
internal sealed class FakeImages : IImageScaler
{
    public bool IsImage(byte[] data) => Size(data) is not null;

    public byte[]? Scale(byte[] data, int minPixels, int maxPixels) =>
        Size(data) is var (w, h) && Math.Min(w, h) >= minPixels ? Encoding.ASCII.GetBytes($"SCALED {Math.Min(maxPixels, Math.Max(w, h))}") : null;

    private static (int, int)? Size(byte[] data)
    {
        var text = Encoding.ASCII.GetString(data);
        if (!text.StartsWith("IMG ")) return null;
        var parts = text[4..].Split('x');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }
}

public class PageParsingTests
{
    private static readonly Uri Base = new("https://www.example.com/forum/topic/1");

    [Fact]
    public void OpenGraphComesFirstAndOnlyTheHeadCounts()
    {
        var html = """
            <html><head>
            <title> Fallback &amp; title </title>
            <meta property="og:title" content="F-16 Walkaround &#8211; Part 2">
            <meta name="twitter:title" content="Not this">
            <meta property="og:site_name" content="Britmodeller">
            <meta name="description" content="  A   detailed
               look at the  main gear. ">
            <meta property="og:image" content="/images/gear.jpg">
            <meta name="twitter:image" content="https://cdn.example.com/twitter.jpg">
            <link rel="icon" href="/favicon-32.png" sizes="32x32">
            <link rel="icon" href="/favicon-192.png" sizes="192x192">
            <link rel="apple-touch-icon" href="/apple.png">
            <link rel="icon" type="image/svg+xml" href="/icon.svg">
            </head><body><meta property="og:image" content="/body.jpg"></body></html>
            """;
        var info = PageDetails.Parse(html, Base);
        Assert.Equal("F-16 Walkaround – Part 2", info.Title);
        Assert.Equal("Britmodeller", info.SiteName);
        Assert.Equal("A detailed look at the main gear.", info.Description);
        Assert.Equal(["https://www.example.com/images/gear.jpg", "https://cdn.example.com/twitter.jpg"], info.ImageUrls.Select(u => u.AbsoluteUri));
        Assert.Equal(["https://www.example.com/apple.png", "https://www.example.com/favicon-192.png", "https://www.example.com/favicon-32.png"],
            info.IconUrls.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void TheTitleElementIsTheLastResort()
    {
        var info = PageDetails.Parse("<head><title>Kit &quot;Review&quot;</title><meta property='og:image' content='javascript:alert(1)'></head>", Base);
        Assert.Equal("Kit \"Review\"", info.Title);
        Assert.Empty(info.ImageUrls);
        Assert.Null(info.SiteName);
    }

    [Fact]
    public void VideoPagesAndDurationsAreRead()
    {
        var page = VideoDetails.ParseYouTubePage("""
            <meta name="title" content="Painting &amp; weathering">
            "lengthSeconds":"754" "publishDate":"2024-03-05" "ownerChannelName":"Scale Channel"
            "shortDescription":"Line one\nLine \"two\""
            """);
        Assert.Equal("Painting & weathering", page.Title);
        Assert.Equal(754, page.DurationSeconds);
        Assert.Equal(new DateTimeOffset(2024, 3, 5, 0, 0, 0, TimeSpan.Zero), page.PublishedAt);
        Assert.Equal("Scale Channel", page.Channel);
        Assert.Equal("Line one\nLine \"two\"", page.Description);

        Assert.Equal(214, VideoDetails.ParseIsoDuration("PT3M34S"));
        Assert.Equal(3723, VideoDetails.ParseIsoDuration("PT1H2M3S"));
        Assert.Equal(45, VideoDetails.ParseIsoDuration("P0DT45S"));
        Assert.Null(VideoDetails.ParseIsoDuration("3 minutes"));
        Assert.Equal(new DateTimeOffset(2013, 10, 15, 14, 8, 29, TimeSpan.Zero), VideoDetails.ParseDate("2013-10-15 14:08:29"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("YouTube", true)]
    [InlineData("Some video - YouTube", true)]
    [InlineData("Some video on Vimeo", true)]
    [InlineData("https://example.com/page", true)]
    [InlineData("www.example.com", true)]
    [InlineData("My notes on the gear", false)]
    public void OnlyPlaceholderTitlesAreReplaced(string? title, bool replace) =>
        Assert.Equal(replace, LinkDetails.ShouldReplaceTitle(title, "www.example.com"));
}

public class LinkDetailsTests
{
    private static (LibraryStoreHolder Holder, string Id) Link(TestLibrary library, string url, string? title = null)
    {
        var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "P" });
        var importer = new ReferenceImporter(new DirectStore(store), library.Location, _ => true);
        var added = (CaptureOutcome.Added)importer.Import([new CapturedItem.Link(url, title)], project.Id)[0];
        return (new LibraryStoreHolder(store), added.Reference.Id);
    }

    internal sealed class LibraryStoreHolder(Store.LibraryStore store) : IDisposable
    {
        public Store.LibraryStore Store { get; } = store;
        public void Dispose() => Store.Dispose();
    }

    [Fact]
    public async Task AWebpageGetsItsDetailsAndPreview()
    {
        using var library = new TestLibrary();
        var (holder, id) = Link(library, "https://www.example.com/page");
        using var _ = holder;
        var fetcher = new FakeFetcher();
        fetcher.Add("https://www.example.com/page", "<head><meta property='og:title' content='Gear Bay'><meta property='og:site_name' content='Example'><meta property='og:image' content='/a.jpg'></head>");
        fetcher.Add("https://www.example.com/a.jpg", "IMG 1200x630", "image/jpeg");
        var details = new LinkDetails(new DirectStore(holder.Store), library.Location, fetcher, new FakeImages());

        Assert.Equal([id], details.Missing());
        Assert.True(await details.Refresh(id));
        var reference = holder.Store.Reference(id)!;
        Assert.Equal("Gear Bay", reference.Title);
        Assert.Equal("Example", reference.LinkSiteName);
        Assert.NotNull(reference.LinkPreviewFetchedAt);
        Assert.Equal("SCALED 960", File.ReadAllText(library.Location.PosterPath(id)));
        Assert.Empty(details.Missing());
        Assert.All(fetcher.Requested, r => Assert.StartsWith("https://www.example.com/", r));
    }

    [Fact]
    public async Task ATitleTheUserTypedIsKeptAndTinyImagesFallBackToTheIcon()
    {
        using var library = new TestLibrary();
        var (holder, id) = Link(library, "https://www.example.com/page", "My own title");
        using var _ = holder;
        var fetcher = new FakeFetcher();
        fetcher.Add("https://www.example.com/page", "<head><title>Site Title</title><meta property='og:image' content='/pixel.gif'></head>");
        fetcher.Add("https://www.example.com/pixel.gif", "IMG 1x1", "image/gif");
        fetcher.Add("https://www.example.com/apple-touch-icon.png", "IMG 180x180", "image/png");
        var details = new LinkDetails(new DirectStore(holder.Store), library.Location, fetcher, new FakeImages());

        await details.Refresh(id);
        Assert.Equal("My own title", holder.Store.Reference(id)!.Title);
        Assert.Equal("SCALED 180", File.ReadAllText(library.Location.PosterPath(id)));
    }

    [Fact]
    public async Task NotFoundIsRecordedButOfflineIsTriedAgain()
    {
        using var library = new TestLibrary();
        var (holder, id) = Link(library, "https://www.example.com/gone");
        using var _ = holder;
        var fetcher = new FakeFetcher { Offline = true };
        var details = new LinkDetails(new DirectStore(holder.Store), library.Location, fetcher, new FakeImages());

        Assert.False(await details.Refresh(id));
        Assert.Null(holder.Store.Reference(id)!.LinkPreviewFetchedAt);

        fetcher.Offline = false; // Now the server answers 404.
        Assert.True(await details.Refresh(id));
        Assert.NotNull(holder.Store.Reference(id)!.LinkPreviewFetchedAt);
        Assert.Empty(details.Missing());

        fetcher.Add("https://www.example.com/gone", "", status: 503);
        var before = holder.Store.Reference(id)!.UpdatedAt;
        Assert.False(await details.Refresh(id));
        Assert.Equal(before, holder.Store.Reference(id)!.UpdatedAt);
    }

    [Fact]
    public async Task AYouTubeVideoGetsDetailsFromOEmbedAndTheWatchPage()
    {
        using var library = new TestLibrary();
        var (holder, id) = Link(library, "https://youtu.be/abc123?t=30", "Gear doors - YouTube");
        using var _ = holder;
        var fetcher = new FakeFetcher();
        fetcher.Add("https://www.youtube.com/oembed?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3Dabc123&format=json",
            """{"title":"Gear doors","author_name":"Scale Channel","author_url":"https://www.youtube.com/@scale"}""", "application/json");
        fetcher.Add("https://www.youtube.com/watch?v=abc123", "\"lengthSeconds\":\"90\" <meta itemprop=\"datePublished\" content=\"2024-01-02\">");
        fetcher.Add("https://i.ytimg.com/vi/abc123/hqdefault.jpg", "IMG 480x360", "image/jpeg");
        var details = new LinkDetails(new DirectStore(holder.Store), library.Location, fetcher, new FakeImages());

        Assert.True(await details.Refresh(id));
        var reference = holder.Store.Reference(id)!;
        Assert.Equal("Gear doors", reference.Title);
        Assert.Equal("Scale Channel", reference.VideoChannel);
        Assert.Equal(90, reference.VideoDurationSeconds);
        Assert.Equal("IMG 480x360", File.ReadAllText(library.Location.PosterPath(id)));
    }
}
