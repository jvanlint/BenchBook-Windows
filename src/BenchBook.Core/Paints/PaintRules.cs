using System.Globalization;
using BenchBook.Core.Model;

namespace BenchBook.Core.Paints;

/// <summary>How paints are identified and searched (behaviour.md, "Paint identity (stored)" and "Paint search").</summary>
public static class PaintIdentity
{
    /// <summary>
    /// brand|range|code: brand and range with whitespace collapsed and lowercased,
    /// code lowercased without whitespace or "- _ . /". So "Tamiya", "Acrylic XF",
    /// "XF-54" gives "tamiya|acrylic xf|xf54". Both apps must agree on this exactly.
    /// </summary>
    public static string Key(string brand, string range, string code) =>
        $"{Words(brand)}|{Words(range)}|{CompactCode(code)}";

    /// <summary>
    /// A mix is known by brand and name, as its code is optional: "Custom",
    /// "Faded Sky Grey" gives "mix|custom|faded sky grey" (behaviour.md, "Paint identity").
    /// </summary>
    public static string MixKey(string brand, string name) => $"mix|{Words(brand)}|{Words(name)}";

    /// <summary>The code folded as in the key, so "AK 480" and "AK480" are the same.</summary>
    public static string FoldCode(string code) => CompactCode(code);

    /// <summary>" Tamiya  Acrylic" gives "tamiya acrylic".</summary>
    public static string Words(string text) => TagNames.CollapseWhitespace(text).ToLowerInvariant();

    private static string CompactCode(string code) =>
        string.Concat(code.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '_' or '.' or '/')));

    /// <summary>
    /// A paint matches when brand, range, code, name or standard contains the
    /// query, ignoring case. The standard is its shown text and its system's
    /// name, e.g. "FS 36270 FS 595", so "FS 595" finds every FS paint. A standard
    /// also matches with everything but letters and digits removed from both, so
    /// "FS 36270" finds "FS36270" and "FS-36270 / BS 637".
    /// </summary>
    public static bool Matches(string query, Paint paint)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        var standard = paint.Standard?.SearchText ?? "";
        if (new[] { paint.Brand, paint.Range, paint.Code, paint.Name, standard }
            .Any(f => CultureInfo.CurrentCulture.CompareInfo.IndexOf(f, query, CompareOptions.IgnoreCase) >= 0))
            return true;
        var folded = LettersAndDigits(query);
        return folded.Length > 0 && LettersAndDigits(standard).Contains(folded, StringComparison.Ordinal);
    }

    private static string LettersAndDigits(string text) => string.Concat(text.ToLowerInvariant().Where(char.IsLetterOrDigit));
}

/// <summary>Swatches: "#RRGGBB", "RRGGBB" or "#RGB" in, uppercase "#RRGGBB" out.</summary>
public static class Swatch
{
    public static string? Normalize(string? text)
    {
        if (text is null) return null;
        var hex = text.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length != 6 || !hex.All(char.IsAsciiHexDigit)) return null;
        return "#" + hex.ToUpperInvariant();
    }

    public static (byte R, byte G, byte B)? Components(string? text) =>
        Normalize(text) is { } hex
            ? (Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16))
            : null;
}

/// <summary>A brand or range to filter by, with how many paints have it.</summary>
/// <param name="Key">Folded with <see cref="PaintIdentity.Words"/>.</param>
/// <param name="Name">The spelling most paints use.</param>
public sealed record PaintFilterOption(string Key, string Name, int Count)
{
    /// <summary>One option per distinct value, ignoring blanks, sorted by name.</summary>
    public static List<PaintFilterOption> For(IEnumerable<string> values)
    {
        var spellings = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var trimmed = value.Trim();
            var key = PaintIdentity.Words(trimmed);
            if (key.Length == 0) continue;
            if (!spellings.TryGetValue(key, out var counts)) spellings[key] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
            counts[trimmed] = counts.GetValueOrDefault(trimmed) + 1;
        }
        return spellings.Select(pair =>
            {
                var name = pair.Value.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).First().Key;
                return new PaintFilterOption(pair.Key, name, pair.Value.Values.Sum());
            })
            .OrderBy(o => o.Name, NaturalComparer.Instance)
            .ToList();
    }
}

public static class PaintText
{
    /// <summary>e.g. "Tamiya XF-54 Dark Sea Grey".</summary>
    public static string DisplayName(Paint paint) =>
        string.Join(" ", new[] { paint.Brand, paint.Code, paint.Name }.Where(s => s.Length > 0));

    /// <summary>
    /// Range and colour standard, e.g. "Acrylic XF · FS 36270". A mix's line
    /// shows its recipe, which needs the catalogue: see <see cref="PaintCatalogue.SecondaryLine"/>.
    /// </summary>
    public static string SecondaryLine(Paint paint) =>
        string.Join(" · ", new[] { paint.IsMix ? "Mix" : paint.Range, paint.Standard?.DisplayText ?? "" }.Where(s => s.Length > 0));

    /// <summary>In a recipe: its code, or its name if it has no code, else its brand.</summary>
    public static string ShortName(Paint paint) =>
        paint.Code.Trim() is { Length: > 0 } code ? code : paint.Name.Trim() is { Length: > 0 } name ? name : paint.Brand;

    /// <summary>"a", "a and b", or "a, b and c".</summary>
    public static string List(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";
}
