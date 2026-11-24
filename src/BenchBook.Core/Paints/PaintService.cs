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
    /// <summary>
    /// A system's raw value, e.g. "fs595"; empty for none. Raw text, so a system
    /// this version doesn't know is kept when nothing changes it.
    /// </summary>
    public string StandardSystem { get; init; } = "";
    /// <summary>The code within the system, e.g. "36270".</summary>
    public string StandardCode { get; init; } = "";
    /// <summary>"none", "closest", "exact" or empty. Kept only with a standard.</summary>
    public string Match { get; init; } = "";
    /// <summary>A paint type's raw value, or empty for Not Set.</summary>
    public string Type { get; init; } = "";
    public bool IsMix { get; init; }
    public string Notes { get; init; } = "";
    /// <summary>"#RRGGBB", or null for no swatch.</summary>
    public string? SwatchHex { get; init; }
    public bool IsOwned { get; init; }

    /// <summary>Sets the standard from text such as "FS 36270" (behaviour.md, "Reading standard text").</summary>
    public PaintDraft WithStandardText(string text)
    {
        var standard = PaintStandardParser.Parse(text);
        return this with { StandardSystem = standard?.SystemRaw ?? "", StandardCode = standard?.Code ?? "" };
    }

    public PaintStandard? Standard => PaintStandard.From(StandardSystem, StandardCode);

    public string IdentityKey => IsMix ? PaintIdentity.MixKey(Brand, Name) : PaintIdentity.Key(Brand, Range, Code);

    /// <summary>A paint needs a brand and a code; a mix, a brand and a name.</summary>
    public bool MissingRequiredField => Brand.Trim().Length == 0 || (IsMix ? Name.Trim().Length == 0 : Code.Trim().Length == 0);

    public static PaintDraft From(Paint paint, bool owned) => new()
    {
        Brand = paint.Brand, Range = paint.Range, Code = paint.Code, Name = paint.Name,
        StandardSystem = paint.StandardSystem, StandardCode = paint.StandardCode, Match = paint.Match, Type = paint.Type, IsMix = paint.IsMix,
        Notes = paint.Notes ?? "", SwatchHex = paint.SwatchHex, IsOwned = owned,
    };
}

public sealed class PaintException(string message) : Exception(message)
{
    public static PaintException MissingBrandOrCode() => new("A paint needs a brand and a code.");

    public static PaintException InRecipe(Paint paint, IReadOnlyList<string> mixes) =>
        new($"“{PaintText.DisplayName(paint)}” is in the recipe for {PaintText.List(mixes)}. Take it out of those mixes first.");

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
        var paint = New();
        Apply(draft, paint);
        store.InsertPaint(paint, draft.IsOwned && !draft.IsMix);
        return paint;
    }

    public static Paint Update(LibraryStore store, Paint paint, bool owned, PaintDraft draft)
    {
        Validate(store, draft, paint);
        if (Tidy(draft) == Tidy(PaintDraft.From(paint, owned))) return paint;
        Apply(draft, paint);
        paint.UpdatedAt = IsoDate.Now();
        // A mix has no ownership of its own.
        store.UpdatePaint(paint, draft.IsOwned && !paint.IsMix);
        return paint;
    }

    /// <summary>A paint with every field empty, for <see cref="Apply"/> to fill.</summary>
    public static Paint New()
    {
        var now = IsoDate.Now();
        return new Paint { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, Brand = "", Range = "", Code = "", Name = "", StandardCode = "", IdentityKey = "" };
    }

    /// <summary>
    /// Deletes the paints no project and no mix's recipe uses, with their
    /// ownership and a mix's recipe lines. Returns a message for each one that was refused.
    /// </summary>
    public static List<string> Delete(LibraryStore store, IReadOnlyList<Paint> paints)
    {
        var uses = store.PaintProjects();
        var recipes = store.PaintMixes();
        var deleting = paints.Select(p => p.Id).ToHashSet(Ids.Comparer);
        var refused = new List<string>();
        foreach (var paint in paints)
        {
            if (uses.TryGetValue(paint.Id, out var projects) && projects.Count > 0)
            {
                refused.Add(PaintException.InUse(paint, projects).Message);
                continue;
            }
            // Mixes being deleted at the same time don't count.
            if (recipes.TryGetValue(paint.Id, out var mixes) && mixes.Any(m => !deleting.Contains(m.Id)))
            {
                refused.Add(PaintException.InRecipe(paint, mixes.Where(m => !deleting.Contains(m.Id)).Select(PaintText.DisplayName).ToList()).Message);
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

    /// <summary>
    /// Copies the draft into the paint, trimmed, with its key and a normalized
    /// swatch. With no system there's no code or match. A mix has no range.
    /// </summary>
    public static void Apply(PaintDraft draft, Paint paint)
    {
        var tidy = Tidy(draft);
        paint.Brand = tidy.Brand;
        paint.Range = tidy.Range;
        paint.Code = tidy.Code;
        paint.Name = tidy.Name;
        paint.StandardSystem = tidy.StandardSystem;
        paint.StandardCode = tidy.StandardCode;
        paint.Match = tidy.Match;
        paint.Type = tidy.Type;
        paint.IsMix = tidy.IsMix;
        paint.Notes = tidy.Notes.Length == 0 ? null : tidy.Notes;
        paint.SwatchHex = tidy.SwatchHex;
        paint.IdentityKey = tidy.IdentityKey;
    }

    private static PaintDraft Tidy(PaintDraft draft)
    {
        var system = draft.StandardSystem.Trim();
        return draft with
        {
            Brand = draft.Brand.Trim(), Range = draft.IsMix ? "" : draft.Range.Trim(), Code = draft.Code.Trim(), Name = draft.Name.Trim(),
            StandardSystem = system,
            StandardCode = system.Length == 0 ? "" : draft.StandardCode.Trim(),
            Match = system.Length == 0 ? "" : draft.Match.Trim(),
            Type = draft.Type.Trim(),
            Notes = draft.Notes.Trim(), SwatchHex = Swatch.Normalize(draft.SwatchHex),
        };
    }

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

    /// <summary>A colour from the painting guide, before a paint is chosen for it. Its text, e.g. "FS 36270", is split into a system and a code.</summary>
    public static ProjectPaint AddColour(LibraryStore store, string projectId, string standard, string usedFor)
    {
        var now = IsoDate.Now();
        var parsed = PaintStandardParser.Parse(standard);
        var row = new ProjectPaint
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ProjectId = projectId,
            StandardSystem = parsed?.SystemRaw ?? "", Standard = Trimmed(parsed?.Code), UsedFor = Trimmed(usedFor),
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

    /// <summary>
    /// Saves the colour and "used for" text edited in place. The colour is read
    /// as standard text, e.g. "FS 36270"; left as it's shown, its stored system
    /// is kept, even one this version doesn't know. Nothing is written if neither changed.
    /// </summary>
    public static void SetText(LibraryStore store, ProjectPaint row, string standard, string usedFor)
    {
        var newUsedFor = Trimmed(usedFor);
        var shown = row.GuideStandard?.DisplayText ?? "";
        var (system, code) = (row.StandardSystem, row.Standard);
        if (standard.Trim() != shown)
        {
            var parsed = PaintStandardParser.Parse(standard);
            (system, code) = (parsed?.SystemRaw ?? "", Trimmed(parsed?.Code));
        }
        if (row.StandardSystem == system && row.Standard == code && row.UsedFor == newUsedFor) return;
        row.StandardSystem = system;
        row.Standard = code;
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
            .ThenBy(r => PaintOf(r) is null ? r.GuideStandard?.DisplayText ?? "" : "", NaturalComparer.Instance)
            .ThenBy(r => PaintOf(r)?.Brand ?? "", NaturalComparer.Instance)
            .ThenBy(r => PaintOf(r)?.Code ?? "", NaturalComparer.Instance)
            .ToList();
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
