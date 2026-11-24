using BenchBook.Core.Model;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Store;

/// <summary>A build photo with what its tile and details need.</summary>
public sealed record BuildPhotoEntry(BuildPhoto Photo, Asset? Asset, IReadOnlyList<Tag> Tags, int NoteCount);

/// <summary>Build photos, their tags and box notes, and the project photo.</summary>
public sealed partial class LibraryStore
{
    // MARK: Reading

    /// <summary>A project's build photos, in no particular order (the gallery sorts them).</summary>
    public List<BuildPhotoEntry> ProjectBuildPhotos(string projectId) =>
        PhotoEntries(BuildPhotosTable.ReadWhere(_connection, "projectId = $id", ("$id", projectId)));

    /// <summary>Build photos by id, in the order given. Missing ones are left out.</summary>
    public List<BuildPhotoEntry> BuildPhotoEntries(IEnumerable<string> ids)
    {
        var wanted = ids.ToList();
        var byId = BuildPhotosTable.ReadAll(_connection, null).ToDictionary(p => p.Id, Ids.Comparer);
        return PhotoEntries(wanted.Where(byId.ContainsKey).Select(id => byId[id]).ToList());
    }

    /// <summary>Every build photo with the tag, across all projects.</summary>
    public List<BuildPhotoEntry> BuildPhotosWithTag(string tagId) =>
        PhotoEntries(BuildPhotosTable.ReadWhere(_connection,
            "id IN (SELECT buildPhotoId FROM build_photo_tags WHERE tagId = $id)", ("$id", tagId)));

    /// <summary>Every build photo in the library.</summary>
    public List<BuildPhotoEntry> AllBuildPhotos() => PhotoEntries(BuildPhotosTable.ReadAll(_connection, null));

    private List<BuildPhotoEntry> PhotoEntries(List<BuildPhoto> photos)
    {
        if (photos.Count == 0) return [];
        var ids = photos.Select(p => p.Id).ToHashSet(Ids.Comparer);
        var assetIds = photos.Select(p => p.AssetId).ToHashSet(Ids.Comparer);
        var assets = AssetsTable.ReadAll(_connection, null).Where(a => assetIds.Contains(a.Id)).ToDictionary(a => a.Id, Ids.Comparer);
        var tags = TagsTable.ReadAll(_connection, null).ToDictionary(t => t.Id, Ids.Comparer);
        var links = BuildPhotoTagsTable.ReadAll(_connection, null).Where(l => ids.Contains(l.BuildPhotoId))
            .GroupBy(l => l.BuildPhotoId, Ids.Comparer)
            .ToDictionary(g => g.Key, g => g.Select(l => tags[l.TagId]).OrderBy(t => t.Name, NaturalComparer.Instance).ToList(), Ids.Comparer);
        var noteCounts = new Dictionary<string, int>(Ids.Comparer);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT buildPhotoId, count(*) FROM markups WHERE buildPhotoId IS NOT NULL GROUP BY buildPhotoId";
            using var reader = command.ExecuteReader();
            while (reader.Read()) noteCounts[reader.GetString(0)] = reader.GetInt32(1);
        }
        return photos.Select(p => new BuildPhotoEntry(
            p,
            assets.GetValueOrDefault(p.AssetId),
            links.TryGetValue(p.Id, out var t) ? t : [],
            noteCounts.GetValueOrDefault(p.Id))).ToList();
    }

    public BuildPhoto? BuildPhoto(string id) => BuildPhotosTable.ReadWhere(_connection, "id = $id", ("$id", id)).FirstOrDefault();

    /// <summary>The project's build photo whose file has this hash, if any.</summary>
    public BuildPhoto? BuildPhotoWithFile(string projectId, string sha256) =>
        BuildPhotosTable.ReadWhere(_connection,
            "projectId = $project AND assetId IN (SELECT id FROM assets WHERE sha256 = $hash)",
            ("$project", projectId), ("$hash", sha256)).FirstOrDefault();

    /// <summary>A build photo's box notes.</summary>
    public List<Markup> BuildPhotoMarkups(string buildPhotoId) =>
        MarkupsTable.ReadWhere(_connection, "buildPhotoId = $id", ("$id", buildPhotoId));

    // MARK: Writing

    /// <summary>Saves a new build photo with its asset. All or nothing.</summary>
    public void InsertBuildPhoto(BuildPhoto photo, Asset asset) => RunInTransaction(t =>
    {
        AssetsTable.InsertAll(_connection, t, [asset]);
        BuildPhotosTable.InsertAll(_connection, t, [photo]);
        TouchProjects(t, [photo.ProjectId]);
    });

    /// <summary>Writes the photo's notes, date and stage and, if given, replaces its tags. False if it no longer exists.</summary>
    public bool UpdateBuildPhoto(BuildPhoto photo, IEnumerable<string>? tagNames = null)
    {
        var updated = false;
        RunInTransaction(t =>
        {
            updated = BuildPhotosTable.Update(_connection, photo, t);
            if (!updated) return;
            if (tagNames is not null) SetBuildPhotoTags(t, photo.Id, tagNames);
            TouchProjects(t, [photo.ProjectId]);
        });
        return updated;
    }

    /// <summary>Sets the stage of each photo that doesn't have it yet.</summary>
    public void SetBuildPhotoStage(IReadOnlyCollection<string> photoIds, string stage) => RunInTransaction(t =>
    {
        var now = IsoDate.Write(IsoDate.Now());
        foreach (var id in photoIds)
            Run("UPDATE build_photos SET stage = $stage, updatedAt = $now WHERE id = $id AND stage <> $stage", t, ("$stage", stage), ("$now", now), ("$id", id));
        TouchBuildPhotoProjects(t, photoIds);
    });

    /// <summary>Shows the photo in its project's header, or none.</summary>
    public void SetProjectPhoto(string projectId, string? photoId) => RunInTransaction(t =>
    {
        Run("UPDATE projects SET projectPhotoId = $photo WHERE id = $id", t, ("$photo", (object?)photoId ?? DBNull.Value), ("$id", projectId));
        TouchProjects(t, [projectId]);
    });

    /// <summary>
    /// Deletes build photos with their tag links and box notes, and clears a
    /// project photo that was one of them. Returns the assets they used, so
    /// unused files can be tidied.
    /// </summary>
    public List<string> DeleteBuildPhotos(IReadOnlyCollection<string> photoIds)
    {
        var assets = new List<string>();
        RunInTransaction(t =>
        {
            TouchBuildPhotoProjects(t, photoIds);
            foreach (var id in photoIds)
            {
                Run("UPDATE projects SET projectPhotoId = NULL WHERE projectPhotoId = $id", t, ("$id", id));
                using var command = _connection.CreateCommand();
                command.Transaction = t;
                command.CommandText = "DELETE FROM build_photos WHERE id = $id RETURNING assetId";
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteScalar() is string asset) assets.Add(asset);
            }
        });
        return assets.Distinct(Ids.Comparer).ToList();
    }

    // MARK: Tags

    /// <summary>Adds the tag to every build photo that doesn't have it.</summary>
    public void AddTagToBuildPhotos(string tagId, IReadOnlyCollection<string> photoIds) => RunInTransaction(t =>
    {
        LinkBuildPhotos(tagId, photoIds, t);
        TouchBuildPhotoProjects(t, photoIds);
    });

    /// <summary>Adds tags by name (commas allowed) to every build photo that lacks them, creating new tags as needed.</summary>
    public void AddTagsToBuildPhotos(string text, IReadOnlyCollection<string> photoIds)
    {
        var names = TagNames.Parse(text);
        if (names.Count == 0) return;
        RunInTransaction(t =>
        {
            foreach (var name in names) LinkBuildPhotos(TagFor(name, t).Id, photoIds, t);
            TouchBuildPhotoProjects(t, photoIds);
        });
    }

    public void RemoveTagFromBuildPhotos(string tagId, IReadOnlyCollection<string> photoIds) => RunInTransaction(t =>
    {
        foreach (var id in photoIds)
        {
            Run("DELETE FROM build_photo_tags WHERE tagId = $t AND buildPhotoId = $p", t, ("$t", tagId), ("$p", id));
            Touch("build_photos", id, t);
        }
        TouchBuildPhotoProjects(t, photoIds);
    });

    private void LinkBuildPhotos(string tagId, IEnumerable<string> photoIds, SqliteTransaction transaction)
    {
        var now = IsoDate.Now();
        foreach (var photoId in photoIds)
        {
            var exists = BuildPhotoTagsTable.ReadWhere(_connection, "tagId = $t AND buildPhotoId = $p", transaction, ("$t", tagId), ("$p", photoId)).Count > 0;
            if (exists) continue;
            BuildPhotoTagsTable.InsertAll(_connection, transaction,
                [new BuildPhotoTag { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, BuildPhotoId = photoId, TagId = tagId }]);
            Touch("build_photos", photoId, transaction);
        }
    }

    /// <summary>Makes the photo's tags exactly these names, reusing tags by normalized name and keeping their spelling.</summary>
    private void SetBuildPhotoTags(SqliteTransaction transaction, string photoId, IEnumerable<string> names)
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var clean = TagNames.CollapseWhitespace(name);
            if (clean.Length > 0) wanted.TryAdd(TagNames.Normalize(clean), clean);
        }
        var current = BuildPhotoTagsTable.ReadWhere(_connection, "buildPhotoId = $id", transaction, ("$id", photoId));
        var tagsById = TagsTable.ReadAll(_connection, transaction).ToDictionary(t => t.Id, Ids.Comparer);
        var tagsByName = tagsById.Values.ToDictionary(t => t.NormalizedName, StringComparer.Ordinal);

        foreach (var link in current.Where(l => !wanted.ContainsKey(tagsById[l.TagId].NormalizedName)))
            Run("DELETE FROM build_photo_tags WHERE id = $id", transaction, ("$id", link.Id));

        var have = current.Select(l => tagsById[l.TagId].NormalizedName).ToHashSet(StringComparer.Ordinal);
        var now = IsoDate.Now();
        foreach (var (normalized, name) in wanted.Where(w => !have.Contains(w.Key)).OrderBy(w => w.Key, StringComparer.Ordinal))
        {
            if (!tagsByName.TryGetValue(normalized, out var tag))
            {
                tag = new Tag { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, Name = name, NormalizedName = normalized };
                TagsTable.InsertAll(_connection, transaction, [tag]);
                tagsByName[normalized] = tag;
            }
            BuildPhotoTagsTable.InsertAll(_connection, transaction,
                [new BuildPhotoTag { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, BuildPhotoId = photoId, TagId = tag.Id }]);
        }
    }
}
