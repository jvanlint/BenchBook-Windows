using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Projects;

/// <summary>A project being created or edited. Saved only when the user confirms.</summary>
public sealed record ProjectDraft
{
    public string Name { get; init; } = "";
    public string Subject { get; init; } = "";
    public string MarkingsScheme { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Status { get; init; } = ProjectStatus.Researching;

    public bool HasValidName => Name.Trim().Length > 0;

    public static ProjectDraft From(Project project) => new()
    {
        Name = project.Name,
        Subject = project.Subject,
        MarkingsScheme = project.MarkingsScheme ?? "",
        Notes = project.Notes ?? "",
        Status = project.Status ?? ProjectStatus.Researching,
    };
}

public sealed class ProjectException(string message) : Exception(message);

/// <summary>How the sidebar lists projects (behaviour.md, "Sorting"). Settings keep the raw value.</summary>
public static class ProjectSort
{
    public const string Name = "name";
    public const string DateCreated = "dateCreated";
    public const string LastUpdated = "lastUpdated";

    public static readonly IReadOnlyList<string> All = [Name, DateCreated, LastUpdated];

    public static string Shown(string sort) => sort switch
    {
        DateCreated => "Date Created",
        LastUpdated => "Last Updated",
        _ => "Name",
    };

    /// <summary>Name A to Z, newest created first, or most recently updated first. Ties fall back to name.</summary>
    public static List<Project> Sorted(IEnumerable<Project> projects, string? sort)
    {
        var byName = projects.OrderBy(p => p.Name, NaturalComparer.Instance);
        return (sort switch
        {
            DateCreated => projects.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Name, NaturalComparer.Instance),
            LastUpdated => projects.OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Name, NaturalComparer.Instance),
            _ => byName,
        }).ToList();
    }
}

/// <summary>Project rules, so screens don't repeat them (behaviour.md, "Deleting").</summary>
public static class ProjectService
{
    /// <summary>A new project using these kits from the catalogue. A kit in another project moves to this one.</summary>
    public static Project Create(LibraryStore store, ProjectDraft draft, IReadOnlyCollection<Kit>? kits = null)
    {
        if (!draft.HasValidName) throw new ProjectException("A project needs a name.");
        var now = IsoDate.Now();
        var project = new Project
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now,
            Name = "", Subject = "",
        };
        Apply(draft, project);
        if (kits is { Count: > 0 })
        {
            var left = kits.Select(k => k.ProjectId).ToList();
            foreach (var kit in kits)
            {
                kit.ProjectId = project.Id;
                Kits.KitRules.Follow(kit, project.Status, now);
                kit.UpdatedAt = now;
            }
            store.InsertProject(project, kits, left);
        }
        else
        {
            store.InsertProject(project);
        }
        return project;
    }

    /// <summary>Saves the draft over the project. Nothing changes, not even the date, if the draft is the same.</summary>
    public static Project Update(LibraryStore store, Project project, ProjectDraft draft)
    {
        if (!draft.HasValidName) throw new ProjectException("A project needs a name.");
        if (Tidy(draft) == Tidy(ProjectDraft.From(project))) return project;
        var statusChanged = Tidy(draft).Status != Tidy(ProjectDraft.From(project)).Status;
        Apply(draft, project);
        var now = IsoDate.Now();
        project.UpdatedAt = now;
        // In Progress makes the project's BNIB and Opened kits WIP; Completed makes them Built.
        var kits = statusChanged ? store.ProjectKits(project.Id).Where(k => Kits.KitRules.Follow(k, project.Status, now)).ToList() : [];
        if (!store.UpdateProject(project, kits)) throw new ProjectException("This project no longer exists.");
        return project;
    }

    /// <summary>
    /// The line under a project's name: its one kit's brand and scale, or the
    /// number of kits, then its subject, skipping empty parts, e.g. "Italeri ·
    /// 1:72 · AH-1W" or "2 kits · Normandy" (behaviour.md, "Kits in projects").
    /// </summary>
    public static string Summary(Project project, IReadOnlyList<Kit> kits)
    {
        var parts = new List<string?>();
        if (kits.Count == 1) parts.AddRange([kits[0].Brand, kits[0].Scale]);
        else if (kits.Count > 1) parts.Add($"{kits.Count:N0} kits");
        parts.Add(project.Subject);
        return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
    }

    public static Project SetStatus(LibraryStore store, Project project, string status) =>
        Update(store, project, ProjectDraft.From(project) with { Status = status });

    /// <summary>
    /// Deletes the project with its references, image notes, markups, paint list,
    /// aftermarket parts and build photos, then the posters and stored files
    /// nothing else uses. Kits stay in the catalogue, in no project. Catalogue
    /// paints, ownership and tags are kept.
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
        Add(contents.BuildPhotos, "build photo", "build photos");
        if (parts.Count == 0) return $"The project is empty. {kept}";
        var list = parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts[..^1])} and {parts[^1]}";
        return $"Its {list} will be deleted. {kept}";
    }

    private static ProjectDraft Tidy(ProjectDraft draft) => draft with
    {
        Name = draft.Name.Trim(),
        Subject = draft.Subject.Trim(),
        MarkingsScheme = draft.MarkingsScheme.Trim(),
        Notes = draft.Notes.Trim(),
    };

    private static void Apply(ProjectDraft draft, Project project)
    {
        var tidy = Tidy(draft);
        project.Name = tidy.Name;
        project.Subject = tidy.Subject;
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
