namespace BenchBook.Core.Model;

/// <summary>
/// A colour standard system, stored by its raw value (behaviour.md, "Paint
/// standards (stored)"; PaintStandard.swift on the Mac).
/// </summary>
/// <param name="Raw">Stored, e.g. "fs595".</param>
/// <param name="MenuName">e.g. "FS 595". Also the CSV name.</param>
/// <param name="Prefix">Written before the code, e.g. "FS"; null where the code says it all.</param>
/// <param name="Subjects">The menu item's hint.</param>
/// <param name="Prompt">The code field's placeholder.</param>
public sealed record PaintStandardSystem(string Raw, string MenuName, string? Prefix, string Subjects, string Prompt)
{
    public static readonly PaintStandardSystem Fs595 = new("fs595", "FS 595", "FS", "FS 595 / AMS-STD-595: modern military aviation", "e.g. 36270");
    public static readonly PaintStandardSystem Ral = new("ral", "RAL", "RAL", "RAL Classic: German armour and military vehicles", "e.g. 7021");
    public static readonly PaintStandardSystem Rlm = new("rlm", "RLM", "RLM", "RLM: WWII Luftwaffe", "e.g. 76");
    public static readonly PaintStandardSystem Bs381c = new("bs381c", "BS 381C", "BS", "BS 381C: British military aircraft", "e.g. 637");
    public static readonly PaintStandardSystem Ana = new("ana", "ANA", "ANA", "ANA: WWII US aviation", "e.g. 613");
    public static readonly PaintStandardSystem Map = new("map", "MAP", "MAP", "MAP: WWII RAF and Fleet Air Arm", "e.g. Sky");
    public static readonly PaintStandardSystem Bulletin41 = new("bulletin41", "Bulletin 41", "Bulletin 41", "USAAF Bulletin 41: WWII US Army aircraft", "e.g. 41");
    public static readonly PaintStandardSystem Amt = new("amt", "AMT / A-series", null, "AMT / AII / A-series: Soviet WWII aviation", "e.g. AMT-4");
    public static readonly PaintStandardSystem Ijn = new("ijn", "IJN / IJA", null, "IJN / IJA: Japanese WWII aviation", "e.g. IJN J3");
    public static readonly PaintStandardSystem Tavola10 = new("tavola10", "Tavola 10", "Tavola 10", "Tavola 10: Italian WWII aviation", "e.g. Verde Mimetico 2");
    public static readonly PaintStandardSystem RaafK3 = new("raafK3", "RAAF K3", "K3", "RAAF K3: Australian WWII aircraft", "e.g. 195");
    public static readonly PaintStandardSystem Ships2 = new("ships2", "SHIPS-2", null, "US Navy SHIPS-2: WWII American warships", "e.g. 5-N");
    public static readonly PaintStandardSystem Multiple = new("multiple", "Multiple", null, "More than one standard, written out, e.g. FS 36375 / BS 637", "e.g. FS 36375 / BS 637");
    public static readonly PaintStandardSystem Custom = new("custom", "Custom", null, "Any other standard, written out", "e.g. Humbrol 28");

    /// <summary>In menu order.</summary>
    public static readonly IReadOnlyList<PaintStandardSystem> All =
        [Fs595, Ral, Rlm, Bs381c, Ana, Map, Bulletin41, Amt, Ijn, Tavola10, RaafK3, Ships2, Multiple, Custom];

    /// <summary>The system with this raw value, or null if this version doesn't know it.</summary>
    public static PaintStandardSystem? FromRaw(string? raw) => All.FirstOrDefault(s => s.Raw == raw);

    /// <summary>A raw value or menu name, ignoring case, e.g. "fs595" or "FS 595". Null if it isn't one.</summary>
    public static PaintStandardSystem? FromName(string text)
    {
        var key = text.Trim();
        if (key.Length == 0) return null;
        return All.FirstOrDefault(s => string.Equals(s.Raw, key, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(s.MenuName, key, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A system and a code within it. Shown as e.g. "FS 36270".</summary>
/// <param name="SystemRaw">As stored, so an unknown system is kept. It's shown as Custom.</param>
public sealed record PaintStandard(string SystemRaw, string Code)
{
    public PaintStandard(PaintStandardSystem system, string code) : this(system.Raw, code) { }

    /// <summary>Null for no system.</summary>
    public static PaintStandard? From(string? systemRaw, string? code) =>
        string.IsNullOrEmpty(systemRaw) ? null : new PaintStandard(systemRaw, code ?? "");

    /// <summary>An unknown stored system reads as Custom, so its code still shows.</summary>
    public PaintStandardSystem System => PaintStandardSystem.FromRaw(SystemRaw) ?? PaintStandardSystem.Custom;

    /// <summary>"FS 36270", "RLM 76", "J3" or "FS 36375 / BS 637". A system with no code shows its menu name, e.g. "RLM".</summary>
    public string DisplayText
    {
        get
        {
            var code = Code.Trim();
            if (code.Length == 0) return System.MenuName;
            return System.Prefix is { } prefix ? $"{prefix} {code}" : code;
        }
    }

    /// <summary>
    /// For matching a paint to a guide colour, whatever the spelling: "FS 36270",
    /// "FS36270" and "fs-36270" are the same. Multiple and Custom match on their
    /// text alone, so a Custom "FS 36270" still finds FS 595 36270.
    /// </summary>
    public string MatchKey
    {
        get
        {
            var folded = string.Concat(DisplayText.ToLowerInvariant().Where(char.IsLetterOrDigit));
            return System == PaintStandardSystem.Multiple || System == PaintStandardSystem.Custom ? folded : System.Raw + ":" + folded;
        }
    }

    /// <summary>
    /// What a search matches: the shown text and the system's name, e.g.
    /// "FS 36270 FS 595", so "FS 595" finds every FS paint (behaviour.md, "Paint search").
    /// </summary>
    public string SearchText => $"{DisplayText} {System.MenuName}";
}

/// <summary>
/// Splits free text such as "FS 36270" into a system and a code (behaviour.md,
/// "Reading standard text (stored)"). Nothing is lost: text it doesn't recognise
/// is kept whole as Multiple or Custom.
/// </summary>
public static class PaintStandardParser
{
    /// <summary>Longest first, so "FS 595" wins over "FS".</summary>
    private static readonly (string Pattern, PaintStandardSystem System)[] Prefixes =
    [
        ("AMS-STD-595", PaintStandardSystem.Fs595), ("AMS STD 595", PaintStandardSystem.Fs595),
        ("FS 595", PaintStandardSystem.Fs595), ("FS-595", PaintStandardSystem.Fs595), ("FS595", PaintStandardSystem.Fs595),
        ("BS 381C", PaintStandardSystem.Bs381c), ("BS-381C", PaintStandardSystem.Bs381c), ("BS381C", PaintStandardSystem.Bs381c),
        ("BS 381", PaintStandardSystem.Bs381c), ("BS381", PaintStandardSystem.Bs381c), ("BSC", PaintStandardSystem.Bs381c),
        ("FS", PaintStandardSystem.Fs595), ("RAL", PaintStandardSystem.Ral), ("RLM", PaintStandardSystem.Rlm), ("BS", PaintStandardSystem.Bs381c),
        ("ANA", PaintStandardSystem.Ana), ("MAP", PaintStandardSystem.Map),
    ];

    /// <summary>Null for empty text.</summary>
    public static PaintStandard? Parse(string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) return null;
        // "FS 36375 / BS 637", "RLM 74, RLM 75" or "FS 34102 & RAL 6003".
        if (text.Any(c => c is '/' or ',' or ';' or '&' or '+')) return new PaintStandard(PaintStandardSystem.Multiple, text);
        // IJN and IJA keep their prefix in the code, as it says which service.
        foreach (var prefix in new[] { "IJN", "IJA" })
            if (HasPrefix(text, prefix)) return new PaintStandard(PaintStandardSystem.Ijn, text);
        foreach (var (pattern, system) in Prefixes)
        {
            if (!HasPrefix(text, pattern)) continue;
            var rest = text[pattern.Length..].TrimStart();
            var start = 0;
            while (start < rest.Length && (char.IsWhiteSpace(rest[start]) || rest[start] is '-' or ':' or '#' or '.')) start++;
            return new PaintStandard(system, rest[start..]);
        }
        return new PaintStandard(PaintStandardSystem.Custom, text);
    }

    /// <summary>
    /// Ignoring case. A letter can't follow the prefix, so "Maple" isn't MAP, but
    /// a digit can: "FS36270". After a prefix ending in a digit or "381C", such as
    /// "FS595" or "BS381C", neither can.
    /// </summary>
    private static bool HasPrefix(string text, string prefix)
    {
        if (text.Length < prefix.Length || !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (text.Length == prefix.Length) return true;
        var next = text[prefix.Length];
        var endsInNumber = char.IsDigit(prefix[^1]) || prefix.EndsWith("381C", StringComparison.Ordinal);
        return !char.IsLetter(next) && !(endsInNumber && char.IsDigit(next));
    }
}

/// <summary>How closely a paint follows its standard. Only set when it has one.</summary>
/// <param name="Raw">Stored: "none", "closest" or "exact".</param>
/// <param name="Shown">e.g. "Exact Match".</param>
/// <param name="CsvName">e.g. "Exact match".</param>
public sealed record PaintMatch(string Raw, string Shown, string CsvName)
{
    public static readonly PaintMatch None = new("none", "No Match", "No match");
    public static readonly PaintMatch Closest = new("closest", "Closest Match", "Closest match");
    public static readonly PaintMatch Exact = new("exact", "Exact Match", "Exact match");

    public static readonly IReadOnlyList<PaintMatch> All = [None, Closest, Exact];

    public static PaintMatch? FromRaw(string? raw) => All.FirstOrDefault(m => m.Raw == raw);

    /// <summary>A raw value, shown name or CSV name, ignoring case.</summary>
    public static PaintMatch? FromText(string text)
    {
        var key = text.Trim();
        return All.FirstOrDefault(m => new[] { m.Raw, m.Shown, m.CsvName }.Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>What a paint is made with (behaviour.md, "Paint types (stored)").</summary>
public sealed record PaintType(string Raw, string Shown, string CsvName, string Hint)
{
    public static readonly PaintType WaterAcrylic = new("waterAcrylic", "Water-Based Acrylic", "Water-based acrylic", "Low odour, fast drying, water cleanup. Vallejo Model Color, AK 3rd Gen, Ammo MIG, Citadel, Mr. Hobby Acrysion.");
    public static readonly PaintType AlcoholAcrylic = new("alcoholAcrylic", "Alcohol-Compatible Acrylic", "Alcohol-compatible acrylic", "Fast drying, excellent for airbrushing. Tamiya X/XF, Mr. Hobby Aqueous.");
    public static readonly PaintType Lacquer = new("lacquer", "Lacquer", "Lacquer", "Solvent-based, hard finish. Mr. Color, Tamiya LP, MRP, AK Real Colors.");
    public static readonly PaintType Enamel = new("enamel", "Enamel", "Enamel", "Solvent-based, slower drying. Humbrol Enamel, Tamiya Enamel, Revell Email Color.");
    public static readonly PaintType Oil = new("oil", "Oil Paint", "Oil paint", "Very slow drying, for weathering. Abteilung 502, Winsor & Newton.");
    public static readonly PaintType Polyurethane = new("polyurethane", "Polyurethane-Based Paint", "Polyurethane-based paint", "Primers and coatings. Vallejo Surface Primer.");
    public static readonly PaintType AcrylicWash = new("acrylicWash", "Acrylic Wash", "Acrylic wash", "Thin, water-based, for panel lines and shading. Vallejo Model Wash, Citadel Shade, AK Acrylic Wash.");
    public static readonly PaintType EnamelWash = new("enamelWash", "Enamel Wash", "Enamel wash", "Thin, solvent-based, wiped back with enamel thinner. Ammo MIG Panel Line Wash, AK Interactive washes, Tamiya Panel Line Accent Color.");
    public static readonly PaintType Pigment = new("pigment", "Pigment", "Pigment", "Dry powders for dust, mud and rust. MIG Productions, AK Interactive Pigments, Vallejo Pigments.");
    public static readonly PaintType Marker = new("marker", "Marker", "Marker", "Paint or ink in a pen, for details and touch-ups. Gundam Marker, Molotow Liquid Chrome, Tamiya Paint Marker.");
    public static readonly PaintType Other = new("other", "Other", "Other", "Inks, metal waxes and so on.");

    /// <summary>In menu order.</summary>
    public static readonly IReadOnlyList<PaintType> All =
        [WaterAcrylic, AlcoholAcrylic, Lacquer, Enamel, Oil, Polyurethane, AcrylicWash, EnamelWash, Pigment, Marker, Other];

    /// <summary>Null for Not Set, and for a type this version doesn't know.</summary>
    public static PaintType? FromRaw(string? raw) => All.FirstOrDefault(t => t.Raw == raw);

    /// <summary>A raw value, shown name or CSV name, ignoring case.</summary>
    public static PaintType? FromText(string text)
    {
        var key = text.Trim();
        return All.FirstOrDefault(t => new[] { t.Raw, t.Shown, t.CsvName }.Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase)));
    }
}
