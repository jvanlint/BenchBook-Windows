using System.Collections.Concurrent;
using BenchBook.Core;
using BenchBook.Core.Model;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BenchBook.App.Services;

/// <summary>
/// Gallery thumbnails, made from originals when missing and saved to
/// Thumbnails as PNG, 480 px at most. They're derived data: if one can't be
/// made, the tile shows an icon and the original is untouched.
/// </summary>
public sealed class Thumbnails(LibraryLocation location)
{
    public const uint MaxPixels = 480;

    private readonly ConcurrentDictionary<string, Task<string?>> _making = new(Ids.Comparer);

    /// <summary>The thumbnail's path, making it first if needed. Null if it can't be made, e.g. HEIC without the extension.</summary>
    public Task<string?> PathFor(Asset asset)
    {
        var path = location.ThumbnailPath(asset.Id);
        if (File.Exists(path)) return Task.FromResult<string?>(path);
        if (asset.MimeType == "text/markdown") return Task.FromResult<string?>(null);
        return _making.GetOrAdd(asset.Id, key => Task.Run(async () =>
        {
            try { return await Make(asset, path); }
            finally { _making.TryRemove(key, out _); }
        }));
    }

    /// <summary>Forgets thumbnails being made, after Clear Thumbnails.</summary>
    public void Reset() => _making.Clear();

    private async Task<string?> Make(Asset asset, string destination)
    {
        if (location.FullPath(asset.OriginalRelativePath) is not { } original || !File.Exists(original)) return null;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(original);
            using var png = new InMemoryRandomAccessStream();
            if (asset.MimeType == "application/pdf")
            {
                var document = await PdfDocument.LoadFromFileAsync(file);
                if (document.PageCount == 0) return null;
                using var page = document.GetPage(0);
                var (width, height) = Fit(page.Size.Width, page.Size.Height);
                await page.RenderToStreamAsync(png, new PdfPageRenderOptions
                {
                    DestinationWidth = width,
                    DestinationHeight = height,
                    BitmapEncoderId = BitmapEncoder.PngEncoderId,
                });
            }
            else
            {
                using var input = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(input);
                var (width, height) = Fit(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
                var transform = new BitmapTransform
                {
                    ScaledWidth = width,
                    ScaledHeight = height,
                    InterpolationMode = BitmapInterpolationMode.Fant,
                };
                // Scale is applied before rotation, so swap the target size when the image is turned.
                if (decoder.OrientedPixelWidth != decoder.PixelWidth)
                    (transform.ScaledWidth, transform.ScaledHeight) = (height, width);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var partial = Path.Combine(Path.GetDirectoryName(destination)!, LibraryPaths.PartialPrefix + Ids.New());
            png.Seek(0);
            using (var output = File.Create(partial))
                await png.AsStreamForRead().CopyToAsync(output);
            File.Move(partial, destination, overwrite: true);
            return destination;
        }
        catch (Exception)
        {
            // No codec (HEIC without the extensions), a damaged file, or a password-protected PDF.
            return null;
        }
    }

    private static (uint Width, uint Height) Fit(double width, double height)
    {
        if (width <= 0 || height <= 0) return (MaxPixels, MaxPixels);
        var scale = Math.Min(1, MaxPixels / Math.Max(width, height));
        return ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
    }

    /// <summary>Whether a PDF opens with at least one page. A password-protected PDF counts: it's kept, and opens in its own app.</summary>
    public static bool PdfHasPages(string path)
    {
        try
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            var document = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
            return document.PageCount > 0;
        }
        catch (Exception error) when (error.HResult == unchecked((int)0x8007052B))
        {
            return true; // ERROR_WRONG_PASSWORD
        }
        catch (Exception)
        {
            return false;
        }
    }
}
