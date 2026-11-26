using System.Diagnostics;
using BenchBook.App.Services;
using BenchBook.Core;
using BenchBook.Core.Backup;
using BenchBook.Core.Store;
using BenchBook.Core.Updates;
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

    /// <summary>The icon for every window's title bar, next to BenchBook.exe whether or not it's packaged.</summary>
    public static string IconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "BenchBook.ico");

    /// <summary>Imports what the share window sends, and publishes the projects it lists.</summary>
    internal static Share.ShareInbox? Inbox { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Started by Install Update from the new version's unpacked folder: this
        // process only swaps the folders, opens the new version and exits.
        var commandLine = Environment.GetCommandLineArgs();
        var install = Array.IndexOf(commandLine, UpdateInstaller.InstallArgument);
        if (install >= 0 && install + 1 < commandLine.Length)
        {
            InstallUpdate(commandLine[install + 1]);
            return;
        }

        // Chosen in the Windows share sheet: this process only shows the share window.
        // It doesn't open the library or finish a restore; the running app imports what it sends.
        var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.ShareTarget
            && activation.Data is Windows.ApplicationModel.Activation.ShareTargetActivatedEventArgs share)
        {
            _window = new Share.ShareWindow(share.ShareOperation);
            _window.Closed += (_, _) => Exit();
            _window.Activate();
            return;
        }

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

        // What the last update left is removed; a failure is reported once the window shows.
        UpdateResult? update;
        try
        {
            update = UpdateInstaller.FinishPending(location);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            update = null;
        }

        Services = new AppServices(location, store);
        var main = new MainWindow(Services, outcome);
        _window = main;
        Services.Updates = new AppUpdates(Services, () => main.Content?.XamlRoot, main.DispatcherQueue);
        main.Updated = update;
        Inbox = new Share.ShareInbox(Services, main.DispatcherQueue);
        Inbox.Reported += async (_, report) => await main.ShowReport(report);
        _window.Closed += (_, _) =>
        {
            Inbox?.Dispose();
            Services?.Dispose();
        };
        _window.Activate();
        // Shared items waiting from while BenchBook was closed are added now.
        Inbox.Start();

        // Leftovers from interrupted work are removed at launch, as on the Mac.
        _ = Services.WithStoreAsync(s => LibraryMaintenance.CleanUp(s, location));
        // Links that have never had their details fetched get them when BenchBook opens.
        var services = Services;
        _ = Task.Run(() => services.FetchLinkDetails(services.Links.Missing()));
    }

    private void InstallUpdate(string planPath)
    {
        var start = UpdateInstaller.Install(planPath, LibraryLocation.Default(), WaitForExit);
        try
        {
            var exe = Path.Combine(start, UpdateInstaller.ExeName);
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = start });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            LogError(error);
        }
        Exit();
    }

    /// <summary>Waits up to 30 seconds for the BenchBook that scheduled a restore or an update to close.</summary>
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
