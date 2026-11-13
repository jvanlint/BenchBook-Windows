using BenchBook.Core.Model;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Store;

/// <summary>Paints, ownership, project paints and aftermarket items.</summary>
public sealed partial class LibraryStore
{
    // MARK: Paints

    public List<Paint> Paints() => PaintsTable.ReadAll(_connection, null);

    public Paint? Paint(string id) => PaintsTable.ReadWhere(_connection, "id = $id", ("$id", id)).FirstOrDefault();

    public Paint? PaintWithKey(string identityKey, SqliteTransaction? transaction = null) =>
        PaintsTable.ReadWhere(_connection, "identityKey = $k", transaction, ("$k", identityKey)).FirstOrDefault();

    public HashSet<string> OwnedPaintIds() => Strings("SELECT paintId FROM owned_paints");

    /// <summary>For each paint used in a project, the names of those projects.</summary>
    public Dictionary<string, List<string>> PaintProjects()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT pp.paintId, p.name FROM project_paints pp
            JOIN projects p ON p.id = pp.projectId
            WHERE pp.paintId IS NOT NULL
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, List<string>>(Ids.Comparer);
        while (reader.Read())
        {
            if (!result.TryGetValue(reader.GetString(0), out var names)) result[reader.GetString(0)] = names = [];
            names.Add(reader.GetString(1));
        }
        foreach (var names in result.Values) names.Sort(NaturalComparer.Instance);
        return result;
    }

    public void InsertPaint(Paint paint, bool owned, SqliteTransaction? transaction = null)
    {
        if (transaction is null)
        {
            RunInTransaction(t => InsertPaint(paint, owned, t));
            return;
        }
        PaintsTable.InsertAll(_connection, transaction, [paint]);
        if (owned) SetOwned(paint.Id, true, transaction);
    }

    public void UpdatePaint(Paint paint, bool owned) => RunInTransaction(t =>
    {
        PaintsTable.Update(_connection, paint, t);
        SetOwned(paint.Id, owned, t);
    });

    public void SetOwned(string paintId, bool owned, SqliteTransaction? transaction = null)
    {
        if (transaction is null)
        {
            RunInTransaction(t => SetOwned(paintId, owned, t));
            return;
        }
        var has = OwnedPaintsTable.ReadWhere(_connection, "paintId = $id", transaction, ("$id", paintId)).Count > 0;
        if (owned && !has)
        {
            var now = IsoDate.Now();
            OwnedPaintsTable.InsertAll(_connection, transaction, [new OwnedPaint { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, PaintId = paintId }]);
        }
        else if (!owned && has)
        {
            Run("DELETE FROM owned_paints WHERE paintId = $id", transaction, ("$id", paintId));
        }
    }

    /// <summary>Deletes the paint and its ownership. The store refuses while a project uses it.</summary>
    public void DeletePaint(string paintId) => RunInTransaction(t => Run("DELETE FROM paints WHERE id = $id", t, ("$id", paintId)));

    // MARK: Project paints

    public List<ProjectPaint> ProjectPaints(string projectId) =>
        ProjectPaintsTable.ReadWhere(_connection, "projectId = $id", ("$id", projectId));

    public List<ProjectPaint> AllProjectPaints() => ProjectPaintsTable.ReadAll(_connection, null);

    public void InsertProjectPaints(IEnumerable<ProjectPaint> rows) => RunInTransaction(t => ProjectPaintsTable.InsertAll(_connection, t, rows));

    public bool UpdateProjectPaint(ProjectPaint row) => ProjectPaintsTable.Update(_connection, row);

    public void DeleteProjectPaints(IEnumerable<string> ids) => RunInTransaction(t =>
    {
        foreach (var id in ids) Run("DELETE FROM project_paints WHERE id = $id", t, ("$id", id));
    });

    // MARK: Aftermarket items

    public List<AftermarketItem> AftermarketItems(string projectId) =>
        AftermarketTable.ReadWhere(_connection, "projectId = $id", ("$id", projectId));

    public AftermarketItem? AftermarketItem(string id) =>
        AftermarketTable.ReadWhere(_connection, "id = $id", ("$id", id)).FirstOrDefault();

    public void InsertAftermarketItem(AftermarketItem item) => RunInTransaction(t => AftermarketTable.InsertAll(_connection, t, [item]));

    public bool UpdateAftermarketItem(AftermarketItem item) => AftermarketTable.Update(_connection, item);

    /// <summary>Saves a picture's asset and points the item at it, in one transaction.</summary>
    public void SetAftermarketImage(AftermarketItem item, Asset? asset) => RunInTransaction(t =>
    {
        if (asset is not null) AssetsTable.InsertAll(_connection, t, [asset]);
        AftermarketTable.Update(_connection, item, t);
    });

    /// <summary>Deletes items. Returns the picture assets they used, so unused files can be tidied.</summary>
    public List<string> DeleteAftermarketItems(IReadOnlyCollection<string> ids)
    {
        var assets = new List<string>();
        RunInTransaction(t =>
        {
            foreach (var id in ids)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = t;
                command.CommandText = "DELETE FROM aftermarket_items WHERE id = $id RETURNING imageAssetId";
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteScalar() is string asset) assets.Add(asset);
            }
        });
        return assets.Distinct(Ids.Comparer).ToList();
    }

    // MARK: Markups

    public List<Markup> Markups(string referenceId) =>
        MarkupsTable.ReadWhere(_connection, "referenceId = $id", ("$id", referenceId));

    public void InsertMarkup(Markup markup) => RunInTransaction(t => MarkupsTable.InsertAll(_connection, t, [markup]));

    public bool UpdateMarkup(Markup markup) => MarkupsTable.Update(_connection, markup);

    public void DeleteMarkups(IEnumerable<string> ids) => RunInTransaction(t =>
    {
        foreach (var id in ids) Run("DELETE FROM markups WHERE id = $id", t, ("$id", id));
    });
}
