using System.Diagnostics;
using System.Runtime.InteropServices;
using BenchBook.App.ViewModels;
using BenchBook.App.Viewers;
using BenchBook.Core.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BenchBook.App.Services;

/// <summary>
/// Check Now and the once-a-day check (behaviour.md, "Updates"). Every update
/// shows its release notes first, with Install Update, Remind Me Later and Skip
/// This Version; nothing is downloaded until the user chooses Install Update.
/// The wording follows Sparkle's, which the Mac shows.
/// </summary>
public sealed partial class AppUpdates
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

    // No headers beyond the User-Agent with BenchBook's version, set per request.
    private static readonly HttpClient Feeds = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly HttpClient Downloads = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly AppServices _services;
    private readonly Func<XamlRoot?> _root;
    private readonly DispatcherQueueTimer _timer;
    private bool _launched;

    public AppUpdates(AppServices services, Func<XamlRoot?> root, DispatcherQueue dispatcher)
    {
        _services = services;
        _root = root;
        // Checks again while BenchBook stays open; each tick checks only when a day has passed.
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromHours(1);
        _timer.Tick += async (_, _) => await CheckIfDue();
    }

    /// <summary>True while a check, a download or the update window is under way; Check Now is dimmed.</summary>
    public bool IsBusy { get; private set; }

    public event EventHandler? BusyChanged;

    public bool CheckAutomatically => _services.Settings.CheckForUpdatesAutomatically == true;

    /// <summary>The version to compare: 1.3.0, without the build number.</summary>
    public static string Version => SettingsViewModel.AppVersion().Split(' ')[0];

    private static string? Runtime => UpdateFeed.Runtime(RuntimeInformation.ProcessArchitecture);

    private static bool IsPackaged
    {
        get
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current.Id;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public string? SetCheckAutomatically(bool on)
    {
        _services.Settings.CheckForUpdatesAutomatically = on;
        var error = _services.SaveSettings();
        if (on) _timer.Start(); else _timer.Stop();
        return error;
    }

    /// <summary>At launch: reports an update that failed, asks on the second launch, and starts the daily check.</summary>
    public async Task LaunchedAsync(UpdateResult? pending)
    {
        if (_launched) return;
        _launched = true;
        if (pending is { Installed: false } failed)
            await Show("The Update Couldn't Be Installed", $"BenchBook {failed.Version} wasn't installed, so this is still {Version}. {failed.Message}");

        var settings = _services.Settings;
        if (settings.Launches < 2)
        {
            settings.Launches++;
            _services.SaveSettings();
        }
        if (settings.CheckForUpdatesAutomatically is null && settings.Launches >= 2)
        {
            var ask = new ContentDialog
            {
                Title = "Check for Updates Automatically?",
                Content = Wrapped("Should BenchBook check for updates automatically? It looks once a day and sends only its version number. You can always check for updates in Settings."),
                PrimaryButtonText = "Check Automatically",
                CloseButtonText = "Don't Check",
                DefaultButton = ContentDialogButton.Primary,
            };
            var answer = await ShowDialog(ask);
            if (answer is null) return; // Another dialog was open: asked again next launch.
            SetCheckAutomatically(answer == ContentDialogResult.Primary);
        }
        if (CheckAutomatically)
        {
            _timer.Start();
            await CheckIfDue();
        }
    }

    private async Task CheckIfDue()
    {
        if (!CheckAutomatically || IsBusy) return;
        if (_services.Settings.LastUpdateCheck is { } last && DateTimeOffset.UtcNow - last < CheckEvery) return;
        await CheckAsync(userAsked: false);
    }

    /// <summary>Check Now: says what it found, even when there's nothing new.</summary>
    public Task CheckNowAsync() => CheckAsync(userAsked: true);

    private async Task CheckAsync(bool userAsked)
    {
        if (IsBusy) return;
        SetBusy(true);
        try
        {
            if (Runtime is not { } runtime)
            {
                if (userAsked) await Show("Update Error", "Updates are only made for x64 and Arm64 PCs.");
                return;
            }
            var result = await UpdateFeed.CheckAsync(Feeds, UpdateFeed.Url(runtime), Version, runtime, UpdateFeed.PublicKey);
            if (result is not UpdateCheck.Failed)
            {
                _services.Settings.LastUpdateCheck = DateTimeOffset.UtcNow;
                _services.SaveSettings();
            }
            switch (result)
            {
                case UpdateCheck.Available { Item: var item } when userAsked || item.Version != _services.Settings.SkippedVersion:
                    await OfferAsync(item, runtime);
                    break;
                case UpdateCheck.Available or UpdateCheck.UpToDate when userAsked:
                    await Show("You're Up to Date", $"BenchBook {Version} is the newest version available.");
                    break;
                case UpdateCheck.Failed failed when userAsked:
                    await Show("Update Error", failed.Message);
                    break;
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>The update window: what's new, then Install Update, Skip This Version or Remind Me Later.</summary>
    private async Task OfferAsync(UpdateItem item, string runtime)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Wrapped($"BenchBook {item.Version} is now available. You have {Version}. Would you like to install it now?"));
        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            panel.Children.Add(new TextBlock { Text = "Release Notes", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            var notes = new RichTextBlock { IsTextSelectionEnabled = true };
            MarkdownRenderer.Render(item.Notes, notes);
            panel.Children.Add(new ScrollViewer { Content = notes, MaxHeight = 280 });
        }
        var dialog = new ContentDialog
        {
            Title = "A New Version of BenchBook Is Available",
            Content = panel,
            PrimaryButtonText = "Install Update",
            SecondaryButtonText = "Skip This Version",
            CloseButtonText = "Remind Me Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        switch (await ShowDialog(dialog))
        {
            case ContentDialogResult.Primary:
                await InstallAsync(item, runtime);
                break;
            case ContentDialogResult.Secondary:
                _services.Settings.SkippedVersion = item.Version;
                _services.SaveSettings();
                break;
            // Remind Me Later: the next day's check shows it again.
        }
    }

    private async Task InstallAsync(UpdateItem item, string runtime)
    {
        var appFolder = AppContext.BaseDirectory;
        var reason = IsPackaged
            ? "This copy of BenchBook was installed as a package, so it can't replace itself."
            : UpdateInstaller.CantReplace(appFolder, _services.Location);
        if (reason is not null)
        {
            var manual = new ContentDialog
            {
                Title = "BenchBook Can't Update Itself Here",
                Content = Wrapped($"{reason} You can download BenchBook {item.Version} from its download page instead, and replace this copy with it. Your library stays as it is."),
                PrimaryButtonText = "Open Download Page",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await ShowDialog(manual) == ContentDialogResult.Primary)
                await Windows.System.Launcher.LaunchUriAsync(new Uri(UpdateFeed.DownloadPage));
            return;
        }

        // Downloading, with Cancel.
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = true };
        var downloading = new StackPanel { Spacing = 12 };
        downloading.Children.Add(Wrapped($"Downloading BenchBook {item.Version} ({FileSize(item.Length)})…"));
        downloading.Children.Add(bar);
        var progressDialog = new ContentDialog { Title = "Downloading Update", Content = downloading, CloseButtonText = "Cancel" };
        using var cancel = new CancellationTokenSource();
        var finished = false;
        progressDialog.Closing += (_, args) =>
        {
            if (!finished) cancel.Cancel();
        };
        var shown = ShowDialog(progressDialog);
        string newFolder;
        try
        {
            var progress = new Progress<double>(value => { bar.IsIndeterminate = false; bar.Value = value; });
            newFolder = await UpdateInstaller.DownloadAsync(Downloads, item, Version, _services.Location, UpdateFeed.PublicKey, progress, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            UpdateInstaller.FinishPending(_services.Location);
            return;
        }
        catch (Exception error) when (error is UpdateException or IOException or UnauthorizedAccessException)
        {
            finished = true;
            progressDialog.Hide();
            await shown;
            UpdateInstaller.FinishPending(_services.Location);
            await Show("Update Error", error.Message);
            return;
        }
        finished = true;
        progressDialog.Hide();
        await shown;

        var ready = new ContentDialog
        {
            Title = "Ready to Install",
            Content = Wrapped($"BenchBook {item.Version} is ready. BenchBook will close, replace its folder with the new version, and open again. Your library isn't changed."),
            PrimaryButtonText = "Install and Relaunch",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialog(ready) != ContentDialogResult.Primary)
        {
            UpdateInstaller.FinishPending(_services.Location);
            return;
        }

        try
        {
            var (exe, arguments) = UpdateInstaller.Schedule(_services.Location, item.Version, newFolder, appFolder, Environment.ProcessId);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            Process.Start(start);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            UpdateInstaller.FinishPending(_services.Location);
            await Show("Update Error", $"The update couldn't be started. {error.Message}");
            return;
        }
        // The updater waits for this process to close, then swaps the folders and opens the new version.
        App.MainWindow?.Close();
        Application.Current.Exit();
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task Show(string title, string message)
    {
        await ShowDialog(new ContentDialog { Title = title, Content = Wrapped(message), CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close });
    }

    /// <summary>Shows a dialog, or returns null if there's no window or another dialog is already open.</summary>
    private async Task<ContentDialogResult?> ShowDialog(ContentDialog dialog)
    {
        if (_root() is not { } root) return null;
        dialog.XamlRoot = root;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (COMException)
        {
            return null; // Only one dialog can be open at a time.
        }
    }

    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static string FileSize(long bytes) => bytes <= 0 ? "size unknown" : $"{bytes / 1024d / 1024d:0.0} MB";
}
