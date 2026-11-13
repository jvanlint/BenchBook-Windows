using BenchBook.Core.Links;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BenchBook.App.Services;

/// <summary>Checks and scales downloaded images with Windows Imaging Component. Called off the UI thread.</summary>
internal sealed class WicImages : IImageScaler
{
    public bool IsImage(byte[] data)
    {
        try
        {
            using var stream = Stream(data);
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            return decoder.FrameCount > 0 && decoder.PixelWidth > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public byte[]? Scale(byte[] data, int minPixels, int maxPixels)
    {
        try
        {
            return ScaleAsync(data, minPixels, maxPixels).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null; // Not an image Windows can decode.
        }
    }

    private static async Task<byte[]?> ScaleAsync(byte[] data, int minPixels, int maxPixels)
    {
        using var input = Stream(data);
        var decoder = await BitmapDecoder.CreateAsync(input);

        // Icons often hold several sizes: use the largest.
        BitmapFrame? best = null;
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            if (best is null || frame.PixelWidth > best.PixelWidth) best = frame;
        }
        if (best is null) return null;
        var (width, height) = (best.OrientedPixelWidth, best.OrientedPixelHeight);
        if (Math.Min(width, height) < minPixels) return null;

        var scale = Math.Min(1.0, maxPixels / (double)Math.Max(width, height));
        var (w, h) = ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        (transform.ScaledWidth, transform.ScaledHeight) = best.OrientedPixelWidth != best.PixelWidth ? (h, w) : (w, h);
        var pixels = await best.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var bytes = pixels.DetachPixelData();

        var transparent = false;
        for (var i = 3; i < bytes.Length; i += 4)
            if (bytes[i] < 255) { transparent = true; break; }

        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        if (transparent)
        {
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        }
        else
        {
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.82f, Windows.Foundation.PropertyType.Single) };
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
        }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, w, h, 96, 96, bytes);
        await encoder.FlushAsync();

        var result = new byte[output.Size];
        output.Seek(0);
        await output.AsStreamForRead().ReadExactlyAsync(result);
        return result;
    }

    private static InMemoryRandomAccessStream Stream(byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        stream.AsStreamForWrite().Write(data);
        stream.AsStreamForWrite().Flush();
        stream.Seek(0);
        return stream;
    }
}
