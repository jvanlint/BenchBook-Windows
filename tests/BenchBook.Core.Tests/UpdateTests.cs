using System.IO.Compression;
using System.Net;
using System.Text;
using BenchBook.Core.Updates;

namespace BenchBook.Core.Tests;

/// <summary>Update checks and installs (behaviour.md, "Updates").</summary>
public sealed class UpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BenchBook Update Tests " + Guid.NewGuid().ToString("N"));
    private readonly (string PrivateKey, string PublicKey) _keys = UpdateFeed.NewKeys();
    private readonly LibraryLocation _location;

    public UpdateTests()
    {
        Directory.CreateDirectory(_root);
        _location = new LibraryLocation(Path.Combine(_root, "Local", "BenchBook"));
        Directory.CreateDirectory(_location.AppFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly Uri FeedUrl = UpdateFeed.Url("win-x64");
    private static readonly Uri ZipUrl = new("https://github.com/jvanlint/BenchBook-Windows/releases/download/v1.4.0/BenchBook-1.4.0-win-x64.zip");

    /// <summary>Serves fixed responses and records the requests.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public Dictionary<Uri, byte[]> Files { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Files.TryGetValue(request.RequestUri!, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>No connection at all.</summary>
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No such host is known.");
    }

    private static byte[] Zip(string exeText)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("BenchBook/BenchBook.exe").Open())) writer.Write(exeText);
            using (var writer = new StreamWriter(archive.CreateEntry("BenchBook/Assets/BenchBook.ico").Open())) writer.Write("icon");
        }
        return memory.ToArray();
    }

    /// <summary>A server with a signed x64 feed offering <paramref name="version"/>.</summary>
    private Server Serve(string version = "1.4.0", byte[]? zip = null, string? signingKey = null, string runtime = "win-x64")
    {
        zip ??= Zip("new");
        var key = signingKey ?? _keys.PrivateKey;
        var feed = UpdateFeed.WriteFeed(version, runtime, "- Check for updates", ZipUrl, zip.Length, UpdateFeed.Sign(key, zip), DateTimeOffset.UtcNow);
        var bytes = Encoding.UTF8.GetBytes(feed);
        var server = new Server();
        server.Files[FeedUrl] = bytes;
        server.Files[new Uri(FeedUrl + ".signature")] = Encoding.UTF8.GetBytes(UpdateFeed.Sign(key, bytes));
        server.Files[ZipUrl] = zip;
        return server;
    }

    [Fact]
    public void FeedsAreNamedForEachProcessor()
    {
        Assert.Equal("https://jvanlint.github.io/BenchBook-Site/appcast-windows-x64.xml", UpdateFeed.Url("win-x64").AbsoluteUri);
        Assert.Equal("https://jvanlint.github.io/BenchBook-Site/appcast-windows-arm64.xml", UpdateFeed.Url("win-arm64").AbsoluteUri);
        Assert.Equal("win-arm64", UpdateFeed.Runtime(System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Null(UpdateFeed.Runtime(System.Runtime.InteropServices.Architecture.X86));
        Assert.Equal("BenchBook-1.4.0-win-x64.zip", UpdateFeed.ZipName("1.4.0", "win-x64"));
    }

    [Fact]
    public void TheBuiltInKeyIsAnEd25519PublicKey()
    {
        Assert.Equal(32, Convert.FromBase64String(UpdateFeed.PublicKey).Length);
        Assert.True(UpdateFeed.Verifier(UpdateFeed.PublicKey).HasValidKeyInformation());
    }

    [Fact]
    public void KeysPairUp()
    {
        Assert.Equal(_keys.PublicKey, UpdateFeed.PublicKeyFor(_keys.PrivateKey));
        Assert.Equal(32, Convert.FromBase64String(_keys.PublicKey).Length);
    }

    [Fact]
    public async Task ANewerVersionIsFound()
    {
        var server = Serve();
        var result = await UpdateFeed.CheckAsync(new HttpClient(server), FeedUrl, "1.3.0", "win-x64", _keys.PublicKey);
        var item = Assert.IsType<UpdateCheck.Available>(result).Item;
        Assert.Equal("1.4.0", item.Version);
        Assert.Equal("- Check for updates", item.Notes);
        Assert.Equal(ZipUrl, item.Download);
        Assert.Equal(server.Files[ZipUrl].Length, item.Length);
    }

    [Fact]
    public async Task AChecksSendsOnlyTheVersion()
    {
        var server = Serve();
        await UpdateFeed.CheckAsync(new HttpClient(server), FeedUrl, "1.3.0", "win-x64", _keys.PublicKey);
        Assert.Equal(2, server.Requests.Count);
        foreach (var request in server.Requests)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Equal("BenchBook/1.3.0", request.Headers.UserAgent.ToString());
            Assert.Single(request.Headers);
        }
        Assert.Equal(new Uri(FeedUrl + ".signature"), server.Requests[1].RequestUri);
    }

    [Theory]
    [InlineData("1.3.0")]
    [InlineData("1.2.2")]
    public async Task TheSameOrAnOlderVersionIsUpToDate(string offered)
    {
        var result = await UpdateFeed.CheckAsync(new HttpClient(Serve(offered)), FeedUrl, "1.3.0", "win-x64", _keys.PublicKey);
        Assert.IsType<UpdateCheck.UpToDate>(result);
    }

    [Fact]
    public async Task AnotherProcessorsUpdateIsIgnored()
    {
        var result = await UpdateFeed.CheckAsync(new HttpClient(Serve(runtime: "win-arm64")), FeedUrl, "1.3.0", "win-x64", _keys.PublicKey);
        Assert.IsType<UpdateCheck.UpToDate>(result);
    }

    [Fact]
    public async Task AFeedSignedWithAnotherKeyIsIgnored()
    {
        var other = UpdateFeed.NewKeys();
        var result = await UpdateFeed.CheckAsync(new HttpClient(Serve(signingKey: other.PrivateKey)), FeedUrl, "1.3.0", "win-x64", _keys.PublicKey);
        Assert.Contains("isn't signed", Assert.IsType<UpdateCheck.Failed>(result).Message);
    }

    [Fact]
    public async Task AChangedFeedIsIgnored()
    {
        var server = Serve();
        var feed = Encoding.UTF8.GetString(server.Files[FeedUrl]).Replace("1.4.0", "1.5.0");
        server.Files[FeedUrl] = Encoding.UTF8.GetBytes(feed);
        var result = await UpdateFeed.CheckAsync(new HttpClient(server), FeedUrl, "1.3.0", "win-x64", _keys.PublicKey);
        Assert.IsType<UpdateCheck.Failed>(result);
    }

    [Fact]
    public async Task AMissingSignatureIsIgnored()
    {
        var server = Serve();
        server.Files.Remove(new Uri(FeedUrl + ".signature"));
        var result = await UpdateFeed.CheckAsync(new HttpClient(server), FeedUrl, "1.3.0", "win-x64", _keys.PublicKey);
        Assert.IsType<UpdateCheck.Failed>(result);
    }

    [Fact]
    public async Task WithoutAKeyNothingIsChecked()
    {
        var server = Serve();
        var result = await UpdateFeed.CheckAsync(new HttpClient(server), FeedUrl, "1.3.0", "win-x64", "");
        Assert.IsType<UpdateCheck.Failed>(result);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void FeedsHaveOnlyLineFeeds()
    {
        // Git on Windows would otherwise turn CRLF into LF, and the signature would no longer match.
        var feed = UpdateFeed.WriteFeed("1.4.0", "win-x64", "- One\r\n- Two", ZipUrl, 10, "c2ln", DateTimeOffset.UtcNow);
        Assert.DoesNotContain('\r', feed);
        Assert.Contains('\n', feed);
    }

    [Fact]
    public async Task AMissingFeedSaysSo()
    {
        var server = new Server();
        var result = await UpdateFeed.CheckAsync(new HttpClient(server), FeedUrl, "1.3.0", "win-x64", _keys.PublicKey);
        var message = Assert.IsType<UpdateCheck.Failed>(result).Message;
        Assert.Contains("no list of updates", message);
        Assert.Contains("404", message);
        Assert.Contains(FeedUrl.AbsoluteUri, message);
    }

    [Fact]
    public async Task BeingOfflineSaysSo()
    {
        var result = await UpdateFeed.CheckAsync(new HttpClient(new Offline()), FeedUrl, "1.3.0", "win-x64", _keys.PublicKey);
        Assert.Contains("online", Assert.IsType<UpdateCheck.Failed>(result).Message);
    }

    private async Task<string> Download(Server server)
    {
        var check = await UpdateFeed.CheckAsync(new HttpClient(server), FeedUrl, "1.3.0", "win-x64", _keys.PublicKey);
        var item = Assert.IsType<UpdateCheck.Available>(check).Item;
        return await UpdateInstaller.DownloadAsync(new HttpClient(server), item, "1.3.0", _location, _keys.PublicKey);
    }

    [Fact]
    public async Task TheSignedZipIsDownloadedAndUnpacked()
    {
        var folder = await Download(Serve());
        Assert.Equal("new", File.ReadAllText(Path.Combine(folder, "BenchBook.exe")));
        Assert.True(File.Exists(Path.Combine(folder, "Assets", "BenchBook.ico")));
        Assert.False(File.Exists(Path.Combine(UpdateInstaller.StagingFolder(_location), "Update.zip")));
    }

    [Fact]
    public async Task AChangedZipIsntInstalled()
    {
        var server = Serve();
        var check = await UpdateFeed.CheckAsync(new HttpClient(server), FeedUrl, "1.3.0", "win-x64", _keys.PublicKey);
        var item = Assert.IsType<UpdateCheck.Available>(check).Item;
        var changed = Zip("bad");
        Assert.Equal(server.Files[ZipUrl].Length, changed.Length);
        server.Files[ZipUrl] = changed;
        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateInstaller.DownloadAsync(new HttpClient(server), item, "1.3.0", _location, _keys.PublicKey));
        Assert.Contains("isn't signed", error.Message);
        Assert.False(Directory.Exists(Path.Combine(UpdateInstaller.StagingFolder(_location), "New")));
    }

    [Fact]
    public async Task AZipWithoutBenchBookIsntInstalled()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(archive.CreateEntry("Other/Other.exe").Open())) writer.Write("x");
        await Assert.ThrowsAsync<UpdateException>(() => Download(Serve(zip: memory.ToArray())));
    }

    /// <summary>An installed copy: Programs\BenchBook with BenchBook.exe and a file only the old version has.</summary>
    private string InstalledApp()
    {
        var app = Path.Combine(_root, "Programs", "BenchBook");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "BenchBook.exe"), "old");
        File.WriteAllText(Path.Combine(app, "Removed.dll"), "old");
        return app;
    }

    [Fact]
    public async Task InstallingReplacesTheAppFolder()
    {
        var app = InstalledApp();
        var store = Path.Combine(_location.AppFolder, "BenchBook.db");
        File.WriteAllText(store, "library");
        var folder = await Download(Serve());
        Assert.Null(UpdateInstaller.CantReplace(app, _location));

        var (exe, arguments) = UpdateInstaller.Schedule(_location, "1.4.0", folder, app, 1234);
        Assert.Equal(Path.Combine(folder, "BenchBook.exe"), exe);
        Assert.Equal(UpdateInstaller.InstallArgument, arguments[0]);
        int? waitedFor = null;
        var start = UpdateInstaller.Install(arguments[1], _location, id => { waitedFor = id; return true; });

        Assert.Equal(1234, waitedFor);
        Assert.Equal(app, start);
        Assert.Equal("new", File.ReadAllText(Path.Combine(app, "BenchBook.exe")));
        Assert.False(File.Exists(Path.Combine(app, "Removed.dll")));
        Assert.Equal("library", File.ReadAllText(store));

        // The next launch removes the old folder and the download.
        var result = UpdateInstaller.FinishPending(_location);
        Assert.NotNull(result);
        Assert.True(result.Installed);
        Assert.Equal("1.4.0", result.Version);
        Assert.False(Directory.Exists(result.OldFolder));
        Assert.Equal(["BenchBook"], Directory.GetDirectories(Path.Combine(_root, "Programs")).Select(Path.GetFileName));
        Assert.False(Directory.Exists(UpdateInstaller.StagingFolder(_location)));
        Assert.Null(UpdateInstaller.FinishPending(_location));
    }

    [Fact]
    public async Task IfBenchBookDoesntCloseNothingChanges()
    {
        var app = InstalledApp();
        var folder = await Download(Serve());
        var (_, arguments) = UpdateInstaller.Schedule(_location, "1.4.0", folder, app, 1234);
        var start = UpdateInstaller.Install(arguments[1], _location, _ => false);

        Assert.Equal(app, start);
        Assert.Equal("old", File.ReadAllText(Path.Combine(app, "BenchBook.exe")));
        var result = UpdateInstaller.FinishPending(_location);
        Assert.NotNull(result);
        Assert.False(result.Installed);
        Assert.Equal("BenchBook didn't close.", result.Message);
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "Programs")));
    }

    [Fact]
    public void AFolderThatHoldsTheLibraryIsntReplaced()
    {
        // BenchBook unzipped into %LOCALAPPDATA%\BenchBook, where the library is.
        File.WriteAllText(Path.Combine(_location.AppFolder, "BenchBook.exe"), "old");
        Assert.Contains("holds its library", UpdateInstaller.CantReplace(_location.AppFolder, _location));
        // Or into a folder inside it.
        var inside = Path.Combine(_location.AppFolder, "App");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "BenchBook.exe"), "old");
        Assert.NotNull(UpdateInstaller.CantReplace(inside, _location));
    }

    [Fact]
    public void AFolderWithoutBenchBookIsntReplaced()
    {
        var other = Path.Combine(_root, "Other");
        Directory.CreateDirectory(other);
        Assert.NotNull(UpdateInstaller.CantReplace(other, _location));
    }

    [Fact]
    public void AnUnusedDownloadIsRemovedAtLaunch()
    {
        var staging = UpdateInstaller.StagingFolder(_location);
        Directory.CreateDirectory(Path.Combine(staging, "New"));
        Assert.Null(UpdateInstaller.FinishPending(_location));
        Assert.False(Directory.Exists(staging));
    }
}
