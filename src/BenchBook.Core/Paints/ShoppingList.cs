using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Paints;

/// <summary>
/// Paints to buy: not owned, and chosen in at least one project that isn't
/// Completed. Then colours with no paint chosen (behaviour.md, "Shopping list").
/// </summary>
public sealed class ShoppingList
{
    public sealed record Use(string Project, string? UsedFor)
    {
        public override string ToString() => UsedFor is null ? Project : $"{Project} ({UsedFor})";
    }

    public sealed record Item(Paint Paint, IReadOnlyList<Use> Uses)
    {
        /// <summary>e.g. "XF-54 Dark Sea Grey".</summary>
        public string Title => string.Join(" ", new[] { Paint.Code, Paint.Name }.Where(s => s.Length > 0));

        /// <summary>e.g. "Acrylic XF · BS 640".</summary>
        public string Detail => PaintText.SecondaryLine(Paint);

        /// <summary>e.g. "F-16C (upper surfaces), Spitfire".</summary>
        public string UsesLine => string.Join(", ", Uses);
    }

    /// <param name="RowId">The project paint row's id.</param>
    public sealed record Colour(string RowId, string Standard, Use Use);

    public sealed record BrandGroup(string Key, string Name, IReadOnlyList<Item> Items);

    public const string UnchosenHeading = "No Paint Chosen";

    public ShoppingList(LibraryStore store)
    {
        var projects = store.Projects().ToDictionary(p => p.Id, Ids.Comparer);
        var paints = store.Paints().ToDictionary(p => p.Id, Ids.Comparer);
        var owned = store.OwnedPaintIds();
        bool Unfinished(ProjectPaint row) => projects.TryGetValue(row.ProjectId, out var p) && p.Status != ProjectStatus.Completed;
        var rows = store.AllProjectPaints().Where(Unfinished).ToList();

        var items = rows.Where(r => r.PaintId is not null && paints.ContainsKey(r.PaintId) && !owned.Contains(r.PaintId))
            .GroupBy(r => r.PaintId!, Ids.Comparer)
            .Select(g => new Item(paints[g.Key], g
                .Select(r => new Use(projects[r.ProjectId].Name, Trimmed(r.UsedFor)))
                .OrderBy(u => u.Project, NaturalComparer.Instance).ToList()))
            .ToList();

        // Brands with the same folded name go together, shown with the most common spelling.
        var names = PaintFilterOption.For(items.Select(i => i.Paint.Brand)).ToDictionary(o => o.Key, o => o.Name, StringComparer.Ordinal);
        Groups = items.GroupBy(i => PaintIdentity.Words(i.Paint.Brand))
            .Select(g => new BrandGroup(g.Key, names.GetValueOrDefault(g.Key) ?? g.Key, g.OrderBy(i => i.Paint.Code, NaturalComparer.Instance).ToList()))
            .OrderBy(g => g.Name, NaturalComparer.Instance)
            .ToList();

        Unchosen = rows.Where(r => r.PaintId is null && Trimmed(r.Standard) is not null)
            .Select(r => new Colour(r.Id, r.Standard!.Trim(), new Use(projects[r.ProjectId].Name, Trimmed(r.UsedFor))))
            .OrderBy(c => c.Standard, NaturalComparer.Instance).ThenBy(c => c.Use.Project, NaturalComparer.Instance)
            .ToList();
    }

    /// <summary>Brands by name; each brand's paints by code.</summary>
    public IReadOnlyList<BrandGroup> Groups { get; }

    /// <summary>By standard, then project.</summary>
    public IReadOnlyList<Colour> Unchosen { get; }

    public IEnumerable<Item> Items => Groups.SelectMany(g => g.Items);
    public int PaintCount => Items.Count();
    public bool IsEmpty => Groups.Count == 0 && Unchosen.Count == 0;
    public int ProjectCount => Items.SelectMany(i => i.Uses.Select(u => u.Project)).Distinct().Count();

    /// <summary>e.g. "9 paints for 3 projects · 2 colours without paint".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (PaintCount > 0) parts.Add($"{Count(PaintCount, "paint")} for {Count(ProjectCount, "project")}");
            if (Unchosen.Count > 0) parts.Add($"{Count(Unchosen.Count, "colour")} without paint");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The plain-text checklist, e.g. "☐ XF-54 Dark Sea Grey · Acrylic XF · BS 640".</summary>
    /// <param name="date">The date as the user reads it, e.g. "13 November 2026".</param>
    public string Text(string date)
    {
        var lines = new List<string> { "Paint Shopping List", $"{date} · {Summary}" };
        foreach (var group in Groups)
        {
            lines.Add("");
            lines.Add(group.Name);
            foreach (var item in group.Items)
            {
                lines.Add("☐ " + string.Join(" · ", new[] { item.Title, item.Detail }.Where(s => s.Length > 0)));
                if (item.Uses.Count > 0) lines.Add("    " + item.UsesLine);
            }
        }
        if (Unchosen.Count > 0)
        {
            lines.Add("");
            lines.Add(UnchosenHeading);
            foreach (var colour in Unchosen)
            {
                lines.Add("☐ " + colour.Standard);
                lines.Add("    " + colour.Use);
            }
        }
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>e.g. "Paint Shopping List 2026-11-13.txt".</summary>
    public static string FileName(DateTime localDate) => $"Paint Shopping List {localDate:yyyy-MM-dd}.txt";

    private static string Count(int n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
