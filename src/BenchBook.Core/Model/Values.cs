using System.Globalization;

namespace BenchBook.Core.Model;

/// <summary>Record ids: uppercase UUID strings with hyphens, compared without regard to case.</summary>
public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("D").ToUpperInvariant();

    /// <summary>The canonical form of an id read from elsewhere, or null if it isn't a UUID.</summary>
    public static string? Normalize(string? id) =>
        id is not null && Guid.TryParseExact(id, "D", out var guid) ? guid.ToString("D").ToUpperInvariant() : null;

    public static bool IsValid(string? id) => Normalize(id) is not null;

    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
}

/// <summary>Dates are ISO 8601 in UTC with milliseconds: 2026-11-13T09:41:07.250Z.</summary>
public static class IsoDate
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Write(DateTimeOffset date) =>
        date.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>Reads any ISO 8601 date with a time zone, with or without fractions of a second.</summary>
    public static bool TryRead(string? text, out DateTimeOffset date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;
        // Require the date part to be ISO shaped, so "13/11/2026" isn't taken.
        if (text.Length < 10 || text[4] != '-' || text[7] != '-') return false;
        date = RoundToMilliseconds(parsed.ToUniversalTime());
        return true;
    }

    public static DateTimeOffset Read(string text) =>
        TryRead(text, out var date) ? date : throw new FormatException($"“{text}” isn't an ISO 8601 date.");

    /// <summary>
    /// The stored precision, to the nearest millisecond (library-format.md, "Value
    /// types"). Keeps round trips exact.
    /// </summary>
    public static DateTimeOffset RoundToMilliseconds(DateTimeOffset date)
    {
        var ms = TimeSpan.TicksPerMillisecond;
        var ticks = (date.Ticks + ms / 2) / ms * ms;
        return new DateTimeOffset(ticks, date.Offset);
    }

    public static DateTimeOffset Now() => RoundToMilliseconds(DateTimeOffset.UtcNow);
}

/// <summary>
/// The decimal value type: an amount of money as a string with a full stop and
/// two decimals and no currency, e.g. "18.50" (library-format.md, "Value types").
/// </summary>
public static class Money
{
    public static string Write(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A plain number with a full stop, as written by either app.</summary>
    public static bool TryRead(string? text, out decimal amount) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
}
