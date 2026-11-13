using System.Text.Json;
using System.Text.Json.Serialization;
using BenchBook.Core.Model;

namespace BenchBook.Core.Settings;

/// <summary>
/// The user's settings (behaviour.md, "Settings"), kept in Settings.json beside
/// the store. They aren't part of the library, so a restore keeps them.
/// </summary>
public sealed class AppSettings
{
    public bool FetchLinkDetails { get; set; } = true;
    public bool OpenImagesAtActualSize { get; set; }
    public bool ShowNoteBoxes { get; set; } = true;
    public string NewProjectStatus { get; set; } = ProjectStatus.Researching;
    /// <summary>The sidebar's status filter; null shows every project.</summary>
    public string? ProjectStatusFilter { get; set; }
    public DateTimeOffset? LastBackup { get; set; }
    /// <summary>Each viewer's last size and position: x, y, width, height.</summary>
    public Dictionary<string, int[]> WindowFrames { get; set; } = [];
    /// <summary>The last colour chosen for each kind of PDF markup.</summary>
    public Dictionary<string, string> MarkupColors { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>The saved settings, or the defaults if there are none or they can't be read.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(path), Options) ?? new AppSettings();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    /// <summary>Writes to a partial file, then renames it into place.</summary>
    public void Save(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var partial = Path.Combine(folder, $"{LibraryPaths.PartialPrefix}{Ids.New()}");
        File.WriteAllBytes(partial, JsonSerializer.SerializeToUtf8Bytes(this, Options));
        File.Move(partial, path, overwrite: true);
    }
}
