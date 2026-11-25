using BenchBook.Core.Kits;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using BenchBook.Core.Projects;
using BenchBook.Core.Store;

namespace BenchBook.Core.Backup;

public enum ProjectImportMode
{
    /// <summary>The project isn't in the library.</summary>
    Add,
    /// <summary>Replace the library's project that has the same id.</summary>
    Replace,
    /// <summary>Keep the library's project and add this one beside it, with new ids and " Copy" after its name.</summary>
    KeepBoth,
}

/// <summary>What an import brought in, for "Project Imported".</summary>
public sealed record ProjectImportSummary(
    string ProjectId,
    string ProjectName,
    int PaintsAdded,
    int PaintsMatched,
    int TagsAdded,
    int KitsAdded,
    int KitsMatched,
    IReadOnlyList<string> KitsInOtherProjects)
{
    /// <summary>
    /// e.g. "“F-16C Viper” has 212 references, 14 build photos and 4 aftermarket
    /// parts.", then the paints, tags and kits lines that apply (screens.md, "Export and import a project").
    /// </summary>
    public string Message(ProjectFileCounts counts)
    {
        static string Count(long count, string singular, string plural) => $"{count:N0} {(count == 1 ? singular : plural)}";
        var lines = new List<string>
        {
            $"“{ProjectName}” has {Count(counts.References, "reference", "references")}, {Count(counts.BuildPhotos, "build photo", "build photos")} and {Count(counts.AftermarketItems, "aftermarket part", "aftermarket parts")}.",
        };
        if (PaintsAdded + PaintsMatched > 0) lines.Add($"Paints: {PaintsAdded:N0} added to your catalogue, {PaintsMatched:N0} already there.");
        if (TagsAdded > 0) lines.Add($"{Count(TagsAdded, "new tag", "new tags")}.");
        if (KitsAdded + KitsMatched > 0) lines.Add($"Kits: {KitsAdded:N0} added to your catalogue, {KitsMatched:N0} already there.");
        if (KitsInOtherProjects.Count > 0)
        {
            var names = KitsInOtherProjects.Count == 1
                ? KitsInOtherProjects[0]
                : $"{string.Join(", ", KitsInOtherProjects.Take(KitsInOtherProjects.Count - 1))} and {KitsInOtherProjects[^1]}";
            lines.Add($"{names} stayed in the project already building them.");
        }
        return string.Join("\n", lines);
    }
}

/// <summary>
/// Adds a project file's project to the library, merging with what's there
/// (library-format.md, "Project file › Importing"): paints match by identity key
/// and tags by name, and neither is changed; files already in the library (same
/// asset id and checksum) are shared; kits are never copied. It's one save, so
/// it all imports or nothing does. The file's records are used up: discard the
/// file afterwards.
/// </summary>
public static class ProjectImporter
{
    /// <summary>The library's project with the file's project id, if any.</summary>
    public static Project? ExistingProject(LibraryStore store, ProjectFile file) => store.Project(file.Project.Id);

    public static ProjectImportSummary Import(LibraryStore store, LibraryLocation location, ProjectFile file, ProjectImportMode mode)
    {
        var records = file.Records;
        var library = store.ReadSnapshot();
        var replaced = mode == ProjectImportMode.Replace
            ? library.Projects.FirstOrDefault(p => Ids.Comparer.Equals(p.Id, records.Projects[0].Id))
            : null;
        var keepBoth = mode == ProjectImportMode.KeepBoth;

        // Records of the replaced project free their ids; nothing else's do.
        var freed = replaced is null ? new HashSet<string>(Ids.Comparer) : OwnedIds(library, replaced.Id);
        var taken = new HashSet<string>(library.AllRecords().Select(r => r.Id).Where(id => !freed.Contains(id)), Ids.Comparer);
        // The file's id, unless the library uses it (or Keep Both asks for new ones).
        string Fresh(string id, bool renew = false)
        {
            var result = renew || taken.Contains(id) ? Ids.New() : id;
            taken.Add(result);
            return result;
        }
        string NewId(string id) => Fresh(id, keepBoth);

        var added = new LibrarySnapshot();
        var copied = new List<string>();
        try
        {
            var assets = MergeAssets(library, records, location, file, added, copied, id => Fresh(id));
            var (tags, tagsAdded) = MergeTags(library, records, added, id => Fresh(id));
            var (paints, paintsAdded, paintsMatched) = MergePaints(library, records, added, id => Fresh(id));

            // The project and everything that's its own.
            var project = records.Projects[0];
            var oldProjectId = project.Id;
            project.Id = NewId(project.Id);
            if (keepBoth) project.Name = $"{project.Name} Copy";
            added.Projects.Add(project);

            var references = new Dictionary<string, string>(Ids.Comparer);
            foreach (var reference in records.References)
            {
                var old = reference.Id;
                reference.Id = NewId(old);
                reference.ProjectId = project.Id;
                reference.AssetId = Mapped(assets, reference.AssetId);
                references[old] = reference.Id;
                added.References.Add(reference);
            }
            AddLinks(records.ReferenceTags, added.ReferenceTags, l => l.ReferenceId, (l, v) => l.ReferenceId = v, references, tags, NewId);
            foreach (var moment in records.VideoMoments)
            {
                moment.Id = NewId(moment.Id);
                moment.ReferenceId = references[moment.ReferenceId];
                added.VideoMoments.Add(moment);
            }

            var photos = new Dictionary<string, string>(Ids.Comparer);
            foreach (var photo in records.BuildPhotos)
            {
                var old = photo.Id;
                photo.Id = NewId(old);
                photo.ProjectId = project.Id;
                photo.AssetId = assets[photo.AssetId];
                photos[old] = photo.Id;
                added.BuildPhotos.Add(photo);
            }
            project.ProjectPhotoId = Mapped(photos, project.ProjectPhotoId);
            AddLinks(records.BuildPhotoTags, added.BuildPhotoTags, l => l.BuildPhotoId, (l, v) => l.BuildPhotoId = v, photos, tags, NewId);
            foreach (var markup in records.Markups)
            {
                markup.Id = NewId(markup.Id);
                markup.ReferenceId = Mapped(references, markup.ReferenceId);
                markup.BuildPhotoId = Mapped(photos, markup.BuildPhotoId);
                added.Markups.Add(markup);
            }

            foreach (var row in records.ProjectPaints)
            {
                row.Id = NewId(row.Id);
                row.ProjectId = project.Id;
                row.PaintId = Mapped(paints, row.PaintId);
                added.ProjectPaints.Add(row);
            }
            foreach (var item in records.AftermarketItems)
            {
                item.Id = NewId(item.Id);
                item.ProjectId = project.Id;
                item.ImageAssetId = Mapped(assets, item.ImageAssetId);
                added.AftermarketItems.Add(item);
            }

            // A kit is one physical box: it's never copied, and a kit building another project stays there.
            var libraryKits = library.Kits.ToDictionary(k => k.Id, Ids.Comparer);
            var kitsToJoin = new List<string>();
            var kitsInOtherProjects = new List<string>();
            var kitsAdded = 0;
            foreach (var kit in records.Kits)
            {
                if (libraryKits.TryGetValue(kit.Id, out var existing))
                {
                    if (existing.ProjectId is not null && !Ids.Comparer.Equals(existing.ProjectId, replaced?.Id))
                        kitsInOtherProjects.Add(KitText.DisplayName(existing));
                    else
                        kitsToJoin.Add(existing.Id);
                    continue;
                }
                kit.Id = NewId(kit.Id);
                kit.ProjectId = project.Id;
                kit.PictureAssetId = Mapped(assets, kit.PictureAssetId);
                added.Kits.Add(kit);
                kitsAdded++;
            }

            // One save. The records keep the file's dates, so "last updated" isn't changed.
            store.InsertImportedProject(replaced?.Id, added, kitsToJoin, project.Id);

            AfterSave(store, location, file, library, replaced, references);
            return new ProjectImportSummary(project.Id, project.Name, paintsAdded, paintsMatched, tagsAdded,
                kitsAdded, kitsToJoin.Count, kitsInOtherProjects);
        }
        catch
        {
            foreach (var path in copied) BackupWriter.TryDeleteFile(path);
            throw;
        }
    }

    private static string? Mapped(Dictionary<string, string> map, string? id) =>
        id is not null && map.TryGetValue(id, out var mapped) ? mapped : null;

    /// <summary>Tag links, once per tag, pointing at the imported records and the merged tags.</summary>
    private static void AddLinks<T>(
        List<T> links, List<T> into, Func<T, string> owner, Action<T, string> setOwner,
        Dictionary<string, string> owners, Dictionary<string, string> tags, Func<string, string> newId)
        where T : LibraryRecord
    {
        var seen = new HashSet<(string, string)>();
        foreach (var link in links)
        {
            var ownerId = owners[owner(link)];
            var tagId = tags[link switch { ReferenceTag r => r.TagId, BuildPhotoTag b => b.TagId, _ => throw new InvalidOperationException() }];
            // Two tags in the file can match one tag in the library.
            if (!seen.Add((ownerId.ToUpperInvariant(), tagId.ToUpperInvariant()))) continue;
            link.Id = newId(link.Id);
            setOwner(link, ownerId);
            switch (link)
            {
                case ReferenceTag r: r.TagId = tagId; break;
                case BuildPhotoTag b: b.TagId = tagId; break;
            }
            into.Add(link);
        }
    }

    /// <summary>
    /// Files: an asset whose id and checksum are already in the library is
    /// shared. Otherwise the file is copied into Originals under its id (a new
    /// one if that's taken). Returns the file's asset ids mapped to the library's.
    /// </summary>
    private static Dictionary<string, string> MergeAssets(
        LibrarySnapshot library, LibrarySnapshot records, LibraryLocation location, ProjectFile file,
        LibrarySnapshot added, List<string> copied, Func<string, string> fresh)
    {
        var libraryAssets = library.Assets.ToDictionary(a => a.Id, Ids.Comparer);
        var assets = new Dictionary<string, string>(Ids.Comparer);
        foreach (var asset in records.Assets)
        {
            var old = asset.Id;
            if (libraryAssets.TryGetValue(old, out var existing) && existing.Sha256 == asset.Sha256)
            {
                assets[old] = existing.Id;
                continue;
            }
            var source = file.PathOf(BackupWriter.LibraryPrefix + asset.OriginalRelativePath);
            asset.Id = fresh(old);
            asset.OriginalRelativePath = $"Originals/{asset.Id}{Path.GetExtension(asset.OriginalRelativePath)}";
            var destination = location.FullPath(asset.OriginalRelativePath)!;
            Directory.CreateDirectory(location.OriginalsFolder);
            // Copied under a partial name first, so an interrupted copy is never taken for the file.
            var partial = Path.Combine(location.OriginalsFolder, LibraryPaths.PartialPrefix + Guid.NewGuid().ToString("N"));
            try
            {
                File.Copy(source, partial);
                File.Move(partial, destination, overwrite: true);
            }
            finally
            {
                BackupWriter.TryDeleteFile(partial);
            }
            copied.Add(destination);
            // A thumbnail left from an earlier file with this id would be wrong.
            BackupWriter.TryDeleteFile(location.ThumbnailPath(asset.Id));
            assets[old] = asset.Id;
            added.Assets.Add(asset);
        }
        return assets;
    }

    /// <summary>Tags by name, ignoring case and spacing. One already in the library is used as it is; a new one keeps its colour.</summary>
    private static (Dictionary<string, string> Tags, int Added) MergeTags(
        LibrarySnapshot library, LibrarySnapshot records, LibrarySnapshot added, Func<string, string> fresh)
    {
        var byName = library.Tags.ToDictionary(t => t.NormalizedName, t => t.Id, StringComparer.Ordinal);
        var tags = new Dictionary<string, string>(Ids.Comparer);
        var count = 0;
        foreach (var tag in records.Tags)
        {
            var normalized = TagNames.Normalize(tag.Name);
            if (byName.TryGetValue(normalized, out var existing))
            {
                tags[tag.Id] = existing;
                continue;
            }
            var old = tag.Id;
            tag.Id = fresh(old);
            tag.NormalizedName = normalized;
            byName[normalized] = tag.Id;
            tags[old] = tag.Id;
            added.Tags.Add(tag);
            count++;
        }
        return (tags, count);
    }

    /// <summary>
    /// Paints by identity key, recomputed from brand, range and code (brand and
    /// name for a mix). One already in the catalogue is used as it is and never
    /// changed. A new one is added, owned only if the file says so; a new mix gets its recipe.
    /// </summary>
    private static (Dictionary<string, string> Paints, int Added, int Matched) MergePaints(
        LibrarySnapshot library, LibrarySnapshot records, LibrarySnapshot added, Func<string, string> fresh)
    {
        var byKey = library.Paints.ToDictionary(p => p.IdentityKey, StringComparer.Ordinal);
        var owned = records.OwnedPaints.GroupBy(o => o.PaintId, Ids.Comparer).ToDictionary(g => g.Key, g => g.First(), Ids.Comparer);
        var paints = new Dictionary<string, string>(Ids.Comparer);
        var addedPaints = new Dictionary<string, Paint>(Ids.Comparer);
        int addedCount = 0, matched = 0;
        foreach (var paint in records.Paints)
        {
            var key = paint.IsMix ? PaintIdentity.MixKey(paint.Brand, paint.Name) : PaintIdentity.Key(paint.Brand, paint.Range, paint.Code);
            if (byKey.TryGetValue(key, out var existing))
            {
                paints[paint.Id] = existing.Id;
                matched++;
                continue;
            }
            var old = paint.Id;
            paint.Id = fresh(old);
            paint.IdentityKey = key;
            paint.SwatchHex = Swatch.Normalize(paint.SwatchHex);
            if (paint.StandardSystem.Length == 0)
            {
                paint.StandardCode = "";
                paint.Match = "";
            }
            byKey[key] = paint;
            paints[old] = paint.Id;
            addedPaints[old] = paint;
            added.Paints.Add(paint);
            addedCount++;
            if (!paint.IsMix && owned.TryGetValue(old, out var ownership))
            {
                ownership.Id = fresh(ownership.Id);
                ownership.PaintId = paint.Id;
                added.OwnedPaints.Add(ownership);
            }
        }

        // Recipes only for mixes this import added.
        var isMix = library.Paints.Concat(added.Paints).ToDictionary(p => p.Id, p => p.IsMix, Ids.Comparer);
        var lines = new HashSet<(string, string)>();
        foreach (var part in records.PaintMixParts.OrderBy(p => p.Position))
        {
            if (!addedPaints.TryGetValue(part.MixId, out var mix) || !paints.TryGetValue(part.PaintId, out var ingredient)) continue;
            if (isMix[ingredient] || !lines.Add((mix.Id.ToUpperInvariant(), ingredient.ToUpperInvariant()))) continue;
            part.Id = fresh(part.Id);
            part.MixId = mix.Id;
            part.PaintId = ingredient;
            added.PaintMixParts.Add(part);
        }
        return (paints, addedCount, matched);
    }

    /// <summary>The ids of a project's own records, freed when it's replaced. Not its kits, which aren't its own.</summary>
    private static HashSet<string> OwnedIds(LibrarySnapshot library, string projectId)
    {
        bool Mine(string? id) => Ids.Comparer.Equals(id, projectId);
        var references = new HashSet<string>(library.References.Where(r => Mine(r.ProjectId)).Select(r => r.Id), Ids.Comparer);
        var photos = new HashSet<string>(library.BuildPhotos.Where(p => Mine(p.ProjectId)).Select(p => p.Id), Ids.Comparer);
        var ids = new HashSet<string>(Ids.Comparer) { projectId };
        ids.UnionWith(references);
        ids.UnionWith(photos);
        ids.UnionWith(library.ReferenceTags.Where(t => references.Contains(t.ReferenceId)).Select(t => t.Id));
        ids.UnionWith(library.VideoMoments.Where(m => references.Contains(m.ReferenceId)).Select(m => m.Id));
        ids.UnionWith(library.Markups.Where(m => (m.ReferenceId is { } r && references.Contains(r)) || (m.BuildPhotoId is { } p && photos.Contains(p))).Select(m => m.Id));
        ids.UnionWith(library.BuildPhotoTags.Where(t => photos.Contains(t.BuildPhotoId)).Select(t => t.Id));
        ids.UnionWith(library.ProjectPaints.Where(p => Mine(p.ProjectId)).Select(p => p.Id));
        ids.UnionWith(library.AftermarketItems.Where(a => Mine(a.ProjectId)).Select(a => a.Id));
        return ids;
    }

    /// <summary>
    /// After the save: the replaced project's posters go, the file's posters are
    /// copied under the imported references' ids, and files the replaced project
    /// used that nothing uses now are removed. Posters are previews, so a missing one isn't an error.
    /// </summary>
    private static void AfterSave(
        LibraryStore store, LibraryLocation location, ProjectFile file, LibrarySnapshot library,
        Project? replaced, Dictionary<string, string> references)
    {
        var oldAssets = new List<string>();
        if (replaced is not null)
        {
            bool Mine(string? id) => Ids.Comparer.Equals(id, replaced.Id);
            foreach (var reference in library.References.Where(r => Mine(r.ProjectId)))
            {
                BackupWriter.TryDeleteFile(location.PosterPath(reference.Id));
                if (reference.AssetId is not null) oldAssets.Add(reference.AssetId);
            }
            oldAssets.AddRange(library.AftermarketItems.Where(a => Mine(a.ProjectId) && a.ImageAssetId is not null).Select(a => a.ImageAssetId!));
            oldAssets.AddRange(library.BuildPhotos.Where(p => Mine(p.ProjectId)).Select(p => p.AssetId));
            oldAssets.AddRange(library.Kits.Where(k => Mine(k.ProjectId) && k.PictureAssetId is not null).Select(k => k.PictureAssetId!));
        }

        foreach (var (old, id) in references)
        {
            var poster = file.PathOf($"{BackupWriter.LibraryPrefix}Posters/{old}.jpg");
            if (!File.Exists(poster)) continue;
            try
            {
                Directory.CreateDirectory(location.PostersFolder);
                File.Copy(poster, location.PosterPath(id), overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // It's made again when the link's details are fetched.
            }
        }

        ProjectService.RemoveUnusedFiles(store, location, oldAssets.Distinct(Ids.Comparer));
    }
}
