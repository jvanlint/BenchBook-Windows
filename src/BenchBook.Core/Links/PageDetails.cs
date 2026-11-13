using System.Globalization;
using System.Text.RegularExpressions;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;

namespace BenchBook.Core.Links;

/// <summary>What a webpage says about itself for link previews. Any of it can be missing.</summary>
public sealed record PageInfo
{
    public string? Title { get; init; }
    public string? SiteName { get; init; }
    public string? Description { get; init; }
    /// <summary>Preview images the page names, best first.</summary>
    public IReadOnlyList<Uri> ImageUrls { get; init; } = [];
    /// <summary>The site's icons, best first, used when no preview image works.</summary>
    public IReadOnlyList<Uri> IconUrls { get; init; } = [];
}

/// <summary>
/// A webpage's preview image, title, site name and description (behaviour.md,
/// "Webpages"). Reads only the page's header and never runs its scripts.
/// </summary>
public static partial class PageDetails
{
    public const int MaxDescriptionLength = 2_000;
    public const int MaxImagePixels = 960;
    public const int MaxIconPixels = 192;
    /// <summary>Smaller images are usually tracking pixels or spacers.</summary>
    public const int MinImagePixels = 100;
    /// <summary>Smaller icons look blurry scaled up in a tile.</summary>
    public const int MinIconPixels = 64;
    private const int PageLimit = 1_000_000;

    public static async Task<PageInfo> Fetch(LinkFetcher fetcher, Uri url, CancellationToken cancellation = default)
    {
        var response = await fetcher.Get(url, "text/html,application/xhtml+xml,*/*;q=0.8", limit: PageLimit, cancellation: cancellation);
        if (response.Status is >= 400 and < 500) throw new LinkNotFoundException();
        if (response.Status is < 200 or >= 300) throw new LinkUnavailableException();

        // A link straight to an image is its own preview.
        if (response.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return new PageInfo { ImageUrls = [response.FinalUrl] };

        var info = Parse(LinkFetcher.Text(response.Body), response.FinalUrl);
        // Most sites have these even when the page doesn't list them.
        var origin = new Uri(response.FinalUrl, "/");
        return info with { IconUrls = Unique(info.IconUrls.Concat([new Uri(origin, "apple-touch-icon.png"), new Uri(origin, "favicon.ico")])) };
    }

    /// <summary>The first preview image that downloads and decodes, else the first icon. Scaled and ready to store.</summary>
    public static async Task<(byte[] Data, bool IsIcon)?> Preview(LinkFetcher fetcher, IImageScaler images, PageInfo info, bool allowIcons = true, CancellationToken cancellation = default)
    {
        foreach (var url in info.ImageUrls)
            if (await Image(fetcher, images, url, MinImagePixels, MaxImagePixels, cancellation) is { } data) return (data, false);
        if (!allowIcons) return null;
        foreach (var url in info.IconUrls)
            if (await Image(fetcher, images, url, MinIconPixels, MaxIconPixels, cancellation) is { } data) return (data, true);
        return null;
    }

    private static async Task<byte[]?> Image(LinkFetcher fetcher, IImageScaler images, Uri url, int min, int max, CancellationToken cancellation)
    {
        try
        {
            var response = await fetcher.Get(url, "image/*", cancellation: cancellation);
            return response.Status is >= 200 and < 300 ? images.Scale(response.Body, min, max) : null;
        }
        catch (LinkUnavailableException)
        {
            return null;
        }
    }

    // MARK: Parsing

    /// <summary>Reads preview details from a page's HTML. Only the part before &lt;/head&gt; counts.</summary>
    public static PageInfo Parse(string html, Uri baseUrl)
    {
        var end = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        var head = end >= 0 ? html[..end] : html;

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var links = new List<(HashSet<string> Rel, string Href, int Size, string Type)>();
        foreach (Match tag in Tag().Matches(head))
        {
            var name = tag.Groups[1].Value.ToLowerInvariant();
            var attributes = Attributes(tag.Groups[2].Value);
            if (name == "meta")
            {
                var content = attributes.TryGetValue("content", out var c) ? Trimmed(DecodeEntities(c)) : null;
                var key = (attributes.GetValueOrDefault("property") ?? attributes.GetValueOrDefault("name") ?? attributes.GetValueOrDefault("itemprop"))?.ToLowerInvariant();
                if (content is not null && key is not null) meta.TryAdd(key, content);
            }
            else if (attributes.TryGetValue("href", out var href) && Trimmed(DecodeEntities(href)) is { } link)
            {
                var rel = (attributes.GetValueOrDefault("rel") ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                // "180x180", "32x32 16x16" or "any": the largest width.
                var size = (attributes.GetValueOrDefault("sizes") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Split('x', 'X')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
                    .DefaultIfEmpty(0).Max();
                links.Add((rel, link, size, (attributes.GetValueOrDefault("type") ?? "").ToLowerInvariant()));
            }
        }

        string? First(params string[] keys) => keys.Select(k => meta.GetValueOrDefault(k)).FirstOrDefault(v => v is not null);
        Uri? Resolve(string text) =>
            Uri.TryCreate(baseUrl, text, out var url) && url.Scheme is "http" or "https" ? url : null;

        var title = First("og:title", "twitter:title") ?? TitleElement(head);
        var description = First("og:description", "description", "twitter:description");
        var images = new[] { "og:image:secure_url", "og:image", "og:image:url", "twitter:image", "twitter:image:src", "image" }
            .Select(k => meta.GetValueOrDefault(k)).OfType<string>()
            .Concat(links.Where(l => l.Rel.Contains("image_src")).Select(l => l.Href));

        // Apple touch icons first, then other icons, largest first. SVG icons can't be decoded here.
        var icons = links
            .Where(l => (l.Rel.Contains("icon") || l.Rel.Any(r => r.StartsWith("apple-touch-icon", StringComparison.Ordinal)))
                        && !l.Type.Contains("svg") && !l.Href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.Rel.Any(r => r.StartsWith("apple-touch-icon", StringComparison.Ordinal)))
            .ThenByDescending(l => l.Size);

        return new PageInfo
        {
            Title = title is null ? null : TagNames.CollapseWhitespace(title),
            SiteName = First("og:site_name", "application-name", "apple-mobile-web-app-title") is { } site ? TagNames.CollapseWhitespace(site) : null,
            Description = description is null ? null : ReferenceImporter.Cut(TagNames.CollapseWhitespace(description), MaxDescriptionLength),
            ImageUrls = Unique(images.Select(Resolve).OfType<Uri>()),
            IconUrls = Unique(icons.Select(l => Resolve(l.Href)).OfType<Uri>()),
        };
    }

    /// <summary>Named entities common in titles, plus numeric ones like &amp;#8217;.</summary>
    public static string DecodeEntities(string text)
    {
        if (!text.Contains('&')) return text;
        return Entity().Replace(text, match =>
        {
            var entity = match.Groups[1].Value;
            if (entity.StartsWith('#'))
            {
                var hex = entity.Length > 1 && entity[1] is 'x' or 'X';
                var digits = hex ? entity[2..] : entity[1..];
                if (int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                    && code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                    return char.ConvertFromUtf32(code);
                return match.Value;
            }
            return entity switch
            {
                "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'", "nbsp" => " ",
                "ndash" => "–", "mdash" => "—", "lsquo" => "‘", "rsquo" => "’", "ldquo" => "“", "rdquo" => "”", "hellip" => "…",
                _ => match.Value,
            };
        });
    }

    private static Dictionary<string, string> Attributes(string body)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match attribute in Attribute().Matches(body))
        {
            var value = attribute.Groups[2].Success ? attribute.Groups[2].Value
                : attribute.Groups[3].Success ? attribute.Groups[3].Value
                : attribute.Groups[4].Value;
            attributes.TryAdd(attribute.Groups[1].Value.ToLowerInvariant(), value);
        }
        return attributes;
    }

    private static string? TitleElement(string html) =>
        Title().Match(html) is { Success: true } m ? Trimmed(DecodeEntities(m.Groups[1].Value)) : null;

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static List<Uri> Unique(IEnumerable<Uri> urls)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return urls.Where(u => seen.Add(u.AbsoluteUri)).ToList();
    }

    [GeneratedRegex("<(meta|link)\\b([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    [GeneratedRegex("([a-zA-Z_:.-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s\"'>]+))")]
    private static partial Regex Attribute();

    [GeneratedRegex("<title\\b[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex Title();

    [GeneratedRegex("&(#[xX][0-9a-fA-F]+|#[0-9]+|[a-zA-Z]+);")]
    private static partial Regex Entity();
}
