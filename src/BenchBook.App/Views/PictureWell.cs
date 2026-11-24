using BenchBook.App.Services;
using BenchBook.Core.Links;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace BenchBook.App.Views;

/// <summary>
/// The picture in the aftermarket and kit editors: drop an image, choose one,
/// or take the product page's preview image. Nothing is stored until the
/// editor saves: <see cref="PendingImage"/> is a file waiting to be added, and
/// <see cref="RemovePicture"/> asks for the current one to go.
/// </summary>
internal sealed class PictureWell : StackPanel
{
    private readonly AppServices _services;
    private readonly Func<Uri?> _page;
    private readonly Image _picture = new() { Stretch = Stretch.Uniform, MaxHeight = 140 };
    private readonly TextBlock _dropHint = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Button _fromPage = new() { Content = "Use Image from Product Page" };
    private readonly Button _removeImage = new() { Content = "Remove Image" };
    private readonly ProgressRing _fetching = new() { Width = 16, Height = 16, IsActive = false };
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "BenchBook Pictures", Guid.NewGuid().ToString("N"));

    /// <param name="page">The product page or link as typed, if it's a web address.</param>
    /// <param name="accessibleName">e.g. "Picture of the part".</param>
    public PictureWell(AppServices services, Func<Uri?> page, string accessibleName, string? assetId)
    {
        _services = services;
        _page = page;
        Spacing = 6;
        _dropHint.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var dropArea = new Border
        {
            AllowDrop = true, MinHeight = 120, CornerRadius = new CornerRadius(6), Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new Grid { Children = { _picture, _dropHint } },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dropArea, accessibleName);
        dropArea.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Bitmap)
                || e.DataView.Contains(StandardDataFormats.WebLink) || e.DataView.Contains(StandardDataFormats.Uri))
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
            PendingImage = null;
            RemovePicture = true;
            ShowPicture(null);
        };

        Children.Add(new TextBlock { Text = "Picture" });
        Children.Add(dropArea);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { choose, _fromPage, _fetching, _removeImage } });
        Unloaded += (_, _) =>
        {
            try { if (Directory.Exists(_temporary)) Directory.Delete(_temporary, recursive: true); }
            catch (IOException) { }
        };
        _ = ShowCurrent(assetId);
        UpdatePage();
    }

    /// <summary>A file to add as the picture when the editor saves.</summary>
    public string? PendingImage { get; private set; }

    public string? PendingName { get; private set; }

    /// <summary>The current picture should be removed when the editor saves.</summary>
    public bool RemovePicture { get; private set; }

    /// <summary>A problem with an image, for the editor to show.</summary>
    public event Action<string>? Problem;

    /// <summary>A web link dropped on the picture, for an editor that fills in an empty Link.</summary>
    public event Action<Uri>? LinkDropped;

    /// <summary>Call when the page address changes, to enable Use Image from Product Page.</summary>
    public void UpdatePage()
    {
        var page = _page();
        _fromPage.IsEnabled = page is not null && !_fetching.IsActive;
        ToolTipService.SetToolTip(_fromPage, page is null ? "Enter the product page first" : "Download the picture the product page shows in link previews");
    }

    private async Task ShowCurrent(string? assetId)
    {
        if (assetId is null || _services.WithStore(s => s.Asset(assetId)) is not { } asset) { ShowPicture(null); return; }
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
        PendingImage = path;
        PendingName = name;
        RemovePicture = false;
        ShowPicture(path);
    }

    private async Task ChooseImage()
    {
        // The picker is a window of its own, so it can open over a dialog.
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
            else if (data.Contains(StandardDataFormats.WebLink))
            {
                LinkDropped?.Invoke(await data.GetWebLinkAsync());
            }
            else if (data.Contains(StandardDataFormats.Uri))
            {
                LinkDropped?.Invoke(await data.GetUriAsync());
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Problem?.Invoke($"The image couldn't be read ({error.Message}).");
        }
    }

    private async Task ImageFromPage()
    {
        if (_page() is not { } page) return;
        _fetching.IsActive = true;
        UpdatePage();
        try
        {
            var path = await Task.Run(() => AftermarketService.DownloadPageImage(new LinkFetcher(), new WicImages(), page, _temporary));
            UseImage(path, Path.GetFileName(path));
        }
        catch (AftermarketException error)
        {
            Problem?.Invoke(error.Message);
        }
        finally
        {
            _fetching.IsActive = false;
            UpdatePage();
        }
    }
}
