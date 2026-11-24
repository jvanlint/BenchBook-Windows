using BenchBook.App.Services;
using BenchBook.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace BenchBook.App.Views;

/// <summary>What the project page needs: the library, the project, and the window's project commands.</summary>
internal sealed record ProjectPageArgs(AppServices Services, Project Project, Func<Project, Task> Edit, Func<Project, Task> Delete);

/// <summary>A project's header and its Kits, References, Paint, Aftermarket and Build Photos tabs.</summary>
public sealed partial class ProjectPage : Page
{
    private ProjectPageArgs? _args;
    private ProjectPaintView? _paint;
    private ProjectAftermarketView? _aftermarket;
    private ProjectKitsView? _kits;
    private BuildPhotosView? _photos;

    public ProjectPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (ProjectPageArgs)e.Parameter;
        var project = _args.Project;
        NameText.Text = project.Name;
        ToolTipService.SetToolTip(NameText, project.Name);
        ShowSummary();
        StatusIcon.Glyph = Words.StatusGlyph(project.Status);
        _ = ShowProjectPhoto();
        await References.ShowAsync(_args.Services, project);
    }

    /// <summary>A part shown from elsewhere, e.g. search: show the Aftermarket tab.</summary>
    public void ShowAftermarket() => Tabs.SelectedItem = AftermarketTab;

    /// <summary>A build photo opened from elsewhere, e.g. search: show Build Photos, with it selected.</summary>
    public void RevealBuildPhoto(string photoId)
    {
        Tabs.SelectedItem = BuildPhotosTab;
        _photos?.Reveal(photoId);
    }

    private Core.Store.BuildPhotoEntry? _projectPhoto;

    /// <summary>The project photo, small, in the header; nothing when none is set.</summary>
    private async Task ShowProjectPhoto()
    {
        if (_args is null) return;
        var services = _args.Services;
        var projectId = _args.Project.Id;
        _projectPhoto = services.WithStore(s => s.Project(projectId)?.ProjectPhotoId is { } id ? s.BuildPhotoEntries(new[] { id }).FirstOrDefault() : null);
        ProjectPhotoButton.Visibility = _projectPhoto is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectPhotoImage.Source = null;
        if (_projectPhoto?.Asset is not { } asset || await services.Thumbnails.PathFor(asset) is not { } path) return;
        try
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 104, DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Physical };
            using (var stream = File.OpenRead(path)) await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            ProjectPhotoImage.Source = bitmap;
        }
        catch (Exception)
        {
            // Without a thumbnail the button stays empty.
        }
    }

    private void ProjectPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (_args is not null && _projectPhoto is { } photo) Viewers.ImageViewerWindow.Show(_args.Services, photo);
    }

    /// <summary>"Italeri · 1:72 · AH-1W · In Progress": it follows the project's kits.</summary>
    private void ShowSummary()
    {
        if (_args is null) return;
        var project = _args.Project;
        var summary = Words.ProjectSummary(project, _args.Services.WithStore(s => s.ProjectKits(project.Id)));
        SummaryText.Text = summary.Length > 0 ? $"{summary} · {Words.StatusName(project.Status)}" : Words.StatusName(project.Status);
    }

    /// <summary>The name always keeps this much width; the command bar gets what's left.</summary>
    private const double NameMinimumWidth = 260;

    private void Header_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Commands.MaxWidth = Math.Max(48, e.NewSize.Width - NameMinimumWidth - Header.ColumnSpacing);

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var references = sender.SelectedItem == ReferencesTab;
        var photos = sender.SelectedItem == BuildPhotosTab;
        References.Visibility = references ? Visibility.Visible : Visibility.Collapsed;
        // In Build Photos, Add Files and New Note are replaced by Add Photos.
        AddFilesButton.Visibility = references ? Visibility.Visible : Visibility.Collapsed;
        NewNoteButton.Visibility = references ? Visibility.Visible : Visibility.Collapsed;
        AddPhotosButton.Visibility = photos ? Visibility.Visible : Visibility.Collapsed;
        OtherTab.Visibility = references ? Visibility.Collapsed : Visibility.Visible;
        if (_args is null || references) return;
        // Each tab is made the first time it's shown.
        OtherTab.Content = sender.SelectedItem == KitsTab
            ? _kits ??= new ProjectKitsView(_args.Services, _args.Project, ShowSummary)
            : sender.SelectedItem == PaintTab
                ? _paint ??= new ProjectPaintView(_args.Services, _args.Project)
                : sender.SelectedItem == AftermarketTab
                    ? _aftermarket ??= new ProjectAftermarketView(_args.Services, _args.Project)
                    : _photos ??= new BuildPhotosView(_args.Services, _args.Project, () => _ = ShowProjectPhoto());
    }

    // Kits, References, Paint, Aftermarket, Build Photos: Ctrl+1 to Ctrl+5, as the Mac's ⌘1 to ⌘5.
    private void Tab1_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(KitsTab, args);
    private void Tab2_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(ReferencesTab, args);
    private void Tab3_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(PaintTab, args);
    private void Tab4_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(AftermarketTab, args);
    private void Tab5_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(BuildPhotosTab, args);

    private void Select(SelectorBarItem tab, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Tabs.SelectedItem = tab;
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        // Ctrl+O in Build Photos adds photos there.
        if (Tabs.SelectedItem == BuildPhotosTab && _photos is not null) { await _photos.AddPhotosAsync(); return; }
        Tabs.SelectedItem = ReferencesTab;
        await References.AddFilesAsync();
    }

    private async void AddPhotos_Click(object sender, RoutedEventArgs e)
    {
        if (_photos is not null) await _photos.AddPhotosAsync();
    }

    private async void NewNote_Click(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedItem = ReferencesTab;
        await References.NewNoteAsync();
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_args is not null) await _args.Edit(_args.Project);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_args is not null) await _args.Delete(_args.Project);
    }
}
