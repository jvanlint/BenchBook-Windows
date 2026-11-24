using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using BenchBook.Core.Store;

namespace BenchBook.Core.Kits;

/// <summary>What a kit import did, for the summary.</summary>
/// <param name="Updated">Existing entries the file changed.</param>
/// <param name="Unchanged">Rows for entries the library already has as they are.</param>
/// <param name="Skipped">Rows that aren't kits, by their Type, most first.</param>
public sealed record KitImportSummary(int Added, int Updated, int Unchanged, IReadOnlyList<(string Type, int Count)> Skipped, IReadOnlyList<string> Problems)
{
    public int SkippedCount => Skipped.Sum(s => s.Count);

    /// <summary>"Added 3 Kits and Updated 1", "Added 3 Kits", "Updated 1 Kit" or "No Kits Added".</summary>
    public string Title => (Added, Updated) switch
    {
        ( > 0, > 0) => $"Added {Added:N0} {(Added == 1 ? "Kit" : "Kits")} and Updated {Updated:N0}",
        ( > 0, _) => $"Added {Added:N0} {(Added == 1 ? "Kit" : "Kits")}",
        (_, > 0) => $"Updated {Updated:N0} {(Updated == 1 ? "Kit" : "Kits")}",
        _ => "No Kits Added",
    };

    /// <summary>"Already in your catalogue: 110.", the skipped rows, then up to ten problems.</summary>
    public List<string> Lines()
    {
        var lines = new List<string>();
        if (Unchanged > 0) lines.Add($"Already in your catalogue: {Unchanged:N0}.");
        if (SkippedCount > 0)
            lines.Add($"Skipped {SkippedCount:N0} {(SkippedCount == 1 ? "row" : "rows")} for things other than kits: {string.Join(", ", Skipped.Select(s => $"{s.Type} {s.Count:N0}"))}.");
        lines.AddRange(Problems.Take(10));
        if (Problems.Count > 10) lines.Add($"…and {Problems.Count - 10:N0} more problems.");
        return lines;
    }
}

public sealed class KitImportException(string message) : Exception(message);

/// <summary>
/// Imports kits from a Scalemates stash export or BenchBook's own export, which
/// has a "BenchBook ID" column (behaviour.md, "Kits › Import", and
/// KitCSVImporter.swift). Each row is one physical kit. All of it imports, or none.
/// </summary>
public static class KitCsvImporter
{
    /// <summary>The Scalemates type of a kit. Other rows are decals, detail sets and so on.</summary>
    public const string KitType = "Static model kits";

    private enum Column { Id, Brand, Name, Scale, Number, Type, Price, Link, Owned, Status, Rating, ForSale, SalePrice, Notes }

    private static readonly Dictionary<string, Column> Headers = new(StringComparer.Ordinal)
    {
        ["benchbook id"] = Column.Id,
        ["brand"] = Column.Brand, ["manufacturer"] = Column.Brand,
        ["title"] = Column.Name, ["name"] = Column.Name,
        ["scale"] = Column.Scale,
        ["number"] = Column.Number, ["kit number"] = Column.Number,
        ["type"] = Column.Type,
        ["price"] = Column.Price, ["price paid"] = Column.Price,
        ["link"] = Column.Link, ["url"] = Column.Link,
        ["owned"] = Column.Owned, ["status"] = Column.Status, ["rating"] = Column.Rating,
        ["for sale"] = Column.ForSale, ["sale price"] = Column.SalePrice, ["notes"] = Column.Notes,
    };

    private static readonly HashSet<string> Yes = ["yes", "y", "true", "1", "x", "✓", "✔"];

    public static bool IsYes(string text) => Yes.Contains(text.Trim().ToLowerInvariant());

    /// <summary>One kit row, read and checked. Optional fields are only set when the file has them.</summary>
    private sealed class Row
    {
        public required int Line;
        public string? Id;
        public required string Brand;
        public required string Name;
        public string? Scale;
        public string? Number;
        public string? Link;
        public decimal? Price;
        // Only in BenchBook's own files.
        public bool? Owned;
        public string? Status;
        public bool HasRating;
        public int? Rating;
        public bool? ForSale;
        public decimal? SalePrice;
        public bool HasNotes;
        public string? Notes;
    }

    public static KitImportSummary Import(LibraryStore store, string text)
    {
        var table = CsvParser.Parse(text);
        if (table.Count == 0) throw new KitImportException("The file has no rows to import.");

        var columns = new Dictionary<Column, int>();
        var header = table[0].Fields;
        for (var index = 0; index < header.Count; index++)
            if (Headers.TryGetValue(header[index].Trim().ToLowerInvariant(), out var column)) columns.TryAdd(column, index);
        var missing = new[] { (Column.Brand, "Brand"), (Column.Name, "Title") }.Where(c => !columns.ContainsKey(c.Item1)).Select(c => c.Item2).ToList();
        if (missing.Count > 0) throw new KitImportException($"The file's first row must name the columns. Missing: {PaintText.List(missing)}.");
        if (table.Count < 2) throw new KitImportException("The file has no rows to import.");

        var isBenchBook = columns.ContainsKey(Column.Id);
        var problems = new List<string>();
        var skipped = new Dictionary<string, (string Name, int Count)>(StringComparer.Ordinal);
        var rows = new List<Row>();

        foreach (var (line, fields) in table.Skip(1))
        {
            string? Value(Column column) => columns.TryGetValue(column, out var index) && index < fields.Count ? fields[index].Trim() : null;
            string? Filled(Column column) => Value(column) is { Length: > 0 } v ? v : null;

            if (Filled(Column.Type) is { } type && !string.Equals(type, KitType, StringComparison.OrdinalIgnoreCase))
            {
                var key = type.ToLowerInvariant();
                skipped[key] = skipped.TryGetValue(key, out var s) ? (s.Name, s.Count + 1) : (type, 1);
                continue;
            }
            if (Filled(Column.Brand) is not { } brand || Filled(Column.Name) is not { } name)
            {
                problems.Add($"Line {line}: a kit needs a brand and a name.");
                continue;
            }
            var row = new Row { Line = line, Brand = brand, Name = name, Scale = Filled(Column.Scale), Number = Filled(Column.Number), Id = Ids.Normalize(Value(Column.Id)) };

            if (Filled(Column.Link) is { } link)
            {
                if (WebAddress.Parse(link) is { } url) row.Link = url;
                else problems.Add($"Line {line}: “{link}” isn't a web address, so the link was left out.");
            }
            row.Price = Price(Value(Column.Price), line, zeroIsBlank: !isBenchBook, problems);
            row.SalePrice = Price(Value(Column.SalePrice), line, zeroIsBlank: false, problems);

            if (isBenchBook)
            {
                if (Value(Column.Owned) is { } owned) row.Owned = IsYes(owned);
                if (Value(Column.ForSale) is { } forSale) row.ForSale = IsYes(forSale);
                if (Value(Column.Notes) is { } notes) { row.HasNotes = true; row.Notes = notes.Length == 0 ? null : notes; }
                if (Filled(Column.Status) is { } status)
                {
                    // The file's words are English whatever the language.
                    if (KitStatus.All.Contains(status.ToLowerInvariant())) row.Status = status.ToLowerInvariant();
                    else problems.Add($"Line {line}: “{status}” isn't a status. Use BNIB, Opened, WIP or Built.");
                }
                if (Value(Column.Rating) is { } rating)
                {
                    if (rating.Length == 0) { row.HasRating = true; row.Rating = null; }
                    else if (int.TryParse(rating, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var stars) && stars is >= 1 and <= 5)
                    { row.HasRating = true; row.Rating = stars; }
                    else problems.Add($"Line {line}: “{rating}” isn't a rating from 1 to 5.");
                }
            }
            rows.Add(row);
        }

        var skippedList = skipped.Values
            .OrderByDescending(s => s.Count).ThenBy(s => s.Name, NaturalComparer.Instance)
            .Select(s => (s.Name, s.Count)).ToList();

        var kits = store.Kits().OrderBy(k => k.CreatedAt).ToList();
        var counts = new Counts();
        var save = new List<Kit>();
        if (isBenchBook) ImportBenchBook(rows, kits, counts, save);
        else ImportScalemates(rows, kits, counts, save);

        // One transaction for the whole file: it all imports, or nothing does.
        store.SaveKits(save.Distinct());
        return new KitImportSummary(counts.Added, counts.Updated, counts.Unchanged, skippedList, problems);
    }

    private sealed class Counts
    {
        public int Added, Updated, Unchanged;
    }

    /// <summary>
    /// Rows for the same kit are compared with the library's entries for it:
    /// owned entries count first, then entries that aren't owned become owned,
    /// then new entries are added for any rows left.
    /// </summary>
    private static void ImportScalemates(List<Row> rows, List<Kit> kits, Counts counts, List<Kit> save)
    {
        var groups = new List<List<Row>>();
        foreach (var row in rows)
        {
            var group = groups.FirstOrDefault(g => IsSame(row, g[0].Brand, g[0].Name, g[0].Number, g[0].Scale, g[0].Link));
            if (group is null) groups.Add([row]);
            else group.Add(row);
        }

        var claimed = new HashSet<string>(Ids.Comparer);
        var now = IsoDate.Now();
        foreach (var group in groups)
        {
            var first = group[0];
            var matches = kits.Where(k => !claimed.Contains(k.Id) && IsSame(first, k.Brand, k.Name, k.Number, k.Scale, k.Link)).ToList();
            claimed.UnionWith(matches.Select(k => k.Id));
            var changed = new HashSet<string>(Ids.Comparer);
            // The details on every entry for the kit follow the file.
            foreach (var kit in matches.Where(k => ApplyDetails(first, k))) changed.Add(kit.Id);

            var entries = matches.Where(k => k.Owned).ToList();
            foreach (var kit in matches.Where(k => !k.Owned).Take(Math.Max(group.Count - entries.Count, 0)))
            {
                kit.Owned = true;
                changed.Add(kit.Id);
                entries.Add(kit);
            }
            while (entries.Count < group.Count)
            {
                var kit = new Kit
                {
                    Id = Ids.New(), CreatedAt = now, UpdatedAt = now, Brand = first.Brand, Name = first.Name,
                    Scale = first.Scale, Number = first.Number, Link = first.Link, Owned = true, Status = KitStatus.Bnib, ForSale = false,
                };
                entries.Add(kit);
                save.Add(kit);
                counts.Added++;
            }

            // Prices only fill entries that have none, row by row.
            var existing = matches.Select(k => k.Id).ToHashSet(Ids.Comparer);
            foreach (var (row, kit) in group.Zip(entries))
            {
                if (kit.PricePaid is not null || row.Price is not { } price) continue;
                kit.PricePaid = price;
                if (existing.Contains(kit.Id)) changed.Add(kit.Id);
            }

            foreach (var kit in matches.Where(k => changed.Contains(k.Id)))
            {
                kit.UpdatedAt = now;
                save.Add(kit);
            }
            counts.Updated += changed.Count;
            // Rows whose entry was already there, just as the file has it.
            counts.Unchanged += entries.Take(group.Count).Count(k => existing.Contains(k.Id) && !changed.Contains(k.Id));
        }
    }

    /// <summary>Each row matches its entry by BenchBook ID and sets every field the file has. Rows without a known ID are added.</summary>
    private static void ImportBenchBook(List<Row> rows, List<Kit> kits, Counts counts, List<Kit> save)
    {
        var byId = new Dictionary<string, Kit>(Ids.Comparer);
        foreach (var kit in kits) byId.TryAdd(kit.Id, kit);
        var now = IsoDate.Now();
        foreach (var row in rows)
        {
            if (row.Id is { } id && byId.TryGetValue(id, out var kit))
            {
                if (ApplyAll(row, kit))
                {
                    kit.UpdatedAt = now;
                    save.Add(kit);
                    counts.Updated++;
                }
                else
                {
                    counts.Unchanged++;
                }
            }
            else
            {
                kit = new Kit
                {
                    Id = row.Id ?? Ids.New(), CreatedAt = now, UpdatedAt = now, Brand = row.Brand, Name = row.Name,
                    Owned = true, Status = KitStatus.Bnib, ForSale = false,
                };
                ApplyAll(row, kit);
                byId[kit.Id] = kit;
                save.Add(kit);
                counts.Added++;
            }
        }
    }

    /// <summary>Brand, name, scale, number and link from the file. Blank values never clear the entry's.</summary>
    private static bool ApplyDetails(Row row, Kit kit)
    {
        var changed = false;
        void Set(string? current, string? value, Action<string> apply)
        {
            if (value is null || current == value) return;
            apply(value);
            changed = true;
        }
        Set(kit.Brand, row.Brand, v => kit.Brand = v);
        Set(kit.Name, row.Name, v => kit.Name = v);
        Set(kit.Scale, row.Scale, v => kit.Scale = v);
        Set(kit.Number, row.Number, v => kit.Number = v);
        Set(kit.Link, row.Link, v => kit.Link = v);
        return changed;
    }

    private static bool ApplyAll(Row row, Kit kit)
    {
        var changed = ApplyDetails(row, kit);
        if (row.Owned is { } owned && kit.Owned != owned) { kit.Owned = owned; changed = true; }
        if (row.Status is { } status && kit.Status != status) { kit.Status = status; changed = true; }
        if (row.HasRating && kit.Rating != row.Rating) { kit.Rating = row.Rating; changed = true; }
        if (row.Price is { } price && kit.PricePaid != price) { kit.PricePaid = price; changed = true; }
        if (row.ForSale is { } forSale && kit.ForSale != forSale) { kit.ForSale = forSale; changed = true; }
        if (row.SalePrice is { } sale && kit.SalePrice != sale) { kit.SalePrice = sale; changed = true; }
        if (row.HasNotes && kit.Notes != row.Notes) { kit.Notes = row.Notes; changed = true; }
        return changed;
    }

    /// <summary>
    /// The same kit, as the editor decides it. Rows with neither a number nor a
    /// Scalemates address also match on brand, name and scale, so importing
    /// such a file twice doesn't add everything again.
    /// </summary>
    private static bool IsSame(Row row, string brand, string name, string? number, string? scale, string? link)
    {
        if (KitIdentity.IsSameKit(row.Brand, row.Number, row.Scale, row.Link, brand, number, scale, link)) return true;
        var neither = row.Number is null && number is null
            && KitIdentity.ScalematesKey(row.Link) is null && KitIdentity.ScalematesKey(link) is null;
        return neither
            && KitIdentity.Fold(row.Brand) == KitIdentity.Fold(brand)
            && KitIdentity.Fold(row.Name) == KitIdentity.Fold(name)
            && (KitIdentity.Fold(row.Scale) ?? "") == (KitIdentity.Fold(scale) ?? "");
    }

    /// <summary>Null for blank, and for 0 in a Scalemates file, which means not entered.</summary>
    private static decimal? Price(string? text, int line, bool zeroIsBlank, List<string> problems)
    {
        if (text is not { Length: > 0 }) return null;
        if (KitPrice.Parse(text) is not { } price)
        {
            problems.Add($"Line {line}: “{text}” isn't a price, so it was left out.");
            return null;
        }
        return zeroIsBlank && price == 0 ? null : price;
    }
}

/// <summary>Writes kits as a CSV file the importer reads back unchanged (behaviour.md, "Kits › Export").</summary>
public static class KitCsvExporter
{
    public static readonly IReadOnlyList<string> Header =
        ["BenchBook ID", "Brand", "Title", "Scale", "Number", "Type", "Price", "Link", "Owned", "Status", "Rating", "For Sale", "Sale Price"];

    /// <summary>Sorted by brand, number and scale, then the order they were added. CRLF line endings. Notes only when asked.</summary>
    public static string Csv(IEnumerable<Kit> kits, bool includeNotes = false)
    {
        var header = Header.ToList();
        if (includeNotes) header.Add("Notes");
        var lines = new List<IEnumerable<string>> { header };
        foreach (var kit in kits.OrderBy(k => k.Brand, NaturalComparer.Instance)
                     .ThenBy(k => k.Number ?? "", NaturalComparer.Instance)
                     .ThenBy(k => k.Scale ?? "", NaturalComparer.Instance)
                     .ThenBy(k => k.CreatedAt))
        {
            var fields = new List<string>
            {
                kit.Id, kit.Brand, kit.Name, kit.Scale ?? "", kit.Number ?? "", KitCsvImporter.KitType,
                kit.PricePaid is { } paid ? KitPrice.Plain(paid) : "", kit.Link ?? "",
                kit.Owned ? "Yes" : "", Status(kit.Status), KitText.Stars(kit)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                kit.ForSale ? "Yes" : "", kit.SalePrice is { } sale ? KitPrice.Plain(sale) : "",
            };
            if (includeNotes) fields.Add(kit.Notes ?? "");
            lines.Add(fields);
        }
        return string.Concat(lines.Select(l => string.Join(",", l.Select(Escape)) + "\r\n"));
    }

    /// <summary>e.g. "BenchBook Kits 2026-11-19.csv".</summary>
    public static string FileName(DateTime localDate) => $"BenchBook Kits {localDate:yyyy-MM-dd}.csv";

    /// <summary>"116 kits will be saved, one row per kit, …".</summary>
    public static string ExportMessage(int count) =>
        $"{KitText.Count(count)} will be saved, one row per kit, with details, status, rating, prices and links, in a CSV file BenchBook can import. Pictures aren't included; a backup has them.";

    /// <summary>BNIB, Opened, WIP or Built. An unknown stored status is written as it is.</summary>
    private static string Status(string raw) => KitStatus.All.Contains(raw) ? KitText.StatusName(raw) : raw;

    private static string Escape(string field)
    {
        var needsQuotes = field.Any(c => c is ',' or '"' or '\n' or '\r') || (field.Length > 0 && (char.IsWhiteSpace(field[0]) || char.IsWhiteSpace(field[^1])));
        return needsQuotes ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
