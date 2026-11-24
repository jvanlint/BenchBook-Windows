using System.Globalization;
using System.Text.Json.Nodes;

namespace BenchBook.Core.Model;

/// <summary>
/// The changes between library schema versions that move data, so an older
/// library or backup reads as the current one (library-format.md,
/// "Compatibility"; behaviour.md, "Upgrading from schema 3" and "from schema 4").
/// The other versions only add records or fields, which read as empty.
/// </summary>
public static class SchemaUpgrades
{
    /// <summary>
    /// Brings an older library.json up to <see cref="LibrarySnapshot.CurrentSchemaVersion"/>,
    /// in place. A current or newer one is left as it is.
    /// </summary>
    public static void Upgrade(JsonObject library)
    {
        if (!TryParse(library["schemaVersion"]?.GetValue<string>(), out var version)) return;
        if (version < new Version(4, 0, 0))
        {
            foreach (var project in Objects(library, "projects"))
            {
                var line = KitDetailsNote.Line(Text(project, "manufacturer"), Text(project, "kitNumber"), Text(project, "scale"));
                Set(project, "notes", KitDetailsNote.Notes(Text(project, "notes"), line));
                project.Remove("manufacturer");
                project.Remove("scale");
                project.Remove("kitNumber");
            }
        }
        if (version < new Version(5, 0, 0))
        {
            foreach (var paint in Objects(library, "paints"))
            {
                if (Text(paint, "standardSystem").Length > 0) continue;
                var standard = PaintStandardParser.Parse(Text(paint, "standardCode"));
                paint["standardSystem"] = standard?.SystemRaw ?? "";
                paint["standardCode"] = standard?.Code ?? "";
            }
            foreach (var row in Objects(library, "projectPaints"))
            {
                if (Text(row, "standardSystem").Length > 0) continue;
                var standard = PaintStandardParser.Parse(Text(row, "standard"));
                row["standardSystem"] = standard?.SystemRaw ?? "";
                Set(row, "standard", standard is { Code.Length: > 0 } ? standard.Code : null);
            }
        }
        if (TryParse(LibrarySnapshot.CurrentSchemaVersion, out var current) && version < current)
            library["schemaVersion"] = LibrarySnapshot.CurrentSchemaVersion;
    }

    /// <summary>"a.b.c" as a version; false for anything else.</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null) return false;
        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    private static IEnumerable<JsonObject> Objects(JsonObject library, string name) =>
        library[name] is JsonArray array ? array.OfType<JsonObject>() : [];

    private static string Text(JsonObject record, string name) =>
        record[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static void Set(JsonObject record, string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) record.Remove(name);
        else record[name] = value;
    }
}

/// <summary>
/// The line the 4.0.0 upgrade adds to a project's notes, e.g. "Kit: Italeri ·
/// 1234 · 1:72". Not localized: it's stored, and both apps write the same text.
/// </summary>
public static class KitDetailsNote
{
    /// <summary>Null when manufacturer, number and scale are all empty.</summary>
    public static string? Line(string? manufacturer, string? number, string? scale)
    {
        var parts = new[] { manufacturer, number, scale }.Select(p => (p ?? "").Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? null : "Kit: " + string.Join(" · ", parts);
    }

    /// <summary>The notes with the line on its own at the end, after a blank line, unless it's already the last line.</summary>
    public static string? Notes(string? notes, string? line)
    {
        if (line is null) return notes;
        var existing = (notes ?? "").Trim();
        if (existing.Length == 0) return line;
        var lines = existing.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        if (lines[^1] == line) return notes;
        return existing + "\n\n" + line;
    }
}
