using BenchBook.Core.Model;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Store;

/// <summary>A reference with what its tile and details need.</summary>
public sealed record ReferenceEntry(
    Reference Reference, Asset? Asset, IReadOnlyList<Tag> Tags, int MarkupCount, IReadOnlyList<VideoMoment> Moments);

/// <summary>A tag and how many references use it.</summary>
public sealed record TagUsage(Tag Tag, int References, int Projects);

public sealed partial class LibraryStore
{
    // MARK: Reading

    /// <summary>A project's references, newest capture first.</summary>
    public List<ReferenceEntry> ProjectReferences(string projectId) =>
        Entries(ReferencesTable.ReadWhere(_connection, "projectId = $id", ("$id", projectId)));

    /// <summary>References by id, in the order given. Missing ones are left out.</summary>
    public List<ReferenceEntry> ReferenceEntries(IEnumerable<string> ids)
    {
        var wanted = ids.ToList();
        var found = new List<Reference>();
        foreach (var chunk in wanted.Chunk(500))
        {
            var names = chunk.Select((_, i) => "$r" + i).ToArray();
            found.AddRange(ReferencesTable.ReadWhere(_connection, $"id IN ({string.Join(", ", names)})",
                chunk.Select((id, i) => (names[i], (object)id)).ToArray()));
        }
        var byId = found.ToDictionary(r => r.Id, Ids.Comparer);
        return Entries(wanted.Where(byId.ContainsKey).Select(id => byId[id]).ToList(), keepOrder: true);
    }

    /// <summary>Every reference with the tag, across all projects.</summary>
    public List<ReferenceEntry> ReferencesWithTag(string tagId) =>
        Entries(ReferencesTable.ReadWhere(_connection,
            "id IN (SELECT referenceId FROM reference_tags WHERE tagId = $id)", ("$id", tagId)));

    private List<ReferenceEntry> Entries(List<Reference> references, bool keepOrder = false)
    {
        if (references.Count == 0) return [];
        var ids = new HashSet<string>(references.Select(r => r.Id), Ids.Comparer);
        var assetIds = new HashSet<string>(references.Where(r => r.AssetId is not null).Select(r => r.AssetId!), Ids.Comparer);

        // Small libraries are read whole; filtering in memory keeps the SQL simple.
        var assets = AssetsTable.ReadAll(_connection, null).Where(a => assetIds.Contains(a.Id)).ToDictionary(a => a.Id, Ids.Comparer);
        var tags = TagsTable.ReadAll(_connection, null).ToDictionary(t => t.Id, Ids.Comparer);
        var links = ReferenceTagsTable.ReadAll(_connection, null).Where(l => ids.Contains(l.ReferenceId))
            .GroupBy(l => l.ReferenceId, Ids.Comparer)
            .ToDictionary(g => g.Key, g => g.Select(l => tags[l.TagId]).OrderBy(t => t.Name, NaturalComparer.Instance).ToList(), Ids.Comparer);
        var markupCounts = new Dictionary<string, int>(Ids.Comparer);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT referenceId, count(*) FROM markups GROUP BY referenceId";
            using var reader = command.ExecuteReader();
            while (reader.Read()) markupCounts[reader.GetString(0)] = reader.GetInt32(1);
        }
        var moments = VideoMomentsTable.ReadAll(_connection, null).Where(m => ids.Contains(m.ReferenceId))
            .GroupBy(m => m.ReferenceId, Ids.Comparer)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.OffsetSeconds).ToList(), Ids.Comparer);

        var entries = references.Select(r => new ReferenceEntry(
            r,
            r.AssetId is { } assetId && assets.TryGetValue(assetId, out var asset) ? asset : null,
            links.TryGetValue(r.Id, out var t) ? t : [],
            markupCounts.GetValueOrDefault(r.Id),
            moments.TryGetValue(r.Id, out var m) ? m : []));
        return keepOrder ? entries.ToList() : entries.OrderByDescending(e => e.Reference.CapturedAt).ToList();
    }

    /// <summary>The project's reference whose file has this hash, if any.</summary>
    public Reference? ReferenceWithFile(string projectId, string sha256) =>
        ReferencesTable.ReadWhere(_connection,
            "projectId = $project AND assetId IN (SELECT id FROM assets WHERE sha256 = $hash)",
            ("$project", projectId), ("$hash", sha256)).FirstOrDefault();

    public Reference? Reference(string id) =>
        ReferencesTable.ReadWhere(_connection, "id = $id", ("$id", id)).FirstOrDefault();

    public long ReferenceCount(string projectId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM reference_items WHERE projectId = $id";
        command.Parameters.AddWithValue("$id", projectId);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Tags by how many references use them (most first), then by name.</summary>
    public List<TagUsage> TagsByUsage()
    {
        var usage = new Dictionary<string, (int References, int Projects)>(Ids.Comparer);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t.id, count(l.id), count(DISTINCT r.projectId)
                FROM tags t
                LEFT JOIN reference_tags l ON l.tagId = t.id
                LEFT JOIN reference_items r ON r.id = l.referenceId
                GROUP BY t.id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) usage[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        }
        return TagsTable.ReadAll(_connection, null)
            .Select(t => new TagUsage(t, usage.GetValueOrDefault(t.Id).References, usage.GetValueOrDefault(t.Id).Projects))
            .OrderByDescending(u => u.References)
            .ThenBy(u => u.Tag.Name, NaturalComparer.Instance)
            .ToList();
    }

    public Tag? TagNamed(string name) =>
        TagsTable.ReadWhere(_connection, "normalizedName = $name", ("$name", TagNames.Normalize(name))).FirstOrDefault();

    public Tag? Tag(string id) => TagsTable.ReadWhere(_connection, "id = $id", ("$id", id)).FirstOrDefault();

    // MARK: Writing

    /// <summary>
    /// Saves a new reference, with its asset and moments if it has them, and
    /// tags it by name, reusing tags with the same normalized name. All or nothing.
    /// </summary>
    public void InsertReference(Reference reference, Asset? asset, IEnumerable<VideoMoment> moments, IEnumerable<string> tagNames)
    {
        using var transaction = _connection.BeginTransaction();
        if (asset is not null) AssetsTable.InsertAll(_connection, transaction, [asset]);
        ReferencesTable.InsertAll(_connection, transaction, [reference]);
        VideoMomentsTable.InsertAll(_connection, transaction, moments);
        SetTags(transaction, reference.Id, tagNames);
        transaction.Commit();
    }

    /// <summary>
    /// Writes every field of the reference and, if given, replaces its tags. All
    /// or nothing. False if the reference no longer exists.
    /// </summary>
    public bool UpdateReference(Reference reference, IEnumerable<string>? tagNames = null)
    {
        using var transaction = _connection.BeginTransaction();
        if (!ReferencesTable.Update(_connection, reference, transaction)) return false;
        if (tagNames is not null) SetTags(transaction, reference.Id, tagNames);
        transaction.Commit();
        return true;
    }

    /// <summary>Makes the reference's tags exactly these names. Existing tags are reused by normalized name, keeping their spelling.</summary>
    private void SetTags(SqliteTransaction transaction, string referenceId, IEnumerable<string> names)
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var clean = TagNames.CollapseWhitespace(name);
            if (clean.Length > 0) wanted.TryAdd(TagNames.Normalize(clean), clean);
        }

        var current = ReferenceTagsTable.ReadWhere(_connection, "referenceId = $id", transaction, ("$id", referenceId));
        var tagsById = TagsTable.ReadAll(_connection, transaction).ToDictionary(t => t.Id, Ids.Comparer);
        var tagsByName = tagsById.Values.ToDictionary(t => t.NormalizedName, StringComparer.Ordinal);

        foreach (var link in current.Where(l => !wanted.ContainsKey(tagsById[l.TagId].NormalizedName)))
            Execute(_connection, $"DELETE FROM reference_tags WHERE id = '{link.Id}'", transaction);

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
            ReferenceTagsTable.InsertAll(_connection, transaction,
                [new ReferenceTag { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ReferenceId = referenceId, TagId = tag.Id }]);
        }
    }

    /// <summary>
    /// Deletes references, and with them their tag links, moments and markups.
    /// Returns the ids of the assets they used, so unused files can be tidied.
    /// </summary>
    public List<string> DeleteReferences(IReadOnlyCollection<string> referenceIds)
    {
        var assets = new List<string>();
        using var transaction = _connection.BeginTransaction();
        foreach (var id in referenceIds)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM reference_items WHERE id = $id RETURNING assetId";
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteScalar() is string assetId) assets.Add(assetId);
        }
        transaction.Commit();
        return assets.Distinct(Ids.Comparer).ToList();
    }

    /// <summary>Runs the work in one transaction: all of it, or none.</summary>
    public void RunInTransaction(Action<SqliteTransaction> work)
    {
        using var transaction = _connection.BeginTransaction();
        work(transaction);
        transaction.Commit();
    }

    public bool UpdateReferenceRow(Reference reference, SqliteTransaction transaction) =>
        ReferencesTable.Update(_connection, reference, transaction);

    /// <summary>
    /// Saves a copy of a reference: its row and moments as given, the original's
    /// tags, and the original's markups with new ids and the same values and dates.
    /// </summary>
    public void InsertCopy(string originalId, ReferenceEntry copy, SqliteTransaction transaction)
    {
        var now = IsoDate.Now();
        ReferencesTable.InsertAll(_connection, transaction, [copy.Reference]);
        VideoMomentsTable.InsertAll(_connection, transaction, copy.Moments);
        var links = ReferenceTagsTable.ReadWhere(_connection, "referenceId = $id", transaction, ("$id", originalId));
        ReferenceTagsTable.InsertAll(_connection, transaction, links.Select(l => new ReferenceTag
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ReferenceId = copy.Reference.Id, TagId = l.TagId,
        }));
        var markups = MarkupsTable.ReadWhere(_connection, "referenceId = $id", transaction, ("$id", originalId));
        foreach (var markup in markups)
        {
            markup.Id = Ids.New();
            markup.ReferenceId = copy.Reference.Id;
            markup.UpdatedAt = now;
        }
        MarkupsTable.InsertAll(_connection, transaction, markups);
    }
}
