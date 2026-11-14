using BenchBook.App.Services;
using BenchBook.App.Views;
using BenchBook.Core.Capture;
using BenchBook.Core.Markups;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace BenchBook.App.Viewers;

/// <summary>
/// An image in its own window (screens.md, "Image viewer"): the original, with
/// zoom and pan, numbered note boxes, and a panel of notes and information.
/// </summary>
internal sealed partial class ImageViewerWindow : Window
{
    private static readonly Dictionary<string, ImageViewerWindow> Open = new(Ids.Comparer);

    /// <summary>Shows the image, in the window already showing it if there is one, optionally zoomed to one of its notes.</summary>
    public static void Show(AppServices services, ReferenceEntry entry, string? noteId = null)
    {
        if (Open.TryGetValue(entry.Reference.Id, out var existing))
        {
            WindowFrames.Present(existing);
            if (noteId is not null) existing.FocusNote(noteId);
            return;
        }
        var window = new ImageViewerWindow(services, entry) { _focusAfterLoad = noteId };
        WindowFrames.Present(window);
    }

    private string? _focusAfterLoad;

    /// <summary>Selects a note and zooms to its box, with some of the picture around it.</summary>
    private void FocusNote(string noteId)
    {
        if (!_loaded || _notes.FirstOrDefault(n => n.Id == noteId) is not { } note) { _focusAfterLoad = noteId; return; }
        _showBoxes.IsChecked = true;
        Select(noteId);
        _fitOnResize = false;
        var rect = NoteGeometry.ToPixels(note, _imageWidth, _imageHeight);
        var zoom = (float)Math.Clamp(
            Math.Min(_scroller.ViewportWidth / (rect.Width * _layout * 2.5), _scroller.ViewportHeight / (rect.Height * _layout * 2.5)),
            _scroller.MinZoomFactor, Math.Min(_scroller.MaxZoomFactor, ActualZoom * 4));
        // The box's centre, in the surface's unzoomed units, goes to the middle of the view.
        ZoomTo(zoom, (rect.X + rect.Width / 2) * _layout, (rect.Y + rect.Height / 2) * _layout, _scroller.ViewportWidth / 2, _scroller.ViewportHeight / 2);
        _panelTabs.SelectedItem = _notesTab;
        if (_noteFields.TryGetValue(noteId, out var field)) field.StartBringIntoView();
    }

    private readonly AppServices _services;
    private ReferenceEntry _entry;
    private List<ReferenceEntry> _siblings = [];

    // The image, laid out at one unit per image pixel; zoom scales it.
    private readonly ScrollViewer _scroller = new()
    {
        ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.1f, MaxZoomFactor = 16f,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled,
        IsTabStop = true,
    };
    private readonly CursorCanvas _surface = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };

    /// <summary>
    /// Layout units per image pixel. Very large images are laid out smaller, so
    /// fitting them never needs a zoom below what WinUI allows (0.1). Notes still
    /// work in the image's own pixels, on the overlay.
    /// </summary>
    private double _layout = 1;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new();
    private readonly Rectangle _drawing = new() { StrokeThickness = 2, Visibility = Visibility.Collapsed, StrokeDashArray = [4, 2] };
    private readonly StackPanel _problem = new() { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 420, Visibility = Visibility.Collapsed };
    private readonly InfoBar _scaledNotice = new() { Severity = InfoBarSeverity.Informational, IsClosable = true, Margin = new Thickness(12) };

    // Toolbar.
    private readonly AppBarButton _previous = new() { Label = "Previous Image", Icon = new SymbolIcon(Symbol.Back) };
    private readonly AppBarButton _next = new() { Label = "Next Image", Icon = new SymbolIcon(Symbol.Forward) };
    private readonly AppBarToggleButton _showBoxes = new() { Label = "Show Boxes" };
    private readonly AppBarToggleButton _drawBox = new() { Label = "Draw Box" };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 52, TextAlignment = TextAlignment.Center };
    private readonly AppBarToggleButton _panelButton = new() { Label = "Notes and Info" };

    // Side panel.
    private readonly Grid _panel = new() { Width = 320, Padding = new Thickness(12) };
    private readonly SelectorBar _panelTabs = new();
    private readonly SelectorBarItem _notesTab = new() { Text = "Notes", IsSelected = true };
    private readonly SelectorBarItem _infoTab = new() { Text = "Info" };
    private readonly StackPanel _notesList = new() { Spacing = 12 };
    private readonly StackPanel _infoList = new() { Spacing = 14 };
    private readonly ScrollViewer _notesScroller = new();
    private readonly ScrollViewer _infoScroller = new() { Visibility = Visibility.Collapsed };

    private List<Markup> _notes = [];
    private string? _selected;
    private double _imageWidth, _imageHeight;
    private bool _loaded;

    private ImageViewerWindow(AppServices services, ReferenceEntry entry)
    {
        _services = services;
        _entry = entry;
        Open[entry.Reference.Id] = this;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon("Assets/BenchBook.ico");
        WindowFrames.Restore(this, services, "image", 1100, 760);
        Content = Build();
        _showBoxes.IsChecked = services.Settings.ShowNoteBoxes;
        Closed += (_, _) =>
        {
            CommitTyping();
            Open.Remove(_entry.Reference.Id);
            WindowFrames.Save(this, services, "image");
        };
        _ = LoadAsync(entry, firstTime: true);
    }

    // MARK: Layout

    private Grid Build()
    {
        _surface.Children.Add(_image);
        _surface.Children.Add(_overlay);
        _overlay.Children.Add(_drawing);
        _scroller.Content = _surface;
        _scroller.ViewChanged += (_, _) => { UpdateZoomText(); DrawBoxes(); };
        _scroller.SizeChanged += (_, _) => { if (_loaded && _fitOnResize) Fit(); };

        _surface.PointerPressed += Surface_PointerPressed;
        _surface.PointerMoved += Surface_PointerMoved;
        _surface.PointerReleased += Surface_PointerReleased;
        _surface.PointerCaptureLost += (_, _) => { if (_drag != Drag.None) EndDrag(commit: false); };
        // The scroll wheel zooms around the pointer (Ctrl+wheel also works anywhere in the view).
        _surface.PointerWheelChanged += (_, e) =>
        {
            if (!_loaded) return;
            var delta = e.GetCurrentPoint(_scroller).Properties.MouseWheelDelta;
            ZoomAround(e.GetCurrentPoint(_scroller).Position, Math.Pow(1.15, delta / 120.0));
            e.Handled = true;
        };

        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        Tip(_previous, "Previous Image (Ctrl+[)");
        Tip(_next, "Next Image (Ctrl+])");
        _previous.Click += (_, _) => Step(-1);
        _next.Click += (_, _) => Step(1);
        _showBoxes.Icon = new FontIcon { Glyph = "\uE7B3" };
        Tip(_showBoxes, "Show or hide boxes (Ctrl+B)");
        _showBoxes.Click += (_, _) => DrawBoxes();
        _drawBox.Icon = new FontIcon { Glyph = "\uE9A6" };
        Tip(_drawBox, "Draw a box around a feature, then describe it (Ctrl+D)");
        _drawBox.Click += (_, _) => UpdateCursor();
        var zoomOut = new AppBarButton { Label = "Zoom Out", Icon = new SymbolIcon(Symbol.ZoomOut) };
        Tip(zoomOut, "Zoom Out (Ctrl+−)");
        zoomOut.Click += (_, _) => ZoomBy(1 / 1.25);
        var zoomIn = new AppBarButton { Label = "Zoom In", Icon = new SymbolIcon(Symbol.ZoomIn) };
        Tip(zoomIn, "Zoom In (Ctrl+=)");
        zoomIn.Click += (_, _) => ZoomBy(1.25);
        var fit = new AppBarButton { Label = "Zoom to Fit", Icon = new FontIcon { Glyph = "\uE9A6" } };
        fit.Icon = new FontIcon { Glyph = "\uE740" };
        Tip(fit, "Zoom to Fit (Ctrl+9)");
        fit.Click += (_, _) => Fit();
        var actual = new AppBarButton { Label = "Actual Size", Icon = new FontIcon { Glyph = "\uE71E" } };
        Tip(actual, "Actual Size (Ctrl+0)");
        actual.Click += (_, _) => ActualSize();
        _panelButton.Icon = new FontIcon { Glyph = "\uE8A0" };
        Tip(_panelButton, "Show or hide Notes and Info (Ctrl+Alt+I)");
        _panelButton.IsChecked = true;
        _panelButton.Click += (_, _) => _panel.Visibility = _panelButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        foreach (var command in new ICommandBarElement[] { _previous, _next, new AppBarSeparator(), _showBoxes, _drawBox, new AppBarSeparator(), zoomOut, new AppBarElementContainer { Content = _zoomText, VerticalContentAlignment = VerticalAlignment.Center }, zoomIn, fit, actual, new AppBarSeparator(), _panelButton })
            commands.PrimaryCommands.Add(command);

        _panelTabs.Items.Add(_notesTab);
        _panelTabs.Items.Add(_infoTab);
        _panelTabs.SelectionChanged += (_, _) =>
        {
            var notes = _panelTabs.SelectedItem == _notesTab;
            _notesScroller.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
            _infoScroller.Visibility = notes ? Visibility.Collapsed : Visibility.Visible;
        };
        _notesScroller.Content = _notesList;
        _infoScroller.Content = _infoList;
        _panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _panel.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        _panel.Children.Add(_panelTabs);
        Grid.SetRow(_notesScroller, 1);
        Grid.SetRow(_infoScroller, 1);
        _notesScroller.Margin = _infoScroller.Margin = new Thickness(0, 12, 0, 0);
        _panel.Children.Add(_notesScroller);
        _panel.Children.Add(_infoScroller);

        var canvasArea = new Grid();
        canvasArea.Children.Add(_scroller);
        canvasArea.Children.Add(_problem);
        _scaledNotice.VerticalAlignment = VerticalAlignment.Bottom;
        canvasArea.Children.Add(_scaledNotice);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(canvasArea);
        Grid.SetColumn(_panel, 1);
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

    private static void Tip(DependencyObject element, string text) => ToolTipService.SetToolTip(element, text);

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
        Key(VirtualKey.B, VirtualKeyModifiers.Control, () => { _showBoxes.IsChecked = _showBoxes.IsChecked != true; DrawBoxes(); });
        Key(VirtualKey.D, VirtualKeyModifiers.Control, () => { _drawBox.IsChecked = _drawBox.IsChecked != true; UpdateCursor(); });
        Key(Plus, VirtualKeyModifiers.Control, () => ZoomBy(1.25));
        Key(VirtualKey.Add, VirtualKeyModifiers.Control, () => ZoomBy(1.25));
        Key(Minus, VirtualKeyModifiers.Control, () => ZoomBy(1 / 1.25));
        Key(VirtualKey.Subtract, VirtualKeyModifiers.Control, () => ZoomBy(1 / 1.25));
        Key(VirtualKey.Number9, VirtualKeyModifiers.Control, Fit);
        Key(VirtualKey.Number0, VirtualKeyModifiers.Control, ActualSize);
        Key(VirtualKey.I, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, () =>
        {
            _panelButton.IsChecked = _panelButton.IsChecked != true;
            _panel.Visibility = _panelButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        });
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        _scroller.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Escape && _drawBox.IsChecked == true) { _drawBox.IsChecked = false; UpdateCursor(); e.Handled = true; }
            else if (e.Key == VirtualKey.Delete && _selected is { } id && _notes.FirstOrDefault(n => n.Id == id) is { } note) { e.Handled = true; await DeleteNote(note); }
        };
    }

    // MARK: Loading

    private async Task LoadAsync(ReferenceEntry entry, bool firstTime)
    {
        CommitTyping();
        if (!firstTime && !ReferenceEquals(entry, _entry))
        {
            Open.Remove(_entry.Reference.Id);
            if (Open.TryGetValue(entry.Reference.Id, out var other) && other != this) other.Close();
            Open[entry.Reference.Id] = this;
        }
        _entry = entry;
        _selected = null;
        _loaded = false;
        _siblings = _services.WithStore(s => s.ProjectReferences(entry.Reference.ProjectId)).Where(e => e.Reference.Kind == ReferenceKind.Image).ToList();
        var index = _siblings.FindIndex(e => Ids.Comparer.Equals(e.Reference.Id, entry.Reference.Id));
        _previous.IsEnabled = index > 0;
        _next.IsEnabled = index >= 0 && index < _siblings.Count - 1;
        _image.Source = null;
        _problem.Visibility = Visibility.Collapsed;
        _scaledNotice.IsOpen = false;

        var asset = entry.Asset;
        var path = asset is null ? null : _services.Location.FullPath(asset.OriginalRelativePath);
        if (asset is null || path is null)
        {
            ShowProblem("Can't Show Image", "This reference has no saved image.", withOpen: false);
        }
        else if (!File.Exists(path))
        {
            ShowProblem("Image Not Found", "The saved copy of this image is missing from the library.", withOpen: false);
        }
        else
        {
            try
            {
                var bitmap = new BitmapImage();
                // Very large images are shown smaller; their notes still use the original's size.
                var (width, height) = await OrientedSize(path);
                const int MaxSide = 16384;
                if (Math.Max(width, height) > MaxSide)
                {
                    if (width >= height) bitmap.DecodePixelWidth = MaxSide; else bitmap.DecodePixelHeight = MaxSide;
                    var scale = MaxSide / (double)Math.Max(width, height);
                    _scaledNotice.Message = $"Shown at {Math.Round(width * scale):N0} × {Math.Round(height * scale):N0} because the original is larger than the display can handle. The original is unchanged.";
                    _scaledNotice.IsOpen = true;
                }
                using (var stream = File.OpenRead(path)) await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                if (width == 0 || height == 0) (width, height) = (bitmap.PixelWidth, bitmap.PixelHeight);
                _imageWidth = width;
                _imageHeight = height;
                _layout = Math.Min(1, 4096.0 / Math.Max(width, height));
                _surface.Width = _image.Width = width * _layout;
                _surface.Height = _image.Height = height * _layout;
                _overlay.Width = width;
                _overlay.Height = height;
                _overlay.RenderTransform = new ScaleTransform { ScaleX = _layout, ScaleY = _layout };
                _image.Source = bitmap;
                _loaded = true;
            }
            catch (Exception)
            {
                var heic = asset.MimeType == "image/heic";
                ShowProblem("Can't Show Image",
                    heic ? "HEIC preview needs the HEIF Image Extensions from the Microsoft Store. The image is kept as it is; you can open it in its default app."
                         : "This image couldn't be shown. It may be damaged.", withOpen: true);
            }
        }

        var project = _services.WithStore(s => s.Project(entry.Reference.ProjectId));
        var title = ReferenceText.FallbackTitle(entry);
        var subtitle = string.Join(" · ", new[] { project?.Name, _loaded ? $"{_imageWidth:N0} × {_imageHeight:N0}" : null }.Where(s => s is not null));
        Title = subtitle.Length > 0 ? $"{title} — {subtitle}" : title;

        LoadNotes();
        _ = ShowInfo(entry, path);
        if (_loaded)
        {
            await Task.Yield();
            if (_services.Settings.OpenImagesAtActualSize) ActualSize(); else Fit();
            if (_focusAfterLoad is { } note)
            {
                _focusAfterLoad = null;
                await Task.Yield();
                FocusNote(note);
            }
        }
    }

    /// <summary>The image's size as shown, after its orientation is applied.</summary>
    private static async Task<(int Width, int Height)> OrientedSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

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
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _siblings.Count) return;
        _ = LoadAsync(_siblings[target], firstTime: false);
    }

    // MARK: Zoom

    private bool _fitOnResize;

    /// <summary>One image pixel per screen pixel.</summary>
    private float ActualZoom => (float)(1 / ((Content.XamlRoot?.RasterizationScale ?? 1) * _layout));

    private void Fit()
    {
        if (!_loaded || _scroller.ViewportWidth <= 0) return;
        var zoom = Math.Min(_scroller.ViewportWidth / (_imageWidth * _layout), _scroller.ViewportHeight / (_imageHeight * _layout));
        // Small images aren't enlarged past actual size.
        _scroller.ChangeView(0, 0, (float)Math.Clamp(Math.Min(zoom, ActualZoom), _scroller.MinZoomFactor, _scroller.MaxZoomFactor), disableAnimation: true);
        _fitOnResize = true;
    }

    private void ActualSize()
    {
        _fitOnResize = false;
        _scroller.ChangeView(null, null, Math.Clamp(ActualZoom, _scroller.MinZoomFactor, _scroller.MaxZoomFactor), disableAnimation: true);
    }

    private void ZoomBy(double factor)
    {
        _fitOnResize = false;
        var zoom = (float)Math.Clamp(_scroller.ZoomFactor * factor, _scroller.MinZoomFactor, _scroller.MaxZoomFactor);
        // Keep the middle of the view where it is.
        var centreX = (_scroller.HorizontalOffset + _scroller.ViewportWidth / 2) / _scroller.ZoomFactor;
        var centreY = (_scroller.VerticalOffset + _scroller.ViewportHeight / 2) / _scroller.ZoomFactor;
        ZoomTo(zoom, centreX, centreY, _scroller.ViewportWidth / 2, _scroller.ViewportHeight / 2);
    }

    /// <summary>
    /// Zooms, then puts a point of the content (in unzoomed units) at a place in the
    /// view. The zoom is applied first: an offset set together with a new zoom is
    /// limited to what the old zoom could scroll to, which lands in the wrong place.
    /// </summary>
    private void ZoomTo(float zoom, double contentX, double contentY, double viewX, double viewY)
    {
        _scroller.ChangeView(null, null, zoom, disableAnimation: true);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            _scroller.ChangeView(Math.Max(0, contentX * zoom - viewX), Math.Max(0, contentY * zoom - viewY), null, disableAnimation: true));
    }

    /// <summary>Zooms by a factor, keeping the point under the pointer where it is.</summary>
    private void ZoomAround(Windows.Foundation.Point pointer, double factor)
    {
        _fitOnResize = false;
        var zoom = (float)Math.Clamp(_scroller.ZoomFactor * factor, _scroller.MinZoomFactor, _scroller.MaxZoomFactor);
        var contentX = (_scroller.HorizontalOffset + pointer.X) / _scroller.ZoomFactor;
        var contentY = (_scroller.VerticalOffset + pointer.Y) / _scroller.ZoomFactor;
        ZoomTo(zoom, contentX, contentY, pointer.X, pointer.Y);
    }

    private void UpdateZoomText()
    {
        var percent = (int)Math.Round(_scroller.ZoomFactor / ActualZoom * 100);
        _zoomText.Text = $"{percent}%";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_zoomText, $"Zoom {percent} percent");
    }

    // MARK: Boxes

    private const double HandleSize = 9;

    /// <summary>Draws each note as a numbered outline, sized so lines stay thin at any zoom.</summary>
    private void DrawBoxes()
    {
        foreach (var element in _overlay.Children.Where(c => c != _drawing).ToList()) _overlay.Children.Remove(element);
        if (!_loaded || _showBoxes.IsChecked != true) return;
        // Screen-sized lines and badges, whatever the zoom: one image pixel is this many screen units.
        var scale = 1 / Math.Max(_scroller.ZoomFactor * _layout, 0.0001);
        var number = 0;
        foreach (var note in _notes)
        {
            number++;
            var rect = NoteGeometry.ToPixels(note, _imageWidth, _imageHeight);
            var color = NamedColors.Color(note.Color) ?? NamedColors.Color("yellow")!.Value;
            var selected = note.Id == _selected;
            var box = new Rectangle
            {
                Width = rect.Width, Height = rect.Height,
                Stroke = new SolidColorBrush(color), StrokeThickness = (selected ? 3 : 2) * scale,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(selected ? (byte)40 : (byte)0, color.R, color.G, color.B)),
            };
            Canvas.SetLeft(box, rect.X);
            Canvas.SetTop(box, rect.Y);
            _overlay.Children.Insert(0, box);

            var badge = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(color), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White), BorderThickness = new Thickness(1.5),
                Child = new TextBlock
                {
                    Text = number.ToString(), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(note.Color is "yellow" or "green" ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White),
                },
                RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale },
            };
            Canvas.SetLeft(badge, rect.X - 10 * scale);
            Canvas.SetTop(badge, rect.Y - 10 * scale);
            _overlay.Children.Add(badge);

            if (!selected) continue;
            foreach (var corner in Enum.GetValues<Corner>())
            {
                var (cx, cy) = NoteGeometry.Point(corner, rect);
                var handle = new Rectangle
                {
                    Width = HandleSize * scale, Height = HandleSize * scale,
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.White), Stroke = new SolidColorBrush(color), StrokeThickness = 1.5 * scale,
                };
                Canvas.SetLeft(handle, cx - HandleSize * scale / 2);
                Canvas.SetTop(handle, cy - HandleSize * scale / 2);
                _overlay.Children.Add(handle);
            }
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_surface,
            $"Image with {_notes.Count:N0} {(_notes.Count == 1 ? "note" : "notes")}. The notes are listed in the side panel.");
    }

    // MARK: Pointer

    private enum Drag { None, Draw, Move, Resize, Pan }

    private Drag _drag;
    private Windows.Foundation.Point _dragStart, _panStart;
    private double _panOffsetX, _panOffsetY;
    private PixelRect _dragRect;
    private Corner _dragCorner;

    private void UpdateCursor() =>
        _surface.SetCursor(InputSystemCursor.Create(_drawBox.IsChecked == true ? InputSystemCursorShape.Cross : InputSystemCursorShape.Arrow));

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_loaded || !e.GetCurrentPoint(_surface).Properties.IsLeftButtonPressed) return;
        _scroller.Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(_overlay).Position;
        var tolerance = HandleSize / Math.Max(_scroller.ZoomFactor * _layout, 0.0001);
        _dragStart = point;

        if (_drawBox.IsChecked == true)
        {
            _drag = Drag.Draw;
            _drawing.Stroke = new SolidColorBrush(NamedColors.Color("yellow")!.Value);
        }
        else if (_showBoxes.IsChecked == true && _selected is { } id && _notes.FirstOrDefault(n => n.Id == id) is { } selected
                 && NoteGeometry.CornerAt(point.X, point.Y, NoteGeometry.ToPixels(selected, _imageWidth, _imageHeight), tolerance) is { } corner)
        {
            _drag = Drag.Resize;
            _dragCorner = corner;
            _dragRect = NoteGeometry.ToPixels(selected, _imageWidth, _imageHeight);
        }
        else if (_showBoxes.IsChecked == true && _notes.LastOrDefault(n => NoteGeometry.ToPixels(n, _imageWidth, _imageHeight).Contains(point.X, point.Y)) is { } hit)
        {
            Select(hit.Id);
            _drag = Drag.Move;
            _dragRect = NoteGeometry.ToPixels(hit, _imageWidth, _imageHeight);
        }
        else
        {
            Select(null);
            _drag = Drag.Pan;
            _panStart = e.GetCurrentPoint(_scroller).Position;
            _panOffsetX = _scroller.HorizontalOffset;
            _panOffsetY = _scroller.VerticalOffset;
        }
        _surface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == Drag.None) return;
        var point = e.GetCurrentPoint(_overlay).Position;
        switch (_drag)
        {
            case Drag.Draw:
                var drawn = NoteGeometry.FromDrag(_dragStart.X, _dragStart.Y, point.X, point.Y, _imageWidth, _imageHeight);
                _drawing.StrokeThickness = 2 / Math.Max(_scroller.ZoomFactor * _layout, 0.0001);
                _drawing.Width = drawn.Width;
                _drawing.Height = drawn.Height;
                Canvas.SetLeft(_drawing, drawn.X);
                Canvas.SetTop(_drawing, drawn.Y);
                _drawing.Visibility = Visibility.Visible;
                break;
            case Drag.Move or Drag.Resize when _notes.FirstOrDefault(n => n.Id == _selected) is { } note:
                var rect = _drag == Drag.Move
                    ? NoteGeometry.Move(_dragRect, point.X - _dragStart.X, point.Y - _dragStart.Y, _imageWidth, _imageHeight)
                    : NoteGeometry.Resize(_dragRect, _dragCorner, point.X, point.Y, _imageWidth, _imageHeight);
                (note.X, note.Y, note.Width, note.Height) = NoteGeometry.ToFractions(rect, _imageWidth, _imageHeight);
                DrawBoxes();
                break;
            case Drag.Pan:
                var now = e.GetCurrentPoint(_scroller).Position;
                _scroller.ChangeView(_panOffsetX - (now.X - _panStart.X), _panOffsetY - (now.Y - _panStart.Y), null, disableAnimation: true);
                break;
        }
        e.Handled = true;
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == Drag.None) return;
        var point = e.GetCurrentPoint(_overlay).Position;
        // Save first: releasing the pointer raises PointerCaptureLost straight away, which cancels an open drag.
        EndDrag(commit: true, point);
        _surface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void EndDrag(bool commit, Windows.Foundation.Point? end = null)
    {
        var drag = _drag;
        _drag = Drag.None;
        _drawing.Visibility = Visibility.Collapsed;
        if (!commit || end is not { } point) { if (drag is Drag.Move or Drag.Resize) LoadNotes(); return; }
        switch (drag)
        {
            case Drag.Draw:
                var rect = NoteGeometry.FromDrag(_dragStart.X, _dragStart.Y, point.X, point.Y, _imageWidth, _imageHeight);
                var added = _services.WithStore(s => ImageNotes.Add(s, _entry.Reference.Id, rect, _imageWidth, _imageHeight));
                if (added is null) return; // A click, not a box.
                _showBoxes.IsChecked = true;
                Changed(added.Id, focusText: true);
                break;
            case Drag.Move or Drag.Resize when _notes.FirstOrDefault(n => n.Id == _selected) is { } note:
                var moved = NoteGeometry.ToPixels(note, _imageWidth, _imageHeight);
                _services.WithStore(s => { ImageNotes.Move(s, note, moved, _imageWidth, _imageHeight); return 0; });
                Changed(note.Id);
                break;
        }
    }

    // MARK: Notes panel

    private readonly Dictionary<string, TextBox> _noteFields = [];

    private void LoadNotes()
    {
        _notes = ImageNotes.Sorted(_services.WithStore(s => s.Markups(_entry.Reference.Id)));
        _noteFields.Clear();
        _notesList.Children.Clear();
        if (_notes.Count == 0)
        {
            _notesList.Children.Add(new TextBlock { Text = "No Notes", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            _notesList.Children.Add(new TextBlock
            {
                Text = "Choose Draw Box, then drag across a feature of the aircraft to mark it and add a note.",
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        var number = 0;
        foreach (var note in _notes)
        {
            number++;
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = $"Note {number}", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
            var delete = new Button { Content = new SymbolIcon(Symbol.Delete), Padding = new Thickness(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            Tip(delete, "Delete Note…");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, $"Delete Note {number}");
            delete.Click += async (_, _) => await DeleteNote(note);
            Grid.SetColumn(delete, 1);
            header.Children.Add(delete);

            var field = new TextBox { Text = note.Text, PlaceholderText = "Describe this feature", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, $"Note {number}");
            field.GotFocus += (_, _) => Select(note.Id, fromPanel: true);
            field.LostFocus += (_, _) => SaveText(note, field.Text);
            WindowFrames.CommitOnEnter(field, () => { SaveText(note, field.Text); _scroller.Focus(FocusState.Keyboard); });
            Tip(field, "Enter saves the note. Shift+Enter starts a new line.");
            _noteFields[note.Id] = field;

            var card = new StackPanel { Spacing = 4, Children = { header, field } };
            _notesList.Children.Add(card);
        }
        DrawBoxes();
    }

    private void Select(string? noteId, bool fromPanel = false)
    {
        _selected = noteId;
        DrawBoxes();
        if (!fromPanel && noteId is not null && _noteFields.TryGetValue(noteId, out var field)) field.StartBringIntoView();
    }

    private void SaveText(Markup note, string text)
    {
        var clean = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (note.Text == clean) return;
        _services.WithStore(s => { ImageNotes.SetText(s, note, clean); return 0; });
        _services.NotifyLibraryChanged();
    }

    /// <summary>Saves the note being typed, before switching images or closing.</summary>
    private void CommitTyping()
    {
        foreach (var note in _notes)
            if (_noteFields.TryGetValue(note.Id, out var field)) SaveText(note, field.Text);
    }

    private void Changed(string? select = null, bool focusText = false)
    {
        CommitTyping();
        LoadNotes();
        if (select is not null) Select(select);
        if (focusText && select is not null && _noteFields.TryGetValue(select, out var field))
        {
            _panelButton.IsChecked = true;
            _panel.Visibility = Visibility.Visible;
            _panelTabs.SelectedItem = _notesTab;
            field.Focus(FocusState.Programmatic);
        }
        _services.NotifyLibraryChanged();
    }

    private async Task DeleteNote(Markup note)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Delete Note?",
            Content = new TextBlock { Text = "The box and its note will be removed. The image isn't changed.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _noteFields.Remove(note.Id);
        _services.WithStore(s => { s.DeleteMarkups(new[] { note.Id }); return 0; });
        _selected = null;
        Changed();
    }

    // MARK: Info panel

    private async Task ShowInfo(ReferenceEntry entry, string? path)
    {
        _infoList.Children.Clear();
        var asset = entry.Asset;
        var image = new List<(string, string)>();
        if (asset is not null)
        {
            image.Add(("Format", asset.MimeType switch { "image/jpeg" => "JPEG", "image/png" => "PNG", "image/webp" => "WebP", "image/heic" => "HEIC", _ => asset.MimeType }));
            if (_loaded)
            {
                image.Add(("Dimensions", $"{_imageWidth:N0} × {_imageHeight:N0} pixels"));
                image.Add(("Megapixels", $"{_imageWidth * _imageHeight / 1_000_000:0.0} MP"));
            }
        }
        var camera = new List<(string, string)>();
        if (path is not null && File.Exists(path))
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                var props = await file.Properties.RetrievePropertiesAsync(new[] {
                    "System.Image.ColorSpace", "System.Image.BitDepth", "System.Image.HorizontalResolution",
                    "System.Photo.CameraManufacturer", "System.Photo.CameraModel", "System.Photo.LensModel", "System.Photo.DateTaken",
                });
                string? Value(string key) => props.TryGetValue(key, out var v) && v is not null ? Convert.ToString(v, System.Globalization.CultureInfo.CurrentCulture) : null;
                if (Value("System.Image.ColorSpace") is { } space) image.Add(("Colour", space == "1" ? "sRGB" : "Uncalibrated"));
                if (props.TryGetValue("System.Image.BitDepth", out var depth) && depth is uint bits && bits > 0)
                    image.Add(("Bit Depth", $"{(bits >= 24 ? bits / (bits % 32 == 0 ? 4u : 3u) : bits)} bits per channel"));
                if (props.TryGetValue("System.Image.HorizontalResolution", out var dpi) && dpi is double resolution && resolution > 0)
                    image.Add(("Resolution", $"{Math.Round(resolution)} dpi"));
                var make = Value("System.Photo.CameraManufacturer");
                var model = Value("System.Photo.CameraModel");
                if (make is not null || model is not null)
                    camera.Add(("Camera", model is not null && make is not null && model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : string.Join(" ", new[] { make, model }.Where(s => s is not null))));
                if (Value("System.Photo.LensModel") is { } lens) camera.Add(("Lens", lens));
                if (props.TryGetValue("System.Photo.DateTaken", out var taken) && taken is DateTimeOffset date) camera.Add(("Taken", Words.DateAndTime(date)));
            }
            catch (Exception)
            {
                // Missing metadata just leaves rows out.
            }
            if (asset is not null) image.Add(("Transparency", asset.MimeType is "image/png" or "image/webp" ? "Possible" : "No"));
        }
        Section("Image", image);
        Section("Camera", camera);

        var source = new List<(string, string)> { ("Added", Words.DateAndTime(entry.Reference.CapturedAt)) };
        if (asset?.OriginalFilename is { } name) source.Add(("File Name", name));
        if (asset is not null) source.Add(("Size", Shell.FormatBytes(asset.ByteCount)));
        Section("Source", source);
        if (Uri.TryCreate(entry.Reference.SourcePageUrl ?? entry.Reference.OriginalResourceUrl, UriKind.Absolute, out var page))
            _infoList.Children.Add(new HyperlinkButton { Content = $"Page: {page.Host}", NavigateUri = page, Padding = new Thickness(0) });
        if (entry.Tags.Count > 0) Section("Tags", [("", string.Join(", ", entry.Tags.Select(t => t.Name)))]);
    }

    private void Section(string title, List<(string Label, string Value)> rows)
    {
        if (rows.Count == 0) return;
        var section = new StackPanel { Spacing = 4 };
        section.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        foreach (var (label, value) in rows)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(label.Length == 0 ? 0 : 100) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            section.Children.Add(row);
        }
        _infoList.Children.Add(section);
    }
}

/// <summary>A canvas that can set its own pointer cursor, which WinUI only allows from inside an element.</summary>
internal sealed partial class CursorCanvas : Canvas
{
    public void SetCursor(InputCursor cursor) => ProtectedCursor = cursor;
}
