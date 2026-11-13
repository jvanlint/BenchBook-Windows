using BenchBook.App.Services;
using BenchBook.App.ViewModels;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BenchBook.App.Views;

/// <summary>A project's References tab (screens.md, "References tab").</summary>
public sealed partial class ReferencesView : UserControl
{
    private AppServices _services = null!;
    private Project _project = null!;
    private List<ReferenceTile> _tiles = [];

    // The filter is view state only; it isn't saved.
    private string? _kind;
    private readonly HashSet<string> _tags = new(StringComparer.Ordinal);

    public ReferencesView()
    {
        InitializeComponent();
        var paste = new KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.V };
        paste.Invoked += async (_, args) =>
        {
            args.Handled = true;
            await PasteAsync();
        };
        KeyboardAccelerators.Add(paste);
        var editTags = new KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.T };
        editTags.Invoked += async (_, args) =>
        {
            args.Handled = true;
            await EditTagsAsync([]);
        };
        KeyboardAccelerators.Add(editTags);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        // Tiles handle their own presses, so listen for handled ones too.
        Gallery.AddHandler(PointerPressedEvent, new PointerEventHandler(Gallery_PointerPressed), handledEventsToo: true);
        Gallery.AddHandler(PointerMovedEvent, new PointerEventHandler(Gallery_PointerMoved), handledEventsToo: true);
        Gallery.AddHandler(PointerReleasedEvent, new PointerEventHandler(Gallery_PointerReleased), handledEventsToo: true);
        Gallery.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Gallery_PointerCaptureLost), handledEventsToo: true);
    }

    public async Task ShowAsync(AppServices services, Project project)
    {
        if (_services is not null)
        {
            _services.LinkDetailsSaved -= LinkDetailsSaved;
            _services.LibraryChanged -= LibraryChanged;
            _services.TransferNoticed -= TransferNoticed;
        }
        _services = services;
        _project = project;
        services.LinkDetailsSaved += LinkDetailsSaved;
        services.LibraryChanged += LibraryChanged;
        services.TransferNoticed += TransferNoticed;
        Unloaded -= View_Unloaded;
        Unloaded += View_Unloaded;
        await ReloadAsync();
    }

    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        _services.LinkDetailsSaved -= LinkDetailsSaved;
        _services.LibraryChanged -= LibraryChanged;
        _services.TransferNoticed -= TransferNoticed;
    }

    private bool _notifying;

    /// <summary>The library changed somewhere else, e.g. references dropped on a tag: show it.</summary>
    private async void LibraryChanged(object? sender, EventArgs e)
    {
        if (!_notifying) await ReloadAsync();
    }

    private void TransferNoticed(object? sender, (string Message, string ProjectId, bool CanShow) notice) =>
        ShowNotice(notice.Message, notice.ProjectId, notice.CanShow);

    /// <summary>A link in this project got its details or poster: show them.</summary>
    private async void LinkDetailsSaved(object? sender, string referenceId)
    {
        if (_tiles.Any(t => Ids.Comparer.Equals(t.Reference.Id, referenceId))) await ReloadAsync();
    }

    // MARK: Loading and filtering

    public async Task ReloadAsync()
    {
        var entries = await _services.WithStoreAsync(s => s.ProjectReferences(_project.Id));
        var selected = Gallery.SelectedItems.OfType<ReferenceTile>().Select(t => t.Reference.Id).ToHashSet(Ids.Comparer);
        _tiles = entries.Select(e => new ReferenceTile(e)).ToList();

        // Drop filters that no longer match anything in the project.
        if (_kind is not null && _tiles.All(t => t.Reference.Kind != _kind)) _kind = null;
        _tags.IntersectWith(_tiles.SelectMany(t => t.Entry.Tags).Select(t => t.NormalizedName));

        ApplyFilter(selected);
        foreach (var tile in _tiles) _ = tile.LoadPictureAsync(_services);
    }

    private void ApplyFilter(ISet<string>? reselect = null)
    {
        var visible = _tiles.Where(Matches).ToList();
        Gallery.ItemsSource = visible;
        if (reselect is not null)
            foreach (var tile in visible.Where(t => reselect.Contains(t.Reference.Id))) Gallery.SelectedItems.Add(tile);

        BuildFilterBar(visible.Count);
        var empty = _tiles.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        FilterBar.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        NoMatchState.Visibility = !empty && visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A reference shows when it has the chosen kind, if any, and every chosen tag.</summary>
    private bool Matches(ReferenceTile tile)
    {
        if (_kind is not null && tile.Reference.Kind != _kind) return false;
        if (_tags.Count == 0) return true;
        var tags = tile.Entry.Tags.Select(t => t.NormalizedName).ToHashSet(StringComparer.Ordinal);
        return _tags.IsSubsetOf(tags);
    }

    private static readonly string[] KindOrder =
        [ReferenceKind.Image, ReferenceKind.Pdf, ReferenceKind.Markdown, ReferenceKind.Webpage, ReferenceKind.VideoLink, ReferenceKind.TextSnippet, ReferenceKind.Note];

    private void BuildFilterBar(int visibleCount)
    {
        var counts = _tiles.GroupBy(t => t.Reference.Kind).ToDictionary(g => g.Key, g => g.Count());
        TypeButton.Content = _kind is null ? $"All Types ({_tiles.Count})" : $"{ReferenceText.KindPlural(_kind)} ({counts.GetValueOrDefault(_kind)})";
        TypeMenu.Items.Clear();
        var all = new RadioMenuFlyoutItem { Text = $"All Types ({_tiles.Count})", GroupName = "kind", IsChecked = _kind is null };
        all.Click += (_, _) => SetKind(null);
        TypeMenu.Items.Add(all);
        TypeMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var kind in KindOrder.Where(counts.ContainsKey).Concat(counts.Keys.Except(KindOrder).Order()))
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = $"{ReferenceText.KindPlural(kind)} ({counts[kind]})",
                GroupName = "kind",
                IsChecked = _kind == kind,
                Icon = new FontIcon { Glyph = ReferenceTile.KindGlyph(kind) },
            };
            item.Click += (_, _) => SetKind(kind);
            TypeMenu.Items.Add(item);
        }

        // Tags used in the project, most used first, then by name.
        TagFilters.Children.Clear();
        var tags = _tiles.SelectMany(t => t.Entry.Tags)
            .GroupBy(t => t.NormalizedName)
            .Select(g => (Tag: g.First(), Count: g.Count()))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Tag.Name, NaturalComparer.Instance);
        foreach (var (tag, _) in tags)
        {
            var isOn = _tags.Contains(tag.NormalizedName);
            var color = NamedColors.TagColor(tag.Color);
            var chip = new ToggleButton
            {
                IsChecked = isOn,
                Content = tag.Name,
                Padding = new Thickness(10, 3, 10, 3),
                CornerRadius = new CornerRadius(12),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color),
                Background = isOn ? null : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, color.R, color.G, color.B)),
            };
            ToolTipService.SetToolTip(chip, isOn ? $"Stop filtering by “{tag.Name}”" : $"Show only references tagged “{tag.Name}”");
            var normalized = tag.NormalizedName;
            chip.Click += (_, _) => ToggleTag(normalized);
            TagFilters.Children.Add(chip);
        }

        var active = _kind is not null || _tags.Count > 0;
        FilterSummary.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        FilterCount.Text = $"{visibleCount:N0} of {_tiles.Count:N0}";
    }

    private void SetKind(string? kind)
    {
        _kind = kind;
        ApplyFilter();
    }

    private void ToggleTag(string normalized)
    {
        if (!_tags.Remove(normalized)) _tags.Add(normalized);
        ApplyFilter();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _kind = null;
        _tags.Clear();
        ApplyFilter();
    }

    private List<ReferenceTile> Selection => Gallery.SelectedItems.OfType<ReferenceTile>().ToList();

    // MARK: Adding

    public async Task AddFilesAsync()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow!.AppWindow.Id);
        foreach (var type in new[] { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".pdf", ".md", ".markdown", ".mdown", ".mkd" })
            picker.FileTypeFilter.Add(type);
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;
        await ImportAsync(files.Select(f => (CapturedItem)new CapturedItem.File(f.Path)).ToList(), []);
    }

    public async Task NewNoteAsync()
    {
        var dialog = new NoteEditorDialog(_services, _project.Id, null) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ChangedAsync();
    }

    private async Task PasteAsync()
    {
        DataPackageView content;
        try { content = Clipboard.GetContent(); }
        catch (Exception) { return; }
        await CaptureAsync(content);
    }

    private void View_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(AppServices.TagDragFormat))
        {
            // A tag from the sidebar, dropped on a tile, tags it (and the rest of the selection, if it's selected).
            if (TileAt(e.GetPosition(Gallery)) is { } tile)
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                var count = Gallery.SelectedItems.Contains(tile) ? Selection.Count : 1;
                e.DragUIOverride.Caption = count == 1 ? $"Tag “{tile.Title}”" : $"Tag {count:N0} references";
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
            return;
        }
        if (CaptureReader.CanCapture(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"Add to {_project.Name}";
        }
    }

    private async void View_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(AppServices.TagDragFormat))
                await DropTagAsync(e);
            else if (CaptureReader.CanCapture(e.DataView))
                await CaptureAsync(e.DataView);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task DropTagAsync(DragEventArgs e)
    {
        if (TileAt(e.GetPosition(Gallery)) is not { } tile) return;
        if (await e.DataView.GetDataAsync(AppServices.TagDragFormat) is not string tagId) return;
        var targets = Gallery.SelectedItems.Contains(tile) ? Selection : [tile];
        var ids = targets.Select(t => t.Reference.Id).ToList();
        try
        {
            _services.WithStore(s => { s.AddTag(tagId, ids); return 0; });
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            await ShowMessage("Couldn't Change Tags", error.Message);
            return;
        }
        await ChangedAsync();
    }

    /// <summary>The tile under a point in the gallery, if any.</summary>
    private ReferenceTile? TileAt(Windows.Foundation.Point point)
    {
        foreach (var tile in Gallery.Items.OfType<ReferenceTile>())
        {
            if (Gallery.ContainerFromItem(tile) is not FrameworkElement container) continue;
            var bounds = container.TransformToVisual(Gallery).TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Contains(point)) return tile;
        }
        return null;
    }

    /// <summary>Reads files, links, pictures and text from a drop or the clipboard, then imports them.</summary>
    private async Task CaptureAsync(DataPackageView data)
    {
        Busy.Visibility = Visibility.Visible;
        CaptureReport? report;
        try
        {
            report = await CaptureReader.CaptureAsync(_services, data, _project.Id);
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
        await ChangedAsync();
        if (report is not null) await ShowMessage(report.Title, report.Message);
    }

    private async Task ImportAsync(IReadOnlyList<CapturedItem> items, IReadOnlyList<string> skipped)
    {
        Busy.Visibility = Visibility.Visible;
        CaptureReport? report;
        try
        {
            report = await CaptureReader.ImportAsync(_services, items, skipped, _project.Id);
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
        await ChangedAsync();
        if (report is not null) await ShowMessage(report.Title, report.Message);
    }

    private async Task ChangedAsync()
    {
        await ReloadAsync();
        _notifying = true;
        try { _services.NotifyLibraryChanged(); }
        finally { _notifying = false; }
    }

    // MARK: Dragging tiles out

    /// <summary>Drags the selected references, to drop on a project or tag in the sidebar.</summary>
    private void Gallery_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var ids = e.Items.OfType<ReferenceTile>().Select(t => t.Reference.Id).ToList();
        if (ids.Count == 0 || _marquee is not null)
        {
            e.Cancel = true;
            return;
        }
        e.Data.SetData(AppServices.ReferencesDragFormat, string.Join(",", ids));
        e.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy;
    }

    // MARK: The selection rectangle

    /// <summary>Where the drag began, in the gallery's scrolled content, so it stays put while scrolling.</summary>
    private Windows.Foundation.Point? _marquee;
    private Windows.Foundation.Point _marqueePointer;
    private HashSet<ReferenceTile> _marqueeStartSelection = [];
    private ScrollViewer? _scroller;
    private DispatcherTimer? _autoScroll;

    /// <summary>How close to the top or bottom edge the pointer must be to scroll, and how far each step goes.</summary>
    private const double ScrollEdge = 32, ScrollStep = 24;

    private ScrollViewer? Scroller => _scroller ??= FindScroller(Gallery);

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }

    private Windows.Foundation.Point Offset => new(Scroller?.HorizontalOffset ?? 0, Scroller?.VerticalOffset ?? 0);

    private void Gallery_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Gallery);
        if (!point.Properties.IsLeftButtonPressed || IsOnTile(e.OriginalSource as DependencyObject) || IsOnScrollBar(e.OriginalSource as DependencyObject)) return;
        var adding = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control) || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        _marqueeStartSelection = adding ? Selection.ToHashSet() : [];
        if (!adding) Gallery.SelectedItems.Clear();
        var offset = Offset;
        _marquee = new Windows.Foundation.Point(point.Position.X + offset.X, point.Position.Y + offset.Y);
        _marqueePointer = point.Position;
        Gallery.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Gallery_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_marquee is null) return;
        _marqueePointer = e.GetCurrentPoint(Gallery).Position;
        UpdateMarquee();
        // Near the top or bottom edge, keep scrolling while the pointer stays there.
        var nearEdge = _marqueePointer.Y < ScrollEdge || _marqueePointer.Y > Gallery.ActualHeight - ScrollEdge;
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
        var step = _marqueePointer.Y < ScrollEdge ? -ScrollStep
            : _marqueePointer.Y > Gallery.ActualHeight - ScrollEdge ? ScrollStep
            : 0;
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
        if (rect.Width < 4 && rect.Height < 4 && Marquee.Visibility == Visibility.Collapsed) return;

        // Draw only the part inside the gallery; the rest has scrolled out of view.
        var visible = rect;
        visible.Intersect(new Windows.Foundation.Rect(0, 0, Gallery.ActualWidth, Gallery.ActualHeight));
        if (visible.IsEmpty) visible = new Windows.Foundation.Rect(0, 0, 0, 0);
        Marquee.Visibility = Visibility.Visible;
        Canvas.SetLeft(Marquee, visible.X);
        Canvas.SetTop(Marquee, visible.Y);
        Marquee.Width = visible.Width;
        Marquee.Height = visible.Height;

        // Select every tile the rectangle touches, plus what was selected before when adding.
        foreach (var tile in Gallery.Items.OfType<ReferenceTile>())
        {
            if (Gallery.ContainerFromItem(tile) is not FrameworkElement container) continue;
            var bounds = container.TransformToVisual(Gallery).TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            var selected = Intersects(rect, bounds) || _marqueeStartSelection.Contains(tile);
            var isSelected = Gallery.SelectedItems.Contains(tile);
            if (selected && !isSelected) Gallery.SelectedItems.Add(tile);
            else if (!selected && isSelected) Gallery.SelectedItems.Remove(tile);
        }
    }

    private void Gallery_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_marquee is null) return;
        EndMarquee();
        Gallery.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Gallery_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndMarquee();

    private void EndMarquee()
    {
        _marquee = null;
        StopAutoScroll();
        Marquee.Visibility = Visibility.Collapsed;
    }

    private static bool Intersects(Windows.Foundation.Rect a, Windows.Foundation.Rect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static bool IsOnTile(DependencyObject? element)
    {
        for (; element is not null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
            if (element is GridViewItem) return true;
        return false;
    }

    private static bool IsOnScrollBar(DependencyObject? element)
    {
        for (; element is not null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
            if (element is ScrollBar) return true;
        return false;
    }

    // MARK: Opening

    private async void Gallery_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ReferenceTile tile) await OpenAsync(tile);
    }

    private async void Gallery_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter when Selection.Count > 0:
                e.Handled = true;
                await OpenAsync(Selection[0]);
                break;
            case Windows.System.VirtualKey.Space when Selection.Count > 0:
                e.Handled = true;
                if (Selection[0].Entry.Asset is not null) await OpenFileAsync(Selection[0]);
                else await OpenAsync(Selection[0]);
                break;
            case Windows.System.VirtualKey.Delete when Selection.Count > 0:
                e.Handled = true;
                await DeleteAsync(Selection);
                break;
        }
    }

    private async Task OpenAsync(ReferenceTile tile)
    {
        if (await ReferenceActions.Open(XamlRoot, _services, tile.Entry)) await ChangedAsync();
    }

    private Task OpenFileAsync(ReferenceTile tile) => ReferenceActions.OpenFile(XamlRoot, _services, tile.Entry);

    private async Task EditNoteAsync(ReferenceTile tile)
    {
        if (await ReferenceActions.EditNote(XamlRoot, _services, tile.Entry)) await ChangedAsync();
    }

    private async Task EditDetailsAsync(ReferenceTile tile)
    {
        if (await ReferenceActions.EditDetails(XamlRoot, _services, tile.Entry)) await ChangedAsync();
    }

    // MARK: Right-click

    private void Gallery_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var tile = (args.OriginalSource as FrameworkElement)?.DataContext as ReferenceTile;
        if (tile is null) return;
        args.Handled = true;
        if (!Gallery.SelectedItems.Contains(tile))
        {
            Gallery.SelectedItems.Clear();
            Gallery.SelectedItems.Add(tile);
        }
        var selection = Selection;
        var menu = selection.Count == 1 ? MenuFor(tile) : MenuFor(selection);
        if (args.TryGetPosition(Gallery, out var point))
            menu.ShowAt(Gallery, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(args.OriginalSource as FrameworkElement ?? Gallery);
    }

    private MenuFlyout MenuFor(ReferenceTile tile)
    {
        var reference = tile.Reference;
        var menu = new MenuFlyout();
        void Add(string text, Func<Task> action, Symbol? icon = null)
        {
            var item = new MenuFlyoutItem { Text = text };
            if (icon is { } symbol) item.Icon = new SymbolIcon(symbol);
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }

        Add("Open", () => OpenAsync(tile), Symbol.OpenFile);
        if (tile.Entry.Asset is not null) Add("Open in Default App", () => OpenFileAsync(tile));
        foreach (var moment in tile.Entry.Moments)
            Add($"Open at {VideoLink.Format(moment.OffsetSeconds)}",
                () => Opener.OpenLink(VideoLink.PlaybackUrl(reference.SourcePageUrl ?? "", moment.OffsetSeconds)));
        if (reference.SourcePageUrl is not null && reference.Kind is not (ReferenceKind.Webpage or ReferenceKind.VideoLink))
            Add("Open Source Page", () => Opener.OpenLink(reference.SourcePageUrl), Symbol.World);

        if (reference.Kind is ReferenceKind.Webpage or ReferenceKind.VideoLink)
            Add(reference.Kind == ReferenceKind.Webpage ? "Refresh Page Preview" : "Refresh Video Details", () =>
            {
                _services.FetchLinkDetails(new[] { reference.Id }, evenIfOff: true);
                return Task.CompletedTask;
            }, Symbol.Refresh);

        menu.Items.Add(new MenuFlyoutSeparator());
        if (reference.Kind == ReferenceKind.Note) Add("Edit Note…", () => EditNoteAsync(tile), Symbol.Edit);
        else Add("Edit Details…", () => EditDetailsAsync(tile), Symbol.Edit);
        Add("Edit Tags…", () => EditTagsAsync([tile]), Symbol.Tag);
        menu.Items.Add(TagsMenu([tile]));

        if (tile.Entry.Tags.Count > 0)
        {
            var filterBy = new MenuFlyoutSubItem { Text = "Filter by Tag" };
            foreach (var tag in tile.Entry.Tags)
            {
                var item = new MenuFlyoutItem { Text = tag.Name };
                var normalized = tag.NormalizedName;
                item.Click += (_, _) => { _tags.Add(normalized); ApplyFilter(); };
                filterBy.Items.Add(item);
            }
            menu.Items.Add(filterBy);
        }
        var only = new MenuFlyoutItem { Text = $"Show Only {ReferenceText.KindPlural(reference.Kind)}" };
        only.Click += (_, _) => SetKind(reference.Kind);
        menu.Items.Add(only);

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(TransferMenu([tile], TransferMode.Move));
        menu.Items.Add(TransferMenu([tile], TransferMode.Copy));

        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Delete…", () => DeleteAsync([tile]), Symbol.Delete);
        return menu;
    }

    private MenuFlyout MenuFor(List<ReferenceTile> tiles)
    {
        var menu = new MenuFlyout();
        var editTags = new MenuFlyoutItem { Text = "Edit Tags…", Icon = new SymbolIcon(Symbol.Tag) };
        editTags.Click += async (_, _) => await EditTagsAsync(tiles);
        menu.Items.Add(editTags);
        menu.Items.Add(TagsMenu(tiles));
        menu.Items.Add(TransferMenu(tiles, TransferMode.Move));
        menu.Items.Add(TransferMenu(tiles, TransferMode.Copy));
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = $"Delete {tiles.Count:N0} References…", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await DeleteAsync(tiles);
        menu.Items.Add(delete);
        return menu;
    }

    // MARK: Tags

    public async Task EditTagsAsync(List<ReferenceTile> tiles)
    {
        if (tiles.Count == 0) tiles = Selection;
        if (tiles.Count == 0) return;
        var dialog = new ReferenceTagsDialog(_services, tiles.Select(t => t.Entry).ToList()) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
        if (dialog.Changed) await ChangedAsync();
    }

    /// <summary>"Tags ▸": the library's tags, ticked when every chosen reference has it.</summary>
    private MenuFlyoutSubItem TagsMenu(List<ReferenceTile> tiles)
    {
        var menu = new MenuFlyoutSubItem { Text = "Tags" };
        var ids = tiles.Select(t => t.Reference.Id).ToList();
        foreach (var usage in _services.WithStore(s => s.TagsByUsage()))
        {
            var tag = usage.Tag;
            var onAll = tiles.All(t => t.Entry.Tags.Any(x => Ids.Comparer.Equals(x.Id, tag.Id)));
            var item = new ToggleMenuFlyoutItem { Text = tag.Name, IsChecked = onAll, Icon = TagCommands.Dot(tag.Color) };
            item.Click += async (_, _) =>
            {
                _services.WithStore(s => { if (onAll) s.RemoveTag(tag.Id, ids); else s.AddTag(tag.Id, ids); return 0; });
                await ChangedAsync();
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "No Tags", IsEnabled = false });
        return menu;
    }

    // MARK: Moving and copying

    private string? _noticeProjectId;

    /// <summary>"Move to Project ▸" or "Copy to Project ▸", listing the other projects.</summary>
    private MenuFlyoutSubItem TransferMenu(List<ReferenceTile> tiles, TransferMode mode)
    {
        var menu = new MenuFlyoutSubItem { Text = mode == TransferMode.Move ? "Move to Project" : "Copy to Project" };
        var projects = _services.WithStore(s => s.Projects())
            .Where(p => !Ids.Comparer.Equals(p.Id, _project.Id))
            .OrderBy(p => p.Name, NaturalComparer.Instance);
        foreach (var project in projects)
        {
            var item = new MenuFlyoutItem { Text = project.Name };
            item.Click += async (_, _) => await TransferAsync(tiles, project, mode);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "No Other Projects", IsEnabled = false });
        return menu;
    }

    private async Task TransferAsync(List<ReferenceTile> tiles, Project target, TransferMode mode)
    {
        var ids = tiles.Select(t => t.Reference.Id).ToList();
        TransferResult result;
        try
        {
            result = await _services.WithStoreAsync(s => ReferenceTransfer.Transfer(s, _services.Location, ids, target.Id, mode));
        }
        catch (Exception error) when (error is CaptureException or Microsoft.Data.Sqlite.SqliteException)
        {
            await ShowMessage(mode == TransferMode.Move ? "Couldn't Move" : "Couldn't Copy", error.Message);
            return;
        }
        await ChangedAsync();
        ShowNotice(result.Notice(mode, target.Name), target.Id, result.Transferred.Count > 0);
    }

    /// <summary>"Moved 3 references to F-16C.", with "Show F-16C".</summary>
    private void ShowNotice(string message, string projectId, bool canShow)
    {
        var name = _services.WithStore(s => s.Project(projectId))?.Name ?? "Project";
        _noticeProjectId = projectId;
        Notice.Message = message;
        NoticeAction.Content = $"Show {name}";
        NoticeAction.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        Notice.IsOpen = true;
    }

    private void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        Notice.IsOpen = false;
        if (_noticeProjectId is { } id) _services.ShowProject?.Invoke(id);
    }

    // MARK: Deleting

    private async Task DeleteAsync(List<ReferenceTile> tiles)
    {
        if (tiles.Count == 0) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = tiles.Count == 1 ? $"Delete “{tiles[0].Title}”?" : $"Delete {tiles.Count:N0} References?",
            Content = new TextBlock
            {
                Text = tiles.Count == 1
                    ? "This removes the reference and its saved copy from BenchBook. This can't be undone."
                    : "This removes the references and their saved copies from BenchBook. This can't be undone.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        var ids = tiles.Select(t => t.Reference.Id).ToList();
        try
        {
            await _services.WithStoreAsync(s => { ReferenceService.Delete(s, _services.Location, ids); return 0; });
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            await ShowMessage("Couldn't Delete", error.Message);
        }
        await ChangedAsync();
    }

    // MARK: Buttons in the empty state

    private async void AddFiles_Click(object sender, RoutedEventArgs e) => await AddFilesAsync();

    private async void NewNote_Click(object sender, RoutedEventArgs e) => await NewNoteAsync();

    private Task ShowMessage(string title, string message) => ReferenceActions.Message(XamlRoot, title, message);
}
