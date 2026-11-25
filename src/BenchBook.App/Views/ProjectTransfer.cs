using BenchBook.App.Services;
using BenchBook.App.ViewModels;
using BenchBook.Core.Backup;
using BenchBook.Core.Model;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BenchBook.App.Views;

/// <summary>
/// Export Project… and Import Project… (screens.md, "Export and import a project").
/// The dialogs say what the Mac's do, with Show in Explorer for Show in Finder.
/// </summary>
internal static class ProjectTransfer
{
    public const string FileTypeName = "BenchBook project";

    public static async Task ExportAsync(XamlRoot root, AppServices services, Project project)
    {
        var include = new CheckBox { Content = "Include kit prices and paint ownership", IsChecked = false };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(include);
        panel.Children.Add(new TextBlock
        {
            Text = "The project is saved with its references, notes, tags, build photos, paint list, aftermarket parts, kits and files, in one file BenchBook on a Mac or a Windows PC can import. Leave prices and ownership out when sharing it with someone else.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var ask = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Export “{project.Name}”",
            Content = panel,
            PrimaryButtonText = "Export…",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ask.ShowAsync() != ContentDialogResult.Primary) return;
        var personal = include.IsChecked == true;

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.MainWindow!.AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(ProjectExporter.SuggestedFileName(project.Name)),
            DefaultFileExtension = ProjectExporter.Extension,
        };
        picker.FileTypeChoices.Add(FileTypeName, [ProjectExporter.Extension]);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var destination = file.Path;

        try
        {
            var library = await services.WithStoreAsync(s => s.ReadSnapshot());
            var version = SettingsViewModel.AppVersion();
            // The store isn't held while the files are copied into the project file.
            await WithSpinner(root, $"Exporting “{project.Name}”…", () =>
                Task.Run(() => ProjectExporter.Export(library, services.Location, project.Id, personal, destination, version)));
        }
        catch (Exception error) when (error is ProjectFileException or IOException or UnauthorizedAccessException or SqliteException)
        {
            await ReferenceActions.Message(root, "Couldn't Export Project", error.Message);
            return;
        }

        var done = new ContentDialog
        {
            XamlRoot = root,
            Title = "Project Exported",
            Content = new TextBlock
            {
                Text = $"“{project.Name}” is saved in “{Path.GetFileName(destination)}”. BenchBook on a Mac or a Windows PC can import it with File › Import Project….",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Show in Explorer",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await done.ShowAsync() == ContentDialogResult.Primary) Shell.ShowInExplorer(destination);
    }

    /// <summary>Asks for a project file and adds its project to the library. <paramref name="select"/> shows the imported project.</summary>
    public static async Task ImportAsync(XamlRoot root, AppServices services, Action<string> select)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow!.AppWindow.Id);
        picker.FileTypeFilter.Add(ProjectExporter.Extension);
        var picked = await picker.PickSingleFileAsync();
        if (picked is null) return;

        ProjectFile file;
        try
        {
            file = await WithSpinner(root, "Checking the project file…", () => Task.Run(() => ProjectFile.Open(picked.Path, services.Location)));
        }
        catch (Exception error) when (error is ProjectFileException or IOException or UnauthorizedAccessException)
        {
            await ReferenceActions.Message(root, "Couldn't Import Project", error.Message);
            return;
        }

        try
        {
            var mode = ProjectImportMode.Add;
            if (services.WithStore(s => ProjectImporter.ExistingProject(s, file)) is { } existing)
            {
                var choice = new ContentDialog
                {
                    XamlRoot = root,
                    Title = $"“{existing.Name}” Is Already in Your Library",
                    Content = new TextBlock
                    {
                        Text = "Replace it with the version in the file, or keep both? Replacing loses any changes made to it since the file was exported.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "Replace",
                    SecondaryButtonText = "Keep Both",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                ProjectImportMode? chosen = await choice.ShowAsync() switch
                {
                    ContentDialogResult.Primary => ProjectImportMode.Replace,
                    ContentDialogResult.Secondary => ProjectImportMode.KeepBoth,
                    _ => null,
                };
                if (chosen is not { } replaceOrKeep) return;
                mode = replaceOrKeep;
            }

            var counts = file.Manifest.Counts;
            var summary = await WithSpinner(root, $"Importing “{file.Project.Name}”…", () =>
                services.WithStoreAsync(s => ProjectImporter.Import(s, services.Location, file, mode)));
            services.NotifyLibraryChanged();
            select(summary.ProjectId);
            await ReferenceActions.Message(root, "Project Imported", summary.Message(counts));
        }
        catch (Exception error) when (error is ProjectFileException or IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            await ReferenceActions.Message(root, "Couldn't Import Project", error.Message);
        }
        finally
        {
            file.Discard();
        }
    }

    /// <summary>Shows a spinner while the work runs. It can't be dismissed: the work finishes either way.</summary>
    private static async Task<T> WithSpinner<T>(XamlRoot root, string text, Func<Task<T>> work)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        var dialog = new ContentDialog { XamlRoot = root, Content = panel };
        var finished = false;
        dialog.Closing += (_, args) => args.Cancel = !finished;
        var shown = dialog.ShowAsync();
        try
        {
            return await work();
        }
        finally
        {
            finished = true;
            dialog.Hide();
            await shown;
        }
    }
}
