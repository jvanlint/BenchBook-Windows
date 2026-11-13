using System.Globalization;

namespace BenchBook.Core.Capture;

public enum VideoProvider { YouTube, Vimeo }

/// <summary>A recognised video address (behaviour.md, "Video links").</summary>
public sealed record VideoLink(VideoProvider Provider, string VideoId, double? StartSeconds)
{
    private static readonly HashSet<string> YouTubeHosts =
        ["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "www.youtube-nocookie.com"];

    private static readonly HashSet<string> VimeoHosts = ["vimeo.com", "www.vimeo.com", "player.vimeo.com"];

    public static VideoLink? Parse(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? Parse(uri) : null;

    public static VideoLink? Parse(Uri url)
    {
        if (!url.IsAbsoluteUri || string.IsNullOrEmpty(url.Host)) return null;
        var host = url.Host.ToLowerInvariant();
        var path = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var query = Query(url.Query);
        var timestamp = query.FirstOrDefault(q => q.Name is "t" or "start").Value ?? FragmentTimestamp(url.Fragment);
        double? start = timestamp is null ? null : ParseTimestamp(timestamp);

        if (host is "youtu.be" or "www.youtu.be")
            return path.Length > 0 && IsPlausibleId(path[0]) ? new VideoLink(VideoProvider.YouTube, path[0], start) : null;

        if (YouTubeHosts.Contains(host))
        {
            string? id = null;
            if (path.Length > 0 && path[0] == "watch") id = query.FirstOrDefault(q => q.Name == "v").Value;
            else if (path.Length > 1 && path[0] is "shorts" or "live" or "embed" or "v") id = path[1];
            return id is not null && IsPlausibleId(id) ? new VideoLink(VideoProvider.YouTube, id, start) : null;
        }

        if (VimeoHosts.Contains(host))
        {
            var id = path.LastOrDefault(p => p.Length > 0 && p.All(char.IsAsciiDigit));
            return id is null ? null : new VideoLink(VideoProvider.Vimeo, id, start);
        }
        return null;
    }

    /// <summary>Accepts "90", "90s", "1m30s", "1h2m3s", "1:30" and "1:02:03".</summary>
    public static double? ParseTimestamp(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        if (text.Length == 0) return null;

        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length is < 2 or > 3) return null;
            double total = 0;
            foreach (var part in parts)
            {
                if (!TryNumber(part, out var number) || number < 0) return null;
                total = total * 60 + number;
            }
            return total;
        }
        if (TryNumber(text, out var seconds)) return double.IsFinite(seconds) && seconds >= 0 ? seconds : null;

        double sum = 0;
        var digits = "";
        var sawUnit = false;
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c) || c == '.')
            {
                digits += c;
                continue;
            }
            if (!TryNumber(digits, out var amount)) return null;
            switch (c)
            {
                case 'h': sum += amount * 3600; break;
                case 'm': sum += amount * 60; break;
                case 's': sum += amount; break;
                default: return null;
            }
            digits = "";
            sawUnit = true;
        }
        return digits.Length == 0 && sawUnit && double.IsFinite(sum) ? sum : null;
    }

    /// <summary>A link that opens the video at <paramref name="seconds"/>. Unknown addresses come back unchanged.</summary>
    public static string PlaybackUrl(string sourceUrl, double seconds)
    {
        if (Parse(sourceUrl) is not { } video) return sourceUrl;
        var whole = Math.Max(0, (long)Math.Floor(seconds));
        return video.Provider == VideoProvider.YouTube
            ? $"https://www.youtube.com/watch?v={Uri.EscapeDataString(video.VideoId)}&t={whole}s"
            : $"https://vimeo.com/{video.VideoId}#t={whole}s";
    }

    /// <summary>"0:45", "12:03" or "1:02:03".</summary>
    public static string Format(double seconds)
    {
        var whole = Math.Max(0, (long)Math.Floor(seconds));
        var hours = whole / 3600;
        var minutes = whole % 3600 / 60;
        var secs = whole % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{secs:00}");
    }

    private static bool TryNumber(string text, out double number) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);

    private static string? FragmentTimestamp(string fragment)
    {
        var text = fragment.StartsWith('#') ? fragment[1..] : fragment;
        return text.StartsWith("t=", StringComparison.Ordinal) ? text[2..] : null;
    }

    private static bool IsPlausibleId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static List<(string Name, string? Value)> Query(string query)
    {
        var items = new List<(string, string?)>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            items.Add(equals < 0
                ? (Uri.UnescapeDataString(pair), null)
                : (Uri.UnescapeDataString(pair[..equals]), Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '))));
        }
        return items;
    }
}
