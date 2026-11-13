using System.Diagnostics;
using BenchBook.App.Services;
using BenchBook.Core;
using BenchBook.Core.Backup;
using BenchBook.Core.Store;
using Microsoft.UI.Xaml;

namespace BenchBook.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        // An error nothing caught is written to a log and the app carries on,
        // so a problem doesn't lose the user's work and can be traced afterwards.
        UnhandledException += (_, e) =>
        {
            LogError(e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError(e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>%LOCALAPPDATA%\BenchBook\Logs\Errors.log. It stays on this PC and is never sent anywhere.</summary>
    public static string ErrorLogPath => Path.Combine(LibraryLocation.Default().AppFolder, "Logs", "Errors.log");

    public static void LogError(Exception error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            File.AppendAllText(ErrorLogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} BenchBook {Environment.Version}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nowhere to write: nothing more can be done.
        }
    }

    /// <summary>The running app's library, or null when it couldn't be opened.</summary>
    public static AppServices? Services { get; private set; }

    public static Window? MainWindow => (Current as App)?._window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var location = LibraryLocation.Default();

        // A restore scheduled by the last run finishes now, before the store opens.
        RestoreOutcome? outcome;
        try
        {
            outcome = LibraryRestore.FinishPending(location, WaitForExit);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            outcome = new RestoreOutcome.Failed($"The backup couldn't be restored, so your library hasn't been changed. {error.Message}");
        }

        LibraryStore store;
        try
        {
            store = LibraryStore.Open(location.StorePath);
        }
        catch (StoreOpenException error)
        {
            _window = new LibraryErrorWindow(location, error.Message);
            _window.Activate();
            return;
        }

        Services = new AppServices(location, store);
        _window = new MainWindow(Services, outcome);
        _window.Closed += (_, _) => Services?.Dispose();
        _window.Activate();

        // Leftovers from interrupted work are removed at launch, as on the Mac.
        _ = Services.WithStoreAsync(s => LibraryMaintenance.CleanUp(s, location));
        // Links that have never had their details fetched get them when BenchBook opens.
        var services = Services;
        _ = Task.Run(() => services.FetchLinkDetails(services.Links.Missing()));
    }

    /// <summary>Waits up to 30 seconds for the BenchBook that scheduled a restore to close.</summary>
    private static bool WaitForExit(int processId)
    {
        if (processId == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            // The id may have been reused by an unrelated program.
            if (!string.Equals(process.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                return true;
            return process.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            return true; // Not running.
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
