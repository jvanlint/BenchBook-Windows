using BenchBook.Core.Model;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Store;

public sealed class TagException(string message) : Exception(message)
{
    public static TagException EmptyName() => new("A tag needs a name.");
}

/// <summary>
/// Library-wide tag changes (behaviour.md, "Tags (stored)"): renaming,
/// merging, colours, deleting, and tagging many references at once.
/// </summary>
public sealed partial class LibraryStore
{
    /// <summary>The tag with this name ignoring case and spacing, or a new one. Keeps an existing tag's spelling.</summary>
    private Tag TagFor(string name, SqliteTransaction transaction)
    {
        var clean = TagNames.Parse(name).FirstOrDefault() ?? throw TagException.EmptyName();
        var normalized = TagNames.Normalize(clean);
        var existing = TagsTable.ReadWhere(_connection, "normalizedName = $n", transaction, ("$n", normalized)).FirstOrDefault();
        if (existing is not null) return existing;
        var now = IsoDate.Now();
        var tag = new Tag { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, Name = clean, NormalizedName = normalized };
        TagsTable.InsertAll(_connection, transaction, [tag]);
        return tag;
    }

    /// <summary>Creates an unused tag, or returns the one that already has the name.</summary>
    public Tag CreateTag(string name)
    {
        using var transaction = _connection.BeginTransaction();
        var tag = TagFor(name, transaction);
        transaction.Commit();
        return tag;
    }

    /// <summary>Adds tags by name (commas allowed) to every reference that lacks them, creating new tags as needed.</summary>
    public void AddTags(string text, IReadOnlyCollection<string> referenceIds)
    {
        var names = TagNames.Parse(text);
        if (names.Count == 0) return;
        using var transaction = _connection.BeginTransaction();
        foreach (var name in names)
            Link(TagFor(name, transaction).Id, referenceIds, transaction);
        TouchReferenceProjects(transaction, referenceIds);
        transaction.Commit();
    }

    /// <summary>Adds the tag to every reference that doesn't have it.</summary>
    public void AddTag(string tagId, IReadOnlyCollection<string> referenceIds)
    {
        using var transaction = _connection.BeginTransaction();
        Link(tagId, referenceIds, transaction);
        TouchReferenceProjects(transaction, referenceIds);
        transaction.Commit();
    }

    public void RemoveTag(string tagId, IReadOnlyCollection<string> referenceIds)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var id in referenceIds)
        {
            Run("DELETE FROM reference_tags WHERE tagId = $t AND referenceId = $r", transaction, ("$t", tagId), ("$r", id));
            Touch("reference_items", id, transaction);
        }
        TouchReferenceProjects(transaction, referenceIds);
        transaction.Commit();
    }

    /// <summary>
    /// Renames the tag. If another tag already has the new name, the two merge
    /// into that one, which takes the new spelling. Returns the tag that remains.
    /// </summary>
    public Tag RenameTag(string tagId, string newName)
    {
        var clean = TagNames.Parse(newName).FirstOrDefault() ?? throw TagException.EmptyName();
        var normalized = TagNames.Normalize(clean);
        using var transaction = _connection.BeginTransaction();
        var tag = TagsTable.ReadWhere(_connection, "id = $id", transaction, ("$id", tagId)).Single();
        var other = TagsTable.ReadWhere(_connection, "normalizedName = $n AND id <> $id", transaction, ("$n", normalized), ("$id", tagId)).FirstOrDefault();
        var now = IsoDate.Now();
        Tag result;
        if (other is not null)
        {
            other.Name = clean;
            other.UpdatedAt = now;
            TagsTable.Update(_connection, other, transaction);
            Merge([tag], other, transaction);
            result = other;
        }
        else
        {
            tag.Name = clean;
            tag.NormalizedName = normalized;
            tag.UpdatedAt = now;
            TagsTable.Update(_connection, tag, transaction);
            result = tag;
        }
        transaction.Commit();
        return result;
    }

    /// <summary>Moves every reference from the tags onto the target, then deletes them. The target keeps its colour, or takes the first one found.</summary>
    public void MergeTags(IReadOnlyCollection<string> tagIds, string targetId)
    {
        using var transaction = _connection.BeginTransaction();
        var target = TagsTable.ReadWhere(_connection, "id = $id", transaction, ("$id", targetId)).Single();
        var sources = tagIds.Where(id => !Ids.Comparer.Equals(id, targetId))
            .Select(id => TagsTable.ReadWhere(_connection, "id = $id", transaction, ("$id", id)).Single()).ToList();
        Merge(sources, target, transaction);
        transaction.Commit();
    }

    private void Merge(IEnumerable<Tag> sources, Tag target, SqliteTransaction transaction)
    {
        foreach (var source in sources)
        {
            var references = ReferenceTagsTable.ReadWhere(_connection, "tagId = $t", transaction, ("$t", source.Id)).Select(l => l.ReferenceId).ToList();
            Link(target.Id, references, transaction);
            // Build photos with the tag move across too.
            var linked = BuildPhotoTagsTable.ReadWhere(_connection, "tagId = $t", transaction, ("$t", target.Id)).Select(l => l.BuildPhotoId).ToHashSet(Ids.Comparer);
            var now = IsoDate.Now();
            var moved = BuildPhotoTagsTable.ReadWhere(_connection, "tagId = $t", transaction, ("$t", source.Id))
                .Where(l => linked.Add(l.BuildPhotoId))
                .Select(l => new BuildPhotoTag { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, BuildPhotoId = l.BuildPhotoId, TagId = target.Id })
                .ToList();
            BuildPhotoTagsTable.InsertAll(_connection, transaction, moved);
            target.Color ??= source.Color;
            Run("DELETE FROM tags WHERE id = $id", transaction, ("$id", source.Id));
        }
        target.UpdatedAt = IsoDate.Now();
        TagsTable.Update(_connection, target, transaction);
    }

    /// <summary>Deletes tags and their links. The references stay.</summary>
    public void DeleteTags(IReadOnlyCollection<string> tagIds)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var id in tagIds)
        {
            Run("UPDATE reference_items SET updatedAt = $now WHERE id IN (SELECT referenceId FROM reference_tags WHERE tagId = $id)",
                transaction, ("$now", IsoDate.Write(IsoDate.Now())), ("$id", id));
            Run("DELETE FROM tags WHERE id = $id", transaction, ("$id", id));
        }
        transaction.Commit();
    }

    public void SetTagColor(IReadOnlyCollection<string> tagIds, string? color)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var id in tagIds)
            Run("UPDATE tags SET color = $c, updatedAt = $now WHERE id = $id", transaction,
                ("$c", (object?)color ?? DBNull.Value), ("$now", IsoDate.Write(IsoDate.Now())), ("$id", id));
        transaction.Commit();
    }

    /// <summary>Deletes tags no reference or build photo uses. Returns how many.</summary>
    public int RemoveUnusedTags()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM tags WHERE id NOT IN (SELECT tagId FROM reference_tags) AND id NOT IN (SELECT tagId FROM build_photo_tags)";
        return command.ExecuteNonQuery();
    }

    private void Link(string tagId, IEnumerable<string> referenceIds, SqliteTransaction transaction)
    {
        var now = IsoDate.Now();
        foreach (var referenceId in referenceIds)
        {
            var exists = ReferenceTagsTable.ReadWhere(_connection, "tagId = $t AND referenceId = $r", transaction, ("$t", tagId), ("$r", referenceId)).Count > 0;
            if (exists) continue;
            ReferenceTagsTable.InsertAll(_connection, transaction,
                [new ReferenceTag { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ReferenceId = referenceId, TagId = tagId }]);
            Touch("reference_items", referenceId, transaction);
        }
    }

    private void Touch(string table, string id, SqliteTransaction transaction) =>
        Run($"UPDATE {table} SET updatedAt = $now WHERE id = $id", transaction, ("$now", IsoDate.Write(IsoDate.Now())), ("$id", id));

    private void Run(string sql, SqliteTransaction transaction, params (string Name, object Value)[] values)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
