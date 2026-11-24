using System.Globalization;
using BenchBook.Core.Backup;
using BenchBook.Core.Model;

namespace BenchBook.App.Services;

/// <summary>Wording shared across screens. British English, singular for one.</summary>
internal static class Words
{
    /// <summary>e.g. "1 project", "1,267 paints".</summary>
    public static string Count(long count, string singular, string? plural = null) =>
        $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>e.g. "13 November 2026 at 09:41".</summary>
    public static string DateAndTime(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        return $"{local.ToString("d MMMM yyyy", culture)} at {local.ToString("t", culture)}";
    }

    /// <summary>e.g. "12 projects, 340 references and 1.20 GB of files".</summary>
    public static string BackupSummary(BackupManifest manifest) =>
        $"{Count(manifest.Counts.Projects, "project")}, {Count(manifest.Counts.References, "reference")} and {Shell.FormatBytes(manifest.TotalSize)} of files";

    public static string Platform(string platform) => platform switch
    {
        "mac" => "Mac",
        "windows" => "Windows",
        _ => platform,
    };

    public static string StatusName(string? status) => status switch
    {
        ProjectStatus.InProgress => "In Progress",
        ProjectStatus.Paused => "Paused",
        ProjectStatus.Completed => "Completed",
        null or ProjectStatus.Researching => "Researching",
        _ => status,
    };

    /// <summary>Segoe Fluent Icons glyphs for each status.</summary>
    public static string StatusGlyph(string? status) => status switch
    {
        ProjectStatus.InProgress => "\uE90F", // Repair
        ProjectStatus.Paused => "\uE769",     // Pause
        ProjectStatus.Completed => "\uE73E",  // CheckMark
        _ => "\uE721",                        // Search
    };

    /// <summary>"Italeri · 1:72 · AH-1W" or "2 kits · Normandy", leaving out empty parts.</summary>
    public static string ProjectSummary(Project project, IReadOnlyList<Kit> kits) =>
        BenchBook.Core.Projects.ProjectService.Summary(project, kits);

    /// <summary>Each project's kits, for <see cref="ProjectSummary"/>.</summary>
    public static ILookup<string, Kit> KitsByProject(AppServices services) =>
        services.WithStore(s => s.Kits()).Where(k => k.ProjectId is not null).ToLookup(k => k.ProjectId!, Ids.Comparer);
}
