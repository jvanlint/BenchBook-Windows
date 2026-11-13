using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BenchBook.Core.Capture;

namespace BenchBook.Core.Links;

/// <summary>Public details about a video. Any of them can be missing.</summary>
public sealed record VideoInfo
{
    public string? Title { get; init; }
    public string? Channel { get; init; }
    public string? ChannelUrl { get; init; }
    public double? DurationSeconds { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Description { get; init; }
    /// <summary>Posters to try, best first.</summary>
    public IReadOnlyList<Uri> PosterUrls { get; init; } = [];
}

/// <summary>
/// A YouTube or Vimeo video's title, channel, poster, length, publish date and
/// description, from the sites' public pages (behaviour.md, "Video links").
/// Never downloads the video itself.
/// </summary>
public static partial class VideoDetails
{
    public const int MaxDescriptionLength = 5_000;

    public static async Task<VideoInfo> Fetch(LinkFetcher fetcher, VideoLink video, CancellationToken cancellation = default) =>
        video.Provider == VideoProvider.YouTube
            ? await YouTube(fetcher, video.VideoId, cancellation)
            : await Vimeo(fetcher, video.VideoId, cancellation);

    /// <summary>The first poster that downloads and decodes, as it came.</summary>
    public static async Task<byte[]?> Poster(LinkFetcher fetcher, IImageScaler images, VideoInfo info, CancellationToken cancellation = default)
    {
        foreach (var url in info.PosterUrls)
        {
            try
            {
                var response = await fetcher.Get(url, "image/*", youTubeConsent: true, cancellation: cancellation);
                if (response.Status == 200 && images.IsImage(response.Body)) return response.Body;
            }
            catch (LinkUnavailableException)
            {
            }
        }
        return null;
    }

    private static async Task<VideoInfo> YouTube(LinkFetcher fetcher, string id, CancellationToken cancellation)
    {
        var watch = new Uri($"https://www.youtube.com/watch?v={Uri.EscapeDataString(id)}");
        var oEmbedUrl = new Uri($"https://www.youtube.com/oembed?url={Uri.EscapeDataString(watch.AbsoluteUri)}&format=json");

        // oEmbed refuses videos that can't be embedded, but the watch page still has their details.
        var oEmbed = await fetcher.Get(oEmbedUrl, "application/json", youTubeConsent: true, cancellation: cancellation);
        if (oEmbed.Status >= 500) throw new LinkUnavailableException();
        var info = oEmbed.Status == 200 ? ParseOEmbed(oEmbed.Body) ?? new VideoInfo() : new VideoInfo();

        try
        {
            var page = await fetcher.Get(watch, "text/html", youTubeConsent: true, cancellation: cancellation);
            if (page.Status == 200)
            {
                var parsed = ParseYouTubePage(LinkFetcher.Text(page.Body));
                info = info with
                {
                    Title = info.Title ?? parsed.Title,
                    Channel = info.Channel ?? parsed.Channel,
                    DurationSeconds = parsed.DurationSeconds,
                    PublishedAt = parsed.PublishedAt,
                    Description = parsed.Description,
                };
            }
        }
        catch (LinkUnavailableException)
        {
        }
        if (info.Title is null && info.DurationSeconds is null) throw new LinkNotFoundException();
        // maxresdefault is 1280 × 720 but missing for some older videos; hqdefault always exists.
        return info with
        {
            PosterUrls =
            [
                new Uri($"https://i.ytimg.com/vi/{Uri.EscapeDataString(id)}/maxresdefault.jpg"),
                new Uri($"https://i.ytimg.com/vi/{Uri.EscapeDataString(id)}/hqdefault.jpg"),
            ],
        };
    }

    private static async Task<VideoInfo> Vimeo(LinkFetcher fetcher, string id, CancellationToken cancellation)
    {
        var url = new Uri($"https://vimeo.com/api/oembed.json?url={Uri.EscapeDataString($"https://vimeo.com/{id}")}&width=1280");
        var response = await fetcher.Get(url, "application/json", cancellation: cancellation);
        if (response.Status is >= 400 and < 500) throw new LinkNotFoundException();
        if (response.Status is < 200 or >= 300) throw new LinkUnavailableException();
        return ParseOEmbed(response.Body) ?? throw new LinkNotFoundException();
    }

    // MARK: Parsing

    /// <summary>Reads an oEmbed response from YouTube or Vimeo.</summary>
    public static VideoInfo? ParseOEmbed(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Trimmed(value.GetString()) : null;
            double? Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
            var thumbnail = Text("thumbnail_url");
            return new VideoInfo
            {
                Title = Text("title"),
                Channel = Text("author_name"),
                ChannelUrl = Uri.TryCreate(Text("author_url"), UriKind.Absolute, out var channel) ? channel.AbsoluteUri : null,
                DurationSeconds = Number("duration") is > 0 and var d ? d : null,
                PublishedAt = Text("upload_date") is { } date ? ParseDate(date) : null,
                Description = CleanDescription(Text("description")),
                PosterUrls = Uri.TryCreate(thumbnail, UriKind.Absolute, out var poster) ? [poster] : [],
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads details from a YouTube watch page. Missing pieces stay null, so a layout change degrades quietly.</summary>
    public static VideoInfo ParseYouTubePage(string html)
    {
        double? duration = null;
        if (Match(LengthSeconds(), html) is { } length && double.TryParse(length, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            duration = seconds;
        else if (MetaItemprop(html, "duration") is { } iso)
            duration = ParseIsoDuration(iso);

        string? description = null;
        if (Match(ShortDescription(), html) is { } escaped)
        {
            try { description = CleanDescription(JsonSerializer.Deserialize<string>($"\"{escaped}\"")); }
            catch (JsonException) { }
        }

        var published = MetaItemprop(html, "datePublished") ?? Match(PublishDate(), html);
        var title = Match(MetaTitle(), html);
        return new VideoInfo
        {
            DurationSeconds = duration,
            PublishedAt = published is null ? null : ParseDate(published),
            Description = description,
            Title = title is null ? null : Trimmed(DecodeBasicEntities(title)),
            Channel = Trimmed(Match(OwnerChannel(), html)),
        };
    }

    /// <summary>"PT3M34S", "PT1H2M3S" or "P0DT45S" as seconds.</summary>
    public static double? ParseIsoDuration(string text)
    {
        if (!text.StartsWith('P')) return null;
        double total = 0;
        var number = "";
        var inTime = false;
        foreach (var c in text[1..])
        {
            if (c == 'T') { inTime = true; continue; }
            if (char.IsAsciiDigit(c) || c == '.') { number += c; continue; }
            if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
            number = "";
            switch (c, inTime)
            {
                case ('D', false): total += value * 86_400; break;
                case ('H', true): total += value * 3_600; break;
                case ('M', true): total += value * 60; break;
                case ('S', true): total += value; break;
                default: return null;
            }
        }
        return number.Length == 0 && total > 0 ? total : null;
    }

    /// <summary>ISO 8601 with a time zone, a plain date, or Vimeo's "2013-10-15 14:08:29" (UTC).</summary>
    public static DateTimeOffset? ParseDate(string text)
    {
        text = text.Trim();
        var styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"];
        if (DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, styles, out var date)) return date;
        if (text.Length >= 10 && DateTimeOffset.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, styles, out date)) return date;
        return null;
    }

    private static string? CleanDescription(string? text) =>
        Trimmed(text) is { } clean ? ReferenceImporter.Cut(clean, MaxDescriptionLength) : null;

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Match(Regex regex, string text) => regex.Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    private static string? MetaItemprop(string html, string name) =>
        Regex.Match(html, $"<meta itemprop=\"{Regex.Escape(name)}\" content=\"([^\"]*)\"") is { Success: true } m ? m.Groups[1].Value : null;

    private static string DecodeBasicEntities(string text) =>
        text.Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    [GeneratedRegex("\"lengthSeconds\":\"(\\d+)\"")]
    private static partial Regex LengthSeconds();

    [GeneratedRegex("\"publishDate\":\"([^\"]+)\"")]
    private static partial Regex PublishDate();

    [GeneratedRegex("\"shortDescription\":\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex ShortDescription();

    [GeneratedRegex("<meta name=\"title\" content=\"([^\"]*)\"")]
    private static partial Regex MetaTitle();

    [GeneratedRegex("\"ownerChannelName\":\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex OwnerChannel();
}
