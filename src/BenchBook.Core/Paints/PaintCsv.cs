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
public sealed record PaintImportSummary(int Added, int Duplicates, IReadOnlyList<string> Problems);

public sealed class PaintImportException(string message) : Exception(message);

/// <summary>Imports paints from a CSV file with a header row (behaviour.md, "Paint CSV"). All of it, or none.</summary>
public static class PaintCsvImporter
{
    private enum Column { Brand, Range, Code, Name, Swatch, Owned, Notes }

    private static readonly Dictionary<string, Column> Headers = new(StringComparer.Ordinal)
    {
        ["brand"] = Column.Brand, ["manufacturer"] = Column.Brand, ["maker"] = Column.Brand,
        ["range"] = Column.Range, ["line"] = Column.Range, ["series"] = Column.Range,
        ["code"] = Column.Code, ["number"] = Column.Code, ["paint code"] = Column.Code, ["ref"] = Column.Code,
        ["name"] = Column.Name, ["colour name"] = Column.Name, ["color name"] = Column.Name, ["paint name"] = Column.Name,
        ["colour"] = Column.Swatch, ["color"] = Column.Swatch, ["hex"] = Column.Swatch, ["swatch"] = Column.Swatch, ["rgb"] = Column.Swatch,
        ["owned"] = Column.Owned, ["own"] = Column.Owned, ["have"] = Column.Owned, ["in stock"] = Column.Owned,
        ["notes"] = Column.Notes, ["note"] = Column.Notes, ["comments"] = Column.Notes,
    };

    /// <summary>Standard columns. A named system's bare numbers get its prefix: "36270" in an FS column becomes "FS 36270".</summary>
    private static readonly Dictionary<string, string?> StandardHeaders = new(StringComparer.Ordinal)
    {
        ["fs/rlm"] = null, ["fs / rlm"] = null, ["standard"] = null, ["colour standard"] = null,
        ["color standard"] = null, ["standards"] = null, ["equivalent"] = null,
        ["fs"] = "FS", ["fs code"] = "FS", ["federal standard"] = "FS", ["fs595"] = "FS", ["fs 595"] = "FS",
        ["rlm"] = "RLM", ["rlm code"] = "RLM", ["bs"] = "BS", ["bs381c"] = "BS", ["ral"] = "RAL", ["ana"] = "ANA",
    };

    private static readonly HashSet<string> Yes = ["yes", "y", "true", "1", "x", "✓", "✔"];

    public static PaintImportSummary Import(LibraryStore store, string text)
    {
        var rows = CsvParser.Parse(text);
        if (rows.Count == 0) throw new PaintImportException("The file has no rows to import.");

        var columns = new Dictionary<Column, int>();
        var standards = new List<(int Index, string? Prefix)>();
        for (var i = 0; i < rows[0].Fields.Count; i++)
        {
            var key = rows[0].Fields[i].Trim().ToLowerInvariant();
            if (StandardHeaders.TryGetValue(key, out var prefix)) standards.Add((i, prefix));
            else if (Headers.TryGetValue(key, out var column)) columns.TryAdd(column, i);
        }
        var missing = new[] { (Column.Brand, "Brand"), (Column.Code, "Code") }.Where(c => !columns.ContainsKey(c.Item1)).Select(c => c.Item2).ToList();
        if (missing.Count > 0) throw new PaintImportException($"The file's first row must name the columns. Missing: {PaintText.List(missing)}.");
        if (rows.Count < 2) throw new PaintImportException("The file has no rows to import.");

        var added = 0;
        var duplicates = 0;
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // One transaction for the whole file: it all imports, or nothing does.
        store.RunInTransaction(transaction =>
        {
            foreach (var (line, fields) in rows.Skip(1))
            {
                string Value(Column column) => columns.TryGetValue(column, out var i) && i < fields.Count ? fields[i].Trim() : "";
                var standard = string.Join(" / ", standards
                    .Where(s => s.Index < fields.Count && fields[s.Index].Trim().Length > 0)
                    .Select(s =>
                    {
                        var value = fields[s.Index].Trim();
                        return s.Prefix is not null && char.IsDigit(value[0]) ? $"{s.Prefix} {value}" : value;
                    }));
                var draft = new PaintDraft
                {
                    Brand = Value(Column.Brand), Range = Value(Column.Range), Code = Value(Column.Code), Name = Value(Column.Name),
                    Notes = Value(Column.Notes), StandardCode = standard, IsOwned = Yes.Contains(Value(Column.Owned).ToLowerInvariant()),
                };
                var swatch = Value(Column.Swatch);
                if (swatch.Length > 0)
                {
                    if (Swatch.Normalize(swatch) is { } hex) draft = draft with { SwatchHex = hex };
                    else problems.Add($"Line {line}: “{swatch}” isn't a colour like #4A5A6B, so no swatch was set.");
                }
                if (draft.MissingRequiredField)
                {
                    problems.Add($"Line {line}: a paint needs a brand and a code.");
                    continue;
                }
                var key = draft.IdentityKey;
                if (!seen.Add(key) || store.PaintWithKey(key, transaction) is not null)
                {
                    duplicates++;
                    continue;
                }
                var now = IsoDate.Now();
                var paint = new Paint { Id = Ids.New(), CreatedAt = now, UpdatedAt = now, Brand = "", Range = "", Code = "", Name = "", StandardCode = "", IdentityKey = "" };
                PaintService.Apply(draft, paint);
                store.InsertPaint(paint, draft.IsOwned, transaction);
                added++;
            }
        });
        return new PaintImportSummary(added, duplicates, problems);
    }
}

/// <summary>Writes paints as CSV that the importer reads back (behaviour.md, "Export").</summary>
public static class PaintCsvExporter
{
    /// <summary>Sorted by brand, range, then code. CRLF line endings. Owned and Notes only when asked: they're personal.</summary>
    public static string Csv(IEnumerable<Paint> paints, ISet<string> owned, bool includeOwnership = false, bool includeNotes = false)
    {
        var header = new List<string> { "Brand", "Range", "Code", "Name", "FS/RLM", "Colour" };
        if (includeOwnership) header.Add("Owned");
        if (includeNotes) header.Add("Notes");
        var lines = new List<IEnumerable<string>> { header };
        foreach (var paint in paints.OrderBy(p => p.Brand, NaturalComparer.Instance).ThenBy(p => p.Range, NaturalComparer.Instance).ThenBy(p => p.Code, NaturalComparer.Instance))
        {
            var fields = new List<string> { paint.Brand, paint.Range, paint.Code, paint.Name, paint.StandardCode, paint.SwatchHex ?? "" };
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
