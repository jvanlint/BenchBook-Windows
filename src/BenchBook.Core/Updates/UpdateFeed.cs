using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Chaos.NaCl;
using NetSparkleUpdater;
using NetSparkleUpdater.AppCastHandlers;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;

namespace BenchBook.Core.Updates;

/// <summary>A newer version in the update feed.</summary>
public sealed record UpdateItem(string Version, string Notes, Uri Download, long Length, string Signature);

/// <summary>What a check for updates found.</summary>
public abstract record UpdateCheck
{
    private UpdateCheck() { }

    public sealed record UpToDate : UpdateCheck;

    public sealed record Available(UpdateItem Item) : UpdateCheck;

    public sealed record Failed(string Message) : UpdateCheck;
}

/// <summary>
/// The Windows update feeds (behaviour.md, "Updates"; windows-app.md,
/// "Distribution and updates"). There's one feed per processor, next to the
/// Mac's appcast.xml on the website. NetSparkleUpdater reads and writes them,
/// and checks their Ed25519 signatures: the feed's in a .signature file beside
/// it, the zip's in the feed. Both must be signed with the Windows update key.
/// </summary>
public static class UpdateFeed
{
    public const string SiteUrl = "https://jvanlint.github.io/BenchBook-Site/";

    /// <summary>Where a copy that can't update itself sends the user.</summary>
    public const string DownloadPage = "https://github.com/jvanlint/BenchBook-Windows/releases/latest";

    /// <summary>
    /// The public half of the Windows update key, in base64. Only the release
    /// build has the private half (a GitHub Actions secret). If this is
    /// empty, no update can be checked or installed.
    /// </summary>
    public const string PublicKey = "Adc4vcEnBt9EostqdXfOvZcyrU68M5GQZtO0+ImJNpU=";

    /// <summary>win-x64 or win-arm64, as in the release zips' names; null on other processors.</summary>
    public static string? Runtime(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => null,
    };

    /// <summary>appcast-windows-x64.xml or appcast-windows-arm64.xml.</summary>
    public static string FileName(string runtime) => $"appcast-{OperatingSystem(runtime)}.xml";

    /// <summary>The enclosure's sparkle:os: windows-x64 or windows-arm64.</summary>
    public static string OperatingSystem(string runtime) => "windows-" + runtime["win-".Length..];

    public static Uri Url(string runtime) => new(SiteUrl + FileName(runtime));

    /// <summary>The zip's name on GitHub Releases, as build\Release.ps1 makes it.</summary>
    public static string ZipName(string version, string runtime) => $"BenchBook-{version}-{runtime}.zip";

    /// <summary>
    /// Fetches the feed and its signature, checks it's signed with
    /// <paramref name="publicKey"/>, and returns the newest version above
    /// <paramref name="installedVersion"/> for this processor. The requests
    /// carry only BenchBook's version, in the User-Agent.
    /// </summary>
    public static async Task<UpdateCheck> CheckAsync(HttpClient http, Uri feed, string installedVersion, string runtime, string publicKey, CancellationToken cancellation = default)
    {
        var verifier = Verifier(publicKey);
        if (!verifier.HasValidKeyInformation())
            return new UpdateCheck.Failed("This copy of BenchBook wasn't built with an update key, so it can't check for updates.");

        byte[] data;
        string signature;
        try
        {
            data = await Get(http, feed, installedVersion, cancellation);
            signature = Encoding.UTF8.GetString(await Get(http, new Uri(feed + ".signature"), installedVersion, cancellation)).Trim();
        }
        catch (HttpRequestException error) when (error.StatusCode is not null)
        {
            // The website answered, but hasn't got this processor's feed, or refused.
            return new UpdateCheck.Failed($"BenchBook's website has no list of updates for this PC yet ({(int)error.StatusCode} from {error.Data["url"]}).");
        }
        catch (Exception error) when (error is HttpRequestException or IOException || (error is TaskCanceledException && !cancellation.IsCancellationRequested))
        {
            return new UpdateCheck.Failed("BenchBook couldn't reach its website to check for updates. Check that this PC is online, then try again.");
        }

        if (!IsSigned(verifier, signature, data))
            return new UpdateCheck.Failed("The list of updates isn't signed with BenchBook's update key, so it was ignored.");

        List<AppCastItem> items;
        try
        {
            items = new XMLAppCastGenerator().DeserializeAppCast(Encoding.UTF8.GetString(data)).Items;
        }
        catch (Exception error) when (error is System.Xml.XmlException or FormatException or InvalidOperationException)
        {
            return new UpdateCheck.Failed("The list of updates couldn't be read.");
        }
        return Newest(items, installedVersion, runtime) is { } item ? new UpdateCheck.Available(item) : new UpdateCheck.UpToDate();
    }

    /// <summary>The newest item for this processor that's above the installed version and can be installed.</summary>
    internal static UpdateItem? Newest(IEnumerable<AppCastItem> items, string installedVersion, string runtime)
    {
        var installed = SemVerLike.Parse(installedVersion);
        var os = OperatingSystem(runtime);
        return items
            .Where(i => string.Equals(i.OperatingSystem, os, StringComparison.OrdinalIgnoreCase))
            .Where(i => !string.IsNullOrWhiteSpace(i.Version) && SemVerLike.Parse(i.Version).CompareTo(installed) > 0)
            .Where(i => !string.IsNullOrWhiteSpace(i.DownloadSignature))
            .Select(i => (Item: i, Link: Uri.TryCreate(i.DownloadLink, UriKind.Absolute, out var link) ? link : null))
            .Where(i => i.Link is { Scheme: "https" })
            .OrderByDescending(i => SemVerLike.Parse(i.Item.Version))
            .Select(i => new UpdateItem(i.Item.Version!, i.Item.Description ?? "", i.Link!, i.Item.UpdateSize, i.Item.DownloadSignature!))
            .FirstOrDefault();
    }

    internal static Ed25519Checker Verifier(string publicKey) =>
        // No key file: the key is only ever the one built in.
        new(SecurityMode.Strict, string.IsNullOrWhiteSpace(publicKey) ? null : publicKey, publicKeyFile: null, readFileBeingVerifiedInChunks: true);

    internal static bool IsSigned(Ed25519Checker verifier, string signature, byte[] data)
    {
        try
        {
            return verifier.VerifySignature(signature, data) == ValidationResult.Valid;
        }
        catch (FormatException)
        {
            return false; // Not base64.
        }
    }

    /// <summary>Whether a downloaded zip is the one the feed signed.</summary>
    public static bool IsSignedFile(string publicKey, string signature, string path)
    {
        try
        {
            return Verifier(publicKey).VerifySignatureOfFile(signature, path) == ValidationResult.Valid;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Only BenchBook's version is sent, as the Mac's Sparkle does.</summary>
    internal static HttpRequestMessage Request(Uri url, string installedVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd($"BenchBook/{installedVersion}");
        return request;
    }

    private static async Task<byte[]> Get(HttpClient http, Uri url, string installedVersion, CancellationToken cancellation)
    {
        using var request = Request(url, installedVersion);
        using var response = await http.SendAsync(request, cancellation);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"{(int)response.StatusCode} from {url}", null, response.StatusCode) { Data = { ["url"] = url.AbsoluteUri } };
        return await response.Content.ReadAsByteArrayAsync(cancellation);
    }

    // Writing the feeds happens only in the release build (build\UpdateFeed).

    /// <summary>A new key pair: the private seed and the public key, both base64.</summary>
    public static (string PrivateKey, string PublicKey) NewKeys()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        return (Convert.ToBase64String(seed), Convert.ToBase64String(Ed25519.PublicKeyFromSeed(seed)));
    }

    /// <summary>The public key that goes with a private key, to check a secret is the right one.</summary>
    public static string PublicKeyFor(string privateKey) => Convert.ToBase64String(Ed25519.PublicKeyFromSeed(Seed(privateKey)));

    public static string Sign(string privateKey, byte[] data) =>
        Convert.ToBase64String(Ed25519.Sign(data, Ed25519.ExpandedPrivateKeyFromSeed(Seed(privateKey))));

    /// <summary>One processor's feed, with one item: this release's zip and notes.</summary>
    public static string WriteFeed(string version, string runtime, string notes, Uri download, long length, string zipSignature, DateTimeOffset published)
    {
        var item = new AppCastItem
        {
            Title = $"BenchBook {version}",
            Version = version,
            ShortVersion = version,
            Description = notes,
            DownloadLink = download.AbsoluteUri,
            DownloadSignature = zipSignature,
            UpdateSize = length,
            OperatingSystem = OperatingSystem(runtime),
            MIMEType = "application/zip",
            PublicationDate = published.UtcDateTime,
        };
        var feed = new AppCast
        {
            Title = $"BenchBook for Windows ({runtime["win-".Length..]})",
            Link = SiteUrl,
            Description = "New versions of BenchBook for Windows.",
            Language = "en",
            Items = [item],
        };
        // Line feeds only, so nothing on the way to the website (such as git's
        // line-ending conversion) changes the bytes the signature covers.
        return new XMLAppCastGenerator().SerializeAppCast(feed).ReplaceLineEndings("\n");
    }

    private static byte[] Seed(string privateKey)
    {
        var seed = Convert.FromBase64String(privateKey.Trim());
        if (seed.Length != 32) throw new FormatException("The private key should be 32 bytes, in base64.");
        return seed;
    }
}
