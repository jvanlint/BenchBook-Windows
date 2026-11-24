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

    /// <summary>For each paint in a mix's recipe, the mixes that use it.</summary>
    public Dictionary<string, List<Paint>> PaintMixes()
    {
        var paints = PaintsTable.ReadAll(_connection, null).ToDictionary(p => p.Id, Ids.Comparer);
        var result = new Dictionary<string, List<Paint>>(Ids.Comparer);
        foreach (var part in PaintMixPartsTable.ReadAll(_connection, null))
        {
            if (!paints.TryGetValue(part.MixId, out var mix)) continue;
            if (!result.TryGetValue(part.PaintId, out var mixes)) result[part.PaintId] = mixes = [];
            mixes.Add(mix);
        }
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

    public void UpdatePaint(Paint paint, bool owned, SqliteTransaction? transaction = null)
    {
        if (transaction is null)
        {
            RunInTransaction(t => UpdatePaint(paint, owned, t));
            return;
        }
        PaintsTable.Update(_connection, paint, transaction);
        SetOwned(paint.Id, owned, transaction);
    }

    public bool IsOwned(string paintId, SqliteTransaction? transaction = null) =>
        OwnedPaintsTable.ReadWhere(_connection, "paintId = $id", transaction, ("$id", paintId)).Count > 0;

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

    /// <summary>Deletes the paint, its ownership and a mix's recipe lines. The store refuses while a project or a recipe uses it.</summary>
    public void DeletePaint(string paintId) => RunInTransaction(t => Run("DELETE FROM paints WHERE id = $id", t, ("$id", paintId)));

    // MARK: Project paints

    public List<ProjectPaint> ProjectPaints(string projectId) =>
        ProjectPaintsTable.ReadWhere(_connection, "projectId = $id", ("$id", projectId));

    public List<ProjectPaint> AllProjectPaints() => ProjectPaintsTable.ReadAll(_connection, null);

    public void InsertProjectPaints(IEnumerable<ProjectPaint> rows) => RunInTransaction(t =>
    {
        var list = rows.ToList();
        ProjectPaintsTable.InsertAll(_connection, t, list);
        TouchProjects(t, list.Select(r => r.ProjectId));
    });

    public bool UpdateProjectPaint(ProjectPaint row)
    {
        var updated = false;
        RunInTransaction(t =>
        {
            updated = ProjectPaintsTable.Update(_connection, row, t);
            if (updated) TouchProjects(t, [row.ProjectId]);
        });
        return updated;
    }

    public void DeleteProjectPaints(IEnumerable<string> ids) => RunInTransaction(t =>
    {
        foreach (var id in ids)
        {
            TouchProjects(t, [Scalar("SELECT projectId FROM project_paints WHERE id = $id", t, id)]);
            Run("DELETE FROM project_paints WHERE id = $id", t, ("$id", id));
        }
    });

    // MARK: Aftermarket items

    public List<AftermarketItem> AftermarketItems(string projectId) =>
        AftermarketTable.ReadWhere(_connection, "projectId = $id", ("$id", projectId));

    public AftermarketItem? AftermarketItem(string id) =>
        AftermarketTable.ReadWhere(_connection, "id = $id", ("$id", id)).FirstOrDefault();

    public void InsertAftermarketItem(AftermarketItem item) => RunInTransaction(t =>
    {
        AftermarketTable.InsertAll(_connection, t, [item]);
        TouchProjects(t, [item.ProjectId]);
    });

    public bool UpdateAftermarketItem(AftermarketItem item)
    {
        var updated = false;
        RunInTransaction(t =>
        {
            updated = AftermarketTable.Update(_connection, item, t);
            if (updated) TouchProjects(t, [item.ProjectId]);
        });
        return updated;
    }

    /// <summary>Saves a picture's asset and points the item at it, in one transaction.</summary>
    public void SetAftermarketImage(AftermarketItem item, Asset? asset) => RunInTransaction(t =>
    {
        if (asset is not null) AssetsTable.InsertAll(_connection, t, [asset]);
        AftermarketTable.Update(_connection, item, t);
        TouchProjects(t, [item.ProjectId]);
    });

    /// <summary>Deletes items. Returns the picture assets they used, so unused files can be tidied.</summary>
    public List<string> DeleteAftermarketItems(IReadOnlyCollection<string> ids)
    {
        var assets = new List<string>();
        RunInTransaction(t =>
        {
            foreach (var id in ids)
            {
                TouchProjects(t, [Scalar("SELECT projectId FROM aftermarket_items WHERE id = $id", t, id)]);
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

    public void InsertMarkup(Markup markup) => RunInTransaction(t =>
    {
        MarkupsTable.InsertAll(_connection, t, [markup]);
        TouchMarkupOwner(t, markup);
    });

    public bool UpdateMarkup(Markup markup)
    {
        var updated = false;
        RunInTransaction(t =>
        {
            updated = MarkupsTable.Update(_connection, markup, t);
            if (updated) TouchMarkupOwner(t, markup);
        });
        return updated;
    }

    public void DeleteMarkups(IEnumerable<string> ids) => RunInTransaction(t =>
    {
        var list = ids.ToList();
        TouchMarkupProjects(t, list);
        foreach (var id in list) Run("DELETE FROM markups WHERE id = $id", t, ("$id", id));
    });
}
