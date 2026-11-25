using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace BenchBook.Core.Links;

/// <summary>The site says the item doesn't exist or is private. The attempt is recorded, so it isn't retried at every launch.</summary>
public sealed class LinkNotFoundException() : Exception("The page or video wasn't found.");

/// <summary>Offline, timed out or a server problem. Nothing is recorded, so it's tried again later.</summary>
public sealed class LinkUnavailableException(Exception? inner = null) : Exception("The site couldn't be reached.", inner);

/// <summary>Decodes, checks and scales downloaded images. The app provides it with the platform's own codecs.</summary>
public interface IImageScaler
{
    /// <summary>True if the bytes are an image the platform can decode.</summary>
    bool IsImage(byte[] data);

    /// <summary>
    /// The largest frame scaled down to <paramref name="maxPixels"/> on its longer
    /// side, as JPEG at quality 0.82, or PNG if it's transparent. Null if it isn't
    /// an image or its shorter side is under <paramref name="minPixels"/>.
    /// </summary>
    byte[]? Scale(byte[] data, int minPixels, int maxPixels);
}

/// <summary>A downloaded response: its status, final address, type and the first part of its body.</summary>
/// <param name="Location">A redirect's target, as the server gave it.</param>
public sealed record FetchedResponse(int Status, Uri FinalUrl, string? MediaType, byte[] Body, Uri? Location = null)
{
    /// <summary>Not fetched: the address wasn't allowed.</summary>
    public static FetchedResponse NotAllowed(Uri url) => new(0, url, null, []);
}

/// <summary>How much of a download to read, and whether its address must be public (WebLoader.swift).</summary>
/// <param name="MaxBytes">Reading stops here; what was read so far is returned.</param>
/// <param name="PublicOnly">The address came from a website rather than the user.</param>
public sealed record FetchRules(int MaxBytes, bool PublicOnly)
{
    /// <summary>A page the user saved. Only its header is read for previews.</summary>
    public static readonly FetchRules Page = new(1_000_000, false);

    /// <summary>An image or icon a page names.</summary>
    public static readonly FetchRules PageImage = new(10_000_000, true);

    /// <summary>YouTube and Vimeo pages, details and posters. Watch pages are large.</summary>
    public static readonly FetchRules Video = new(5_000_000, true);
}

/// <summary>
/// Makes the only web requests BenchBook makes on its own (behaviour.md, "Link
/// details" and "Webpages"): 20-second timeouts, Accept-Language: en, an honest
/// User-Agent, reading stopped at a size limit, and redirects followed only to
/// public web addresses.
/// </summary>
public class LinkFetcher
{
    public const string UserAgent = "BenchBook (link preview)";

    /// <summary>More redirects than this is a loop.</summary>
    public const int MaxRedirects = 10;

    private readonly HttpClient _client;

    public LinkFetcher(HttpClient? client = null)
    {
        _client = client ?? new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            // Redirects are followed here, one at a time, so each target can be checked.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    /// <summary>
    /// GETs the address, following redirects only to public web addresses, and
    /// reads at most the rules' limit. An address the rules don't allow, or a
    /// redirect that isn't followed, comes back as a response that isn't a
    /// success, which callers treat as no preview. Throws
    /// <see cref="LinkUnavailableException"/> when it can't connect.
    /// </summary>
    public async Task<FetchedResponse> Get(Uri url, string accept, FetchRules rules, bool youTubeConsent = false, CancellationToken cancellation = default)
    {
        if (!WebAddresses.IsWeb(url) || (rules.PublicOnly && !WebAddresses.IsPublic(url))) return FetchedResponse.NotAllowed(url);
        var current = url;
        for (var redirects = 0; ; redirects++)
        {
            var response = await Send(current, accept, youTubeConsent, rules.MaxBytes, cancellation);
            if (response.Status is not (301 or 302 or 303 or 307 or 308) || response.Location is not { } location) return response;
            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (redirects >= MaxRedirects || !WebAddresses.IsPublic(next)) return response;
            current = next;
        }
    }

    /// <summary>One request, without following redirects, reading at most <paramref name="limit"/> bytes.</summary>
    protected virtual async Task<FetchedResponse> Send(Uri url, string accept, bool youTubeConsent, int limit, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        // Skips YouTube's cookie-consent page in some regions.
        if (youTubeConsent) request.Headers.TryAddWithoutValidation("Cookie", "CONSENT=YES+1");
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            // Stop at the limit rather than reading it all first.
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while (body.Length < limit && (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - body.Length)), cancellation)) > 0)
                body.Write(buffer, 0, read);
            return new FetchedResponse((int)response.StatusCode, url, response.Content.Headers.ContentType?.MediaType, body.ToArray(), response.Headers.Location);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new LinkUnavailableException(error);
        }
    }

    public static string Text(byte[] body) => Encoding.UTF8.GetString(body);
}
