using BenchBook.App.Services;
using BenchBook.Core.Capture;
using BenchBook.Core.Markups;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace BenchBook.App.Viewers;

/// <summary>
/// Two to four images side by side (screens.md, "Compare"): one row for two or
/// three, a 2 × 2 grid for four, and each zooms and pans on its own.
/// </summary>
internal sealed partial class CompareWindow : Window
{
    public const int MaxImages = 4;

    public static void Show(AppServices services, IReadOnlyList<ReferenceEntry> images) =>
        new CompareWindow(services, images.Take(MaxImages).ToList()).Activate();

    private readonly AppServices _services;
    private readonly List<ImagePane> _panes = [];
    private readonly Grid _grid = new() { ColumnSpacing = 6, RowSpacing = 6, Padding = new Thickness(6), AllowDrop = true };
    private readonly AppBarToggleButton _gridLayout = new() { Label = "Grid", Icon = new FontIcon { Glyph = "\uF0E2" } };
    private readonly AppBarToggleButton _showBoxes = new() { Label = "Show Boxes", Icon = new FontIcon { Glyph = "\uE7B3" } };
    private readonly TextBlock _dropHint = new()
    {
        Text = "Drop images here to compare them too.", Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
    };
    private ImagePane? _selected;
    private bool _gridChosen;

    private CompareWindow(AppServices services, List<ReferenceEntry> images)
    {
        _services = services;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon("Assets/Square44x44Logo.png");
        WindowFrames.Restore(this, services, "compare", 1400, 820);
        Closed += (_, _) => WindowFrames.Save(this, services, "compare");

        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(_gridLayout, "Side by side, or in a grid");
        _gridLayout.Click += (_, _) => { _gridChosen = true; Layout(); };
        _showBoxes.IsChecked = services.Settings.ShowNoteBoxes;
        ToolTipService.SetToolTip(_showBoxes, "Show or hide boxes on every image (Ctrl+B)");
        _showBoxes.Click += (_, _) => { foreach (var pane in _panes) pane.ShowBoxes = _showBoxes.IsChecked == true; };
        var zoomOut = new AppBarButton { Label = "Zoom Out", Icon = new SymbolIcon(Symbol.ZoomOut) };
        zoomOut.Click += (_, _) => _selected?.ZoomBy(1 / 1.25);
        var zoomIn = new AppBarButton { Label = "Zoom In", Icon = new SymbolIcon(Symbol.ZoomIn) };
        zoomIn.Click += (_, _) => _selected?.ZoomBy(1.25);
        var fit = new AppBarButton { Label = "Zoom to Fit", Icon = new FontIcon { Glyph = "\uE740" } };
        fit.Click += (_, _) => _selected?.Fit();
        ToolTipService.SetToolTip(zoomOut, "Zoom out the selected image (Ctrl+−)");
        ToolTipService.SetToolTip(zoomIn, "Zoom in the selected image (Ctrl+=)");
        ToolTipService.SetToolTip(fit, "Fit the selected image (Ctrl+9)");
        foreach (var command in new ICommandBarElement[] { _gridLayout, _showBoxes, new AppBarSeparator(), zoomOut, zoomIn, fit, new AppBarElementContainer { Content = _dropHint } })
            commands.PrimaryCommands.Add(command);

        // References dragged in from the gallery join the comparison, up to four.
        _grid.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(AppServices.ReferencesDragFormat) && _panes.Count < MaxImages) e.AcceptedOperation = DataPackageOperation.Copy;
        };
        _grid.Drop += async (_, e) =>
        {
            if (await e.DataView.GetDataAsync(AppServices.ReferencesDragFormat) is not string ids) return;
            var entries = _services.WithStore(s => s.ReferenceEntries(ids.Split(',')));
            foreach (var entry in entries.Where(x => x.Reference.Kind == ReferenceKind.Image && _panes.All(p => p.Entry.Reference.Id != x.Reference.Id)).Take(MaxImages - _panes.Count))
                AddPane(entry);
            Layout();
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(commands);
        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);
        void Key(Windows.System.VirtualKey key, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = Windows.System.VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, args) => { args.Handled = true; action(); };
            root.KeyboardAccelerators.Add(accelerator);
        }
        Key(Windows.System.VirtualKey.B, () => { _showBoxes.IsChecked = _showBoxes.IsChecked != true; foreach (var p in _panes) p.ShowBoxes = _showBoxes.IsChecked == true; });
        Key((Windows.System.VirtualKey)187, () => _selected?.ZoomBy(1.25));
        Key((Windows.System.VirtualKey)189, () => _selected?.ZoomBy(1 / 1.25));
        Key(Windows.System.VirtualKey.Number9, () => _selected?.Fit());
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Content = root;

        foreach (var entry in images) AddPane(entry);
        Layout();
    }

    private void AddPane(ReferenceEntry entry)
    {
        var pane = new ImagePane(_services, entry) { ShowBoxes = _showBoxes.IsChecked == true };
        pane.Selected += (_, _) => Select(pane);
        pane.Removed += (_, _) =>
        {
            _panes.Remove(pane);
            if (_selected == pane) _selected = null;
            if (_panes.Count == 0) Close(); else Layout();
        };
        pane.ShowOnly += (_, _) => ImageViewerWindow.Show(_services, pane.Entry);
        _panes.Add(pane);
        if (_selected is null) Select(pane);
    }

    private void Select(ImagePane pane)
    {
        _selected = pane;
        foreach (var p in _panes) p.IsSelected = p == pane;
    }

    /// <summary>One row for two or three; a 2 × 2 grid for four, unless the other was chosen.</summary>
    private void Layout()
    {
        var grid = _gridChosen ? _gridLayout.IsChecked == true : _panes.Count == 4;
        _gridLayout.IsChecked = grid;
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        var columns = grid ? Math.Min(2, _panes.Count) : _panes.Count;
        var rows = (int)Math.Ceiling(_panes.Count / (double)Math.Max(1, columns));
        for (var c = 0; c < columns; c++) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows; r++) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < _panes.Count; i++)
        {
            Grid.SetColumn(_panes[i], i % columns);
            Grid.SetRow(_panes[i], i / columns);
            _grid.Children.Add(_panes[i]);
        }
        Title = $"Comparing {_panes.Count:N0} images";
        _dropHint.Visibility = _panes.Count < MaxImages ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>One image in Compare: its own zoom and pan, its boxes, and Previous, Next and Remove.</summary>
internal sealed partial class ImagePane : UserControl
{
    private readonly AppServices _services;
    private readonly ScrollViewer _scroller = new()
    {
        ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.02f, MaxZoomFactor = 16f,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled,
    };
    private readonly Grid _surface = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new();
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _frame = new() { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6) };
    private List<Markup> _notes = [];
    private double _width, _height;
    private bool _loaded, _showBoxes = true, _isSelected;
    private Windows.Foundation.Point _panStart;
    private double _panX, _panY;
    private bool _panning;

    public ImagePane(AppServices services, ReferenceEntry entry)
    {
        _services = services;
        Entry = entry;
        _surface.Children.Add(_image);
        _surface.Children.Add(_overlay);
        _scroller.Content = _surface;
        _scroller.ViewChanged += (_, _) => DrawBoxes();
        _scroller.SizeChanged += (_, _) => { if (_loaded && _fitted) Fit(); };

        _surface.PointerPressed += (_, e) =>
        {
            Selected?.Invoke(this, EventArgs.Empty);
            if (!e.GetCurrentPoint(_surface).Properties.IsLeftButtonPressed) return;
            _panning = true;
            _panStart = e.GetCurrentPoint(_scroller).Position;
            _panX = _scroller.HorizontalOffset;
            _panY = _scroller.VerticalOffset;
            _surface.CapturePointer(e.Pointer);
        };
        _surface.PointerMoved += (_, e) =>
        {
            if (!_panning) return;
            var now = e.GetCurrentPoint(_scroller).Position;
            _fitted = false;
            _scroller.ChangeView(_panX - (now.X - _panStart.X), _panY - (now.Y - _panStart.Y), null, disableAnimation: true);
        };
        _surface.PointerReleased += (_, e) => { _panning = false; _surface.ReleasePointerCapture(e.Pointer); };
        _surface.DoubleTapped += (_, _) => ShowOnly?.Invoke(this, EventArgs.Empty);

        var previous = Small("\uE76B", "Previous image in the project");
        previous.Click += (_, _) => Step(-1);
        var next = Small("\uE76C", "Next image in the project");
        next.Click += (_, _) => Step(1);
        var only = Small("\uE740", "Show only this image");
        only.Click += (_, _) => ShowOnly?.Invoke(this, EventArgs.Empty);
        var remove = Small("\uE711", "Remove from Comparison");
        remove.Click += (_, _) => Removed?.Invoke(this, EventArgs.Empty);
        var bar = new Grid { Padding = new Thickness(8, 4, 4, 4), ColumnSpacing = 2 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var _ in new[] { previous, next, only, remove }) bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_title);
        var buttons = new[] { previous, next, only, remove };
        for (var i = 0; i < buttons.Length; i++) { Grid.SetColumn(buttons[i], i + 1); bar.Children.Add(buttons[i]); }

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(bar);
        Grid.SetRow(_scroller, 1);
        layout.Children.Add(_scroller);
        _frame.Child = layout;
        _frame.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        Content = _frame;
        IsSelected = false;
        _ = Load(entry);
    }

    public ReferenceEntry Entry { get; private set; }

    public event EventHandler? Selected;
    public event EventHandler? Removed;
    public event EventHandler? ShowOnly;

    public bool ShowBoxes
    {
        get => _showBoxes;
        set { _showBoxes = value; DrawBoxes(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            _frame.BorderBrush = value
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private static Button Small(string glyph, string tip)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    private async Task Load(ReferenceEntry entry)
    {
        Entry = entry;
        _loaded = false;
        _image.Source = null;
        _title.Text = ReferenceText.FallbackTitle(entry);
        _notes = ImageNotes.Sorted(_services.WithStore(s => s.Markups(entry.Reference.Id)));
        if (entry.Asset is not { } asset || _services.Location.FullPath(asset.OriginalRelativePath) is not { } path || !File.Exists(path)) return;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            (_width, _height) = (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            stream.Position = 0;
            var bitmap = new BitmapImage();
            if (Math.Max(_width, _height) > 16384) { if (_width >= _height) bitmap.DecodePixelWidth = 16384; else bitmap.DecodePixelHeight = 16384; }
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            _surface.Width = _overlay.Width = _width;
            _surface.Height = _overlay.Height = _height;
            _image.Source = bitmap;
            _loaded = true;
            await Task.Yield();
            Fit();
        }
        catch (Exception)
        {
            _title.Text = $"{ReferenceText.FallbackTitle(entry)} (can't be shown)";
        }
    }

    private void Step(int direction)
    {
        var images = _services.WithStore(s => s.ProjectReferences(Entry.Reference.ProjectId)).Where(e => e.Reference.Kind == ReferenceKind.Image).ToList();
        var index = images.FindIndex(e => Ids.Comparer.Equals(e.Reference.Id, Entry.Reference.Id));
        if (index < 0 || index + direction < 0 || index + direction >= images.Count) return;
        _ = Load(images[index + direction]);
    }

    private bool _fitted = true;

    public void Fit()
    {
        if (!_loaded || _scroller.ViewportWidth <= 0) return;
        var zoom = Math.Min(_scroller.ViewportWidth / _width, _scroller.ViewportHeight / _height);
        _scroller.ChangeView(0, 0, (float)Math.Clamp(zoom, _scroller.MinZoomFactor, _scroller.MaxZoomFactor), disableAnimation: true);
        _fitted = true;
    }

    public void ZoomBy(double factor)
    {
        _fitted = false;
        var zoom = (float)Math.Clamp(_scroller.ZoomFactor * factor, _scroller.MinZoomFactor, _scroller.MaxZoomFactor);
        var cx = (_scroller.HorizontalOffset + _scroller.ViewportWidth / 2) / _scroller.ZoomFactor;
        var cy = (_scroller.VerticalOffset + _scroller.ViewportHeight / 2) / _scroller.ZoomFactor;
        _scroller.ChangeView(cx * zoom - _scroller.ViewportWidth / 2, cy * zoom - _scroller.ViewportHeight / 2, zoom, disableAnimation: true);
    }

    private void DrawBoxes()
    {
        _overlay.Children.Clear();
        if (!_loaded || !_showBoxes) return;
        var scale = 1 / Math.Max(_scroller.ZoomFactor, 0.01);
        var number = 0;
        foreach (var note in _notes)
        {
            number++;
            var rect = NoteGeometry.ToPixels(note, _width, _height);
            var color = NamedColors.Color(note.Color) ?? NamedColors.Color("yellow")!.Value;
            var box = new Rectangle { Width = rect.Width, Height = rect.Height, Stroke = new SolidColorBrush(color), StrokeThickness = 2 * scale };
            Canvas.SetLeft(box, rect.X);
            Canvas.SetTop(box, rect.Y);
            _overlay.Children.Add(box);
            var badge = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White), BorderThickness = new Thickness(1.5),
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
        }
    }
}
