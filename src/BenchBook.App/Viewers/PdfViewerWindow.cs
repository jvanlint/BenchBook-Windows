using System.Runtime.InteropServices.WindowsRuntime;
using BenchBook.App.Services;
using BenchBook.Core.Capture;
using BenchBook.Core.Markups;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using BenchBook.Pdf;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace BenchBook.App.Viewers;

/// <summary>
/// A PDF in its own window (screens.md, "PDF viewer"): page thumbnails, pages
/// in a continuous scroll, Find, and notes, highlights and arrows drawn over
/// the pages. Markups are never written into the PDF.
/// </summary>
internal sealed partial class PdfViewerWindow : Window
{
    private static readonly Dictionary<string, PdfViewerWindow> Open = new(Ids.Comparer);

    /// <summary>Shows the PDF, in the window already showing it if there is one, optionally at one of its markups.</summary>
    public static void Show(AppServices services, ReferenceEntry entry, string? markupId = null)
    {
        if (Open.TryGetValue(entry.Reference.Id, out var existing))
        {
            WindowFrames.Present(existing);
            if (markupId is not null) existing.FocusMarkup(markupId);
            return;
        }
        WindowFrames.Present(new PdfViewerWindow(services, entry) { _focusAfterLoad = markupId });
    }

    private string? _focusAfterLoad;

    /// <summary>Selects a markup and scrolls its page so the markup is in view.</summary>
    private void FocusMarkup(string markupId)
    {
        if (_pages.Count == 0 || _markups.FirstOrDefault(m => m.Id == markupId) is not { } markup) { _focusAfterLoad = markupId; return; }
        _showMarkups.IsChecked = true;
        Select(markupId);
        ScrollToPage(markup.PageIndex, MarkupGeometry.RectOf(markup));
        if (_fields.TryGetValue(markupId, out var field)) field.StartBringIntoView();
    }

    /// <summary>Page points to layout units at 100%: a point is 1/72 inch, a unit 1/96.</summary>
    private const double K = 96.0 / 72.0;

    private enum Tool { Select, Note, Highlight, Arrow }

    private sealed class PageView
    {
        public required PdfPageInfo Info;
        public required Grid Container;
        public required Image Image;
        public required Canvas Overlay;
        public double RenderedScale;
    }

    private readonly AppServices _services;
    private ReferenceEntry _entry;
    private List<ReferenceEntry> _siblings = [];
    private PdfDocument? _pdf;
    private readonly List<PageView> _pages = [];
    private List<Markup> _markups = [];
    private string? _selected;
    private Tool _tool = Tool.Select;

    private readonly ScrollViewer _scroller = new()
    {
        ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.1f, MaxZoomFactor = 8f,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        IsTabStop = true,
    };
    private readonly StackPanel _pageStack = new() { Spacing = 16, Padding = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ListView _thumbnails = new() { Width = 150, SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0, 8, 0, 8) };
    private readonly StackPanel _markupList = new() { Spacing = 10 };
    private readonly Grid _panel = new() { Width = 320, Padding = new Thickness(12) };
    private readonly StackPanel _problem = new() { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 440, Visibility = Visibility.Collapsed };

    // Toolbar.
    private readonly AppBarButton _previous = new() { Label = "Previous PDF", Icon = new SymbolIcon(Symbol.Back) };
    private readonly AppBarButton _next = new() { Label = "Next PDF", Icon = new SymbolIcon(Symbol.Forward) };
    private readonly Dictionary<Tool, AppBarToggleButton> _toolButtons = [];
    private readonly AppBarButton _colour = new() { Label = "Colour" };
    private readonly AppBarToggleButton _showMarkups = new() { Label = "Show Markups", IsChecked = true, Icon = new FontIcon { Glyph = "\uE7B3" } };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, TextAlignment = TextAlignment.Center };
    private readonly AutoSuggestBox _find = new() { PlaceholderText = "Find in PDF", Width = 200, QueryIcon = new SymbolIcon(Symbol.Find) };
    private readonly TextBlock _findStatus = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 80 };
    private readonly AppBarToggleButton _panelButton = new() { Label = "Markups", IsChecked = true, Icon = new FontIcon { Glyph = "\uE8A0" } };

    private List<PdfMatch> _matches = [];
    private int _match = -1;
    private int _currentPage;

    private PdfViewerWindow(AppServices services, ReferenceEntry entry)
    {
        _services = services;
        _entry = entry;
        Open[entry.Reference.Id] = this;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon("Assets/BenchBook.ico");
        WindowFrames.Restore(this, services, "pdf", 1280, 900);
        Content = Build();
        Closed += (_, _) =>
        {
            CommitTyping();
            Open.Remove(_entry.Reference.Id);
            WindowFrames.Save(this, services, "pdf");
            _pdf?.Dispose();
        };
        _ = LoadAsync(entry, firstTime: true);
    }

    private string ColourFor(string kind) => _services.Settings.MarkupColors.GetValueOrDefault(kind) ?? MarkupKinds.DefaultColor(kind);

    // MARK: Layout

    private Grid Build()
    {
        _scroller.Content = _pageStack;
        _scroller.ViewChanged += (_, e) =>
        {
            UpdateZoomText();
            UpdateCurrentPage();
            if (!e.IsIntermediate) RenderVisible();
        };
        _scroller.SizeChanged += (_, _) => RenderVisible();
        _thumbnails.SelectionChanged += (_, _) =>
        {
            if (_thumbnails.SelectedIndex >= 0 && _thumbnails.SelectedIndex != _currentPage) ScrollToPage(_thumbnails.SelectedIndex);
        };

        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(_previous, "Previous PDF (Ctrl+[)");
        ToolTipService.SetToolTip(_next, "Next PDF (Ctrl+])");
        _previous.Click += (_, _) => Step(-1);
        _next.Click += (_, _) => Step(1);
        commands.PrimaryCommands.Add(_previous);
        commands.PrimaryCommands.Add(_next);
        commands.PrimaryCommands.Add(new AppBarSeparator());
        foreach (var (tool, glyph, label) in new[] { (Tool.Select, "\uE8B0", "Select"), (Tool.Note, "\uE70B", "Note"), (Tool.Highlight, "\uE7E6", "Highlight"), (Tool.Arrow, "\uE72A", "Arrow") })
        {
            var button = new AppBarToggleButton { Label = label, Icon = new FontIcon { Glyph = glyph }, IsChecked = tool == Tool.Select };
            ToolTipService.SetToolTip(button, tool == Tool.Select
                ? "Select, or add a note, highlight or arrow. Hold Shift for a straight highlight. Esc stops drawing."
                : label);
            button.Click += (_, _) => SetTool(tool);
            _toolButtons[tool] = button;
            commands.PrimaryCommands.Add(button);
        }
        ToolTipService.SetToolTip(_colour, "Colour of the selected markup, and of new ones");
        _colour.Click += (_, _) => ColourMenu().ShowAt(_colour);
        commands.PrimaryCommands.Add(_colour);
        ToolTipService.SetToolTip(_showMarkups, "Show or hide markups (Ctrl+B)");
        _showMarkups.Click += (_, _) => DrawAllMarkups();
        commands.PrimaryCommands.Add(_showMarkups);
        commands.PrimaryCommands.Add(new AppBarSeparator());
        var zoomOut = new AppBarButton { Label = "Zoom Out", Icon = new SymbolIcon(Symbol.ZoomOut) };
        ToolTipService.SetToolTip(zoomOut, "Zoom Out (Ctrl+−)");
        zoomOut.Click += (_, _) => ZoomBy(1 / 1.25);
        var zoomIn = new AppBarButton { Label = "Zoom In", Icon = new SymbolIcon(Symbol.ZoomIn) };
        ToolTipService.SetToolTip(zoomIn, "Zoom In (Ctrl+=)");
        zoomIn.Click += (_, _) => ZoomBy(1.25);
        var zoomMenu = new AppBarButton { Label = "Zoom", Icon = new FontIcon { Glyph = "\uE740" } };
        ToolTipService.SetToolTip(zoomMenu, "Zoom to Fit (Ctrl+9) or Actual Size (Ctrl+0)");
        var zoomFlyout = new MenuFlyout();
        var fit = new MenuFlyoutItem { Text = "Zoom to Fit" };
        fit.Click += (_, _) => Fit();
        var actual = new MenuFlyoutItem { Text = "Actual Size" };
        actual.Click += (_, _) => SetZoom(1);
        zoomFlyout.Items.Add(fit);
        zoomFlyout.Items.Add(actual);
        zoomMenu.Flyout = zoomFlyout;
        commands.PrimaryCommands.Add(zoomOut);
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _zoomText, VerticalContentAlignment = VerticalAlignment.Center });
        commands.PrimaryCommands.Add(zoomIn);
        commands.PrimaryCommands.Add(zoomMenu);
        commands.PrimaryCommands.Add(new AppBarSeparator());

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_find, "Find in PDF");
        _find.QuerySubmitted += async (_, _) => await FindAsync(forward: true);
        _find.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && _find.Text.Length == 0) ClearFind();
        };
        var previousMatch = new AppBarButton { Label = "Previous Match", Icon = new FontIcon { Glyph = "\uE70E" } };
        ToolTipService.SetToolTip(previousMatch, "Previous Match (Shift+F3)");
        previousMatch.Click += async (_, _) => await FindAsync(forward: false);
        var nextMatch = new AppBarButton { Label = "Next Match", Icon = new FontIcon { Glyph = "\uE70D" } };
        ToolTipService.SetToolTip(nextMatch, "Next Match (F3)");
        nextMatch.Click += async (_, _) => await FindAsync(forward: true);
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _find, VerticalContentAlignment = VerticalAlignment.Center });
        commands.PrimaryCommands.Add(previousMatch);
        commands.PrimaryCommands.Add(nextMatch);
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _findStatus, VerticalContentAlignment = VerticalAlignment.Center });
        commands.PrimaryCommands.Add(new AppBarSeparator());
        var export = new AppBarButton { Label = "Export Marked-Up Copy…", Icon = new FontIcon { Glyph = "\uEDE1" } };
        ToolTipService.SetToolTip(export, "Save a copy of this PDF with its markups drawn in (Ctrl+Shift+E)");
        export.Click += async (_, _) => await ExportAsync();
        commands.PrimaryCommands.Add(export);
        ToolTipService.SetToolTip(_panelButton, "Show or hide Markups (Ctrl+Alt+I)");
        _panelButton.Click += (_, _) => _panel.Visibility = _panelButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        commands.PrimaryCommands.Add(_panelButton);
        UpdateColourButton();

        _panel.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        _panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _panel.Children.Add(new TextBlock { Text = "Markups", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 0, 0, 8) });
        var markupScroller = new ScrollViewer { Content = _markupList };
        Grid.SetRow(markupScroller, 1);
        _panel.Children.Add(markupScroller);

        var centre = new Grid { Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorTertiaryBrush"] };
        centre.Children.Add(_scroller);
        centre.Children.Add(_problem);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(_thumbnails);
        Grid.SetColumn(centre, 1);
        body.Children.Add(centre);
        Grid.SetColumn(_panel, 2);
        body.Children.Add(_panel);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(commands);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        AddShortcuts(root);
        return root;
    }

    private void AddShortcuts(UIElement root)
    {
        void Key(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) => { args.Handled = true; action(); };
            root.KeyboardAccelerators.Add(accelerator);
        }
        const VirtualKey LeftBracket = (VirtualKey)219, RightBracket = (VirtualKey)221, Plus = (VirtualKey)187, Minus = (VirtualKey)189;
        Key(LeftBracket, VirtualKeyModifiers.Control, () => Step(-1));
        Key(RightBracket, VirtualKeyModifiers.Control, () => Step(1));
        Key(VirtualKey.F, VirtualKeyModifiers.Control, () => _find.Focus(FocusState.Keyboard));
        Key(VirtualKey.F3, VirtualKeyModifiers.None, () => _ = FindAsync(forward: true));
        Key(VirtualKey.F3, VirtualKeyModifiers.Shift, () => _ = FindAsync(forward: false));
        Key(VirtualKey.G, VirtualKeyModifiers.Control, () => _ = FindAsync(forward: true));
        Key(VirtualKey.B, VirtualKeyModifiers.Control, () => { _showMarkups.IsChecked = _showMarkups.IsChecked != true; DrawAllMarkups(); });
        Key(Plus, VirtualKeyModifiers.Control, () => ZoomBy(1.25));
        Key(Minus, VirtualKeyModifiers.Control, () => ZoomBy(1 / 1.25));
        Key(VirtualKey.Number9, VirtualKeyModifiers.Control, Fit);
        Key(VirtualKey.Number0, VirtualKeyModifiers.Control, () => SetZoom(1));
        Key(VirtualKey.E, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = ExportAsync());
        Key(VirtualKey.I, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, () =>
        {
            _panelButton.IsChecked = _panelButton.IsChecked != true;
            _panel.Visibility = _panelButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        });
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        _scroller.KeyDown += async (_, e) =>
        {
            switch (e.Key)
            {
                case VirtualKey.Escape: SetTool(Tool.Select); e.Handled = true; break;
                case VirtualKey.PageDown: ScrollToPage(Math.Min(_currentPage + 1, _pages.Count - 1)); e.Handled = true; break;
                case VirtualKey.PageUp: ScrollToPage(Math.Max(_currentPage - 1, 0)); e.Handled = true; break;
                case VirtualKey.Delete when Selected() is { } markup: e.Handled = true; await DeleteMarkup(markup); break;
            }
        };
    }

    // MARK: Loading

    private async Task LoadAsync(ReferenceEntry entry, bool firstTime)
    {
        CommitTyping();
        if (!firstTime)
        {
            Open.Remove(_entry.Reference.Id);
            if (Open.TryGetValue(entry.Reference.Id, out var other) && other != this) other.Close();
            Open[entry.Reference.Id] = this;
        }
        _entry = entry;
        _selected = null;
        _matches = [];
        _match = -1;
        _findStatus.Text = "";
        _pdf?.Dispose();
        _pdf = null;
        _pages.Clear();
        _pageStack.Children.Clear();
        _thumbnails.Items.Clear();
        _problem.Visibility = Visibility.Collapsed;

        _siblings = _services.WithStore(s => s.ProjectReferences(entry.Reference.ProjectId)).Where(e => e.Reference.Kind == ReferenceKind.Pdf).ToList();
        var index = _siblings.FindIndex(e => Ids.Comparer.Equals(e.Reference.Id, entry.Reference.Id));
        _previous.IsEnabled = index > 0;
        _next.IsEnabled = index >= 0 && index < _siblings.Count - 1;

        var path = entry.Asset is { } asset ? _services.Location.FullPath(asset.OriginalRelativePath) : null;
        if (entry.Asset is null || path is null) { ShowProblem("Can't Show PDF", "This reference has no saved PDF.", false); UpdateTitle(); return; }
        if (!File.Exists(path)) { ShowProblem("PDF Not Found", "The saved copy of this PDF is missing from the library.", false); UpdateTitle(); return; }
        try
        {
            _pdf = await Task.Run(() => PdfDocument.Open(path));
        }
        catch (PdfPasswordException error) { ShowProblem("Can't Show PDF", error.Message, true); UpdateTitle(); return; }
        catch (Exception error) when (error is PdfOpenException or IOException or UnauthorizedAccessException or DllNotFoundException)
        {
            ShowProblem("Can't Show PDF", "The saved copy of this PDF can't be read.", true);
            UpdateTitle();
            return;
        }

        foreach (var info in _pdf.Pages)
        {
            var container = new Grid
            {
                Width = info.ShownWidth * K, Height = info.ShownHeight * K,
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                Shadow = new ThemeShadow(), Translation = new System.Numerics.Vector3(0, 0, 8),
            };
            var image = new Image { Stretch = Stretch.Fill };
            var overlay = new Canvas
            {
                Width = info.Crop.Width, Height = info.Crop.Height,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                RenderTransform = OverlayTransform(info),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(container, $"Page {info.Index + 1}");
            var view = new PageView { Info = info, Container = container, Image = image, Overlay = overlay };
            overlay.PointerPressed += (s, e) => Overlay_PointerPressed(view, e);
            overlay.PointerMoved += (s, e) => Overlay_PointerMoved(view, e);
            overlay.PointerReleased += (s, e) => Overlay_PointerReleased(view, e);
            overlay.PointerCaptureLost += (_, _) => CancelDrag(view);
            overlay.RightTapped += (s, e) => Overlay_RightTapped(view, e);
            container.Children.Add(image);
            container.Children.Add(overlay);
            _pages.Add(view);
            _pageStack.Children.Add(container);
            _thumbnails.Items.Add(Thumbnail(info));
        }
        LoadMarkups();
        UpdateTitle();
        await Task.Yield();
        Fit();
        _ = RenderThumbnails();
        if (_focusAfterLoad is { } markupId)
        {
            _focusAfterLoad = null;
            // After Fit's zoom has taken effect and the pages are laid out: a scroll
            // set before then is measured at the old zoom and lands in the wrong place.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusMarkup(markupId));
        }
    }

    /// <summary>
    /// Lays the overlay out in the page's crop box (y down from its top edge),
    /// then turns it as the page turns, so markups sit on the page as shown.
    /// </summary>
    private static CompositeTransform OverlayTransform(PdfPageInfo info)
    {
        var (w, h) = (info.Crop.Width * K, info.Crop.Height * K);
        var (tx, ty) = info.Rotation switch { 1 => (h, 0.0), 2 => (w, h), 3 => (0.0, w), _ => (0.0, 0.0) };
        return new CompositeTransform { ScaleX = K, ScaleY = K, Rotation = info.Rotation * 90, TranslateX = tx, TranslateY = ty };
    }

    private static PagePoint ToPage(PdfPageInfo info, Windows.Foundation.Point p) => new(p.X + info.Crop.X, info.Crop.Top - p.Y);

    private static Windows.Foundation.Point ToOverlay(PdfPageInfo info, PagePoint p) => new(p.X - info.Crop.X, info.Crop.Top - p.Y);

    private void ShowProblem(string title, string message, bool withOpen)
    {
        _problem.Children.Clear();
        _problem.Children.Add(new FontIcon { Glyph = "\uE7BA", FontSize = 32, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        _problem.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        _problem.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        if (withOpen && _entry.Asset is { } asset)
        {
            var open = new Button { Content = "Open in Default App", HorizontalAlignment = HorizontalAlignment.Center };
            open.Click += async (_, _) => await Opener.OpenInDefaultApp(_services.Location, asset);
            _problem.Children.Add(open);
        }
        _problem.Visibility = Visibility.Visible;
    }

    private void Step(int direction)
    {
        var index = _siblings.FindIndex(e => Ids.Comparer.Equals(e.Reference.Id, _entry.Reference.Id));
        if (index < 0 || index + direction < 0 || index + direction >= _siblings.Count) return;
        _ = LoadAsync(_siblings[index + direction], firstTime: false);
    }

    /// <summary>"Title — F-16CJ · Page 3 of 12 · 2 of 7 found".</summary>
    private void UpdateTitle()
    {
        var project = _services.WithStore(s => s.Project(_entry.Reference.ProjectId));
        var parts = new List<string>();
        if (project is not null) parts.Add(project.Name);
        if (_pages.Count > 0) parts.Add($"Page {_currentPage + 1} of {_pages.Count}");
        if (_find.Text.Trim().Length > 0 && _match >= -1 && _findStatus.Text.Length > 0) parts.Add(_findStatus.Text);
        Title = $"{ReferenceText.FallbackTitle(_entry)} — {string.Join(" · ", parts)}";
    }

    // MARK: Rendering

    private int _renderVersion;

    /// <summary>Draws the pages in view at the current zoom, sharp for the screen.</summary>
    private async void RenderVisible()
    {
        if (_pdf is null || _pages.Count == 0) return;
        var version = ++_renderVersion;
        var zoom = _scroller.ZoomFactor;
        var raster = Content.XamlRoot?.RasterizationScale ?? 1;
        var scale = zoom * raster * K;
        var pdf = _pdf;
        foreach (var view in _pages.Where(IsNearView))
        {
            if (Math.Abs(view.RenderedScale - scale) < 0.01 * scale) continue;
            var width = (int)Math.Round(view.Info.ShownWidth * scale);
            var height = (int)Math.Round(view.Info.ShownHeight * scale);
            // Keep a page's bitmap within what graphics memory handles well.
            var limit = 6000.0 / Math.Max(width, height);
            if (limit < 1) { width = (int)(width * limit); height = (int)(height * limit); }
            if (width <= 0 || height <= 0) continue;
            byte[] pixels;
            try { pixels = await Task.Run(() => pdf.Render(view.Info.Index, width, height)); }
            catch (ObjectDisposedException) { return; }
            if (version != _renderVersion || pdf != _pdf) return;
            var bitmap = new WriteableBitmap(width, height);
            using (var stream = bitmap.PixelBuffer.AsStream()) await stream.WriteAsync(pixels);
            bitmap.Invalidate();
            view.Image.Source = bitmap;
            view.RenderedScale = scale;
        }
    }

    private bool IsNearView(PageView view)
    {
        try
        {
            var bounds = view.Container.TransformToVisual(_scroller).TransformBounds(new Windows.Foundation.Rect(0, 0, view.Container.ActualWidth, view.Container.ActualHeight));
            var margin = _scroller.ViewportHeight;
            return bounds.Bottom >= -margin && bounds.Top <= _scroller.ViewportHeight + margin;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private Image Thumbnail(PdfPageInfo info) => new()
    {
        Width = 110, Height = 110 * info.ShownHeight / Math.Max(1, info.ShownWidth), Stretch = Stretch.Uniform, Margin = new Thickness(4),
    };

    private async Task RenderThumbnails()
    {
        var pdf = _pdf;
        if (pdf is null) return;
        var raster = Content.XamlRoot?.RasterizationScale ?? 1;
        for (var i = 0; i < pdf.Pages.Count && pdf == _pdf; i++)
        {
            if (_thumbnails.Items[i] is not Image image) continue;
            var width = (int)(110 * raster);
            var height = (int)(width * pdf.Pages[i].ShownHeight / Math.Max(1, pdf.Pages[i].ShownWidth));
            byte[] pixels;
            try { pixels = await Task.Run(() => pdf.Render(i, width, Math.Max(1, height))); }
            catch (ObjectDisposedException) { return; }
            if (pdf != _pdf) return;
            var bitmap = new WriteableBitmap(width, Math.Max(1, height));
            using (var stream = bitmap.PixelBuffer.AsStream()) await stream.WriteAsync(pixels);
            bitmap.Invalidate();
            image.Source = bitmap;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, $"Page {i + 1}");
        }
    }

    // MARK: Zoom and pages

    private void Fit()
    {
        if (_pages.Count == 0 || _scroller.ViewportWidth <= 0) return;
        var widest = _pages.Max(p => p.Info.ShownWidth) * K + 48;
        SetZoom(Math.Min(_scroller.ViewportWidth / widest, 4));
    }

    private void SetZoom(double zoom) => _scroller.ChangeView(null, null, (float)Math.Clamp(zoom, _scroller.MinZoomFactor, _scroller.MaxZoomFactor), disableAnimation: true);

    private void ZoomBy(double factor) => SetZoom(_scroller.ZoomFactor * factor);

    private void UpdateZoomText()
    {
        var percent = (int)Math.Round(_scroller.ZoomFactor * 100);
        _zoomText.Text = $"{percent}%";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_zoomText, $"Zoom {percent} percent");
    }

    private void UpdateCurrentPage()
    {
        var middle = _scroller.ViewportHeight / 2;
        for (var i = 0; i < _pages.Count; i++)
        {
            try
            {
                var top = _pages[i].Container.TransformToVisual(_scroller).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                var bottom = top + _pages[i].Container.ActualHeight * _scroller.ZoomFactor;
                if (bottom < middle) continue;
                if (_currentPage != i)
                {
                    _currentPage = i;
                    _thumbnails.SelectedIndex = i;
                    UpdateTitle();
                }
                return;
            }
            catch (ArgumentException)
            {
                return;
            }
        }
    }

    private void ScrollToPage(int index, PageRect? reveal = null)
    {
        if (index < 0 || index >= _pages.Count) return;
        var view = _pages[index];
        var stackTop = view.Container.TransformToVisual(_pageStack).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        var offset = stackTop;
        if (reveal is { } rect)
        {
            // Keep a match a third of the way down the view.
            var point = view.Overlay.TransformToVisual(view.Container).TransformPoint(ToOverlay(view.Info, new PagePoint(rect.X, rect.Top)));
            offset += point.Y - _scroller.ViewportHeight / 3 / _scroller.ZoomFactor;
        }
        _scroller.ChangeView(null, Math.Max(0, offset * _scroller.ZoomFactor), null, disableAnimation: true);
    }

    // MARK: Markups

    private Markup? Selected() => _markups.FirstOrDefault(m => m.Id == _selected);

    private void LoadMarkups()
    {
        _markups = PdfMarkups.Sorted(_services.WithStore(s => s.Markups(_entry.Reference.Id)));
        BuildPanel();
        DrawAllMarkups();
    }

    private void DrawAllMarkups()
    {
        foreach (var view in _pages) DrawMarkups(view);
    }

    /// <summary>Draws a page's markups, and its find matches, on its overlay.</summary>
    private void DrawMarkups(PageView view, Markup? moving = null, double dx = 0, double dy = 0, IReadOnlyList<PagePoint>? stroke = null, string? strokeKind = null)
    {
        view.Overlay.Children.Clear();
        var info = view.Info;
        foreach (var (match, i) in _matches.Select((m, i) => (m, i)).Where(p => p.m.Page == info.Index))
            foreach (var rect in match.Rects)
            {
                var p = ToOverlay(info, new PagePoint(rect.X, rect.Top));
                var box = new Rectangle
                {
                    Width = rect.Width + 2, Height = rect.Height + 2,
                    Fill = new SolidColorBrush(i == _match ? Windows.UI.Color.FromArgb(150, 255, 204, 0) : Windows.UI.Color.FromArgb(70, 255, 204, 0)),
                };
                Canvas.SetLeft(box, p.X - 1);
                Canvas.SetTop(box, p.Y - 1);
                view.Overlay.Children.Add(box);
            }

        if (_showMarkups.IsChecked == true)
        {
            var number = 0;
            foreach (var markup in _markups)
            {
                number++;
                if (markup.PageIndex != info.Index) continue;
                var shown = markup == moving ? Offset(markup, dx, dy) : markup;
                DrawMarkup(view.Overlay, info, shown, number, markup.Id == _selected);
            }
        }
        if (stroke is { Count: > 0 } && strokeKind is not null)
        {
            var preview = new Markup
            {
                Id = "", CreatedAt = default, UpdatedAt = default, ReferenceId = "", PageIndex = info.Index, Kind = strokeKind,
                Color = ColourFor(strokeKind), X = 0, Y = 0, Width = 0, Height = 0, Points = MarkupGeometry.Flatten(stroke), Text = "",
            };
            DrawMarkup(view.Overlay, info, preview, 0, false);
        }
    }

    private static Markup Offset(Markup m, double dx, double dy) => new()
    {
        Id = m.Id, CreatedAt = m.CreatedAt, UpdatedAt = m.UpdatedAt, ReferenceId = m.ReferenceId, PageIndex = m.PageIndex,
        Kind = m.Kind, Color = m.Color, X = m.X + dx, Y = m.Y + dy, Width = m.Width, Height = m.Height, Text = m.Text,
        Points = MarkupGeometry.Flatten(MarkupGeometry.PointsOf(m).Select(p => new PagePoint(p.X + dx, p.Y + dy))),
    };

    private static void DrawMarkup(Canvas overlay, PdfPageInfo info, Markup markup, int number, bool selected)
    {
        var (r, g, b) = PdfExporter.Rgb(markup.Color);
        var color = Windows.UI.Color.FromArgb(255, r, g, b);
        switch (markup.Kind)
        {
            case MarkupKinds.Note:
                var topLeft = ToOverlay(info, new PagePoint(markup.X, markup.Y + markup.Height));
                var note = new Border
                {
                    Width = markup.Width, Height = markup.Height, BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(color),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, Blend(r), Blend(g), Blend(b))),
                    Child = new TextBlock
                    {
                        Text = markup.Text, FontSize = MarkupGeometry.NoteFontSize, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(MarkupGeometry.NotePadding - 1), Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
                    },
                };
                Canvas.SetLeft(note, topLeft.X);
                Canvas.SetTop(note, topLeft.Y);
                overlay.Children.Add(note);
                break;
            case MarkupKinds.Highlight:
                var line = new Polyline
                {
                    Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(115, r, g, b)), StrokeThickness = MarkupGeometry.HighlightWidth,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                };
                foreach (var point in MarkupGeometry.PointsOf(markup)) line.Points.Add(ToOverlay(info, point));
                overlay.Children.Add(line);
                break;
            case MarkupKinds.Arrow:
                var ends = MarkupGeometry.PointsOf(markup);
                if (ends.Count < 2 || ends[0] == ends[^1]) break;
                var (tip, left, right, lineEnd) = MarkupGeometry.ArrowHead(ends[0], ends[^1]);
                var start = ToOverlay(info, ends[0]);
                var stop = ToOverlay(info, lineEnd);
                overlay.Children.Add(new Line
                {
                    X1 = start.X, Y1 = start.Y, X2 = stop.X, Y2 = stop.Y, Stroke = new SolidColorBrush(color),
                    StrokeThickness = MarkupGeometry.ArrowWidth, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
                var head = new Polygon { Fill = new SolidColorBrush(color) };
                foreach (var point in new[] { tip, left, right }) head.Points.Add(ToOverlay(info, point));
                overlay.Children.Add(head);
                break;
            default:
                return;
        }
        if (selected)
        {
            var rect = MarkupGeometry.RectOf(markup).Inflate(4);
            var corner = ToOverlay(info, new PagePoint(rect.X, rect.Top));
            var outline = new Rectangle
            {
                Width = rect.Width, Height = rect.Height, StrokeThickness = 1.5, StrokeDashArray = [3, 2],
                Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            };
            Canvas.SetLeft(outline, corner.X);
            Canvas.SetTop(outline, corner.Y);
            overlay.Children.Add(outline);
        }
        if (number <= 0) return;
        var centre = ToOverlay(info, MarkupGeometry.BadgeCentre(markup));
        var d = MarkupGeometry.BadgeDiameter;
        var badge = new Grid { Width = d, Height = d };
        badge.Children.Add(new Ellipse
        {
            Fill = selected ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] : new SolidColorBrush(color),
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 1.5,
        });
        badge.Children.Add(new TextBlock
        {
            Text = number.ToString(), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(!selected && markup.Color is "yellow" or "green" ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White),
        });
        Canvas.SetLeft(badge, centre.X - d / 2);
        Canvas.SetTop(badge, centre.Y - d / 2);
        overlay.Children.Add(badge);
    }

    private static byte Blend(byte channel) => (byte)Math.Round(channel + (255 - channel) * 0.6);

    /// <summary>The height of a note's text, in points, laid out at the note's text width.</summary>
    private static double NoteTextHeight(string text)
    {
        var block = new TextBlock { Text = text.Length == 0 ? " " : text, FontSize = MarkupGeometry.NoteFontSize, TextWrapping = TextWrapping.Wrap };
        block.Measure(new Windows.Foundation.Size(MarkupGeometry.NoteTextWidth, double.PositiveInfinity));
        return block.DesiredSize.Height;
    }

    // MARK: Tools and pointer

    private void SetTool(Tool tool)
    {
        _tool = tool;
        foreach (var (key, button) in _toolButtons) button.IsChecked = key == tool;
        UpdateColourButton();
    }

    private enum Drag { None, Stroke, Move, Pan }

    private Drag _drag;
    private PagePoint _dragStart;
    private readonly List<PagePoint> _stroke = [];
    private (double Dx, double Dy) _moveOffset;
    private Windows.Foundation.Point _panStart;
    private double _panX, _panY;

    private double Tolerance => 6 / (K * Math.Max(_scroller.ZoomFactor, 0.05));

    private Markup? HitTest(PdfPageInfo info, PagePoint point) =>
        _showMarkups.IsChecked != true ? null
            : _markups.Where(m => m.PageIndex == info.Index).Reverse()
                .FirstOrDefault(m => MarkupGeometry.Hits(point, m.Kind, MarkupGeometry.RectOf(m), MarkupGeometry.PointsOf(m), Tolerance));

    private void Overlay_PointerPressed(PageView view, PointerRoutedEventArgs e)
    {
        var current = e.GetCurrentPoint(view.Overlay);
        if (!current.Properties.IsLeftButtonPressed) return;
        _scroller.Focus(FocusState.Pointer);
        var point = MarkupGeometry.Clamp(ToPage(view.Info, current.Position), view.Info.Crop);
        _dragStart = point;
        switch (_tool)
        {
            case Tool.Note:
                var note = _services.WithStore(s => PdfMarkups.AddNote(s, _entry.Reference.Id, view.Info.Index, point, view.Info.Crop, ColourFor(MarkupKinds.Note), NoteTextHeight("")));
                SetTool(Tool.Select);
                Changed(note.Id, focusText: true);
                e.Handled = true;
                return;
            case Tool.Highlight or Tool.Arrow:
                _drag = Drag.Stroke;
                _stroke.Clear();
                _stroke.Add(point);
                break;
            default:
                if (HitTest(view.Info, ToPage(view.Info, current.Position)) is { } hit)
                {
                    Select(hit.Id);
                    _drag = Drag.Move;
                    _moveOffset = (0, 0);
                }
                else
                {
                    Select(null);
                    _drag = Drag.Pan;
                    _panStart = e.GetCurrentPoint(_scroller).Position;
                    _panX = _scroller.HorizontalOffset;
                    _panY = _scroller.VerticalOffset;
                }
                break;
        }
        view.Overlay.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Overlay_PointerMoved(PageView view, PointerRoutedEventArgs e)
    {
        if (_drag == Drag.None) return;
        var point = MarkupGeometry.Clamp(ToPage(view.Info, e.GetCurrentPoint(view.Overlay).Position), view.Info.Crop);
        switch (_drag)
        {
            case Drag.Stroke:
                var kind = _tool == Tool.Arrow ? MarkupKinds.Arrow : MarkupKinds.Highlight;
                // An arrow, or a highlight with Shift held, is a straight line from where the drag began.
                var straight = _tool == Tool.Arrow || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
                if (straight) { _stroke.RemoveRange(1, _stroke.Count - 1); _stroke.Add(point); }
                else _stroke.Add(point);
                DrawMarkups(view, stroke: _stroke, strokeKind: kind);
                break;
            case Drag.Move when Selected() is { } markup:
                _moveOffset = MarkupGeometry.ClampedOffset(point.X - _dragStart.X, point.Y - _dragStart.Y, MarkupGeometry.RectOf(markup), view.Info.Crop);
                DrawMarkups(view, markup, _moveOffset.Dx, _moveOffset.Dy);
                break;
            case Drag.Pan:
                var now = e.GetCurrentPoint(_scroller).Position;
                _scroller.ChangeView(_panX - (now.X - _panStart.X), _panY - (now.Y - _panStart.Y), null, disableAnimation: true);
                break;
        }
        e.Handled = true;
    }

    private void Overlay_PointerReleased(PageView view, PointerRoutedEventArgs e)
    {
        if (_drag == Drag.None) return;
        var drag = _drag;
        var points = _stroke.ToList();
        var offset = _moveOffset;
        // Clear the drag first: releasing the pointer raises PointerCaptureLost straight away.
        _drag = Drag.None;
        _stroke.Clear();
        view.Overlay.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        switch (drag)
        {
            case Drag.Stroke:
                if (points.Count == 0) { DrawMarkups(view); return; }
                var added = _tool == Tool.Arrow
                    ? _services.WithStore(s => PdfMarkups.AddArrow(s, _entry.Reference.Id, view.Info.Index, points[0], points[^1], view.Info.Crop, ColourFor(MarkupKinds.Arrow)))
                    : _services.WithStore(s => PdfMarkups.AddHighlight(s, _entry.Reference.Id, view.Info.Index, points, view.Info.Crop, ColourFor(MarkupKinds.Highlight)));
                if (added is null) { DrawMarkups(view); return; } // Too short: a click.
                Changed(added.Id);
                break;
            case Drag.Move when Selected() is { } markup:
                if (offset == (0, 0)) { DrawMarkups(view); return; }
                _services.WithStore(s => { PdfMarkups.Move(s, markup, offset.Dx, offset.Dy, view.Info.Crop); return 0; });
                Changed(markup.Id);
                break;
        }
    }

    private void CancelDrag(PageView view)
    {
        if (_drag == Drag.None) return;
        if (_drag is Drag.Stroke or Drag.Move) DrawMarkups(view);
        _drag = Drag.None;
        _stroke.Clear();
    }

    private void Overlay_RightTapped(PageView view, RightTappedRoutedEventArgs e)
    {
        if (HitTest(view.Info, ToPage(view.Info, e.GetPosition(view.Overlay))) is not { } markup) return;
        e.Handled = true;
        Select(markup.Id);
        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "Edit Text" };
        edit.Click += (_, _) => FocusText(markup.Id);
        menu.Items.Add(edit);
        var colours = new MenuFlyoutSubItem { Text = "Colour" };
        foreach (var color in MarkupKinds.Colors)
        {
            var item = new RadioMenuFlyoutItem { Text = NamedColors.DisplayName(color), GroupName = "markupColour", IsChecked = markup.Color == color, Icon = Dot(color) };
            item.Click += (_, _) => SetColour(markup, color);
            colours.Items.Add(item);
        }
        menu.Items.Add(colours);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = $"Delete {MarkupKinds.Name(markup.Kind)}…", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await DeleteMarkup(markup);
        menu.Items.Add(delete);
        menu.ShowAt(view.Overlay, e.GetPosition(view.Overlay));
    }

    private static FontIcon Dot(string color)
    {
        var (r, g, b) = PdfExporter.Rgb(color);
        return new FontIcon { Glyph = "\uEA3B", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b)) };
    }

    // MARK: Colour

    /// <summary>The colour of the selected markup, and of new ones of the tool's kind.</summary>
    private string ColourKind => Selected()?.Kind ?? _tool switch { Tool.Arrow => MarkupKinds.Arrow, Tool.Highlight => MarkupKinds.Highlight, _ => MarkupKinds.Note };

    private void UpdateColourButton()
    {
        var color = Selected()?.Color ?? ColourFor(ColourKind);
        _colour.Icon = Dot(color);
    }

    private MenuFlyout ColourMenu()
    {
        var menu = new MenuFlyout();
        var current = Selected()?.Color ?? ColourFor(ColourKind);
        foreach (var color in MarkupKinds.Colors)
        {
            var item = new RadioMenuFlyoutItem { Text = NamedColors.DisplayName(color), GroupName = "toolColour", IsChecked = color == current, Icon = Dot(color) };
            item.Click += (_, _) =>
            {
                if (Selected() is { } markup) SetColour(markup, color);
                else { _services.Settings.MarkupColors[ColourKind] = color; _services.SaveSettings(); UpdateColourButton(); }
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>Recolours a markup; new markups of its kind use that colour too.</summary>
    private void SetColour(Markup markup, string color)
    {
        _services.WithStore(s => { PdfMarkups.SetColor(s, markup, color); return 0; });
        _services.Settings.MarkupColors[markup.Kind] = color;
        _services.SaveSettings();
        Changed(markup.Id);
    }

    // MARK: Markups panel

    private readonly Dictionary<string, TextBox> _fields = [];

    private void BuildPanel()
    {
        _fields.Clear();
        _markupList.Children.Clear();
        if (_markups.Count == 0)
        {
            _markupList.Children.Add(new TextBlock { Text = "No Markups", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            _markupList.Children.Add(new TextBlock
            {
                Text = "Choose Note, Highlight or Arrow in the toolbar, then click or drag on a page.",
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            return;
        }
        var number = 0;
        foreach (var group in _markups.GroupBy(m => m.PageIndex))
        {
            _markupList.Children.Add(new TextBlock { Text = $"Page {group.Key + 1}", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], Margin = new Thickness(0, 6, 0, 0) });
            foreach (var markup in group)
            {
                number++;
                var header = new Grid { ColumnSpacing = 6 };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dot = Dot(markup.Color);
                dot.FontSize = 12;
                header.Children.Add(dot);
                var title = new TextBlock { Text = $"{MarkupKinds.Name(markup.Kind)} {number}", VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
                Grid.SetColumn(title, 1);
                header.Children.Add(title);
                var delete = new Button { Content = new SymbolIcon(Symbol.Delete), Padding = new Thickness(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
                ToolTipService.SetToolTip(delete, $"Delete {MarkupKinds.Name(markup.Kind)}…");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, $"Delete {MarkupKinds.Name(markup.Kind)} {number}");
                delete.Click += async (_, _) => await DeleteMarkup(markup);
                Grid.SetColumn(delete, 2);
                header.Children.Add(delete);

                var field = new TextBox
                {
                    Text = markup.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48,
                    PlaceholderText = markup.Kind switch { MarkupKinds.Note => "Write a note", MarkupKinds.Highlight => "Describe this highlight", _ => "What does it point to?" },
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, $"{MarkupKinds.Name(markup.Kind)} {number}");
                field.GotFocus += (_, _) =>
                {
                    if (_selected == markup.Id) return;
                    Select(markup.Id);
                    ScrollToPage(markup.PageIndex, MarkupGeometry.RectOf(markup));
                };
                field.LostFocus += (_, _) => SaveText(markup, field.Text);
                WindowFrames.CommitOnEnter(field, () => { SaveText(markup, field.Text); _scroller.Focus(FocusState.Keyboard); });
                ToolTipService.SetToolTip(field, "Enter saves the text. Shift+Enter starts a new line.");
                _fields[markup.Id] = field;
                _markupList.Children.Add(new StackPanel { Spacing = 4, Children = { header, field } });
            }
        }
    }

    private void Select(string? id)
    {
        _selected = id;
        DrawAllMarkups();
        UpdateColourButton();
    }

    private void FocusText(string id)
    {
        _panelButton.IsChecked = true;
        _panel.Visibility = Visibility.Visible;
        if (_fields.TryGetValue(id, out var field)) { field.Focus(FocusState.Programmatic); field.StartBringIntoView(); }
    }

    private void SaveText(Markup markup, string text)
    {
        var clean = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (markup.Text == clean) return;
        var height = markup.Kind == MarkupKinds.Note ? NoteTextHeight(clean) : 0;
        _services.WithStore(s => { PdfMarkups.SetText(s, markup, clean, height); return 0; });
        DrawAllMarkups();
        _services.NotifyLibraryChanged();
    }

    private void CommitTyping()
    {
        foreach (var markup in _markups)
            if (_fields.TryGetValue(markup.Id, out var field)) SaveText(markup, field.Text);
    }

    private void Changed(string? select = null, bool focusText = false)
    {
        CommitTyping();
        _markups = PdfMarkups.Sorted(_services.WithStore(s => s.Markups(_entry.Reference.Id)));
        BuildPanel();
        _selected = select;
        DrawAllMarkups();
        UpdateColourButton();
        if (focusText && select is not null) FocusText(select);
        _services.NotifyLibraryChanged();
    }

    private async Task DeleteMarkup(Markup markup)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Delete {MarkupKinds.Name(markup.Kind)}?",
            Content = new TextBlock { Text = "It will be removed along with its text. The PDF isn't changed.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _fields.Remove(markup.Id);
        _services.WithStore(s => { s.DeleteMarkups(new[] { markup.Id }); return 0; });
        Changed();
    }

    // MARK: Find

    private string _findText = "";

    private async Task FindAsync(bool forward)
    {
        if (_pdf is not { } pdf) return;
        var text = _find.Text.Trim();
        if (text.Length == 0) { ClearFind(); return; }
        if (text != _findText)
        {
            _findText = text;
            _matches = await Task.Run(() => pdf.Find(text));
            _match = -1;
        }
        if (_matches.Count == 0)
        {
            _findStatus.Text = "Not found";
            DrawAllMarkups();
            UpdateTitle();
            return;
        }
        _match = forward ? (_match + 1) % _matches.Count : (_match - 1 + _matches.Count) % _matches.Count;
        _findStatus.Text = $"{_match + 1:N0} of {_matches.Count:N0} found";
        DrawAllMarkups();
        var match = _matches[_match];
        ScrollToPage(match.Page, match.Rects[0]);
        UpdateTitle();
    }

    private void ClearFind()
    {
        _findText = "";
        _matches = [];
        _match = -1;
        _findStatus.Text = "";
        DrawAllMarkups();
        UpdateTitle();
    }

    // MARK: Export

    private async Task ExportAsync()
    {
        CommitTyping();
        await ExportMarkedUpCopy(_services, _entry, Content.XamlRoot, AppWindow.Id);
    }

    /// <summary>
    /// Saves "Title (Marked Up).pdf": each page as shown, with its markups drawn in.
    /// The original isn't changed. Used by the viewer and the right-click menus.
    /// </summary>
    public static async Task ExportMarkedUpCopy(AppServices services, ReferenceEntry entry, XamlRoot root, Microsoft.UI.WindowId window)
    {
        if (entry.Asset is not { } asset || services.Location.FullPath(asset.OriginalRelativePath) is not { } path) return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(window)
        {
            SuggestedFileName = SafeName($"{ReferenceText.FallbackTitle(entry)} (Marked Up)"),
            DefaultFileExtension = ".pdf",
        };
        picker.FileTypeChoices.Add("PDF", [".pdf"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            var markups = PdfMarkups.Sorted(services.WithStore(s => s.Markups(entry.Reference.Id)));
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var regular = await File.ReadAllBytesAsync(System.IO.Path.Combine(fonts, "arial.ttf"));
            var bold = await File.ReadAllBytesAsync(System.IO.Path.Combine(fonts, "arialbd.ttf"));
            var original = await File.ReadAllBytesAsync(path);
            // Measured on this thread, since text layout belongs to the window.
            var data = PdfExporter.Export(original, markups, regular, bold, Measure);
            await File.WriteAllBytesAsync(file.Path, data);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PdfOpenException or PdfPasswordException or DllNotFoundException)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root, Title = "Something Went Wrong",
                Content = new TextBlock { Text = $"The marked-up copy couldn't be saved. {error.Message}", TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
            };
            await dialog.ShowAsync();
        }
    }

    private static readonly TextBlock MeasureBlock = new() { FontFamily = new FontFamily("Arial") };

    /// <summary>A line's width in Arial, in points.</summary>
    private static double Measure(string text, double size, bool bold)
    {
        MeasureBlock.Text = text;
        MeasureBlock.FontSize = size;
        MeasureBlock.FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        MeasureBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return MeasureBlock.DesiredSize.Width;
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}
