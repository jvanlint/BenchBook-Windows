using BenchBook.Core;
using BenchBook.Core.Capture;
using BenchBook.Core.Links;
using Microsoft.UI.Dispatching;
using BenchBook.Core.Settings;
using BenchBook.Core.Store;

namespace BenchBook.App.Services;

/// <summary>What the running app shares: where the library is, its store and the settings.</summary>
public sealed class AppServices : IDisposable, IStoreAccess
{
    private readonly object _storeLock = new();
    private LibraryStore? _store;

    public AppServices(LibraryLocation location, LibraryStore store)
    {
        Location = location;
        _store = store;
        Settings = AppSettings.Load(location.SettingsPath);
        Thumbnails = new Thumbnails(location);
        Importer = new ReferenceImporter(this, location, Services.Thumbnails.PdfHasPages);
        Links = new LinkDetails(this, location, new LinkFetcher(), new WicImages());
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    private readonly DispatcherQueue _dispatcher;
    private readonly object _linkQueueLock = new();
    private Task _linkQueue = Task.CompletedTask;

    public LinkDetails Links { get; }

    /// <summary>Raised on the UI thread after a link's details or poster were saved.</summary>
    public event EventHandler<string>? LinkDetailsSaved;

    /// <summary>
    /// Fetches link details in the background, one at a time. Unless
    /// <paramref name="evenIfOff"/>, nothing is fetched when the setting is off.
    /// </summary>
    public void FetchLinkDetails(IEnumerable<string> referenceIds, bool evenIfOff = false)
    {
        if (!evenIfOff && !Settings.FetchLinkDetails) return;
        var ids = referenceIds.ToList();
        if (ids.Count == 0) return;
        lock (_linkQueueLock)
        {
            _linkQueue = _linkQueue.ContinueWith(async _ =>
            {
                foreach (var id in ids)
                {
                    bool saved;
                    try { saved = await Links.Refresh(id); }
                    catch (Exception) { saved = false; } // The library may have closed for a restore.
                    if (saved) _dispatcher.TryEnqueue(() =>
                    {
                        LinkDetailsSaved?.Invoke(this, id);
                        NotifyLibraryChanged();
                    });
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    public Thumbnails Thumbnails { get; }
    public ReferenceImporter Importer { get; }

    /// <summary>Raised on the thread that changed the library, after references are added, changed or deleted.</summary>
    public event EventHandler? LibraryChanged;

    public void NotifyLibraryChanged() => LibraryChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Whether the main window is the active window. All BenchBook's windows share
    /// one UI thread, and moving the focus inside a window activates it, so the main
    /// window doesn't rebuild itself while a viewer is in front: it catches up when
    /// it's activated again.
    /// </summary>
    public bool MainWindowActive { get; private set; } = true;

    /// <summary>Raised when the main window becomes the active window again.</summary>
    public event EventHandler? MainWindowActivated;

    public void SetMainWindowActive(bool active)
    {
        MainWindowActive = active;
        if (active) MainWindowActivated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The drag format for references dragged from the gallery. Private to
    /// BenchBook, so other apps never mistake it for something to open.
    /// </summary>
    public const string ReferencesDragFormat = "BenchBook.References";

    /// <summary>The drag format for a tag dragged from the sidebar onto references.</summary>
    public const string TagDragFormat = "BenchBook.Tag";

    /// <summary>Raised after references were moved or copied from outside the gallery, e.g. by dropping them on a project.</summary>
    public event EventHandler<(string Message, string ProjectId, bool CanShow)>? TransferNoticed;

    public void NoticeTransfer(string message, string projectId, bool canShow) =>
        TransferNoticed?.Invoke(this, (message, projectId, canShow));

    T IStoreAccess.With<T>(Func<LibraryStore, T> work) => WithStore(work);

    public LibraryLocation Location { get; }
    public AppSettings Settings { get; }

    /// <summary>Selects a project in the main window. Set by the window.</summary>
    public Action<string>? ShowProject { get; set; }

    /// <summary>Runs work on the store, one piece at a time, since a connection isn't thread-safe.</summary>
    public T WithStore<T>(Func<LibraryStore, T> work)
    {
        lock (_storeLock)
        {
            var store = _store ?? throw new ObjectDisposedException(nameof(LibraryStore), "The library is closed.");
            return work(store);
        }
    }

    public Task<T> WithStoreAsync<T>(Func<LibraryStore, T> work) => Task.Run(() => WithStore(work));

    /// <summary>Saves the settings. A failure is reported, not thrown, since the app can carry on.</summary>
    public string? SaveSettings()
    {
        try
        {
            Settings.Save(Location.SettingsPath);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"BenchBook couldn't save your settings ({error.Message}).";
        }
    }

    /// <summary>Closes the store, so its files can be moved.</summary>
    public void Dispose()
    {
        lock (_storeLock)
        {
            _store?.Dispose();
            _store = null;
        }
    }
}
