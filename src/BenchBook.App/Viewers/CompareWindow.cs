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
        WindowFrames.Present(new CompareWindow(services, images.Take(MaxImages).ToList()));

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
    /// <summary>The one image shown on its own, if "Show only this image" is on.</summary>
    private ImagePane? _expanded;
    private readonly AppBarToggleButton _drawBox = new() { Label = "Draw Box", Icon = new FontIcon { Glyph = "\uE9A6" } };
    private readonly AppBarToggleButton _panelButton = new() { Label = "Notes and Info", IsChecked = true, Icon = new FontIcon { Glyph = "\uE8A0" } };
    private readonly Grid _panel = new() { Width = 300, Padding = new Thickness(12) };
    private readonly SelectorBar _panelTabs = new();
    private readonly SelectorBarItem _notesTab = new() { Text = "Notes", IsSelected = true };
    private readonly SelectorBarItem _infoTab = new() { Text = "Info" };
    private readonly TextBlock _panelTitle = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _notesList = new() { Spacing = 12 };
    private readonly StackPanel _infoList = new() { Spacing = 6 };
    private readonly ScrollViewer _notesScroller = new();
    private readonly ScrollViewer _infoScroller = new() { Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, TextBox> _noteFields = [];

    private CompareWindow(AppServices services, List<ReferenceEntry> images)
    {
        _services = services;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(App.IconPath);
        WindowFrames.Restore(this, services, "compare", 1400, 820);
        Closed += (_, _) => { CommitTyping(); WindowFrames.Save(this, services, "compare"); };

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
        ToolTipService.SetToolTip(_drawBox, "Draw a box on the selected image, then describe it (Ctrl+D)");
        _drawBox.Click += (_, _) => { foreach (var p in _panes) p.DrawMode = p == _selected && _drawBox.IsChecked == true; };
        ToolTipService.SetToolTip(_panelButton, "Show or hide Notes and Info (Ctrl+Alt+I)");
        _panelButton.Click += (_, _) => _panel.Visibility = _panelButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        foreach (var command in new ICommandBarElement[] { _gridLayout, _showBoxes, _drawBox, new AppBarSeparator(), zoomOut, zoomIn, fit, new AppBarSeparator(), _panelButton, new AppBarElementContainer { Content = _dropHint } })
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
        _panelTitle.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        _panel.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            _panel.RowDefinitions.Add(new RowDefinition { Height = height });
        _panel.Children.Add(_panelTitle);
        Grid.SetRow(_panelTabs, 1);
        _panel.Children.Add(_panelTabs);
        Grid.SetRow(_notesScroller, 2);
        Grid.SetRow(_infoScroller, 2);
        _notesScroller.Margin = _infoScroller.Margin = new Thickness(0, 12, 0, 0);
        _panel.Children.Add(_notesScroller);
        _panel.Children.Add(_infoScroller);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(_grid);
        Grid.SetColumn(_panel, 1);
        body.Children.Add(_panel);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
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
        Key(Windows.System.VirtualKey.D, () =>
        {
            _drawBox.IsChecked = _drawBox.IsChecked != true;
            foreach (var p in _panes) p.DrawMode = p == _selected && _drawBox.IsChecked == true;
        });
        var panelKey = new KeyboardAccelerator { Key = Windows.System.VirtualKey.I, Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Menu };
        panelKey.Invoked += (_, args) =>
        {
            args.Handled = true;
            _panelButton.IsChecked = _panelButton.IsChecked != true;
            _panel.Visibility = _panelButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        };
        root.KeyboardAccelerators.Add(panelKey);
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
            if (_expanded == pane) _expanded = null;
            if (_panes.Count == 0) Close(); else Layout();
        };
        pane.ShowOnly += (_, _) =>
        {
            // Shows just this image, or all of them again.
            _expanded = _expanded == pane ? null : pane;
            foreach (var p in _panes) p.IsExpanded = p == _expanded;
            Select(pane);
            Layout();
        };
        pane.NoteAdded += (_, id) =>
        {
            Select(pane);
            ShowNotes(focus: id);
            _services.NotifyLibraryChanged();
        };
        pane.NoteChosen += (_, id) =>
        {
            if (_selected != pane) Select(pane);
            if (_noteFields.TryGetValue(id, out var field)) field.StartBringIntoView();
        };
        pane.ImageChanged += (_, _) => { if (_selected == pane) ShowNotes(); };
        _panes.Add(pane);
        if (_selected is null) Select(pane);
    }

    private void Select(ImagePane pane)
    {
        if (_selected == pane) return;
        CommitTyping();
        _selected = pane;
        foreach (var p in _panes)
        {
            p.IsSelected = p == pane;
            p.DrawMode = p == pane && _drawBox.IsChecked == true;
        }
        ShowNotes();
    }

    // MARK: Notes and Info, for the selected image

    private void ShowNotes(string? focus = null)
    {
        CommitTyping();
        _noteFields.Clear();
        _notesList.Children.Clear();
        _infoList.Children.Clear();
        if (_selected is not { } pane) { _panelTitle.Text = ""; return; }
        _panelTitle.Text = ReferenceText.FallbackTitle(pane.Entry);

        var notes = pane.Notes;
        if (notes.Count == 0)
        {
            _notesList.Children.Add(new TextBlock { Text = "No Notes", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            _notesList.Children.Add(new TextBlock
            {
                Text = "Choose Draw Box, then drag across a feature of the aircraft to mark it and add a note.",
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        var number = 0;
        foreach (var note in notes)
        {
            number++;
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = $"Note {number}", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
            var delete = new Button { Content = new SymbolIcon(Symbol.Delete), Padding = new Thickness(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(delete, "Delete Note…");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, $"Delete Note {number}");
            delete.Click += async (_, _) => await DeleteNote(pane, note);
            Grid.SetColumn(delete, 1);
            header.Children.Add(delete);

            var field = new TextBox { Text = note.Text, PlaceholderText = "Describe this feature", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, $"Note {number}");
            ToolTipService.SetToolTip(field, "Enter saves the note. Shift+Enter starts a new line.");
            field.GotFocus += (_, _) => pane.SelectNote(note.Id);
            field.LostFocus += (_, _) => SaveText(pane, note, field.Text);
            WindowFrames.CommitOnEnter(field, () => { SaveText(pane, note, field.Text); _grid.Focus(FocusState.Keyboard); });
            _noteFields[note.Id] = field;
            _notesList.Children.Add(new StackPanel { Spacing = 4, Children = { header, field } });
        }
        if (focus is not null && _noteFields.TryGetValue(focus, out var newField))
        {
            _panelButton.IsChecked = true;
            _panel.Visibility = Visibility.Visible;
            _panelTabs.SelectedItem = _notesTab;
            newField.Focus(FocusState.Programmatic);
        }

        // Info: what the Info tab of the image viewer shows first.
        var asset = pane.Entry.Asset;
        var (width, height) = pane.ImageSize;
        void Row(string label, string value)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            _infoList.Children.Add(row);
        }
        if (asset is not null) Row("Format", asset.MimeType switch { "image/jpeg" => "JPEG", "image/png" => "PNG", "image/webp" => "WebP", "image/heic" => "HEIC", _ => asset.MimeType });
        if (width > 0)
        {
            Row("Dimensions", $"{width:N0} × {height:N0} pixels");
            Row("Megapixels", $"{width * height / 1_000_000:0.0} MP");
        }
        Row("Added", Words.DateAndTime(pane.Entry.Reference.CapturedAt));
        if (asset?.OriginalFilename is { } name) Row("File Name", name);
        if (asset is not null) Row("Size", Shell.FormatBytes(asset.ByteCount));
        if (pane.Entry.Tags.Count > 0) Row("Tags", string.Join(", ", pane.Entry.Tags.Select(t => t.Name)));
    }

    private void SaveText(ImagePane pane, Markup note, string text)
    {
        var clean = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (note.Text == clean) return;
        _services.WithStore(st => { ImageNotes.SetText(st, note, clean); return 0; });
        _services.NotifyLibraryChanged();
    }

    private void CommitTyping()
    {
        if (_selected is not { } pane) return;
        foreach (var note in pane.Notes)
            if (_noteFields.TryGetValue(note.Id, out var field)) SaveText(pane, note, field.Text);
    }

    private async Task DeleteNote(ImagePane pane, Markup note)
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
        _services.WithStore(st => { st.DeleteMarkups(new[] { note.Id }); return 0; });
        pane.Reload();
        ShowNotes();
        _services.NotifyLibraryChanged();
    }

    /// <summary>One row for two or three; a 2 × 2 grid for four, unless the other was chosen.</summary>
    private void Layout()
    {
        var grid = _gridChosen ? _gridLayout.IsChecked == true : _panes.Count == 4;
        _gridLayout.IsChecked = grid;
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        if (_expanded is { } only)
        {
            _grid.Children.Add(only);
            Grid.SetColumn(only, 0);
            Grid.SetRow(only, 0);
            Title = $"Comparing {_panes.Count:N0} images";
            _dropHint.Visibility = Visibility.Collapsed;
            only.Fit();
            return;
        }
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
        ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.1f, MaxZoomFactor = 16f,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled,
    };
    private readonly Canvas _surface = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new();
    /// <summary>Layout units per image pixel; large images are laid out smaller, so fitting stays within WinUI's zoom range.</summary>
    private double _layout = 1;
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
            if (!_loaded || !e.GetCurrentPoint(_surface).Properties.IsLeftButtonPressed) return;
            var point = e.GetCurrentPoint(_overlay).Position;
            if (DrawMode)
            {
                _drawStart = point;
                _drawing = true;
            }
            else if (_showBoxes && _notes.LastOrDefault(n => NoteGeometry.ToPixels(n, _width, _height).Contains(point.X, point.Y)) is { } hit)
            {
                SelectNote(hit.Id);
                NoteChosen?.Invoke(this, hit.Id);
            }
            else
            {
                _panning = true;
                _panStart = e.GetCurrentPoint(_scroller).Position;
                _panX = _scroller.HorizontalOffset;
                _panY = _scroller.VerticalOffset;
            }
            _surface.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        _surface.PointerMoved += (_, e) =>
        {
            if (_drawing)
            {
                var point = e.GetCurrentPoint(_overlay).Position;
                var rect = NoteGeometry.FromDrag(_drawStart.X, _drawStart.Y, point.X, point.Y, _width, _height);
                DrawBoxes(rect);
                return;
            }
            if (!_panning) return;
            var now = e.GetCurrentPoint(_scroller).Position;
            _fitted = false;
            _scroller.ChangeView(_panX - (now.X - _panStart.X), _panY - (now.Y - _panStart.Y), null, disableAnimation: true);
        };
        _surface.PointerReleased += (_, e) =>
        {
            var point = e.GetCurrentPoint(_overlay).Position;
            var drawing = _drawing;
            // Finish first: releasing the pointer raises PointerCaptureLost straight away.
            _drawing = false;
            _panning = false;
            _surface.ReleasePointerCapture(e.Pointer);
            if (!drawing) return;
            var rect = NoteGeometry.FromDrag(_drawStart.X, _drawStart.Y, point.X, point.Y, _width, _height);
            var added = _services.WithStore(st => ImageNotes.Add(st, Entry.Reference.Id, rect, _width, _height));
            if (added is null) { DrawBoxes(); return; } // A click, not a box.
            Reload(added.Id);
            NoteAdded?.Invoke(this, added.Id);
        };
        _surface.PointerCaptureLost += (_, _) =>
        {
            if (!_drawing) return;
            _drawing = false;
            DrawBoxes();
        };
        _surface.DoubleTapped += (_, _) => ShowOnly?.Invoke(this, EventArgs.Empty);
        _surface.PointerWheelChanged += (_, e) =>
        {
            if (!_loaded) return;
            Selected?.Invoke(this, EventArgs.Empty);
            var point = e.GetCurrentPoint(_scroller);
            _fitted = false;
            var zoom = (float)Math.Clamp(_scroller.ZoomFactor * Math.Pow(1.15, point.Properties.MouseWheelDelta / 120.0), _scroller.MinZoomFactor, _scroller.MaxZoomFactor);
            var contentX = (_scroller.HorizontalOffset + point.Position.X) / _scroller.ZoomFactor;
            var contentY = (_scroller.VerticalOffset + point.Position.Y) / _scroller.ZoomFactor;
            ZoomTo(zoom, contentX, contentY, point.Position.X, point.Position.Y);
            e.Handled = true;
        };

        var previous = Small("\uE76B", "Previous image in the project");
        previous.Click += (_, _) => Step(-1);
        var next = Small("\uE76C", "Next image in the project");
        next.Click += (_, _) => Step(1);
        _only = Small("\uE740", "Show only this image (or double-click it)");
        var only = _only;
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

    private readonly Button _only;

    /// <summary>Whether this image is shown on its own; its button then shows all the images again.</summary>
    public bool IsExpanded
    {
        set
        {
            var tip = value ? "Show all the images again (or double-click the image)" : "Show only this image (or double-click it)";
            ToolTipService.SetToolTip(_only, tip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_only, tip);
            ((FontIcon)_only.Content).Glyph = value ? "\uE73F" : "\uE740";
        }
    }

    /// <summary>The image's size as shown, in pixels; zero until it has loaded.</summary>
    public (double Width, double Height) ImageSize => _loaded ? (_width, _height) : (0, 0);

    public event EventHandler? Selected;
    /// <summary>A box was drawn on this image; its note's id.</summary>
    public event EventHandler<string>? NoteAdded;
    /// <summary>A box was clicked; its note's id.</summary>
    public event EventHandler<string>? NoteChosen;

    /// <summary>When on, dragging on the image draws a box.</summary>
    public bool DrawMode { get; set; }

    public IReadOnlyList<Markup> Notes => _notes;

    private bool _drawing;
    private Windows.Foundation.Point _drawStart;
    private string? _selectedNote;

    public void SelectNote(string? id)
    {
        _selectedNote = id;
        DrawBoxes();
    }

    /// <summary>Reads the image's notes again, e.g. after one is edited in the panel.</summary>
    public void Reload(string? select = null)
    {
        _notes = ImageNotes.Sorted(_services.WithStore(st => st.Markups(Entry.Reference.Id)));
        if (select is not null) _selectedNote = select;
        DrawBoxes();
    }

    /// <summary>Raised when the pane shows a different image, so the panel can follow.</summary>
    public event EventHandler? ImageChanged;
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
        _selectedNote = null;
        _notes = ImageNotes.Sorted(_services.WithStore(s => s.Markups(entry.Reference.Id)));
        ImageChanged?.Invoke(this, EventArgs.Empty);
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
            _layout = Math.Min(1, 2048.0 / Math.Max(_width, _height));
            _surface.Width = _image.Width = _width * _layout;
            _surface.Height = _image.Height = _height * _layout;
            _overlay.Width = _width;
            _overlay.Height = _height;
            _overlay.RenderTransform = new ScaleTransform { ScaleX = _layout, ScaleY = _layout };
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
        var zoom = Math.Min(_scroller.ViewportWidth / (_width * _layout), _scroller.ViewportHeight / (_height * _layout));
        _scroller.ChangeView(0, 0, (float)Math.Clamp(zoom, _scroller.MinZoomFactor, _scroller.MaxZoomFactor), disableAnimation: true);
        _fitted = true;
    }

    public void ZoomBy(double factor)
    {
        _fitted = false;
        var zoom = (float)Math.Clamp(_scroller.ZoomFactor * factor, _scroller.MinZoomFactor, _scroller.MaxZoomFactor);
        var cx = (_scroller.HorizontalOffset + _scroller.ViewportWidth / 2) / _scroller.ZoomFactor;
        var cy = (_scroller.VerticalOffset + _scroller.ViewportHeight / 2) / _scroller.ZoomFactor;
        ZoomTo(zoom, cx, cy, _scroller.ViewportWidth / 2, _scroller.ViewportHeight / 2);
    }

    /// <summary>Zooms first, then scrolls: an offset set together with a new zoom is limited by the old zoom.</summary>
    private void ZoomTo(float zoom, double contentX, double contentY, double viewX, double viewY)
    {
        _scroller.ChangeView(null, null, zoom, disableAnimation: true);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            _scroller.ChangeView(Math.Max(0, contentX * zoom - viewX), Math.Max(0, contentY * zoom - viewY), null, disableAnimation: true));
    }

    private void DrawBoxes(PixelRect? drawing = null)
    {
        _overlay.Children.Clear();
        if (!_loaded) return;
        if (drawing is { } preview)
        {
            var thin = 2 / Math.Max(_scroller.ZoomFactor * _layout, 0.0001);
            var outline = new Rectangle
            {
                Width = preview.Width, Height = preview.Height, StrokeThickness = thin, StrokeDashArray = [4, 2],
                Stroke = new SolidColorBrush(NamedColors.Color("yellow")!.Value),
            };
            Canvas.SetLeft(outline, preview.X);
            Canvas.SetTop(outline, preview.Y);
            _overlay.Children.Add(outline);
        }
        if (!_showBoxes) return;
        var scale = 1 / Math.Max(_scroller.ZoomFactor * _layout, 0.0001);
        var number = 0;
        foreach (var note in _notes)
        {
            number++;
            var rect = NoteGeometry.ToPixels(note, _width, _height);
            var color = NamedColors.Color(note.Color) ?? NamedColors.Color("yellow")!.Value;
            var chosen = note.Id == _selectedNote;
            var box = new Rectangle
            {
                Width = rect.Width, Height = rect.Height, Stroke = new SolidColorBrush(color), StrokeThickness = (chosen ? 3 : 2) * scale,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(chosen ? (byte)40 : (byte)0, color.R, color.G, color.B)),
            };
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
