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
public sealed record FetchedResponse(int Status, Uri FinalUrl, string? MediaType, byte[] Body);

/// <summary>
/// Makes the only web requests BenchBook makes on its own (behaviour.md, "Link
/// details"): 20-second timeouts, Accept-Language: en, and an honest User-Agent.
/// </summary>
public class LinkFetcher
{
    public const string UserAgent = "BenchBook (link preview)";

    private readonly HttpClient _client;

    public LinkFetcher(HttpClient? client = null)
    {
        _client = client ?? new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    /// <summary>GETs the address and reads at most <paramref name="limit"/> bytes. Throws <see cref="LinkUnavailableException"/> when it can't connect.</summary>
    public virtual async Task<FetchedResponse> Get(Uri url, string accept, bool youTubeConsent = false, int limit = 20 * 1024 * 1024, CancellationToken cancellation = default)
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
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while (body.Length < limit && (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - body.Length)), cancellation)) > 0)
                body.Write(buffer, 0, read);
            return new FetchedResponse((int)response.StatusCode, response.RequestMessage?.RequestUri ?? url,
                response.Content.Headers.ContentType?.MediaType, body.ToArray());
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new LinkUnavailableException(error);
        }
    }

    public static string Text(byte[] body) => Encoding.UTF8.GetString(body);
}
