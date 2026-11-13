using BenchBook.App.Services;
using BenchBook.App.ViewModels;
using BenchBook.Core.Backup;
using BenchBook.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.Storage.Pickers;

namespace BenchBook.App.Views;

/// <summary>Small functions for x:Bind.</summary>
public static class XamlHelpers
{
    public static bool IsSet(string? text) => !string.IsNullOrEmpty(text);
    public static bool Not(bool value) => !value;
}

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; private set; } = null!;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = new SettingsViewModel((AppServices)e.Parameter);
        foreach (var status in ProjectStatus.All)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new FontIcon { Glyph = Words.StatusGlyph(status), FontSize = 14 });
            row.Children.Add(new TextBlock { Text = Words.StatusName(status) });
            StatusBox.Items.Add(row);
        }
        Bindings.Update();
        await ViewModel.MeasureAsync();
    }

    private static Microsoft.UI.WindowId WindowId => App.MainWindow!.AppWindow.Id;

    private void ShowLibrary_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(ViewModel.LibraryPath);

    private async void CleanUp_Click(object sender, RoutedEventArgs e) => await ViewModel.CleanUpAsync();

    private async void ClearThumbnails_Click(object sender, RoutedEventArgs e) => await ViewModel.ClearThumbnailsAsync();

    // MARK: Back up

    private async void BackUp_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(WindowId)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(BackupWriter.SuggestedFileName(DateTime.Now)),
            DefaultFileExtension = ".benchbookbackup",
        };
        picker.FileTypeChoices.Add("BenchBook Backup", [".benchbookbackup"]);
        var result = await picker.PickSaveFileAsync();
        if (result is null) return;

        if (await ViewModel.BackUpAsync(result.Path) is { } error)
            await ShowError(error);
    }

    // MARK: Restore

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(WindowId);
        picker.FileTypeFilter.Add(".benchbookbackup");
        var result = await picker.PickSingleFileAsync();
        if (result is null) return;

        PreparedRestore prepared;
        try
        {
            prepared = await ViewModel.PrepareRestoreAsync(result.Path);
        }
        catch (BackupException error)
        {
            await ShowError(error.Message);
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.RestoreTitle(prepared),
            Content = new TextBlock { Text = await ViewModel.RestoreMessageAsync(prepared), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Restore and Reopen",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            ViewModel.DiscardRestore();
            return;
        }

        try
        {
            ViewModel.ScheduleRestore(prepared);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ViewModel.DiscardRestore();
            await ShowError(error.Message);
            return;
        }

        // Close the store so its files can be moved, then reopen to finish.
        ViewModel.Services.Dispose();
        var failure = AppInstance.Restart("");
        await ShowError($"BenchBook couldn't reopen itself ({failure}). Close BenchBook and open it again to finish restoring.");
    }

    // MARK: Earlier libraries

    private void ShowPrevious_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) Shell.ShowInExplorer(path);
    }

    private async void RecyclePrevious_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Move “{Path.GetFileName(path)}” to the Recycle Bin?",
            Content = new TextBlock { Text = "It's no longer needed once you're sure the restored library has everything.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Move to Recycle Bin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await Task.Run(() => Shell.MoveToRecycleBin(path));
        }
        catch (IOException error)
        {
            await ShowError(error.Message);
        }
        ViewModel.ListPreviousLibraries();
    }

    private async Task ShowError(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Something Went Wrong",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }
}
