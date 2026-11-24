using BenchBook.Core.Model;

namespace BenchBook.Core.Store;

/// <summary>What deleting a project takes with it, for the confirmation.</summary>
public readonly record struct ProjectContents(
    long References, long ImageNotes, long PdfMarkups, long ProjectPaints, long AftermarketItems, long BuildPhotos = 0)
{
    public bool IsEmpty => References + ProjectPaints + AftermarketItems + BuildPhotos == 0;
}

public sealed partial class LibraryStore
{
    public Project? Project(string id) =>
        ProjectsTable.ReadWhere(_connection, "id = $id", ("$id", id)).FirstOrDefault();

    public void InsertProject(Project project)
    {
        using var transaction = _connection.BeginTransaction();
        ProjectsTable.InsertAll(_connection, transaction, [project]);
        transaction.Commit();
    }

    public bool UpdateProject(Project project) => ProjectsTable.Update(_connection, project);

    public ProjectContents ProjectContents(string projectId)
    {
        long Count(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", projectId);
            return (long)command.ExecuteScalar()!;
        }
        return new ProjectContents(
            Count("SELECT count(*) FROM reference_items WHERE projectId = $id"),
            Count("SELECT count(*) FROM markups m JOIN reference_items r ON r.id = m.referenceId WHERE r.projectId = $id AND r.kind = 'image'"),
            Count("SELECT count(*) FROM markups m JOIN reference_items r ON r.id = m.referenceId WHERE r.projectId = $id AND r.kind <> 'image'"),
            Count("SELECT count(*) FROM project_paints WHERE projectId = $id"),
            Count("SELECT count(*) FROM aftermarket_items WHERE projectId = $id"),
            Count("SELECT count(*) FROM build_photos WHERE projectId = $id"));
    }

    /// <summary>
    /// Deletes the project and, through the delete rules, its references, tag
    /// links, moments, markups, paint list, aftermarket items and build photos.
    /// Its kits stay in the catalogue, in no project. Returns the ids of its
    /// references and of the assets they used, so their files can be tidied.
    /// </summary>
    public (List<string> ReferenceIds, List<string> AssetIds) DeleteProject(string projectId)
    {
        var references = new List<string>();
        var assets = new List<string>();
        using var transaction = _connection.BeginTransaction();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, assetId FROM reference_items WHERE projectId = $id;
                SELECT imageAssetId FROM aftermarket_items WHERE projectId = $id AND imageAssetId IS NOT NULL
                UNION ALL SELECT assetId FROM build_photos WHERE projectId = $id;
                """;
            command.Parameters.AddWithValue("$id", projectId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                references.Add(reader.GetString(0));
                if (!reader.IsDBNull(1)) assets.Add(reader.GetString(1));
            }
            reader.NextResult();
            while (reader.Read()) assets.Add(reader.GetString(0));
        }
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM projects WHERE id = $id";
            command.Parameters.AddWithValue("$id", projectId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return (references, assets.Distinct(Ids.Comparer).ToList());
    }

    /// <summary>An asset's record, if it exists.</summary>
    public Asset? Asset(string id) =>
        AssetsTable.ReadWhere(_connection, "id = $id", ("$id", id)).FirstOrDefault();
}
