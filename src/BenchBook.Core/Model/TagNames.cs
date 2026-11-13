using System.Text;

namespace BenchBook.Core.Model;

/// <summary>Tag name rules (behaviour.md, "Tags (stored)"). Both apps must agree on these exactly.</summary>
public static class TagNames
{
    /// <summary>
    /// Splits "wheel well, Cockpit ,, WHEEL WELL ,decals" into
    /// ["wheel well", "Cockpit", "decals"]: whitespace collapsed and trimmed,
    /// empty pieces dropped, later pieces with an already-seen normalized name dropped.
    /// </summary>
    public static List<string> Parse(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var piece in text.Split(','))
        {
            var name = CollapseWhitespace(piece);
            if (name.Length > 0 && seen.Add(Normalize(name))) result.Add(name);
        }
        return result;
    }

    /// <summary>"  Wheel   Well " gives "wheel well".</summary>
    public static string Normalize(string name) => CollapseWhitespace(name).ToLowerInvariant();

    /// <summary>Runs of any Unicode whitespace become one space, with none at the ends.</summary>
    public static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    public static readonly IReadOnlyList<string> Colors =
        ["red", "orange", "yellow", "green", "mint", "teal", "blue", "indigo", "purple", "pink", "brown", "gray"];
}
