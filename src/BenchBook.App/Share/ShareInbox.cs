using BenchBook.App.Services;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using Microsoft.UI.Dispatching;

namespace BenchBook.App.Share;

/// <summary>
/// The running app's side of sharing: it publishes the projects for the share
/// window, and imports what the share window leaves in the inbox, at launch
/// and as it arrives (as CaptureCoordinator.swift does on the Mac).
/// </summary>
internal sealed class ShareInbox : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _queue;
    private readonly CaptureInbox _inbox;
    private FileSystemWatcher? _watcher;
    private bool _importing;
    private bool _again;
    private ShareDirectory? _published;

    public ShareInbox(AppServices services, DispatcherQueue queue)
    {
        _services = services;
        _queue = queue;
        _inbox = new CaptureInbox(services.Location.InboxFolder);
    }

    /// <summary>Runs on the UI thread with what to tell the user after an import, if anything.</summary>
    public event EventHandler<CaptureReport>? Reported;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(_inbox.Folder);
            _inbox.RemoveStaleStagingFolders(DateTimeOffset.UtcNow);
            _watcher = new FileSystemWatcher(_inbox.Folder) { NotifyFilter = NotifyFilters.DirectoryName, IncludeSubdirectories = false };
            _watcher.Created += (_, _) => _queue.TryEnqueue(Import);
            _watcher.Renamed += (_, _) => _queue.TryEnqueue(Import);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            App.LogError(error);
        }
        PublishProjects();
        _services.LibraryChanged += (_, _) => _queue.TryEnqueue(() => PublishProjects());
        Import();
    }

    /// <summary>
    /// Writes the projects for the share window, with the given one as the one to
    /// choose first. Only writes when something changed.
    /// </summary>
    public void PublishProjects(string? activeProjectId = null)
    {
        try
        {
            var projects = _services.WithStore(s => s.Projects());
            var kits = Words.KitsByProject(_services);
            projects.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
            var directory = new ShareDirectory
            {
                Projects = projects.Select(p => new ShareProject(p.Id, p.Name, Words.ProjectSummary(p, kits[p.Id].ToList()))).ToList(),
                // The share window records the project last shared to in the same file.
                ActiveProjectId = activeProjectId ?? ShareDirectory.Load(_services.Location.ShareProjectsPath).ActiveProjectId,
            };
            if (_published is not null && directory.Matches(_published)) return;
            directory.Save(_services.Location.ShareProjectsPath);
            _published = directory;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            App.LogError(error);
        }
    }

    /// <summary>Imports every waiting envelope. Safe to call repeatedly; calls during an import run once more after it.</summary>
    public async void Import()
    {
        if (_importing)
        {
            _again = true;
            return;
        }
        _importing = true;
        var outcomes = new List<CaptureOutcome>();
        try
        {
            do
            {
                _again = false;
                outcomes.AddRange(await Task.Run(ImportWaiting));
            } while (_again);
        }
        finally
        {
            _importing = false;
        }
        if (outcomes.Count == 0) return;

        _services.FetchLinkDetails(outcomes.OfType<CaptureOutcome.Added>()
            .Where(a => a.Reference.Kind is ReferenceKind.VideoLink or ReferenceKind.Webpage).Select(a => a.Reference.Id));
        _services.NotifyLibraryChanged();
        if (ReferenceImporter.Report(outcomes) is { } report) Reported?.Invoke(this, report);
    }

    private List<CaptureOutcome> ImportWaiting()
    {
        // Only one BenchBook imports at a time, if more than one is open.
        FileStream? lockFile = null;
        try
        {
            lockFile = new FileStream(Path.Combine(_inbox.Folder, ".import.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return _inbox.ImportPending(_services.Importer).Select(r => r.Outcome).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Another BenchBook is importing, or the inbox can't be reached; the next change tries again.
            return [];
        }
        finally
        {
            lockFile?.Dispose();
        }
    }

    public void Dispose() => _watcher?.Dispose();
}
