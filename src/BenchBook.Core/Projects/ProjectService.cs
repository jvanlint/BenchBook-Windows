using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Projects;

/// <summary>A project being created or edited. Saved only when the user confirms.</summary>
public sealed record ProjectDraft
{
    public string Name { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Scale { get; init; } = "";
    public string KitNumber { get; init; } = "";
    public string MarkingsScheme { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Status { get; init; } = ProjectStatus.Researching;

    public bool HasValidName => Name.Trim().Length > 0;

    public static ProjectDraft From(Project project) => new()
    {
        Name = project.Name,
        Manufacturer = project.Manufacturer,
        Subject = project.Subject,
        Scale = project.Scale,
        KitNumber = project.KitNumber ?? "",
        MarkingsScheme = project.MarkingsScheme ?? "",
        Notes = project.Notes ?? "",
        Status = project.Status ?? ProjectStatus.Researching,
    };
}

public sealed class ProjectException(string message) : Exception(message);

/// <summary>Project rules, so screens don't repeat them (behaviour.md, "Deleting").</summary>
public static class ProjectService
{
    public static Project Create(LibraryStore store, ProjectDraft draft)
    {
        if (!draft.HasValidName) throw new ProjectException("A project needs a name.");
        var now = IsoDate.Now();
        var project = new Project
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now,
            Name = "", Manufacturer = "", Subject = "", Scale = "",
        };
        Apply(draft, project);
        store.InsertProject(project);
        return project;
    }

    /// <summary>Saves the draft over the project. Nothing changes, not even the date, if the draft is the same.</summary>
    public static Project Update(LibraryStore store, Project project, ProjectDraft draft)
    {
        if (!draft.HasValidName) throw new ProjectException("A project needs a name.");
        if (Tidy(draft) == Tidy(ProjectDraft.From(project))) return project;
        Apply(draft, project);
        project.UpdatedAt = IsoDate.Now();
        if (!store.UpdateProject(project)) throw new ProjectException("This project no longer exists.");
        return project;
    }

    public static Project SetStatus(LibraryStore store, Project project, string status) =>
        Update(store, project, ProjectDraft.From(project) with { Status = status });

    /// <summary>
    /// Deletes the project with its references, image notes, markups, paint list
    /// and aftermarket parts, then the posters and stored files nothing else uses.
    /// Catalogue paints, ownership and tags are kept.
    /// </summary>
    public static void Delete(LibraryStore store, LibraryLocation location, Project project)
    {
        var (references, assets) = store.DeleteProject(project.Id);
        foreach (var reference in references) TryDelete(location.PosterPath(reference));
        foreach (var assetId in assets)
        {
            var asset = store.Asset(assetId);
            if (asset is null || !store.DeleteAssetIfUnused(assetId)) continue;
            if (location.FullPath(asset.OriginalRelativePath) is { } original) TryDelete(original);
            TryDelete(location.ThumbnailPath(assetId));
        }
    }

    /// <summary>
    /// The confirmation's message, e.g. "Its 12 references, 3 image notes and
    /// 2 aftermarket parts will be deleted. Paints in your catalogue and your tags
    /// are kept. This can't be undone."
    /// </summary>
    public static string DeletionMessage(ProjectContents contents)
    {
        const string kept = "Paints in your catalogue and your tags are kept. This can't be undone.";
        var parts = new List<string>();
        void Add(long count, string singular, string plural, string suffix = "")
        {
            if (count > 0) parts.Add($"{count:N0} {(count == 1 ? singular : plural)}{suffix}");
        }
        Add(contents.References, "reference", "references");
        Add(contents.ImageNotes, "image note", "image notes");
        Add(contents.PdfMarkups, "PDF markup", "PDF markups");
        Add(contents.ProjectPaints, "paint", "paints", " from its paint list");
        Add(contents.AftermarketItems, "aftermarket part", "aftermarket parts");
        if (parts.Count == 0) return $"The project is empty. {kept}";
        var list = parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts[..^1])} and {parts[^1]}";
        return $"Its {list} will be deleted. {kept}";
    }

    private static ProjectDraft Tidy(ProjectDraft draft) => draft with
    {
        Name = draft.Name.Trim(),
        Manufacturer = draft.Manufacturer.Trim(),
        Subject = draft.Subject.Trim(),
        Scale = draft.Scale.Trim(),
        KitNumber = draft.KitNumber.Trim(),
        MarkingsScheme = draft.MarkingsScheme.Trim(),
        Notes = draft.Notes.Trim(),
    };

    private static void Apply(ProjectDraft draft, Project project)
    {
        var tidy = Tidy(draft);
        project.Name = tidy.Name;
        project.Manufacturer = tidy.Manufacturer;
        project.Subject = tidy.Subject;
        project.Scale = tidy.Scale;
        project.KitNumber = tidy.KitNumber.Length == 0 ? null : tidy.KitNumber;
        project.MarkingsScheme = tidy.MarkingsScheme.Length == 0 ? null : tidy.MarkingsScheme;
        project.Notes = tidy.Notes.Length == 0 ? null : tidy.Notes;
        project.Status = tidy.Status;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
