using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Links;

/// <summary>
/// Fills in details and posters for video links and webpages after they're
/// saved (behaviour.md, "Link details"). Saving never waits for this.
/// </summary>
public sealed class LinkDetails(IStoreAccess store, LibraryLocation location, LinkFetcher fetcher, IImageScaler images)
{
    /// <summary>Video links and webpages whose details have never been fetched.</summary>
    public List<string> Missing() => store.With(s => s.ReadSnapshot().References
        .Where(r => (r.Kind == ReferenceKind.VideoLink && r.VideoInfoFetchedAt is null)
                 || (r.Kind == ReferenceKind.Webpage && r.LinkPreviewFetchedAt is null))
        .Select(r => r.Id).ToList());

    /// <summary>
    /// Fetches now, replacing details already stored. True if anything was saved.
    /// A 4xx answer records the attempt; offline or a 5xx records nothing.
    /// </summary>
    public async Task<bool> Refresh(string referenceId, CancellationToken cancellation = default)
    {
        var reference = store.With(s => s.Reference(referenceId));
        if (reference?.SourcePageUrl is not { } address || !Uri.TryCreate(address, UriKind.Absolute, out var url)) return false;
        var poster = location.PosterPath(reference.Id);
        var now = IsoDate.Now();
        try
        {
            if (reference.Kind == ReferenceKind.VideoLink)
            {
                if (VideoLink.Parse(url) is not { } video) return false;
                var info = await VideoDetails.Fetch(fetcher, video, cancellation);
                if (await VideoDetails.Poster(fetcher, images, info, cancellation) is { } data) StorePoster(poster, data);
                return Save(referenceId, r =>
                {
                    if (info.Title is not null && ShouldReplaceTitle(r.Title)) r.Title = info.Title;
                    r.VideoChannel = info.Channel;
                    r.VideoChannelUrl = info.ChannelUrl;
                    r.VideoDurationSeconds = info.DurationSeconds;
                    r.VideoPublishedAt = info.PublishedAt;
                    r.VideoDescription = info.Description;
                    r.VideoInfoFetchedAt = now;
                });
            }
            if (reference.Kind == ReferenceKind.Webpage)
            {
                var info = await PageDetails.Fetch(fetcher, url, cancellation);
                // If no image downloads, any earlier one is kept.
                if (await PageDetails.Preview(fetcher, images, info, cancellation: cancellation) is { } preview) StorePoster(poster, preview.Data);
                return Save(referenceId, r =>
                {
                    if (info.Title is not null && ShouldReplaceTitle(r.Title, url.Host)) r.Title = info.Title;
                    r.LinkSiteName = info.SiteName;
                    r.LinkDescription = info.Description;
                    r.LinkPreviewFetchedAt = now;
                });
            }
            return false;
        }
        catch (LinkNotFoundException)
        {
            // Gone, private or refused: record the attempt so it isn't retried at every launch.
            return Save(referenceId, r =>
            {
                if (r.Kind == ReferenceKind.VideoLink) r.VideoInfoFetchedAt = now;
                else r.LinkPreviewFetchedAt = now;
            });
        }
        catch (LinkUnavailableException)
        {
            return false; // Offline or a server problem: try again next time.
        }
    }

    /// <summary>
    /// True for a missing title, a bare link or host, "YouTube", or one ending in
    /// " - YouTube" or " on Vimeo". Never true for something the user typed.
    /// </summary>
    public static bool ShouldReplaceTitle(string? title, string? host = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var lowered = title.Trim().ToLowerInvariant();
        return lowered == "youtube" || lowered.EndsWith(" - youtube", StringComparison.Ordinal) || lowered.EndsWith(" on vimeo", StringComparison.Ordinal)
            || lowered.StartsWith("http://", StringComparison.Ordinal) || lowered.StartsWith("https://", StringComparison.Ordinal)
            || (host is not null && lowered == host.ToLowerInvariant());
    }

    /// <summary>Applies the change to the reference as it is now, since it may have been edited or deleted while fetching.</summary>
    private bool Save(string referenceId, Action<Reference> change) => store.With(s =>
    {
        if (s.Reference(referenceId) is not { } current) return false;
        change(current);
        current.UpdatedAt = IsoDate.Now();
        return s.UpdateReference(current);
    });

    private static void StorePoster(string path, byte[] data)
    {
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder);
            var partial = Path.Combine(folder, LibraryPaths.PartialPrefix + Ids.New());
            File.WriteAllBytes(partial, data);
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
