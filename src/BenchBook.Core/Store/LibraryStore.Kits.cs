using BenchBook.Core.Model;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Store;

/// <summary>Kits, build photos and mixes: the records schema 6.0.0 added.</summary>
public sealed partial class LibraryStore
{
    public List<Kit> Kits() => KitsTable.ReadAll(_connection, null);

    public Kit? Kit(string id) => KitsTable.ReadWhere(_connection, "id = $id", ("$id", id)).FirstOrDefault();

    /// <summary>The kits a project is building.</summary>
    public List<Kit> ProjectKits(string projectId) =>
        KitsTable.ReadWhere(_connection, "projectId = $id", ("$id", projectId));

    /// <summary>
    /// Saves kits as they are now, with any new picture's asset, in one
    /// transaction. New kits are inserted; others are written over. Projects
    /// in <paramref name="touched"/> are marked as updated, as adding a kit to
    /// a project or removing one does; editing a kit alone doesn't.
    /// </summary>
    public void SaveKits(IEnumerable<Kit> kits, IEnumerable<string?>? touched = null, Asset? asset = null)
    {
        RunInTransaction(t =>
        {
            if (asset is not null) AssetsTable.InsertAll(_connection, t, [asset]);
            foreach (var kit in kits)
            {
                if (!KitsTable.Update(_connection, kit, t)) KitsTable.InsertAll(_connection, t, [kit]);
            }
            if (touched is not null) TouchProjects(t, touched);
        });
    }

    /// <summary>Saves kits and a project's new status together, as a status change does.</summary>
    internal void SaveKits(SqliteTransaction transaction, IEnumerable<Kit> kits)
    {
        foreach (var kit in kits) KitsTable.Update(_connection, kit, transaction);
    }

    /// <summary>Deletes kits. Returns the picture assets they used, so unused files can be tidied.</summary>
    public List<string> DeleteKits(IReadOnlyCollection<string> ids)
    {
        var assets = new List<string>();
        RunInTransaction(t =>
        {
            foreach (var id in ids)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = t;
                command.CommandText = "DELETE FROM kits WHERE id = $id RETURNING pictureAssetId";
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteScalar() is string asset) assets.Add(asset);
            }
        });
        return assets.Distinct(Ids.Comparer).ToList();
    }

    /// <summary>Saves a new project and the kits it starts with, marking the projects they left.</summary>
    public void InsertProject(Project project, IReadOnlyCollection<Kit> kits, IEnumerable<string?> left) => RunInTransaction(t =>
    {
        ProjectsTable.InsertAll(_connection, t, [project]);
        foreach (var kit in kits) KitsTable.Update(_connection, kit, t);
        TouchProjects(t, left.Where(id => !Ids.Comparer.Equals(id, project.Id)));
    });

    /// <summary>Saves a project and its kits, whose status followed it.</summary>
    public bool UpdateProject(Project project, IReadOnlyCollection<Kit> kits)
    {
        var updated = false;
        RunInTransaction(t =>
        {
            updated = ProjectsTable.Update(_connection, project, t);
            if (updated) SaveKits(t, kits);
        });
        return updated;
    }

    public List<BuildPhoto> BuildPhotos(string projectId) =>
        BuildPhotosTable.ReadWhere(_connection, "projectId = $id", ("$id", projectId));

    /// <summary>Every recipe line of every mix.</summary>
    public List<PaintMixPart> AllMixParts() => PaintMixPartsTable.ReadAll(_connection, null);

    /// <summary>A mix's recipe, in its order.</summary>
    public List<PaintMixPart> MixParts(string mixId) =>
        PaintMixPartsTable.ReadWhere(_connection, "mixId = $id", ("$id", mixId)).OrderBy(p => p.Position).ToList();
}
