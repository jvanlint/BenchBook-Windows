using System.Collections.ObjectModel;
using BenchBook.App.Services;
using BenchBook.Core;
using BenchBook.Core.Backup;
using BenchBook.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BenchBook.App.ViewModels;

public sealed record PreviousLibraryItem(string Name, string Path);

/// <summary>The Settings page: General, Library and Backup (screens.md, "Settings").</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _loading;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _loading = true;
        FetchLinkDetails = services.Settings.FetchLinkDetails;
        ImageOpenIndex = services.Settings.OpenImagesAtActualSize ? 1 : 0;
        ShowNoteBoxes = services.Settings.ShowNoteBoxes;
        NewProjectStatusIndex = Math.Max(0, ProjectStatus.All.ToList().IndexOf(services.Settings.NewProjectStatus));
        CheckForUpdatesAutomatically = services.Settings.CheckForUpdatesAutomatically == true;
        CanCheckForUpdates = services.Updates is { IsBusy: false };
        _loading = false;
        LibraryPath = services.Location.AppFolder;
        UpdateLastBackupText();
        ListPreviousLibraries();
    }

    public AppServices Services => _services;

    /// <summary>"Version 1.4.0 (123)" in Settings › About; a development package has no build number.</summary>
    public string VersionText { get; } = $"Version {AppVersion()}";

    // MARK: General

    [ObservableProperty] public partial bool FetchLinkDetails { get; set; }
    [ObservableProperty] public partial int ImageOpenIndex { get; set; }
    [ObservableProperty] public partial bool ShowNoteBoxes { get; set; }
    [ObservableProperty] public partial int NewProjectStatusIndex { get; set; }
    [ObservableProperty] public partial bool CheckForUpdatesAutomatically { get; set; }

    /// <summary>False while a check or an update is under way.</summary>
    [ObservableProperty] public partial bool CanCheckForUpdates { get; set; }

    public void RefreshUpdates()
    {
        _loading = true;
        CheckForUpdatesAutomatically = _services.Settings.CheckForUpdatesAutomatically == true;
        _loading = false;
        CanCheckForUpdates = _services.Updates is { IsBusy: false };
    }

    partial void OnCheckForUpdatesAutomaticallyChanged(bool value)
    {
        if (!_loading && _services.Updates is { } updates) SettingsError = updates.SetCheckAutomatically(value);
    }

    /// <summary>Set when a setting couldn't be saved.</summary>
    [ObservableProperty] public partial string? SettingsError { get; set; }

    partial void OnFetchLinkDetailsChanged(bool value) => Save(s => s.FetchLinkDetails = value);
    partial void OnImageOpenIndexChanged(int value) => Save(s => s.OpenImagesAtActualSize = value == 1);
    partial void OnShowNoteBoxesChanged(bool value) => Save(s => s.ShowNoteBoxes = value);
    partial void OnNewProjectStatusIndexChanged(int value)
    {
        if (value >= 0 && value < ProjectStatus.All.Count) Save(s => s.NewProjectStatus = ProjectStatus.All[value]);
    }

    private void Save(Action<Core.Settings.AppSettings> change)
    {
        if (_loading) return;
        change(_services.Settings);
        SettingsError = _services.SaveSettings();
    }

    // MARK: Library

    public string LibraryPath { get; }

    [ObservableProperty] public partial string SavedFilesSize { get; set; } = "…";
    [ObservableProperty] public partial string ThumbnailsSize { get; set; } = "…";
    [ObservableProperty] public partial string PostersSize { get; set; } = "…";
    [ObservableProperty] public partial string TotalSize { get; set; } = "…";
    [ObservableProperty] public partial string? MaintenanceStatus { get; set; }
    [ObservableProperty] public partial bool IsWorking { get; set; }

    public async Task MeasureAsync()
    {
        var usage = await Task.Run(() => LibraryMaintenance.MeasureUsage(_services.Location));
        SavedFilesSize = Shell.FormatBytes(usage.Originals);
        ThumbnailsSize = Shell.FormatBytes(usage.Thumbnails);
        PostersSize = Shell.FormatBytes(usage.Posters);
        TotalSize = Shell.FormatBytes(usage.Total);
    }

    public async Task CleanUpAsync()
    {
        IsWorking = true;
        try
        {
            var removed = await _services.WithStoreAsync(s => LibraryMaintenance.CleanUp(s, _services.Location));
            MaintenanceStatus = removed == 0 ? "Nothing to clean up." : $"Removed {Words.Count(removed, "unused item")}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            MaintenanceStatus = $"BenchBook couldn't clean up ({error.Message}).";
        }
        finally
        {
            IsWorking = false;
        }
        await MeasureAsync();
    }

    public async Task ClearThumbnailsAsync()
    {
        await Task.Run(() => LibraryMaintenance.ClearThumbnails(_services.Location));
        MaintenanceStatus = "Thumbnails cleared.";
        await MeasureAsync();
    }

    // MARK: Backup

    [ObservableProperty] public partial string LastBackupText { get; set; } = "";
    [ObservableProperty] public partial bool IsBackingUp { get; set; }
    [ObservableProperty] public partial double BackupProgress { get; set; }
    [ObservableProperty] public partial bool IsChecking { get; set; }
    [ObservableProperty] public partial string? BackupStatus { get; set; }

    public ObservableCollection<PreviousLibraryItem> PreviousLibraries { get; } = [];

    [ObservableProperty] public partial bool HasPreviousLibraries { get; set; }

    public bool IsIdle => !IsBackingUp && !IsChecking;

    partial void OnIsBackingUpChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    private void UpdateLastBackupText() =>
        LastBackupText = _services.Settings.LastBackup is { } date
            ? $"Last backed up {Words.DateAndTime(date)}."
            : "Not backed up yet.";

    /// <summary>Backs up to the file. Returns an error message, or null when it worked.</summary>
    public async Task<string?> BackUpAsync(string destination)
    {
        IsBackingUp = true;
        BackupProgress = 0;
        BackupStatus = null;
        var progress = new Progress<double>(value => BackupProgress = value * 100);
        try
        {
            var version = AppVersion();
            var manifest = await _services.WithStoreAsync(s => BackupWriter.BackUp(s, _services.Location, destination, version, progress));
            _services.Settings.LastBackup = manifest.CreatedAt;
            SettingsError = _services.SaveSettings();
            UpdateLastBackupText();
            BackupStatus = $"Backed up {Words.BackupSummary(manifest)} to “{Path.GetFileName(destination)}”.";
            return null;
        }
        catch (Exception error) when (error is BackupException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return error.Message;
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    /// <summary>Checks and unpacks a backup. Throws a <see cref="BackupException"/> saying what's wrong.</summary>
    public async Task<PreparedRestore> PrepareRestoreAsync(string archive)
    {
        IsChecking = true;
        BackupStatus = null;
        try
        {
            return await Task.Run(() => LibraryRestore.Prepare(archive, _services.Location));
        }
        finally
        {
            IsChecking = false;
        }
    }

    public string RestoreTitle(PreparedRestore prepared) =>
        $"Restore the Backup from {Words.DateAndTime(prepared.Manifest.CreatedAt)}?";

    public async Task<string> RestoreMessageAsync(PreparedRestore prepared)
    {
        var now = await _services.WithStoreAsync(s => s.Counts());
        var manifest = prepared.Manifest;
        var message =
            $"It has {Words.BackupSummary(manifest)}, and was made by BenchBook {manifest.AppVersion} for {Words.Platform(manifest.Platform)}. " +
            $"It replaces your library now, with {Words.Count(now.Projects, "project")} and {Words.Count(now.References, "reference")}; " +
            "BenchBook keeps that in a folder beside it and lists it here. BenchBook will close and reopen to finish.";
        if (prepared.SchemaIsNewer)
            message += "\n\nThis backup was made by a newer BenchBook. Anything in it that this version doesn't know about won't be kept.";
        return message;
    }

    public void ScheduleRestore(PreparedRestore prepared) =>
        LibraryRestore.Schedule(_services.Location, prepared.Manifest, Environment.ProcessId);

    public void DiscardRestore() => LibraryRestore.Discard(_services.Location);

    public void ListPreviousLibraries()
    {
        PreviousLibraries.Clear();
        foreach (var path in LibraryRestore.PreviousLibraries(_services.Location))
            PreviousLibraries.Add(new PreviousLibraryItem(Path.GetFileName(path), path));
        HasPreviousLibraries = PreviousLibraries.Count > 0;
    }

    /// <summary>
    /// e.g. "0.1.0" from the package, or "0.2.0 (123)" from a release zip, where
    /// the build number counts commits, as on the Mac.
    /// </summary>
    public static string AppVersion()
    {
        try
        {
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch (Exception)
        {
            // Not running from a package: the release script stamps the version on the .exe.
            var assembly = typeof(SettingsViewModel).Assembly;
            var informational = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            var file = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyFileVersionAttribute), false)
                .OfType<System.Reflection.AssemblyFileVersionAttribute>().FirstOrDefault()?.Version;
            var shown = (informational ?? "0.0.0").Split('+')[0];
            return Version.TryParse(file, out var parsed) && parsed.Revision > 0 ? $"{shown} ({parsed.Revision})" : shown;
        }
    }
}
