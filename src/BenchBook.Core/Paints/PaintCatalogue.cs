using System.Globalization;
using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Paints;

/// <summary>
/// The catalogue as screens show it: each paint, who owns what, and each mix's
/// recipe, so a mix's ownership and recipe text can be worked out
/// (behaviour.md, "Mixes (stored)"; Paint+Display.swift).
/// </summary>
public sealed class PaintCatalogue
{
    private readonly Dictionary<string, List<PaintMixPart>> _recipes;
    private readonly Dictionary<string, List<Paint>> _mixesUsing;

    public PaintCatalogue(IEnumerable<Paint> paints, IEnumerable<string> ownedIds, IEnumerable<PaintMixPart> parts)
    {
        ById = paints.ToDictionary(p => p.Id, Ids.Comparer);
        OwnedIds = ownedIds.ToHashSet(Ids.Comparer);
        _recipes = parts.GroupBy(p => p.MixId, Ids.Comparer).ToDictionary(
            g => g.Key,
            g => g.OrderBy(p => p.Position).ThenBy(p => p.CreatedAt).ToList(),
            Ids.Comparer);
        _mixesUsing = _recipes
            .SelectMany(pair => pair.Value.Select(part => (part.PaintId, MixId: pair.Key)))
            .Where(x => ById.ContainsKey(x.MixId))
            .GroupBy(x => x.PaintId, Ids.Comparer)
            .ToDictionary(g => g.Key, g => g.Select(x => ById[x.MixId]).OrderBy(PaintText.DisplayName, NaturalComparer.Instance).ToList(), Ids.Comparer);
    }

    public static PaintCatalogue Load(LibraryStore store) => new(store.Paints(), store.OwnedPaintIds(), store.AllMixParts());

    public IReadOnlyDictionary<string, Paint> ById { get; }

    /// <summary>Paints with an ownership record. A mix never has one.</summary>
    public IReadOnlySet<string> OwnedIds { get; }

    public Paint? Paint(string? id) => id is not null && ById.TryGetValue(id, out var paint) ? paint : null;

    /// <summary>A mix's recipe lines, in order, for paints still in the catalogue.</summary>
    public IReadOnlyList<PaintMixPart> Recipe(Paint mix) =>
        _recipes.TryGetValue(mix.Id, out var parts) ? parts.Where(p => ById.ContainsKey(p.PaintId)).ToList() : [];

    /// <summary>The paints in a mix's recipe, in order.</summary>
    public IReadOnlyList<Paint> RecipePaints(Paint mix) => Recipe(mix).Select(p => ById[p.PaintId]).ToList();

    /// <summary>A paint is owned when the user owns it; a mix when its recipe has paints and the user owns every one.</summary>
    public bool IsOwned(Paint paint)
    {
        if (!paint.IsMix) return OwnedIds.Contains(paint.Id);
        var paints = RecipePaints(paint);
        return paints.Count > 0 && paints.All(p => OwnedIds.Contains(p.Id));
    }

    /// <summary>A mix's recipe paints the user doesn't own yet.</summary>
    public IReadOnlyList<Paint> MissingRecipePaints(Paint mix) => RecipePaints(mix).Where(p => !OwnedIds.Contains(p.Id)).ToList();

    /// <summary>What you'd buy to paint with this: itself, or a mix's recipe paints.</summary>
    public IReadOnlyList<Paint> ShoppingPaints(Paint paint) => paint.IsMix ? RecipePaints(paint) : [paint];

    /// <summary>Mixes whose recipe uses this paint, by name.</summary>
    public IReadOnlyList<Paint> MixesUsing(Paint paint) => _mixesUsing.GetValueOrDefault(paint.Id) ?? [];

    /// <summary>e.g. "3 parts XF-2, 1 part XF-19". Empty for a paint that isn't a mix.</summary>
    public string RecipeSummary(Paint mix) =>
        string.Join(", ", Recipe(mix).Select(p => MixAmount.Line(p.Parts, PaintText.ShortName(ById[p.PaintId]))));

    /// <summary>
    /// Range and colour standard, e.g. "Acrylic XF · FS 36270". A mix shows its
    /// recipe instead of a range: "Mix: 3 parts XF-2, 1 part XF-19".
    /// </summary>
    public string SecondaryLine(Paint paint)
    {
        var first = paint.IsMix ? MixAmount.RecipeLine(RecipeSummary(paint)) : paint.Range;
        return string.Join(" · ", new[] { first, paint.Standard?.DisplayText ?? "" }.Where(s => s.Length > 0));
    }

    /// <summary>The Owned tick box's help for a mix, which can't be ticked.</summary>
    public string MixOwnedHelp(Paint mix)
    {
        if (Recipe(mix).Count == 0) return "Add a recipe to this mix. It's owned when you own every paint in it.";
        var missing = MissingRecipePaints(mix).Select(PaintText.DisplayName).ToList();
        return missing.Count == 0 ? "You own every paint in this mix" : $"Not owned yet: {PaintText.List(missing)}";
    }

    /// <summary>The Owned box's help: a paint's ownership, or a mix's recipe.</summary>
    public string OwnedHelp(Paint paint) =>
        paint.IsMix ? MixOwnedHelp(paint) : IsOwned(paint) ? "You own this paint" : "You don't own this paint yet";
}

/// <summary>How a recipe's parts are written.</summary>
public static class MixAmount
{
    /// <summary>"3", "1.5" or "0.25": up to two decimals.</summary>
    public static string Text(double parts) => parts.ToString("#,##0.##", CultureInfo.CurrentCulture);

    /// <summary>"1 part XF-19" or "3 parts XF-2".</summary>
    public static string Line(double parts, string paint) => parts == 1 ? $"1 part {paint}" : $"{Text(parts)} parts {paint}";

    /// <summary>"part" for exactly one, otherwise "parts".</summary>
    public static string Unit(double parts) => parts == 1 ? "part" : "parts";

    /// <summary>"Mix: 3 parts XF-2, 1 part XF-19", or "Mix" with no recipe yet.</summary>
    public static string RecipeLine(string summary) => summary.Length == 0 ? "Mix" : $"Mix: {summary}";

    /// <summary>Reads a parts field in the user's number format. Null when it isn't a number.</summary>
    public static double? Parse(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) ? value : null;
}

/// <summary>Which paints are clears, drawn as glass. Worked out from the name, so nothing is stored.</summary>
public static class PaintClarity
{
    /// <summary>
    /// True when the name, split into words at anything that isn't a letter,
    /// has the word "clear", ignoring case and accents: "Clear", "Flat clear",
    /// "Clear Red", "Semi-Gloss Super Clear". "Unclear" and "Clearcoat" aren't.
    /// </summary>
    public static bool IsClear(string name)
    {
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        var start = -1;
        for (var i = 0; i <= name.Length; i++)
        {
            // An accent written as its own mark stays part of the word, as in Swift's characters.
            var letter = i < name.Length && (char.IsLetter(name[i])
                || (start >= 0 && CharUnicodeInfo.GetUnicodeCategory(name[i]) == UnicodeCategory.NonSpacingMark));
            if (letter && start < 0) start = i;
            else if (!letter && start >= 0)
            {
                if (compare.Compare(name.AsSpan(start, i - start), "clear", CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0) return true;
                start = -1;
            }
        }
        return false;
    }
}
