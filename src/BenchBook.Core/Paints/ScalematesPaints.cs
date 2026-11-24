using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Paints;

/// <summary>
/// How BenchBook names a Scalemates paint company: Scalemates puts brand and
/// range together ("Vallejo Model Air"), and calls nearly everything
/// water-based "Acrylic" (behaviour.md, "Scalemates import"; ScalematesPaintImporter.swift).
/// </summary>
/// <param name="AcrylicType">The type for a paint Scalemates calls "Acrylic"; null for not set.</param>
public sealed record ScalematesCompany(string Brand, string Range, PaintType? AcrylicType)
{
    /// <summary>Keyed by Scalemates' company name, folded as <see cref="PaintIdentity.Words"/>.</summary>
    private static readonly Dictionary<string, ScalematesCompany> Known = new(StringComparer.Ordinal)
    {
        ["vallejo model air"] = new("Vallejo", "Model Air", PaintType.WaterAcrylic),
        ["vallejo model color"] = new("Vallejo", "Model Color", PaintType.WaterAcrylic),
        ["vallejo metal color"] = new("Vallejo", "Metallic", PaintType.WaterAcrylic),
        ["vallejo model wash"] = new("Vallejo", "Model Wash", PaintType.AcrylicWash),
        ["vallejo game air"] = new("Vallejo", "Game Air", PaintType.WaterAcrylic),
        ["vallejo mecha colors"] = new("Vallejo", "Mecha Color", PaintType.WaterAcrylic),
        ["vallejo surface primer"] = new("Vallejo", "Surface Primer", PaintType.Polyurethane),
        ["ak real colors"] = new("AK Interactive", "Real Color", null),
        ["ak interactive air series"] = new("AK Interactive", "AIR", PaintType.WaterAcrylic),
        ["ak interactive"] = new("AK Interactive", "", null),
        ["mr. aqueous hobby color"] = new("Mr. Hobby", "Aqueous Hobby Color", PaintType.AlcoholAcrylic),
        ["ammo by mig jimenez"] = new("Ammo by Mig", "", PaintType.WaterAcrylic),
    };

    /// <summary>The table's entry, or the company as the brand with no range.</summary>
    public static (ScalematesCompany Company, bool IsKnown) Named(string company) =>
        Known.TryGetValue(PaintIdentity.Words(company), out var known)
            ? (known, true)
            : (new ScalematesCompany(TagNames.CollapseWhitespace(company), "", null), false);

    /// <summary>Scalemates' Type as BenchBook's.</summary>
    public PaintType? TypeFor(string text) => PaintIdentity.Words(text) switch
    {
        "acrylic" => AcrylicType,
        "acrylic lacquer" or "lacquer" => PaintType.Lacquer,
        "enamel" => PaintType.Enamel,
        "pigments" or "pigment" => PaintType.Pigment,
        "oil" => PaintType.Oil,
        _ => PaintType.FromText(text),
    };
}

public sealed record ScalematesPaintSummary
{
    public int MarkedOwned { get; init; }
    public int Added { get; init; }
    public int AlreadyOwned { get; init; }
    /// <summary>Rows for a paint already earlier in the file, e.g. a second bottle.</summary>
    public int SameRows { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
    /// <summary>Company names not in the table, as written in the file.</summary>
    public IReadOnlyList<string> UnknownCompanies { get; init; } = [];

    /// <summary>The "Imported Your Scalemates Stash" message (screens.md, "Paints catalogue").</summary>
    public string Message
    {
        get
        {
            static string Count(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";
            var lines = new List<string>
            {
                $"{Count(MarkedOwned, "paint", "paints")} marked owned.",
                $"{Count(Added, "new paint", "new paints")} added, marked owned.",
            };
            if (AlreadyOwned > 0) lines.Add($"{Count(AlreadyOwned, "paint", "paints")} already owned.");
            if (SameRows > 0) lines.Add($"{Count(SameRows, "row", "rows")} repeated a paint earlier in the file.");
            if (UnknownCompanies.Count > 0)
                lines.Add($"BenchBook doesn't know how to split {PaintText.List(UnknownCompanies)} into brand and range, so the whole name is the brand.");
            lines.AddRange(Problems.Take(10));
            if (Problems.Count > 10) lines.Add($"…and {Problems.Count - 10:N0} more problems.");
            return string.Join("\n", lines);
        }
    }
}

/// <summary>
/// Imports a Scalemates paint stash export (Stash › Paints › export): marks the
/// paints in it owned, adding any the catalogue doesn't have. Paints already
/// in the catalogue are only marked owned; nothing else about them changes,
/// and paints missing from the file stay as they are.
///
/// The header names ten columns, but each row has eleven values: an unnamed
/// column sits between Remaining and Link. Named columns are read by their
/// header position, and Link is the row's last value.
/// </summary>
public static class ScalematesPaintImporter
{
    public static ScalematesPaintSummary Import(LibraryStore store, string text)
    {
        var rows = CsvParser.Parse(text);
        if (rows.Count == 0) throw new PaintImportException("This isn't a Scalemates paint stash export.");
        var keys = rows[0].Fields.Select(PaintIdentity.Words).ToList();
        int? Column(string name) => keys.IndexOf(name) is var i and >= 0 ? i : null;
        if (Column("color") is not { } colorColumn || Column("company") is not { } companyColumn || Column("number") is not { } numberColumn)
            throw new PaintImportException("This isn't a Scalemates paint stash export.");
        var (previewColumn, sizeColumn, typeColumn, finishColumn) = (Column("preview"), Column("size"), Column("type"), Column("finish"));
        if (rows.Count == 1) throw new PaintImportException("The file has no paints to import.");

        var summary = new ScalematesPaintSummary();
        // Read before the transaction: commands inside it must carry it.
        var owned = store.OwnedPaintIds();
        var byBrandAndCode = store.Paints().Where(p => !p.IsMix)
            .GroupBy(p => MatchKey(p.Brand, p.Code), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        // One transaction for the whole file: it all imports, or nothing does.
        store.RunInTransaction(t =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unknown = new Dictionary<string, string>(StringComparer.Ordinal);
            var problems = new List<string>();
            int markedOwned = 0, added = 0, alreadyOwned = 0, sameRows = 0;

            foreach (var (line, fields) in rows.Skip(1))
            {
                string Value(int? index) => index is { } i && i < fields.Count ? fields[i].Trim() : "";
                var companyName = Value(companyColumn);
                var code = Value(numberColumn);
                if (companyName.Length == 0 || code.Length == 0)
                {
                    problems.Add($"Line {line}: a paint needs a company and a number, so it was skipped.");
                    continue;
                }
                var (company, isKnown) = ScalematesCompany.Named(companyName);
                if (!isKnown) unknown[PaintIdentity.Words(companyName)] = companyName;

                var key = MatchKey(company.Brand, code);
                if (!seen.Add(key))
                {
                    sameRows++;
                    continue;
                }

                if (byBrandAndCode.TryGetValue(key, out var found) && found.Count > 0)
                {
                    if (Pick(found, company.Range) is not { } paint)
                    {
                        var names = PaintText.List(found.Select(PaintText.DisplayName).ToList());
                        problems.Add($"Line {line}: {companyName} {code} matches {names}, so none of them was marked owned.");
                        continue;
                    }
                    if (owned.Contains(paint.Id)) alreadyOwned++;
                    else
                    {
                        store.SetOwned(paint.Id, true, t);
                        owned.Add(paint.Id);
                        markedOwned++;
                    }
                    continue;
                }

                var link = fields.Count > 0 ? fields[^1].Trim() : "";
                var notes = string.Join(" · ", new[] { Value(finishColumn), Value(sizeColumn), link.StartsWith("http", StringComparison.Ordinal) ? link : "" }.Where(s => s.Length > 0));
                var preview = Value(previewColumn);
                var swatch = SwatchFromPreview(preview, out var isColour);
                if (!isColour) problems.Add($"Line {line}: “{preview}” isn't a colour like 168;181;155, so no swatch was set.");
                var draft = new PaintDraft
                {
                    Brand = company.Brand, Range = company.Range, Code = code, Name = Value(colorColumn),
                    Type = company.TypeFor(Value(typeColumn))?.Raw ?? "", Notes = notes, SwatchHex = swatch, IsOwned = true,
                };
                var created = PaintService.New();
                PaintService.Apply(draft, created);
                store.InsertPaint(created, owned: true, t);
                owned.Add(created.Id);
                byBrandAndCode[key] = [created];
                added++;
            }

            summary = new ScalematesPaintSummary
            {
                MarkedOwned = markedOwned, Added = added, AlreadyOwned = alreadyOwned, SameRows = sameRows, Problems = problems,
                UnknownCompanies = unknown.Values.OrderBy(n => n, NaturalComparer.Instance).ToList(),
            };
        });
        return summary;
    }

    /// <summary>
    /// Brand and code, ignoring range, since Scalemates' ranges don't line up
    /// with the catalogue's. The code is folded as in the identity key.
    /// </summary>
    public static string MatchKey(string brand, string code) => $"{PaintIdentity.Words(brand)}|{PaintIdentity.FoldCode(code)}";

    /// <summary>The one paint, or among several the one in the table's range.</summary>
    private static Paint? Pick(List<Paint> paints, string range)
    {
        if (paints.Count == 1) return paints[0];
        var wanted = PaintIdentity.Words(range);
        if (wanted.Length == 0) return null;
        var inRange = paints.Where(p => PaintIdentity.Words(p.Range) == wanted).ToList();
        return inRange.Count == 1 ? inRange[0] : null;
    }

    /// <summary>"168;181;155" as "#A8B59B". ";;" or empty is no colour; anything else isn't a colour.</summary>
    public static string? SwatchFromPreview(string text, out bool isColour)
    {
        var parts = text.Split(';').Select(p => p.Trim()).ToList();
        isColour = true;
        if (parts.All(p => p.Length == 0)) return null;
        var values = parts.Select(p => int.TryParse(p, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (int?)null).ToList();
        if (parts.Count != 3 || values.Any(v => v is null or < 0 or > 255))
        {
            isColour = false;
            return null;
        }
        return "#" + string.Concat(values.Select(v => v!.Value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
