using BenchBook.Core.Model;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Store;

/// <summary>
/// When a project was last updated (behaviour.md, stored): each save of the
/// user's own edits to a project, or anything in it, sets its updatedAt to now.
/// Fetching link details and changes to tags themselves don't
/// (ProjectActivity.swift, touchEditedProjects).
/// </summary>
public sealed partial class LibraryStore
{
    /// <summary>Marks the projects as updated now.</summary>
    internal void TouchProjects(SqliteTransaction transaction, IEnumerable<string?> projectIds)
    {
        var now = IsoDate.Write(IsoDate.Now());
        foreach (var id in projectIds.OfType<string>().Distinct(Ids.Comparer))
            Run("UPDATE projects SET updatedAt = $now WHERE id = $id", transaction, ("$now", now), ("$id", id));
    }

    /// <summary>Marks the projects of these references as updated now.</summary>
    internal void TouchReferenceProjects(SqliteTransaction transaction, IEnumerable<string> referenceIds) =>
        TouchProjects(transaction, referenceIds.Select(id => Scalar("SELECT projectId FROM reference_items WHERE id = $id", transaction, id)).ToList());

    /// <summary>Marks the projects of these build photos as updated now.</summary>
    internal void TouchBuildPhotoProjects(SqliteTransaction transaction, IEnumerable<string> photoIds) =>
        TouchProjects(transaction, photoIds.Select(id => Scalar("SELECT projectId FROM build_photos WHERE id = $id", transaction, id)).ToList());

    /// <summary>Marks the project a markup belongs to, through its reference or build photo.</summary>
    private void TouchMarkupProjects(SqliteTransaction transaction, IEnumerable<string> markupIds) =>
        TouchProjects(transaction, markupIds.Select(id => Scalar(
            """
            SELECT COALESCE(
                (SELECT r.projectId FROM reference_items r WHERE r.id = m.referenceId),
                (SELECT b.projectId FROM build_photos b WHERE b.id = m.buildPhotoId))
            FROM markups m WHERE m.id = $id
            """, transaction, id)).ToList());

    private void TouchMarkupOwner(SqliteTransaction transaction, Markup markup)
    {
        if (markup.ReferenceId is { } reference) TouchReferenceProjects(transaction, [reference]);
        if (markup.BuildPhotoId is { } photo) TouchBuildPhotoProjects(transaction, [photo]);
    }

    private string? Scalar(string sql, SqliteTransaction transaction, string id)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }
}
