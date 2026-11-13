using System.Globalization;
using System.Text;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Search;

/// <summary>Text prepared for searching: folded, and compact (letters and digits only).</summary>
public readonly record struct SearchField(string Text, string Folded, string Compact)
{
    public static SearchField Of(string text)
    {
        var folded = Fold(text);
        return new SearchField(text, folded, CompactForm(folded));
    }

    /// <summary>Ignores case, accents and full-width forms.</summary>
    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().ToLowerInvariant();
    }

    /// <summary>Everything but letters and digits removed, so "f16" finds "F-16".</summary>
    public static string CompactForm(string folded) => string.Concat(folded.Where(char.IsLetterOrDigit));
}

/// <summary>What the user typed, split into words. An item matches when every word is in at least one of its fields.</summary>
public sealed class SearchQuery
{
    public SearchQuery(string text, IEnumerable<string>? tags = null)
    {
        // Splitting on null splits on every kind of Unicode whitespace.
        Words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(SearchField.Of).ToList();
        Tags = new HashSet<string>(tags ?? [], StringComparer.Ordinal);
    }

    public IReadOnlyList<SearchField> Words { get; }

    /// <summary>Normalized tag names chosen as tokens.</summary>
    public IReadOnlySet<string> Tags { get; }

    public bool IsEmpty => Words.Count == 0 && Tags.Count == 0;

    public static bool WordMatches(SearchField word, SearchField field) =>
        field.Folded.Contains(word.Folded, StringComparison.Ordinal)
        || (word.Compact.Length > 0 && field.Compact.Contains(word.Compact, StringComparison.Ordinal));

    public bool MatchesAnyWord(SearchField field) => Words.Any(w => WordMatches(w, field));

    public bool MatchesEveryWord(IReadOnlyList<SearchField> fields) => Words.All(w => fields.Any(f => WordMatches(w, f)));
}

/// <summary>Where in a reference a search matched, e.g. "Box 2" or "Description".</summary>
/// <param name="MomentSeconds">For a video moment, its time, so the result can open the video there.</param>
public sealed record SearchMatch(string Label, string Text, bool IsTitle, bool ShownAsLine, double? MomentSeconds = null);

public sealed record ReferenceHit(ReferenceEntry Entry, IReadOnlyList<SearchMatch> Matches);

public sealed record ProjectHits(Project Project, IReadOnlyList<ReferenceHit> Hits);

public sealed record SearchResults(
    IReadOnlyList<Project> Projects,
    IReadOnlyList<Paint> Paints,
    IReadOnlyList<AftermarketItem> AftermarketItems,
    IReadOnlyList<ProjectHits> ReferenceGroups)
{
    public int ReferenceCount => ReferenceGroups.Sum(g => g.Hits.Count);
    public bool IsEmpty => Projects.Count == 0 && Paints.Count == 0 && AftermarketItems.Count == 0 && ReferenceGroups.Count == 0;
}

/// <summary>
/// Searches the whole library (behaviour.md, "Search"). The searchable text is
/// prepared once; make a new index after the library changes.
/// </summary>
public sealed class LibrarySearch
{
    private sealed record Part(SearchMatch Match, SearchField Field);

    private sealed record Entry(ReferenceEntry Reference, HashSet<string> Tags, List<Part> Parts);

    private readonly List<(Project Project, List<SearchField> Fields)> _projects;
    private readonly List<(Paint Paint, List<SearchField> Fields)> _paints;
    private readonly List<(AftermarketItem Item, List<SearchField> Fields)> _items;
    private readonly List<Entry> _references;

    public LibrarySearch(LibraryStore store)
    {
        var snapshot = store.ReadSnapshot();
        static List<SearchField> Fields(params string?[] texts) =>
            texts.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => SearchField.Of(t!)).ToList();

        _projects = snapshot.Projects.OrderBy(p => p.Name, NaturalComparer.Instance)
            .Select(p => (p, Fields(p.Name, p.Manufacturer, p.Subject, p.Scale, p.KitNumber, p.MarkingsScheme, p.Notes))).ToList();
        _paints = snapshot.Paints.OrderBy(p => p.Brand, NaturalComparer.Instance).ThenBy(p => p.Code, NaturalComparer.Instance)
            .Select(p => (p, Fields(p.Brand, p.Range, p.Code, p.Name, p.StandardCode, p.Notes))).ToList();
        _items = snapshot.AftermarketItems.OrderBy(i => i.ProductName, NaturalComparer.Instance)
            .Select(i => (i, Fields(i.ProductName, i.Manufacturer, i.ProductNumber, AftermarketTypeName(i.Type), i.ScaleCompatibility, i.Notes,
                Uri.TryCreate(i.ProductUrl, UriKind.Absolute, out var url) ? url.Host : null))).ToList();

        var entries = store.ReferenceEntries(snapshot.References.Select(r => r.Id));
        var markups = snapshot.Markups.GroupBy(m => m.ReferenceId, Ids.Comparer).ToDictionary(g => g.Key, g => g.ToList(), Ids.Comparer);
        _references = entries.Select(e => new Entry(
            e,
            e.Tags.Select(t => t.NormalizedName).ToHashSet(StringComparer.Ordinal),
            Parts(e, markups.GetValueOrDefault(e.Reference.Id) ?? []))).ToList();
    }

    public SearchResults Search(SearchQuery query)
    {
        if (query.IsEmpty) return new SearchResults([], [], [], []);
        // Only references have tags, so a tag token leaves only references.
        var projects = query.Tags.Count > 0 ? [] : _projects.Where(p => query.MatchesEveryWord(p.Fields)).Select(p => p.Project).ToList();
        var paints = query.Tags.Count > 0 ? [] : _paints.Where(p => query.MatchesEveryWord(p.Fields)).Select(p => p.Paint).ToList();
        var items = query.Tags.Count > 0 ? [] : _items.Where(i => query.MatchesEveryWord(i.Fields)).Select(i => i.Item).ToList();

        var hits = new Dictionary<string, List<ReferenceHit>>(Ids.Comparer);
        foreach (var entry in _references)
        {
            if (!query.Tags.IsSubsetOf(entry.Tags) || !query.MatchesEveryWord(entry.Parts.Select(p => p.Field).ToList())) continue;
            var matches = entry.Parts.Where(p => query.MatchesAnyWord(p.Field)).Select(p => p.Match).ToList();
            var projectId = entry.Reference.Reference.ProjectId;
            if (!hits.TryGetValue(projectId, out var list)) hits[projectId] = list = [];
            list.Add(new ReferenceHit(entry.Reference, matches));
        }
        var groups = _projects.Where(p => hits.ContainsKey(p.Project.Id)).Select(p => new ProjectHits(p.Project,
            // Title matches first, then newest first, as in the gallery.
            hits[p.Project.Id].OrderByDescending(h => h.Matches.Any(m => m.IsTitle)).ThenByDescending(h => h.Entry.Reference.CapturedAt).ToList())).ToList();
        return new SearchResults(projects, paints, items, groups);
    }

    private static List<Part> Parts(ReferenceEntry entry, List<Markup> markups)
    {
        var reference = entry.Reference;
        var title = ReferenceText.FallbackTitle(entry);
        var parts = new List<Part>();
        void Add(string label, string? text, bool isTitle = false, bool line = true, double? moment = null)
        {
            var trimmed = text?.Trim();
            if (!string.IsNullOrEmpty(trimmed)) parts.Add(new Part(new SearchMatch(label, trimmed, isTitle, line, moment), SearchField.Of(trimmed)));
        }
        Add("Title", title, isTitle: true, line: false);
        Add("Type", ReferenceText.KindName(reference.Kind), line: false);
        foreach (var tag in entry.Tags) Add("Tag", tag.Name, line: false);
        if (entry.Asset?.OriginalFilename is { } filename && filename != title) Add("File Name", filename);
        Add("Link", reference.SourcePageUrl);
        Add("Text", reference.Text == title ? null : reference.Text);
        Add("Note", reference.Note);
        Add("Channel", reference.VideoChannel);
        Add("Site", reference.LinkSiteName);
        Add("Description", reference.VideoDescription ?? reference.LinkDescription);
        if (reference.Kind == ReferenceKind.Image)
        {
            var number = 0;
            foreach (var note in markups.OrderBy(m => m.CreatedAt)) Add($"Box {++number}", note.Text);
        }
        else
        {
            var number = 0;
            foreach (var markup in markups.OrderBy(m => m.PageIndex).ThenBy(m => m.CreatedAt))
                Add($"{MarkupKindName(markup.Kind)} {++number}", markup.Text);
        }
        foreach (var moment in entry.Moments)
            Add(VideoLink.Format(moment.OffsetSeconds), string.Join(" — ", new[] { moment.Title, moment.Note }.Where(t => t is not null)), moment: moment.OffsetSeconds);
        return parts;
    }

    public static string MarkupKindName(string kind) => kind switch
    {
        "note" => "Note",
        "highlight" => "Highlight",
        "arrow" => "Arrow",
        _ => kind,
    };

    public static string AftermarketTypeName(string type) => type switch
    {
        "decals" => "Decals",
        "resin" => "Resin",
        "photoEtch" => "Photo-Etch",
        "printed3D" => "3D Printed",
        "masks" => "Masks",
        "metal" => "Metal",
        "wheels" => "Wheels",
        "figures" => "Figures",
        "other" => "Other",
        _ => type,
    };
}

/// <summary>A short excerpt around the first match, with every match marked for bold.</summary>
public static class SearchSnippet
{
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;

    public static (string Text, List<(int Start, int Length)> Bold) Excerpt(string text, SearchQuery query, int radius = 60)
    {
        var flat = string.Join(" ", text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        var first = query.Words.Select(w => Compare.IndexOf(flat, w.Text, Options)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();

        var start = 0;
        var prefix = "";
        if (first > radius)
        {
            start = first - radius / 2;
            // Begin at a word boundary.
            var space = flat.IndexOf(' ', start);
            if (space >= 0 && space < first) start = space + 1;
            prefix = "…";
        }
        var end = Math.Min(flat.Length, start + radius * 2);
        var suffix = end < flat.Length ? "…" : "";
        var excerpt = prefix + flat[start..end] + suffix;
        return (excerpt, Highlights(excerpt, query));
    }

    /// <summary>Every place in the text where a word of the query appears.</summary>
    public static List<(int Start, int Length)> Highlights(string text, SearchQuery query)
    {
        var ranges = new List<(int, int)>();
        foreach (var word in query.Words)
        {
            var from = 0;
            while (from < text.Length)
            {
                var found = Compare.IndexOf(text.AsSpan(from), word.Text.AsSpan(), Options, out var length);
                if (found < 0 || length == 0) break;
                var index = from + found;
                ranges.Add((index, length));
                from = index + length;
            }
        }
        // Keep the earliest of any that overlap.
        var merged = new List<(int Start, int Length)>();
        foreach (var range in ranges.OrderBy(r => r.Item1))
            if (merged.Count == 0 || range.Item1 >= merged[^1].Start + merged[^1].Length) merged.Add(range);
        return merged;
    }
}
