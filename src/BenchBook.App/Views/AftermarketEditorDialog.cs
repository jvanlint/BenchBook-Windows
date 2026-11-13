using BenchBook.App.Services;
using BenchBook.Core.Capture;
using BenchBook.Core.Links;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace BenchBook.App.Views;

/// <summary>"New Part" or "Edit Part" (screens.md, "Aftermarket editor").</summary>
internal sealed class AftermarketEditorDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly Project _project;
    private readonly AftermarketItem? _item;
    private readonly TextBox _name = new() { Header = "Name", PlaceholderText = "e.g. 50th Anniversary 2015" };
    private readonly TextBox _manufacturer = new() { Header = "Manufacturer", PlaceholderText = "e.g. Galaxy Models" };
    private readonly TextBox _number = new() { Header = "Product Number", PlaceholderText = "Optional" };
    private readonly ComboBox _type = new() { Header = "Type", MinWidth = 200 };
    private readonly ComboBox _status = new() { Header = "Status", MinWidth = 200 };
    private readonly TextBox _scale = new() { Header = "Scale or Fits", PlaceholderText = "e.g. 1:48, Tamiya F-16C" };
    private readonly TextBox _url = new() { Header = "Product Page", PlaceholderText = "https://…" };
    private readonly TextBlock _urlError = new() { Text = "Enter a web address, such as hannants.co.uk/product/…", Visibility = Visibility.Collapsed };
    private readonly TextBox _notes = new() { Header = "Notes", PlaceholderText = "Optional", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
    private readonly Image _picture = new() { Stretch = Stretch.Uniform, MaxHeight = 140 };
    private readonly TextBlock _dropHint = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Button _fromPage = new() { Content = "Use Image from Product Page" };
    private readonly Button _removeImage = new() { Content = "Remove Image" };
    private readonly ProgressRing _fetching = new() { Width = 16, Height = 16, IsActive = false };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "BenchBook Aftermarket", Guid.NewGuid().ToString("N"));

    // The picture isn't stored until Save: a file waiting to be added, or a request to remove the current one.
    private string? _pendingImage;
    private string? _pendingName;
    private bool _removePicture;

    public AftermarketEditorDialog(AppServices services, Project project, AftermarketItem? item)
    {
        _services = services;
        _project = project;
        _item = item;
        Title = item is null ? "New Part" : "Edit Part";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _urlError.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        _urlError.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _error.Foreground = _urlError.Foreground;
        _dropHint.Foreground = secondary;

        foreach (var type in AftermarketKinds.Types) _type.Items.Add(AftermarketKinds.TypeName(type));
        foreach (var status in AftermarketKinds.Statuses) _status.Items.Add(AftermarketKinds.StatusName(status));

        var draft = item is null ? AftermarketDraft.For(project) : AftermarketDraft.From(item);
        _name.Text = draft.ProductName;
        _manufacturer.Text = draft.Manufacturer;
        _number.Text = draft.ProductNumber;
        _scale.Text = draft.ScaleCompatibility;
        _url.Text = draft.ProductUrl;
        _notes.Text = draft.Notes;
        // A type or status this version doesn't know is kept unless it's changed.
        _unknownType = AftermarketKinds.Types.Contains(draft.Type) ? null : draft.Type;
        _unknownStatus = AftermarketKinds.Statuses.Contains(draft.Status) ? null : draft.Status;
        _type.SelectedIndex = Math.Max(0, AftermarketKinds.Types.ToList().IndexOf(draft.Type is var t && _unknownType is null ? t : "other"));
        _status.SelectedIndex = Math.Max(0, AftermarketKinds.Statuses.ToList().IndexOf(draft.Status));
        _type.SelectionChanged += (_, _) => _unknownType = null;
        _status.SelectionChanged += (_, _) => _unknownStatus = null;

        // Picture: drop an image, choose one, or take the product page's preview image.
        var dropArea = new Border
        {
            AllowDrop = true, MinHeight = 120, CornerRadius = new CornerRadius(6), Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new Grid { Children = { _picture, _dropHint } },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dropArea, "Picture of the part");
        dropArea.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Bitmap))
                e.AcceptedOperation = DataPackageOperation.Copy;
        };
        dropArea.Drop += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try { await DropImage(e.DataView); }
            finally { deferral.Complete(); }
        };
        var choose = new Button { Content = "Choose Image…" };
        choose.Click += async (_, _) => await ChooseImage();
        _fromPage.Click += async (_, _) => await ImageFromPage();
        _removeImage.Click += (_, _) =>
        {
            _pendingImage = null;
            _removePicture = true;
            ShowPicture(null);
        };
        _url.TextChanged += (_, _) => UpdateState();
        _name.TextChanged += (_, _) => UpdateState();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { choose, _fromPage, _fetching, _removeImage } };
        var picture = new StackPanel { Spacing = 6 };
        picture.Children.Add(new TextBlock { Text = "Picture" });
        picture.Children.Add(dropArea);
        picture.Children.Add(buttons);

        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };
        foreach (var child in new UIElement[] { _name, _manufacturer, _number, _type, _status, _scale, _url, _urlError, _notes, picture, _error })
            panel.Children.Add(child);
        Content = new ScrollViewer { Content = panel, MaxHeight = 600 };

        PrimaryButtonClick += Save_Click;
        Closed += (_, _) =>
        {
            try { if (Directory.Exists(_temporary)) Directory.Delete(_temporary, recursive: true); }
            catch (IOException) { }
        };
        Opened += (_, _) => _name.Focus(FocusState.Programmatic);
        _ = ShowCurrentPicture();
        UpdateState();
    }

    private string? _unknownType;
    private string? _unknownStatus;

    private AftermarketDraft Draft => new()
    {
        ProductName = _name.Text, Manufacturer = _manufacturer.Text, ProductNumber = _number.Text,
        Type = _unknownType ?? AftermarketKinds.Types[Math.Max(0, _type.SelectedIndex)],
        Status = _unknownStatus ?? AftermarketKinds.Statuses[Math.Max(0, _status.SelectedIndex)],
        ScaleCompatibility = _scale.Text, ProductUrl = _url.Text, Notes = _notes.Text.Replace("\r\n", "\n").Replace('\r', '\n'),
    };

    private void UpdateState()
    {
        var draft = Draft;
        IsPrimaryButtonEnabled = draft.HasValidName && draft.HasValidUrl;
        _urlError.Visibility = draft.HasValidUrl ? Visibility.Collapsed : Visibility.Visible;
        _fromPage.IsEnabled = draft.ParsedUrl is not null;
        ToolTipService.SetToolTip(_fromPage, draft.ParsedUrl is null ? "Enter the product page first" : "Download the picture the product page shows in link previews");
    }

    // MARK: Picture

    private async Task ShowCurrentPicture()
    {
        if (_item?.ImageAssetId is not { } id || _services.WithStore(s => s.Asset(id)) is not { } asset) { ShowPicture(null); return; }
        ShowPicture(await _services.Thumbnails.PathFor(asset) ?? _services.Location.FullPath(asset.OriginalRelativePath));
    }

    private async void ShowPicture(string? path)
    {
        _removeImage.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
        _dropHint.Visibility = path is null ? Visibility.Visible : Visibility.Collapsed;
        _dropHint.Text = "Drop an Image\nDrop an image file, or an image from a web page.";
        _picture.Source = null;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var bitmap = new BitmapImage { DecodePixelHeight = 280 };
            using var stream = File.OpenRead(path);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            _picture.Source = bitmap;
        }
        catch (Exception)
        {
            _dropHint.Text = Path.GetFileName(path);
            _dropHint.Visibility = Visibility.Visible;
        }
    }

    private void UseImage(string path, string name)
    {
        _pendingImage = path;
        _pendingName = name;
        _removePicture = false;
        _error.Visibility = Visibility.Collapsed;
        ShowPicture(path);
    }

    private async Task ChooseImage()
    {
        // The picker is a window of its own, so it can open over this dialog.
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow!.AppWindow.Id);
        foreach (var type in new[] { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif" }) picker.FileTypeFilter.Add(type);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) UseImage(file.Path, Path.GetFileName(file.Path));
    }

    private async Task DropImage(DataPackageView data)
    {
        try
        {
            if (data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await data.GetStorageItemsAsync();
                if (items.FirstOrDefault() is Windows.Storage.StorageFile file)
                {
                    var path = file.Path;
                    if (string.IsNullOrEmpty(path))
                    {
                        Directory.CreateDirectory(_temporary);
                        path = (await file.CopyAsync(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(_temporary), file.Name, Windows.Storage.NameCollisionOption.GenerateUniqueName)).Path;
                    }
                    UseImage(path, file.Name);
                }
            }
            else if (data.Contains(StandardDataFormats.Bitmap))
            {
                Directory.CreateDirectory(_temporary);
                var path = Path.Combine(_temporary, "Dropped Image.png");
                using (var input = await (await data.GetBitmapAsync()).OpenReadAsync())
                {
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
                    using var bitmap = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                    using var output = File.Create(path).AsRandomAccessStream();
                    var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
                    encoder.SetSoftwareBitmap(bitmap);
                    await encoder.FlushAsync();
                }
                UseImage(path, "Dropped Image.png");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowError($"The image couldn't be read ({error.Message}).");
        }
    }

    private async Task ImageFromPage()
    {
        if (Draft.ParsedUrl is not { } page) return;
        _fromPage.IsEnabled = false;
        _fetching.IsActive = true;
        try
        {
            var path = await Task.Run(() => AftermarketService.DownloadPageImage(new LinkFetcher(), new WicImages(), page, _temporary));
            UseImage(path, Path.GetFileName(path));
        }
        catch (AftermarketException error)
        {
            ShowError(error.Message);
        }
        finally
        {
            _fetching.IsActive = false;
            UpdateState();
        }
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }

    // MARK: Saving

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var draft = Draft;
        try
        {
            var item = _services.WithStore(s => _item is null
                ? AftermarketService.Create(s, _project.Id, draft)
                : AftermarketService.Update(s, _item, draft));
            if (_pendingImage is { } path)
                AftermarketService.SetImage(_services, _services.Location, item, path, _pendingName ?? Path.GetFileName(path), Thumbnails.PdfHasPages);
            else if (_removePicture)
                _services.WithStore(s => { AftermarketService.RemoveImage(s, _services.Location, item); return 0; });
        }
        catch (Exception error) when (error is AftermarketException or CaptureException or SqliteException or IOException or UnauthorizedAccessException)
        {
            args.Cancel = true;
            ShowError($"Couldn't Save Part. {error.Message}");
        }
    }
}
