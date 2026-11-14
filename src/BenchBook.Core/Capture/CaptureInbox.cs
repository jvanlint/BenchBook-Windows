using System.Text.Json;
using System.Text.Json.Serialization;
using BenchBook.Core.Model;

namespace BenchBook.Core.Capture;

/// <summary>
/// One item in an envelope. <see cref="Kind"/> is "file", "link" or "text". A
/// file's <see cref="Name"/> is a file inside the envelope's folder.
/// </summary>
public sealed record EnvelopeItem(
    string Kind,
    string? Name = null,
    string? OriginalFilename = null,
    string? ResourceUrl = null,
    string? Url = null,
    string? Title = null,
    string? Text = null,
    string? SourceUrl = null);

/// <summary>
/// A capture handed from the share window to the running app through the
/// inbox, as the Mac's Share extension does (Shared/CaptureEnvelope.swift).
/// Each envelope is a folder:
/// <code>
/// Inbox\&lt;id&gt;\envelope.json
/// Inbox\&lt;id&gt;\&lt;file&gt;...      files named in the items
/// </code>
/// The share window builds the folder under <c>Inbox\.staging\</c> and moves it
/// into place only when it's complete, so the app never sees half a capture.
/// </summary>
public sealed record CaptureEnvelope
{
    public const string FileName = "envelope.json";
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public required string Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string ProjectId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Note { get; init; }
    public IReadOnlyList<EnvelopeItem> Items { get; init; } = [];

    /// <summary>An envelope item for a captured item whose file, if any, is already in the envelope's folder.</summary>
    public static EnvelopeItem ItemFor(CapturedItem item) => item switch
    {
        CapturedItem.File file => new EnvelopeItem("file", Name: Path.GetFileName(file.Path), OriginalFilename: file.OriginalFilename, ResourceUrl: file.ResourceUrl),
        CapturedItem.Link link => new EnvelopeItem("link", Url: link.Url, Title: link.Title),
        CapturedItem.Text text => new EnvelopeItem("text", Text: text.Content, SourceUrl: text.SourceUrl, Title: text.Title),
        _ => throw new ArgumentException("Unknown item."),
    };

    /// <summary>
    /// The items, with files in <paramref name="folder"/>. Null for an item that
    /// can't be used, such as a file name that would leave the folder.
    /// </summary>
    public List<CapturedItem?> CapturedItems(string folder) => Items.Select(item => item.Kind switch
    {
        "file" when item.Name is { } name && LibraryPaths.IsSafeName(name) && !name.Contains('/')
            => new CapturedItem.File(Path.Combine(folder, name), item.OriginalFilename, item.ResourceUrl),
        "link" when item.Url is { } url => new CapturedItem.Link(url, item.Title),
        "text" when item.Text is { } text => new CapturedItem.Text(text, item.SourceUrl, item.Title),
        _ => (CapturedItem?)null,
    }).ToList();
}

/// <summary>Reads and writes envelopes in the inbox folder.</summary>
public sealed class CaptureInbox(string folder)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public string Folder { get; } = Path.GetFullPath(folder);
    private string StagingFolder => Path.Combine(Folder, ".staging");

    /// <summary>A fresh, empty folder to collect a capture's files in.</summary>
    public string MakeStagingFolder()
    {
        var staging = Path.Combine(StagingFolder, Guid.NewGuid().ToString("D").ToUpperInvariant());
        Directory.CreateDirectory(staging);
        return staging;
    }

    /// <summary>Writes the envelope into its staging folder, then moves the folder into the inbox.</summary>
    public void Commit(CaptureEnvelope envelope, string stagingFolder)
    {
        var path = Path.Combine(stagingFolder, CaptureEnvelope.FileName);
        File.WriteAllText(path + ".partial", JsonSerializer.Serialize(envelope, Json));
        File.Move(path + ".partial", path, overwrite: true);
        Directory.Move(stagingFolder, Path.Combine(Folder, envelope.Id));
    }

    /// <summary>Removes a staging folder that won't be sent.</summary>
    public static void Discard(string stagingFolder)
    {
        try { if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Envelope folders that have been committed, oldest first.</summary>
    public List<string> PendingFolders()
    {
        if (!Directory.Exists(Folder)) return [];
        return new DirectoryInfo(Folder).EnumerateDirectories()
            .Where(d => Ids.IsValid(d.Name))
            .OrderBy(d => d.CreationTimeUtc)
            .Select(d => d.FullName)
            .ToList();
    }

    /// <summary>The envelope in a folder, or null if it can't be read yet.</summary>
    public static CaptureEnvelope? Read(string envelopeFolder)
    {
        try
        {
            return JsonSerializer.Deserialize<CaptureEnvelope>(File.ReadAllText(Path.Combine(envelopeFolder, CaptureEnvelope.FileName)), Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Imports every complete envelope, then removes its folder. One that can't be
    /// read, or is from a newer version of BenchBook, is left for later.
    /// </summary>
    /// <returns>What happened to each item, by project.</returns>
    public List<(string ProjectId, CaptureOutcome Outcome)> ImportPending(ReferenceImporter importer)
    {
        var results = new List<(string, CaptureOutcome)>();
        foreach (var envelopeFolder in PendingFolders())
        {
            if (Read(envelopeFolder) is not { } envelope || envelope.Version > CaptureEnvelope.CurrentVersion) continue;
            var items = envelope.CapturedItems(envelopeFolder).OfType<CapturedItem>().ToList();
            var projectId = Ids.Normalize(envelope.ProjectId) ?? envelope.ProjectId;
            foreach (var outcome in importer.Import(items, projectId, TagNames.Parse(string.Join(",", envelope.Tags)), Words(envelope.Note)))
                results.Add((projectId, outcome));
            // Every item is now a saved reference, a duplicate or a reported failure.
            Discard(envelopeFolder);
        }
        return results;
    }

    /// <summary>Removes staging folders left for more than a day, e.g. by a share window that was closed mid-way.</summary>
    public void RemoveStaleStagingFolders(DateTimeOffset now)
    {
        if (!Directory.Exists(StagingFolder)) return;
        foreach (var staging in new DirectoryInfo(StagingFolder).EnumerateDirectories())
            if (now - staging.CreationTimeUtc > TimeSpan.FromDays(1)) Discard(staging.FullName);
    }

    private static string? Words(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
}

/// <summary>A project as the share window lists it.</summary>
public sealed record ShareProject(string Id, string Name, string Detail);

/// <summary>
/// The projects the app publishes for the share window, so the share window
/// never opens the store. The active project is the one last open in BenchBook
/// or last shared to, and is chosen first in the share window.
/// </summary>
public sealed record ShareDirectory
{
    public IReadOnlyList<ShareProject> Projects { get; init; } = [];
    public string? ActiveProjectId { get; init; }

    public static ShareDirectory Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ShareDirectory>(File.ReadAllText(path), CaptureInbox.Json) ?? new() : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>Saves by replacing the file, so a reader never sees half of it.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        File.WriteAllText(partial, JsonSerializer.Serialize(this, CaptureInbox.Json));
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>The project to choose first: the active one if it still exists, else the first.</summary>
    public string? InitialProjectId =>
        Projects.FirstOrDefault(p => Ids.Comparer.Equals(p.Id, ActiveProjectId))?.Id ?? Projects.FirstOrDefault()?.Id;

    public bool Matches(ShareDirectory other) =>
        Projects.SequenceEqual(other.Projects) && Ids.Comparer.Equals(ActiveProjectId ?? "", other.ActiveProjectId ?? "");
}
