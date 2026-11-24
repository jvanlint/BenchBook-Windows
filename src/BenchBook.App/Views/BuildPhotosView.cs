using BenchBook.App.Services;
using BenchBook.App.Viewers;
using BenchBook.Core.BuildPhotos;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Windows.ApplicationModel.DataTransfer;

namespace BenchBook.App.Views;

/// <summary>One group in the grid: every completed photo, or a day's work in progress.</summary>
internal sealed class PhotoGroup(BuildPhotoGroup group) : List<BuildPhotoEntry>(group.Entries)
{
    public string Id { get; } = group.Id;
    public string Title { get; } = group.Title;
}

/// <summary>
/// A project's Build Photos tab (screens.md, "Build Photos tab"): the user's
/// own photos of the build, completed first, then work in progress by day.
/// </summary>
internal sealed partial class BuildPhotosView : UserControl
{
    private const string TileTemplate =
        "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid /></DataTemplate>";

    private readonly AppServices _services;
    private readonly Project _project;
    /// <summary>Called when the project photo changes, so the header can show it.</summary>
    private readonly Action _projectPhotoChanged;
    private readonly GridView _gallery;
    private readonly CollectionViewSource _source = new() { IsSourceGrouped = true };
    private readonly Canvas _marqueeLayer = new() { IsHitTestVisible = false };
    private readonly Rectangle _marqueeBox = new() { Visibility = Visibility.Collapsed, StrokeThickness = 1, RadiusX = 2, RadiusY = 2 };
    private readonly StackPanel _empty = new() { MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 10, Visibility = Visibility.Collapsed };
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };

    private List<BuildPhotoEntry> _ordered = [];
    private string? _projectPhotoId;

    public BuildPhotosView(AppServices services, Project project, Action projectPhotoChanged)
    {
        _services = services;
        _project = project;
        _projectPhotoChanged = projectPhotoChanged;
        _projectPhotoId = project.ProjectPhotoId;

        _gallery = new GridView
        {
            SelectionMode = ListViewSelectionMode.Extended,
            CanDragItems = true,
            ItemTemplate = (DataTemplate)XamlReader.Load(TileTemplate),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_gallery, "Build Photos");
        _gallery.GroupStyle.Add(new GroupStyle { HidesIfEmpty = true });
        _gallery.ChoosingGroupHeaderContainer += (_, args) =>
        {
            args.GroupHeaderContainer ??= new GridViewHeaderItem();
            if (args.Group is PhotoGroup group) args.GroupHeaderContainer.Content = GroupHeader(group);
        };
        _gallery.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue || args.ItemContainer.ContentTemplateRoot is not Grid grid || args.Item is not BuildPhotoEntry entry) return;
            FillTile(grid, entry);
            args.Handled = true;
        };
        _gallery.DragItemsStarting += Gallery_DragItemsStarting;
        _gallery.DoubleTapped += Gallery_DoubleTapped;
        _gallery.PreviewKeyDown += Gallery_KeyDown;
        _gallery.ContextRequested += Gallery_ContextRequested;
        _gallery.SelectionChanged += Gallery_SelectionChanged;
        // Tiles handle their own presses, so listen for handled ones too.
        _gallery.AddHandler(PointerPressedEvent, new PointerEventHandler(Gallery_PointerPressed), handledEventsToo: true);
        _gallery.AddHandler(PointerMovedEvent, new PointerEventHandler(Gallery_PointerMoved), handledEventsToo: true);
        _gallery.AddHandler(PointerReleasedEvent, new PointerEventHandler(Gallery_PointerReleased), handledEventsToo: true);
        _gallery.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndMarquee()), handledEventsToo: true);

        _marqueeBox.Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _marqueeBox.Fill = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]) { Opacity = 0.18 };
        _marqueeLayer.Children.Add(_marqueeBox);

        BuildEmpty();
        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(_gallery);
        root.Children.Add(_marqueeLayer);
        root.Children.Add(_empty);
        root.Children.Add(_busy);
        Content = root;

        AllowDrop = true;
        DragOver += View_DragOver;
        Drop += View_Drop;
        var paste = new KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.V };
        paste.Invoked += async (_, args) => { args.Handled = true; await PasteAsync(); };
        KeyboardAccelerators.Add(paste);
        var editTags = new KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.T };
        editTags.Invoked += async (_, args) => { args.Handled = true; await EditTagsAsync(Selection); };
        KeyboardAccelerators.Add(editTags);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        services.LibraryChanged += LibraryChanged;
        services.MainWindowActivated += MainWindowActivated;
        Unloaded += (_, _) =>
        {
            services.LibraryChanged -= LibraryChanged;
            services.MainWindowActivated -= MainWindowActivated;
        };
        Loaded += (_, _) =>
        {
            services.LibraryChanged -= LibraryChanged;
            services.MainWindowActivated -= MainWindowActivated;
            services.LibraryChanged += LibraryChanged;
            services.MainWindowActivated += MainWindowActivated;
        };
        Reload();
    }

    // MARK: Loading

    private bool _stale, _notifying;

    private void LibraryChanged(object? sender, EventArgs e)
    {
        if (_notifying) return;
        // Reloading moves the focus, which would bring the main window in front of a viewer.
        if (!_services.MainWindowActive) { _stale = true; return; }
        Reload();
    }

    private void MainWindowActivated(object? sender, EventArgs e)
    {
        if (!_stale) return;
        _stale = false;
        Reload();
    }

    /// <summary>Reloads the grid, keeping the selection, or selecting the photos given.</summary>
    public void Reload(IReadOnlyCollection<string>? select = null)
    {
        var selected = select?.ToHashSet(Ids.Comparer) ?? Selection.Select(e => e.Photo.Id).ToHashSet(Ids.Comparer);
        var (entries, project) = _services.WithStore(s => (s.ProjectBuildPhotos(_project.Id), s.Project(_project.Id)));
        _projectPhotoId = project?.ProjectPhotoId;
        var groups = BuildPhotoText.Groups(entries).Select(g => new PhotoGroup(g)).ToList();
        _ordered = groups.SelectMany(g => g).ToList();
        _source.Source = groups;
        _gallery.ItemsSource = _source.View;
        foreach (var entry in _ordered.Where(e => selected.Contains(e.Photo.Id))) _gallery.SelectedItems.Add(entry);
        if (select?.Count > 0 && _ordered.FirstOrDefault(e => selected.Contains(e.Photo.Id)) is { } first) _gallery.ScrollIntoView(first);
        var empty = _ordered.Count == 0;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _gallery.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Changed(IReadOnlyCollection<string>? select = null)
    {
        Reload(select);
        _notifying = true;
        try { _services.NotifyLibraryChanged(); }
        finally { _notifying = false; }
    }

    private void BuildEmpty()
    {
        _empty.Children.Add(new TextBlock { Text = "No Build Photos", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock
        {
            // Windows has no Continuity Camera or system photo picker, so the Mac's mention of them is left out.
            Text = "Drop or paste photos here, or choose Add Photos…. Keep progress shots and photos of the completed model here, apart from your references.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var add = new Button { Content = "Add Photos…", HorizontalAlignment = HorizontalAlignment.Center };
        add.Click += async (_, _) => await AddPhotosAsync();
        _empty.Children.Add(add);
    }

    private static StackPanel GroupHeader(PhotoGroup group)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (group.Id == "completed") header.Children.Add(new FontIcon { Glyph = StageGlyph(true), FontSize = 14, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        header.Children.Add(new TextBlock { Text = group.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        header.Children.Add(new TextBlock { Text = group.Count.ToString("N0"), Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(header, $"{group.Title}, {group.Count:N0} {(group.Count == 1 ? "photo" : "photos")}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        return header;
    }

    /// <summary>A hammer for work in progress; a seal (a certificate) for completed.</summary>
    private static string StageGlyph(bool completed) => completed ? "" : "";

    // MARK: Tiles

    /// <summary>The photo, its caption or date, the stage line and its tags. The project photo has a star.</summary>
    private void FillTile(Grid grid, BuildPhotoEntry entry)
    {
        grid.Children.Clear();
        grid.DataContext = entry;
        var photo = entry.Photo;
        var tile = new StackPanel { Width = 220, Height = 248, Padding = new Thickness(6), Spacing = 4 };

        var frame = new Grid { Height = 140, CornerRadius = new CornerRadius(6), Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] };
        var placeholder = new FontIcon { Glyph = "", FontSize = 36, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        frame.Children.Add(placeholder);
        frame.Children.Add(image);
        if (Ids.Comparer.Equals(photo.Id, _projectPhotoId))
        {
            var star = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 0, 0, 0)), CornerRadius = new CornerRadius(12),
                Width = 24, Height = 24, Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Child = new FontIcon { Glyph = "", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gold) },
            };
            ToolTipService.SetToolTip(star, "Project photo");
            frame.Children.Add(star);
        }
        tile.Children.Add(frame);

        var title = Words.BuildPhotoTitle(photo);
        tile.Children.Add(new TextBlock { Text = title, Height = 40, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        details.Children.Add(new FontIcon { Glyph = StageGlyph(BuildPhotoText.IsCompleted(photo)), FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        var detailText = DetailLine(entry);
        details.Children.Add(new TextBlock
        {
            Text = detailText, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextTrimming = TextTrimming.CharacterEllipsis,
        });
        tile.Children.Add(details);
        if (entry.Tags.Count > 0)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var tag in entry.Tags)
            {
                var color = NamedColors.TagColor(tag.Color);
                chips.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(48, color.R, color.G, color.B)), BorderBrush = new SolidColorBrush(color),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 1),
                    Child = new TextBlock { Text = tag.Name, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] },
                });
            }
            tile.Children.Add(chips);
        }
        grid.Children.Add(tile);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, string.Join(", ", new[] { title, detailText }.Concat(entry.Tags.Select(t => t.Name))));
        _ = LoadThumbnail(grid, entry, image, placeholder);
    }

    /// <summary>e.g. "WIP · 2 notes" or "Completed · 2:05 pm".</summary>
    private static string DetailLine(BuildPhotoEntry entry)
    {
        var parts = new List<string> { BuildPhotoText.StageName(entry.Photo) };
        if (BuildPhotoText.Caption(entry.Photo) is not null) parts.Add(entry.Photo.TakenAt.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture));
        if (entry.NoteCount > 0) parts.Add(Words.Count(entry.NoteCount, "note"));
        return string.Join(" · ", parts);
    }

    private async Task LoadThumbnail(Grid grid, BuildPhotoEntry entry, Image image, FontIcon placeholder)
    {
        if (entry.Asset is not { } asset || await _services.Thumbnails.PathFor(asset) is not { } path) return;
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 480, DecodePixelType = DecodePixelType.Physical };
            using (var stream = File.OpenRead(path)) await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            // The container may show another photo by now.
            if (!ReferenceEquals(grid.DataContext, entry)) return;
            image.Source = bitmap;
            placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            // A damaged thumbnail: the tile keeps its icon.
        }
    }

    private List<BuildPhotoEntry> Selection => _gallery.SelectedItems.OfType<BuildPhotoEntry>().ToList();

    // MARK: Adding

    public async Task AddPhotosAsync()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow!.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
        };
        foreach (var type in new[] { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif" }) picker.FileTypeFilter.Add(type);
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;
        await AddAsync(files.Select(f => (f.Path, (string?)null)).ToList(), []);
    }

    private async Task PasteAsync()
    {
        DataPackageView content;
        try { content = Clipboard.GetContent(); }
        catch (Exception) { return; }
        if (CaptureReader.CanCapturePhotos(content)) await CaptureAsync(content);
    }

    private void View_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(AppServices.TagDragFormat))
        {
            // A tag from the sidebar, dropped on a tile, tags it (and the rest of the selection, if it's selected).
            if (TileAt(e.GetPosition(_gallery)) is { } entry)
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                var count = _gallery.SelectedItems.Contains(entry) ? Selection.Count : 1;
                e.DragUIOverride.Caption = count == 1 ? $"Tag “{Words.BuildPhotoTitle(entry.Photo)}”" : $"Tag {count:N0} photos";
            }
            else e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        if (CaptureReader.CanCapturePhotos(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"Add to {_project.Name}'s Build Photos";
        }
    }

    private async void View_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(AppServices.TagDragFormat)) await DropTagAsync(e);
            else if (CaptureReader.CanCapturePhotos(e.DataView)) await CaptureAsync(e.DataView);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task DropTagAsync(DragEventArgs e)
    {
        if (TileAt(e.GetPosition(_gallery)) is not { } entry) return;
        if (await e.DataView.GetDataAsync(AppServices.TagDragFormat) is not string tagId) return;
        var targets = _gallery.SelectedItems.Contains(entry) ? Selection : [entry];
        await Perform("Couldn't Change Tags", s => s.AddTagToBuildPhotos(tagId, targets.Select(t => t.Photo.Id).ToList()));
    }

    private BuildPhotoEntry? TileAt(Windows.Foundation.Point point)
    {
        foreach (var entry in _ordered)
        {
            if (_gallery.ContainerFromItem(entry) is not FrameworkElement container) continue;
            var bounds = container.TransformToVisual(_gallery).TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Contains(point)) return entry;
        }
        return null;
    }

    /// <summary>Reads the files and pictures from a drop or the clipboard, then adds them.</summary>
    private async Task CaptureAsync(DataPackageView data)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "BenchBook Capture", Guid.NewGuid().ToString("N"));
        try
        {
            var (files, skipped) = await CaptureReader.ReadPhotos(data, temporary);
            if (files.Count == 0 && skipped.Count == 0) return;
            await AddAsync(files, skipped);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            await ReferenceActions.Message(XamlRoot, "A Photo Couldn't Be Added", $"BenchBook couldn't read what was dropped or pasted ({error.Message}).");
        }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
            catch (IOException) { }
        }
    }

    private async Task AddAsync(List<(string Path, string? Name)> files, List<string> skipped)
    {
        _busy.Visibility = Visibility.Visible;
        List<BuildPhotoOutcome> outcomes;
        try
        {
            outcomes = await Task.Run(() => BuildPhotoService.Add(_services, _services.Location, files, _project.Id));
        }
        finally
        {
            _busy.Visibility = Visibility.Collapsed;
        }
        var added = outcomes.OfType<BuildPhotoOutcome.Added>().Select(a => a.Photo.Id).ToList();
        Changed(added.Count > 0 ? added : null);
        if (BuildPhotoService.Report(outcomes, skipped) is { } report) await ReferenceActions.Message(XamlRoot, report.Title, report.Message);
    }

    // MARK: Dragging tiles out

    /// <summary>Drags the selected photos, to drop on a tag in the sidebar or into a comparison.</summary>
    private void Gallery_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var ids = e.Items.OfType<BuildPhotoEntry>().Select(t => t.Photo.Id).ToList();
        if (ids.Count == 0 || _marquee is not null) { e.Cancel = true; return; }
        e.Data.SetData(AppServices.BuildPhotosDragFormat, string.Join(",", ids));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    // MARK: The selection rectangle (as in References)

    private Windows.Foundation.Point? _marquee;
    private Windows.Foundation.Point _marqueePointer;
    private HashSet<BuildPhotoEntry> _marqueeStartSelection = [];
    private ScrollViewer? _scroller;
    private DispatcherTimer? _autoScroll;
    private const double ScrollEdge = 32, ScrollStep = 24;

    private ScrollViewer? Scroller => _scroller ??= FindScroller(_gallery);

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }

    private Windows.Foundation.Point Offset => new(Scroller?.HorizontalOffset ?? 0, Scroller?.VerticalOffset ?? 0);

    private void Gallery_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_gallery);
        if (!point.Properties.IsLeftButtonPressed || IsOn<GridViewItem>(e.OriginalSource as DependencyObject) || IsOn<ScrollBar>(e.OriginalSource as DependencyObject)) return;
        var adding = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control) || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        _marqueeStartSelection = adding ? Selection.ToHashSet() : [];
        if (!adding) _gallery.SelectedItems.Clear();
        var offset = Offset;
        _marquee = new Windows.Foundation.Point(point.Position.X + offset.X, point.Position.Y + offset.Y);
        _marqueePointer = point.Position;
        _gallery.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Gallery_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_marquee is null) return;
        _marqueePointer = e.GetCurrentPoint(_gallery).Position;
        UpdateMarquee();
        var nearEdge = _marqueePointer.Y < ScrollEdge || _marqueePointer.Y > _gallery.ActualHeight - ScrollEdge;
        if (nearEdge && _autoScroll is null)
        {
            _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _autoScroll.Tick += AutoScroll_Tick;
            _autoScroll.Start();
        }
        e.Handled = true;
    }

    private void AutoScroll_Tick(object? sender, object e)
    {
        if (_marquee is null || Scroller is not { } scroller) { StopAutoScroll(); return; }
        var step = _marqueePointer.Y < ScrollEdge ? -ScrollStep : _marqueePointer.Y > _gallery.ActualHeight - ScrollEdge ? ScrollStep : 0;
        if (step == 0) { StopAutoScroll(); return; }
        scroller.ChangeView(null, Math.Clamp(scroller.VerticalOffset + step, 0, scroller.ScrollableHeight), null, disableAnimation: true);
        UpdateMarquee();
    }

    private void StopAutoScroll()
    {
        _autoScroll?.Stop();
        _autoScroll = null;
    }

    /// <summary>Draws the rectangle from where the drag began to the pointer, and selects every tile it touches.</summary>
    private void UpdateMarquee()
    {
        if (_marquee is not { } startInContent) return;
        var offset = Offset;
        var start = new Windows.Foundation.Point(startInContent.X - offset.X, startInContent.Y - offset.Y);
        var rect = new Windows.Foundation.Rect(start, _marqueePointer);
        if (rect.Width < 4 && rect.Height < 4 && _marqueeBox.Visibility == Visibility.Collapsed) return;

        var visible = rect;
        visible.Intersect(new Windows.Foundation.Rect(0, 0, _gallery.ActualWidth, _gallery.ActualHeight));
        if (visible.IsEmpty) visible = new Windows.Foundation.Rect(0, 0, 0, 0);
        _marqueeBox.Visibility = Visibility.Visible;
        Canvas.SetLeft(_marqueeBox, visible.X);
        Canvas.SetTop(_marqueeBox, visible.Y);
        _marqueeBox.Width = visible.Width;
        _marqueeBox.Height = visible.Height;

        foreach (var entry in _ordered)
        {
            if (_gallery.ContainerFromItem(entry) is not FrameworkElement container) continue;
            var bounds = container.TransformToVisual(_gallery).TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            var selected = Intersects(rect, bounds) || _marqueeStartSelection.Contains(entry);
            var isSelected = _gallery.SelectedItems.Contains(entry);
            if (selected && !isSelected) _gallery.SelectedItems.Add(entry);
            else if (!selected && isSelected) _gallery.SelectedItems.Remove(entry);
        }
    }

    private void Gallery_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_openOnRelease is not null) OpenPending();
        if (_marquee is null) return;
        EndMarquee();
        _gallery.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void EndMarquee()
    {
        _marquee = null;
        StopAutoScroll();
        _marqueeBox.Visibility = Visibility.Collapsed;
    }

    private static bool Intersects(Windows.Foundation.Rect a, Windows.Foundation.Rect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static bool IsOn<T>(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is T) return true;
        return false;
    }

    // MARK: Opening

    private BuildPhotoEntry? _openOnRelease;
    private DispatcherTimer? _openFallback;
    private List<BuildPhotoEntry> _selectionNow = [];
    private List<BuildPhotoEntry>? _earlierSelection;
    private DateTime _earlierSelectionLost;

    /// <summary>A double-click opens the viewer once the click has finished, so the viewer stays in front (as in References).</summary>
    private void Gallery_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not BuildPhotoEntry entry) return;
        e.Handled = true;
        _openOnRelease = entry;
        _openFallback?.Stop();
        _openFallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _openFallback.Tick += (_, _) => OpenPending();
        _openFallback.Start();
    }

    private void OpenPending()
    {
        _openFallback?.Stop();
        _openFallback = null;
        if (_openOnRelease is not { } entry) return;
        _openOnRelease = null;
        // The double-click's first click selected just this tile. If it was one of several
        // selected a moment ago, open those together, and put the selection back.
        if (_earlierSelection is { } earlier && earlier.Contains(entry) && DateTime.UtcNow - _earlierSelectionLost < TimeSpan.FromSeconds(1.5))
        {
            _gallery.SelectedItems.Clear();
            foreach (var other in earlier.Where(_ordered.Contains)) _gallery.SelectedItems.Add(other);
        }
        _earlierSelection = null;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Open(entry));
    }

    private void Gallery_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var now = Selection;
        if (_selectionNow.Count > 1 && now.Count == 1 && _selectionNow.Contains(now[0]))
        {
            _earlierSelection = _selectionNow;
            _earlierSelectionLost = DateTime.UtcNow;
        }
        _selectionNow = now;
    }

    private async void Gallery_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var selection = Selection;
        if (selection.Count == 0) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                Open(selection[0]);
                break;
            case Windows.System.VirtualKey.Space:
                // Windows has no Quick Look; the photo opens in its default app.
                e.Handled = true;
                await OpenInDefaultApp(selection[0]);
                break;
            case Windows.System.VirtualKey.Delete:
                e.Handled = true;
                await DeleteAsync(selection);
                break;
        }
    }

    /// <summary>The image viewer, or with 2–4 photos selected, them side by side.</summary>
    private void Open(BuildPhotoEntry entry)
    {
        var selection = Selection;
        if (selection.Contains(entry) && selection.Count is >= 2 and <= CompareWindow.MaxImages)
        {
            CompareWindow.Show(_services, selection.Select(e => (ViewerImage)e).ToList());
            return;
        }
        ImageViewerWindow.Show(_services, entry);
    }

    /// <summary>Opens a build photo from elsewhere, e.g. search, selecting it here.</summary>
    public void Reveal(string photoId)
    {
        Reload(new[] { photoId });
    }

    private async Task OpenInDefaultApp(BuildPhotoEntry entry)
    {
        if (entry.Asset is not { } asset || !await Opener.OpenInDefaultApp(_services.Location, asset))
            await ReferenceActions.Message(XamlRoot, "Something Went Wrong", BuildPhotoException.Missing().Message);
    }

    // MARK: Right-click

    private void Gallery_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var element = args.OriginalSource as FrameworkElement;
        while (element is not null && element.DataContext is not BuildPhotoEntry) element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        if (element?.DataContext is not BuildPhotoEntry clicked) return;
        args.Handled = true;
        if (!_gallery.SelectedItems.Contains(clicked))
        {
            _gallery.SelectedItems.Clear();
            _gallery.SelectedItems.Add(clicked);
        }
        var targets = Selection;
        var menu = targets.Count == 1 ? MenuFor(clicked) : MenuFor(targets);
        if (args.TryGetPosition(_gallery, out var point)) menu.ShowAt(_gallery, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(element);
    }

    private MenuFlyout MenuFor(BuildPhotoEntry entry)
    {
        var menu = new MenuFlyout();
        Add(menu, "Open", () => { Open(entry); return Task.CompletedTask; });
        Add(menu, "Open in Default App", () => OpenInDefaultApp(entry));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "Edit Details…", () => EditDetailsAsync(entry), Symbol.Edit);
        menu.Items.Add(StageItem([entry]));
        var isProjectPhoto = Ids.Comparer.Equals(entry.Photo.Id, _projectPhotoId);
        Add(menu, isProjectPhoto ? "Remove as Project Photo" : "Set as Project Photo", async () =>
        {
            await Perform("Something Went Wrong", s => s.SetProjectPhoto(_project.Id, isProjectPhoto ? null : entry.Photo.Id));
            _projectPhotoChanged();
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(TagsMenu([entry]));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "Export…", () => ExportAsync([entry]));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "Delete…", () => DeleteAsync([entry]), Symbol.Delete);
        return menu;
    }

    private MenuFlyout MenuFor(List<BuildPhotoEntry> targets)
    {
        var menu = new MenuFlyout();
        if (targets.Count <= CompareWindow.MaxImages)
            Add(menu, "Open Side by Side", () => { CompareWindow.Show(_services, targets.Select(e => (ViewerImage)e).ToList()); return Task.CompletedTask; });
        else
            menu.Items.Add(new MenuFlyoutItem { Text = $"Open Side by Side (Up to {CompareWindow.MaxImages} Images)", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StageItem(targets));
        menu.Items.Add(TagsMenu(targets));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, $"Export {targets.Count:N0} Photos…", () => ExportAsync(targets));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, $"Delete {targets.Count:N0} Photos…", () => DeleteAsync(targets), Symbol.Delete);
        return menu;
    }

    private static void Add(MenuFlyout menu, string text, Func<Task> action, Symbol? icon = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (icon is { } symbol) item.Icon = new SymbolIcon(symbol);
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }

    /// <summary>Mark as Completed, or as WIP when they're all completed already.</summary>
    private MenuFlyoutItem StageItem(List<BuildPhotoEntry> targets)
    {
        var allCompleted = targets.All(t => BuildPhotoText.IsCompleted(t.Photo));
        var item = new MenuFlyoutItem { Text = allCompleted ? "Mark as WIP" : "Mark as Completed" };
        var stage = allCompleted ? BuildPhotoStage.Wip : BuildPhotoStage.Completed;
        var ids = targets.Select(t => t.Photo.Id).ToList();
        item.Click += async (_, _) => await Perform("Something Went Wrong", s => s.SetBuildPhotoStage(ids, stage), ids);
        return item;
    }

    /// <summary>"Tags ▸": the library's tags, ticked when every chosen photo has it, then Edit Tags….</summary>
    private MenuFlyoutSubItem TagsMenu(List<BuildPhotoEntry> targets)
    {
        var menu = new MenuFlyoutSubItem { Text = "Tags" };
        var ids = targets.Select(t => t.Photo.Id).ToList();
        foreach (var usage in _services.WithStore(s => s.TagsByUsage()).OrderBy(u => u.Tag.Name, NaturalComparer.Instance))
        {
            var tag = usage.Tag;
            var on = targets.Count(t => t.Tags.Any(x => Ids.Comparer.Equals(x.Id, tag.Id)));
            var onAll = on == targets.Count;
            var item = new ToggleMenuFlyoutItem
            {
                Text = on > 0 && !onAll ? $"{tag.Name} ({on:N0} of {targets.Count:N0})" : tag.Name,
                IsChecked = onAll, Icon = TagCommands.Dot(tag.Color),
            };
            // Choosing a tag adds it to every photo, or removes it when they all have it.
            item.Click += async (_, _) => await Perform("Couldn't Change Tags", s =>
            {
                if (onAll) s.RemoveTagFromBuildPhotos(tag.Id, ids);
                else s.AddTagToBuildPhotos(tag.Id, ids);
            }, ids);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        var edit = new MenuFlyoutItem { Text = "Edit Tags…" };
        edit.Click += async (_, _) => await EditTagsAsync(targets);
        menu.Items.Add(edit);
        return menu;
    }

    // MARK: Commands

    private async Task Perform(string title, Action<LibraryStore> change, IReadOnlyCollection<string>? select = null)
    {
        try
        {
            _services.WithStore(s => { change(s); return 0; });
        }
        catch (Exception error) when (error is SqliteException or TagException or BuildPhotoException)
        {
            await ReferenceActions.Message(XamlRoot, title, error.Message);
            return;
        }
        Changed(select);
    }

    private async Task EditDetailsAsync(BuildPhotoEntry entry)
    {
        var dialog = new BuildPhotoDetailsDialog(_services, entry) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
        if (dialog.Saved) Changed(new[] { entry.Photo.Id });
    }

    public async Task EditTagsAsync(List<BuildPhotoEntry> targets)
    {
        if (targets.Count == 0) return;
        var dialog = new ReferenceTagsDialog(_services, targets) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
        if (dialog.Changed) Changed(targets.Select(t => t.Photo.Id).ToList());
    }

    private async Task DeleteAsync(List<BuildPhotoEntry> targets)
    {
        if (targets.Count == 0) return;
        var one = targets.Count == 1;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = one ? "Delete This Photo?" : $"Delete {targets.Count:N0} Photos?",
            Content = new TextBlock
            {
                Text = one
                    ? "This removes the photo, its notes and its saved copy from BenchBook. This can't be undone."
                    : "This removes the photos, their notes and their saved copies from BenchBook. This can't be undone.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var wasProjectPhoto = targets.Any(t => Ids.Comparer.Equals(t.Photo.Id, _projectPhotoId));
        await Perform("Something Went Wrong", s => BuildPhotoService.Delete(s, _services.Location, targets.Select(t => t.Photo.Id).ToList()));
        if (wasProjectPhoto) _projectPhotoChanged();
    }

    /// <summary>
    /// Asks whether to remove location data (on by default), then for a folder,
    /// and saves copies there. The photos in BenchBook aren't changed.
    /// </summary>
    private async Task ExportAsync(List<BuildPhotoEntry> targets)
    {
        var removeLocation = new CheckBox { Content = "Remove location data", IsChecked = true };
        ToolTipService.SetToolTip(removeLocation, "Leave out where each photo was taken. The photos in BenchBook aren't changed.");
        var panel = new StackPanel { Spacing = 12, MaxWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = targets.Count == 1 ? "Choose a folder for the exported photo." : $"Choose a folder for the {targets.Count:N0} exported photos.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(removeLocation);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = targets.Count == 1 ? "Export Photo" : $"Export {targets.Count:N0} Photos", Content = panel,
            PrimaryButtonText = "Choose Folder…", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow!.AppWindow.Id);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var remove = removeLocation.IsChecked == true;
        try
        {
            await Task.Run(() => BuildPhotoService.Export(targets, _services.Location, folder.Path, remove));
        }
        catch (BuildPhotoException error)
        {
            await ReferenceActions.Message(XamlRoot, "Something Went Wrong", error.Message);
        }
    }
}
