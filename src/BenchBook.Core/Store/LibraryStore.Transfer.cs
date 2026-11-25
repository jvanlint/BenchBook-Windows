using BenchBook.Core.Model;

namespace BenchBook.Core.Store;

public sealed partial class LibraryStore
{
    /// <summary>
    /// Saves an imported project in one transaction: deletes the project it
    /// replaces (as deleting a project does, so its kits leave it), adds the
    /// records, and moves kits already in the library into the project. All of
    /// it, or none. Dates are kept as given.
    /// </summary>
    internal void InsertImportedProject(string? replacedProjectId, LibrarySnapshot records, IReadOnlyCollection<string> kitsToJoin, string projectId)
    {
        records.Tidy();
        using var transaction = _connection.BeginTransaction();
        if (replacedProjectId is not null) Run("DELETE FROM projects WHERE id = $id", transaction, ("$id", replacedProjectId));
        // Parents before children, so the foreign keys hold at every step.
        AssetsTable.InsertAll(_connection, transaction, records.Assets);
        TagsTable.InsertAll(_connection, transaction, records.Tags);
        PaintsTable.InsertAll(_connection, transaction, records.Paints);
        OwnedPaintsTable.InsertAll(_connection, transaction, records.OwnedPaints);
        PaintMixPartsTable.InsertAll(_connection, transaction, records.PaintMixParts);
        ProjectsTable.InsertAll(_connection, transaction, records.Projects);
        ReferencesTable.InsertAll(_connection, transaction, records.References);
        ReferenceTagsTable.InsertAll(_connection, transaction, records.ReferenceTags);
        VideoMomentsTable.InsertAll(_connection, transaction, records.VideoMoments);
        BuildPhotosTable.InsertAll(_connection, transaction, records.BuildPhotos);
        BuildPhotoTagsTable.InsertAll(_connection, transaction, records.BuildPhotoTags);
        MarkupsTable.InsertAll(_connection, transaction, records.Markups);
        ProjectPaintsTable.InsertAll(_connection, transaction, records.ProjectPaints);
        AftermarketTable.InsertAll(_connection, transaction, records.AftermarketItems);
        KitsTable.InsertAll(_connection, transaction, records.Kits);
        foreach (var kitId in kitsToJoin)
            Run("UPDATE kits SET projectId = $project WHERE id = $id", transaction, ("$project", projectId), ("$id", kitId));
        if (HasForeignKeyProblems(_connection, transaction))
            throw new InvalidOperationException("The imported records don't fit together.");
        transaction.Commit();
    }
}
