using System.Text;
using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Paints;

/// <summary>
/// Reads comma-separated text: quoted fields, "" inside quotes, commas and line
/// breaks inside quotes, and CRLF, LF or CR line endings. Blank lines are skipped.
/// </summary>
public static class CsvParser
{
    /// <summary>Rows of fields, with the line each row starts on (counting from 1).</summary>
    public static List<(int Line, List<string> Fields)> Parse(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length > 0 && text[0] == (char)0xFEFF) text = text[1..];
        var rows = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var rowStart = 1;

        void EndRow()
        {
            fields.Add(field.ToString());
            if (!(fields.Count == 1 && fields[0].Length == 0)) rows.Add((rowStart, fields));
            fields = [];
            field.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0: inQuotes = true; break;
                case ',': fields.Add(field.ToString()); field.Clear(); break;
                case '\n': EndRow(); line++; rowStart = line; break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || fields.Count > 0) EndRow();
        return rows;
    }
}

/// <summary>What an import did, for the summary.</summary>
/// <param name="Updated">Catalogue paints the file changed.</param>
/// <param name="UpToDate">Catalogue paints the file matched without changing anything.</param>
/// <param name="Repeated">Later rows for a paint already earlier in the file.</param>
public sealed record PaintImportSummary(int Added, int Updated, int UpToDate, int Repeated, IReadOnlyList<string> Problems);

public sealed class PaintImportException(string message) : Exception(message);

/// <summary>
/// Imports paints from a CSV file with a header row (behaviour.md, "Paint CSV",
/// and PaintCSVImporter.swift). A row for a paint already in the catalogue
/// updates it, but only from cells with something in them, so a sparse file
/// can't wipe anything. All of it imports, or none.
/// </summary>
public static class PaintCsvImporter
{
    private enum Column { Brand, Range, Code, Name, Swatch, Owned, Notes, StandardCode, Match, Type }

    private static readonly Dictionary<string, Column> Headers = new(StringComparer.Ordinal)
    {
        ["brand"] = Column.Brand, ["manufacturer"] = Column.Brand, ["maker"] = Column.Brand,
        ["range"] = Column.Range, ["line"] = Column.Range, ["series"] = Column.Range,
        ["code"] = Column.Code, ["number"] = Column.Code, ["paint code"] = Column.Code, ["ref"] = Column.Code,
        ["name"] = Column.Name, ["colour name"] = Column.Name, ["color name"] = Column.Name, ["paint name"] = Column.Name,
        ["colour"] = Column.Swatch, ["color"] = Column.Swatch, ["hex"] = Column.Swatch, ["swatch"] = Column.Swatch, ["rgb"] = Column.Swatch,
        ["owned"] = Column.Owned, ["own"] = Column.Owned, ["have"] = Column.Owned, ["in stock"] = Column.Owned,
        ["notes"] = Column.Notes, ["note"] = Column.Notes, ["comments"] = Column.Notes,
        ["standard code"] = Column.StandardCode,
        ["match"] = Column.Match, ["standard match"] = Column.Match,
        ["type"] = Column.Type, ["paint type"] = Column.Type,
    };

    /// <summary>Standard columns read as text. A named system's bare numbers get its prefix: "36270" in an FS column becomes "FS 36270".</summary>
    private static readonly Dictionary<string, string?> StandardHeaders = new(StringComparer.Ordinal)
    {
        ["fs/rlm"] = null, ["fs / rlm"] = null, ["standard"] = null, ["colour standard"] = null,
        ["color standard"] = null, ["standards"] = null, ["equivalent"] = null,
        ["fs"] = "FS", ["fs code"] = "FS", ["federal standard"] = "FS", ["fs595"] = "FS", ["fs 595"] = "FS",
        ["rlm"] = "RLM", ["rlm code"] = "RLM", ["bs"] = "BS", ["bs381c"] = "BS", ["ral"] = "RAL", ["ana"] = "ANA",
    };

    private static readonly HashSet<string> Yes = ["yes", "y", "true", "1", "x", "✓", "✔"];
    private static readonly HashSet<string> No = ["no", "n", "false", "0"];

    public static PaintImportSummary Import(LibraryStore store, string text)
    {
        var rows = CsvParser.Parse(text);
        if (rows.Count == 0) throw new PaintImportException("The file has no rows to import.");

        var keys = rows[0].Fields.Select(f => f.Trim().ToLowerInvariant()).ToList();
        // With a Standard Code column, Standard names the system.
        int? systemIndex = keys.Contains("standard code") && keys.IndexOf("standard") is >= 0 and var i ? i : null;
        var columns = new Dictionary<Column, int>();
        var standards = new List<(int Index, string? Prefix)>();
        for (var index = 0; index < keys.Count; index++)
        {
            if (index == systemIndex) continue;
            if (StandardHeaders.TryGetValue(keys[index], out var prefix)) standards.Add((index, prefix));
            else if (Headers.TryGetValue(keys[index], out var column)) columns.TryAdd(column, index);
        }
        var missing = new[] { (Column.Brand, "Brand"), (Column.Code, "Code") }.Where(c => !columns.ContainsKey(c.Item1)).Select(c => c.Item2).ToList();
        if (missing.Count > 0) throw new PaintImportException($"The file's first row must name the columns. Missing: {PaintText.List(missing)}.");
        if (rows.Count < 2) throw new PaintImportException("The file has no rows to import.");

        int added = 0, updated = 0, upToDate = 0, repeated = 0;
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // One transaction for the whole file: it all imports, or nothing does.
        store.RunInTransaction(transaction =>
        {
            foreach (var (line, fields) in rows.Skip(1))
            {
                string Cell(int index) => index < fields.Count ? fields[index].Trim() : "";
                string Value(Column column) => columns.TryGetValue(column, out var index) ? Cell(index) : "";

                var identity = new PaintDraft { Brand = Value(Column.Brand), Range = Value(Column.Range), Code = Value(Column.Code) };
                if (identity.MissingRequiredField)
                {
                    problems.Add($"Line {line}: a paint needs a brand and a code.");
                    continue;
                }
                var key = identity.IdentityKey;
                if (!seen.Add(key))
                {
                    repeated++;
                    continue;
                }
                // Mixes have their own keys, so they're never matched.
                var existing = store.PaintWithKey(key, transaction);
                var before = existing is null ? null : PaintDraft.From(existing, store.IsOwned(existing.Id, transaction));
                // Blank cells keep what's there: the paint's values, or a new paint's empty ones.
                var draft = before ?? identity;
                if (Value(Column.Name) is { Length: > 0 } name) draft = draft with { Name = name };
                if (Value(Column.Notes) is { Length: > 0 } notes) draft = draft with { Notes = notes };

                var systemText = systemIndex is { } si ? Cell(si) : "";
                var standardText = string.Join(" / ", standards
                    .Select(s => (s.Prefix, Text: Cell(s.Index)))
                    .Where(s => s.Text.Length > 0)
                    .Select(s => s.Prefix is not null && char.IsDigit(s.Text[0]) ? $"{s.Prefix} {s.Text}" : s.Text));
                var standard = systemText.Length > 0 ? StandardFromColumns(systemText, Value(Column.StandardCode)) : PaintStandardParser.Parse(standardText);
                if (standard is not null) draft = draft with { StandardSystem = standard.SystemRaw, StandardCode = standard.Code };

                if (Value(Column.Match) is { Length: > 0 } matchText && draft.StandardSystem.Length > 0)
                {
                    if (PaintMatch.FromText(matchText) is { } match) draft = draft with { Match = match.Raw };
                    else problems.Add($"Line {line}: “{matchText}” isn't No match, Closest match or Exact match, so no match was set.");
                }
                if (Value(Column.Type) is { Length: > 0 } typeText)
                {
                    if (PaintType.FromText(typeText) is { } type) draft = draft with { Type = type.Raw };
                    else problems.Add($"Line {line}: “{typeText}” isn't a paint type BenchBook knows, so no type was set.");
                }
                if (Value(Column.Owned) is { Length: > 0 } ownedText)
                {
                    var owned = ownedText.ToLowerInvariant();
                    if (Yes.Contains(owned)) draft = draft with { IsOwned = true };
                    else if (No.Contains(owned)) draft = draft with { IsOwned = false };
                    else problems.Add($"Line {line}: “{ownedText}” isn't yes or no, so ownership wasn't changed.");
                }
                if (Value(Column.Swatch) is { Length: > 0 } swatch)
                {
                    if (Swatch.Normalize(swatch) is { } hex) draft = draft with { SwatchHex = hex };
                    else problems.Add($"Line {line}: “{swatch}” isn't a colour like #4A5A6B, so no swatch was set.");
                }

                if (existing is not null)
                {
                    if (draft == before)
                    {
                        upToDate++;
                        continue;
                    }
                    PaintService.Apply(draft, existing);
                    existing.UpdatedAt = IsoDate.Now();
                    store.UpdatePaint(existing, draft.IsOwned, transaction);
                    updated++;
                    continue;
                }
                var paint = PaintService.New();
                PaintService.Apply(draft, paint);
                store.InsertPaint(paint, draft.IsOwned, transaction);
                added++;
            }
        });
        return new PaintImportSummary(added, updated, upToDate, repeated, problems);
    }

    /// <summary>
    /// A Standard column naming the system, e.g. "FS 595", "fs595" or "FS", with
    /// its code. A name BenchBook doesn't know becomes Custom, with the name kept
    /// in front of the code: "Humbrol 28".
    /// </summary>
    internal static PaintStandard StandardFromColumns(string name, string code)
    {
        code = code.Trim();
        if (PaintStandardSystem.FromName(name) is { } system) return new PaintStandard(system, code);
        if (PaintStandardParser.Parse(name) is { Code.Length: 0 } parsed
            && parsed.System != PaintStandardSystem.Custom && parsed.System != PaintStandardSystem.Multiple)
            return new PaintStandard(parsed.SystemRaw, code);
        return new PaintStandard(PaintStandardSystem.Custom, string.Join(" ", new[] { name.Trim(), code }.Where(t => t.Length > 0)));
    }
}

/// <summary>Writes paints as CSV that the importer reads back (behaviour.md, "Paint CSV", "Export").</summary>
public static class PaintCsvExporter
{
    /// <summary>
    /// Sorted by brand, range, then code. CRLF line endings. Mixes are left out.
    /// Owned and Notes only when asked: they're personal. The standard, match and
    /// type are written by their English names; ones this version doesn't know are empty.
    /// </summary>
    public static string Csv(IEnumerable<Paint> paints, ISet<string> owned, bool includeOwnership = false, bool includeNotes = false)
    {
        var header = new List<string> { "Brand", "Range", "Code", "Name", "Standard", "Standard Code", "Match", "Type", "Colour" };
        if (includeOwnership) header.Add("Owned");
        if (includeNotes) header.Add("Notes");
        var lines = new List<IEnumerable<string>> { header };
        foreach (var paint in paints.Where(p => !p.IsMix)
                     .OrderBy(p => p.Brand, NaturalComparer.Instance).ThenBy(p => p.Range, NaturalComparer.Instance).ThenBy(p => p.Code, NaturalComparer.Instance))
        {
            var system = PaintStandardSystem.FromRaw(paint.StandardSystem);
            var fields = new List<string>
            {
                paint.Brand, paint.Range, paint.Code, paint.Name,
                system?.MenuName ?? "", system is null ? "" : paint.StandardCode,
                system is null ? "" : PaintMatch.FromRaw(paint.Match)?.CsvName ?? "",
                PaintType.FromRaw(paint.Type)?.CsvName ?? "",
                paint.SwatchHex ?? "",
            };
            if (includeOwnership) fields.Add(owned.Contains(paint.Id) ? "yes" : "");
            if (includeNotes) fields.Add(paint.Notes ?? "");
            lines.Add(fields);
        }
        return string.Concat(lines.Select(l => string.Join(",", l.Select(Escape)) + "\r\n"));
    }

    /// <summary>e.g. "BenchBook Paints 2026-11-13.csv".</summary>
    public static string FileName(DateTime localDate) => $"BenchBook Paints {localDate:yyyy-MM-dd}.csv";

    /// <summary>Quoted when it holds a comma, quote or line break, or starts or ends with a space.</summary>
    private static string Escape(string field)
    {
        var needsQuotes = field.Any(c => c is ',' or '"' or '\n' or '\r') || (field.Length > 0 && (char.IsWhiteSpace(field[0]) || char.IsWhiteSpace(field[^1])));
        return needsQuotes ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
