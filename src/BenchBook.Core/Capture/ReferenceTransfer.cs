using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Capture;

public enum TransferMode { Move, Copy }

/// <param name="Transferred">The moved references, or the new copies.</param>
/// <param name="Duplicates">References whose file the target project already has. They're left as they were.</param>
public sealed record TransferResult(IReadOnlyList<ReferenceEntry> Transferred, IReadOnlyList<ReferenceEntry> Duplicates)
{
    /// <summary>The notice after a move or copy, e.g. "Moved 3 references to F-16C."</summary>
    public string Notice(TransferMode mode, string projectName)
    {
        var lines = new List<string>();
        var verb = mode == TransferMode.Move ? "Moved" : "Copied";
        if (Transferred.Count == 1) lines.Add($"{verb} “{ReferenceText.FallbackTitle(Transferred[0])}” to {projectName}.");
        else if (Transferred.Count > 1) lines.Add($"{verb} {Transferred.Count:N0} references to {projectName}.");
        if (Duplicates.Count == 1) lines.Add($"{projectName} already has “{ReferenceText.FallbackTitle(Duplicates[0])}”, so it was left as it is.");
        else if (Duplicates.Count > 1) lines.Add($"{projectName} already has {Duplicates.Count:N0} of these files, so they were left as they are.");
        return string.Join(" ", lines);
    }
}

/// <summary>
/// Moves or copies references to another project (behaviour.md, "Moving and
/// copying references"). A project holds each file once, so a reference whose
/// file the target already has is skipped.
/// </summary>
public static class ReferenceTransfer
{
    public static TransferResult Transfer(LibraryStore store, LibraryLocation location, IReadOnlyList<string> referenceIds, string targetProjectId, TransferMode mode)
    {
        if (store.Project(targetProjectId) is null) throw CaptureException.ProjectNotFound();
        var entries = store.ReferenceEntries(referenceIds).Where(e => !Ids.Comparer.Equals(e.Reference.ProjectId, targetProjectId)).ToList();
        var hashes = store.ProjectReferences(targetProjectId).Where(e => e.Asset is not null).Select(e => e.Asset!.Sha256).ToHashSet(StringComparer.Ordinal);

        var transferred = new List<ReferenceEntry>();
        var duplicates = new List<ReferenceEntry>();
        var posters = new List<(string From, string To)>();
        var now = IsoDate.Now();

        store.RunInTransaction(transaction =>
        {
            foreach (var entry in entries)
            {
                if (entry.Asset is { } asset && !hashes.Add(asset.Sha256))
                {
                    duplicates.Add(entry);
                    continue;
                }
                if (mode == TransferMode.Move)
                {
                    entry.Reference.ProjectId = targetProjectId;
                    entry.Reference.UpdatedAt = now;
                    store.UpdateReferenceRow(entry.Reference, transaction);
                    transferred.Add(entry);
                }
                else
                {
                    var copy = Copy(entry, targetProjectId, now);
                    store.InsertCopy(entry.Reference.Id, copy, transaction);
                    posters.Add((location.PosterPath(entry.Reference.Id), location.PosterPath(copy.Reference.Id)));
                    transferred.Add(copy);
                }
            }
        });

        // Posters are named by reference, so each copy gets its own.
        foreach (var (from, to) in posters)
        {
            try { if (File.Exists(from)) File.Copy(from, to, overwrite: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return new TransferResult(transferred, duplicates);
    }

    /// <summary>A new reference with the same values, tags, moments and markups (new ids, same creation dates), sharing the asset.</summary>
    private static ReferenceEntry Copy(ReferenceEntry entry, string projectId, DateTimeOffset now)
    {
        var o = entry.Reference;
        var reference = new Reference
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ProjectId = projectId, Kind = o.Kind,
            Title = o.Title, Note = o.Note, Text = o.Text, SourcePageUrl = o.SourcePageUrl, OriginalResourceUrl = o.OriginalResourceUrl,
            CapturedAt = o.CapturedAt, AssetId = o.AssetId,
            VideoChannel = o.VideoChannel, VideoChannelUrl = o.VideoChannelUrl, VideoDurationSeconds = o.VideoDurationSeconds,
            VideoPublishedAt = o.VideoPublishedAt, VideoDescription = o.VideoDescription, VideoInfoFetchedAt = o.VideoInfoFetchedAt,
            LinkSiteName = o.LinkSiteName, LinkDescription = o.LinkDescription, LinkPreviewFetchedAt = o.LinkPreviewFetchedAt,
        };
        var moments = entry.Moments.Select(m => new VideoMoment
        {
            Id = Ids.New(), CreatedAt = m.CreatedAt, UpdatedAt = now, ReferenceId = reference.Id,
            OffsetSeconds = m.OffsetSeconds, Title = m.Title, Note = m.Note,
        }).ToList();
        return new ReferenceEntry(reference, entry.Asset, entry.Tags, entry.MarkupCount, moments);
    }
}
