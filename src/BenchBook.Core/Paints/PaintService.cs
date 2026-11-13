using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Paints;

/// <summary>A paint being created or edited, also used by CSV import.</summary>
public sealed record PaintDraft
{
    public string Brand { get; init; } = "";
    public string Range { get; init; } = "";
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>e.g. "FS 36270" or "RLM 76".</summary>
    public string StandardCode { get; init; } = "";
    public string Notes { get; init; } = "";
    /// <summary>"#RRGGBB", or null for no swatch.</summary>
    public string? SwatchHex { get; init; }
    public bool IsOwned { get; init; }

    public string IdentityKey => PaintIdentity.Key(Brand, Range, Code);

    /// <summary>Brand and code are required; everything else is optional.</summary>
    public bool MissingRequiredField => Brand.Trim().Length == 0 || Code.Trim().Length == 0;

    public static PaintDraft From(Paint paint, bool owned) => new()
    {
        Brand = paint.Brand, Range = paint.Range, Code = paint.Code, Name = paint.Name,
        StandardCode = paint.StandardCode, Notes = paint.Notes ?? "", SwatchHex = paint.SwatchHex, IsOwned = owned,
    };
}

public sealed class PaintException(string message) : Exception(message)
{
    public static PaintException MissingBrandOrCode() => new("A paint needs a brand and a code.");

    public static PaintException Duplicate(Paint existing) => new($"“{PaintText.DisplayName(existing)}” is already in your catalogue.");

    public static PaintException InUse(Paint paint, IReadOnlyList<string> projects) =>
        new($"“{PaintText.DisplayName(paint)}” is used in {PaintText.List(projects)}. Remove it from those projects first.");
}

/// <summary>The paint catalogue, ownership, and the paints chosen for projects.</summary>
public static class PaintService
{
    // MARK: Catalogue

    public static Paint Create(LibraryStore store, PaintDraft draft)
    {
        Validate(store, draft, null);
        var now = IsoDate.Now();
        var paint = new Paint { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, Brand = "", Range = "", Code = "", Name = "", StandardCode = "", IdentityKey = "" };
        Apply(draft, paint);
        store.InsertPaint(paint, draft.IsOwned);
        return paint;
    }

    public static Paint Update(LibraryStore store, Paint paint, bool owned, PaintDraft draft)
    {
        Validate(store, draft, paint);
        if (Tidy(draft) == Tidy(PaintDraft.From(paint, owned))) return paint;
        Apply(draft, paint);
        paint.UpdatedAt = IsoDate.Now();
        store.UpdatePaint(paint, draft.IsOwned);
        return paint;
    }

    /// <summary>Deletes the paints no project uses. Returns a message for each one that was refused.</summary>
    public static List<string> Delete(LibraryStore store, IReadOnlyList<Paint> paints)
    {
        var uses = store.PaintProjects();
        var refused = new List<string>();
        foreach (var paint in paints)
        {
            if (uses.TryGetValue(paint.Id, out var projects) && projects.Count > 0)
            {
                refused.Add(PaintException.InUse(paint, projects).Message);
                continue;
            }
            store.DeletePaint(paint.Id);
        }
        return refused;
    }

    public static void Validate(LibraryStore store, PaintDraft draft, Paint? paint)
    {
        if (draft.MissingRequiredField) throw PaintException.MissingBrandOrCode();
        if (store.PaintWithKey(draft.IdentityKey) is { } existing && (paint is null || !Ids.Comparer.Equals(existing.Id, paint.Id)))
            throw PaintException.Duplicate(existing);
    }

    /// <summary>Copies the draft into the paint, trimmed, with its key and a normalized swatch.</summary>
    public static void Apply(PaintDraft draft, Paint paint)
    {
        var tidy = Tidy(draft);
        paint.Brand = tidy.Brand;
        paint.Range = tidy.Range;
        paint.Code = tidy.Code;
        paint.Name = tidy.Name;
        paint.StandardCode = tidy.StandardCode;
        paint.Notes = tidy.Notes.Length == 0 ? null : tidy.Notes;
        paint.SwatchHex = tidy.SwatchHex;
        paint.IdentityKey = draft.IdentityKey;
    }

    private static PaintDraft Tidy(PaintDraft draft) => draft with
    {
        Brand = draft.Brand.Trim(), Range = draft.Range.Trim(), Code = draft.Code.Trim(), Name = draft.Name.Trim(),
        StandardCode = draft.StandardCode.Trim(), Notes = draft.Notes.Trim(), SwatchHex = Swatch.Normalize(draft.SwatchHex),
    };

    /// <summary>By brand, then code, in natural order.</summary>
    public static IOrderedEnumerable<Paint> Sorted(IEnumerable<Paint> paints) =>
        paints.OrderBy(p => p.Brand, NaturalComparer.Instance).ThenBy(p => p.Code, NaturalComparer.Instance);

    // MARK: Projects

    /// <summary>Adds paints the project doesn't have yet. Returns how many were added.</summary>
    public static int AddPaints(LibraryStore store, string projectId, IEnumerable<string> paintIds)
    {
        var existing = store.ProjectPaints(projectId).Where(r => r.PaintId is not null).Select(r => r.PaintId!).ToHashSet(Ids.Comparer);
        var now = IsoDate.Now();
        var rows = paintIds.Distinct(Ids.Comparer).Where(id => !existing.Contains(id))
            .Select(id => new ProjectPaint { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ProjectId = projectId, PaintId = id })
            .ToList();
        store.InsertProjectPaints(rows);
        return rows.Count;
    }

    /// <summary>A colour from the painting guide, before a paint is chosen for it.</summary>
    public static ProjectPaint AddColour(LibraryStore store, string projectId, string standard, string usedFor)
    {
        var now = IsoDate.Now();
        var row = new ProjectPaint
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ProjectId = projectId,
            Standard = Trimmed(standard), UsedFor = Trimmed(usedFor),
        };
        store.InsertProjectPaints([row]);
        return row;
    }

    /// <summary>Chooses the paint for a row, replacing any earlier choice.</summary>
    public static void Choose(LibraryStore store, ProjectPaint row, string paintId)
    {
        if (Ids.Comparer.Equals(row.PaintId, paintId)) return;
        row.PaintId = paintId;
        row.UpdatedAt = IsoDate.Now();
        store.UpdateProjectPaint(row);
    }

    /// <summary>Saves the colour and "used for" text edited in place. Nothing is written if neither changed.</summary>
    public static void SetText(LibraryStore store, ProjectPaint row, string standard, string usedFor)
    {
        var newStandard = Trimmed(standard);
        var newUsedFor = Trimmed(usedFor);
        if (row.Standard == newStandard && row.UsedFor == newUsedFor) return;
        row.Standard = newStandard;
        row.UsedFor = newUsedFor;
        row.UpdatedAt = IsoDate.Now();
        store.UpdateProjectPaint(row);
    }

    /// <summary>
    /// Colours with no paint chosen first (by standard), then paints by brand
    /// and code (behaviour.md, "Sorting").
    /// </summary>
    public static List<ProjectPaint> SortedRows(IEnumerable<ProjectPaint> rows, IReadOnlyDictionary<string, Paint> paints)
    {
        Paint? PaintOf(ProjectPaint r) => r.PaintId is { } id && paints.TryGetValue(id, out var p) ? p : null;
        return rows
            .OrderBy(r => PaintOf(r) is null ? 0 : 1)
            .ThenBy(r => PaintOf(r) is null ? r.Standard ?? "" : "", NaturalComparer.Instance)
            .ThenBy(r => PaintOf(r)?.Brand ?? "", NaturalComparer.Instance)
            .ThenBy(r => PaintOf(r)?.Code ?? "", NaturalComparer.Instance)
            .ToList();
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
