using System.Globalization;

namespace BenchBook.Core.Model;

/// <summary>
/// "Natural" order (behaviour.md, "Sorting"): ignores case and compares runs
/// of digits as numbers, so XF-2 comes before XF-10.
/// </summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    private static readonly CompareInfo Culture = CultureInfo.InvariantCulture.CompareInfo;

    public int Compare(string? x, string? y) => CompareStrings(x, y);

    private static int CompareStrings(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i; var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(startX, i - startX).TrimStart('0');
                var b = y.AsSpan(startY, j - startY).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var digits = a.SequenceCompareTo(b);
                if (digits != 0) return Math.Sign(digits);
            }
            else
            {
                var startX = i; var startY = j;
                while (i < x.Length && !char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && !char.IsAsciiDigit(y[j])) j++;
                var text = Culture.Compare(x.AsSpan(startX, i - startX), y.AsSpan(startY, j - startY), CompareOptions.IgnoreCase);
                if (text != 0) return Math.Sign(text);
            }
        }
        var rest = (x.Length - i).CompareTo(y.Length - j);
        // Equal ignoring case and leading zeros: fall back to an exact order, so sorting is stable.
        return rest != 0 ? rest : Math.Sign(string.CompareOrdinal(x, y));
    }
}
