using System.Buffers.Binary;
using System.Text;

namespace BenchBook.Core.Capture;

public enum ImageFormat { Jpeg, Png, WebP, Heic }

/// <summary>
/// Recognises images by their signature and reads their pixel size from the
/// file's own headers, without decoding. Windows can't decode HEIC without an
/// extension, but a HEIC file must still be accepted, so nothing here needs a codec.
/// </summary>
public static class ImageHeaders
{
    private static readonly HashSet<string> HeicBrands = ["heic", "heix", "heim", "heis", "mif1", "msf1"];

    public static ImageFormat? Recognise(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return ImageFormat.Jpeg;
        if (header.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ImageFormat.Png;
        if (header.Length >= 12 && Ascii(header[..4]) == "RIFF" && Ascii(header[8..12]) == "WEBP") return ImageFormat.WebP;
        if (header.Length >= 12 && Ascii(header[4..8]) == "ftyp" && HeicBrands.Contains(Ascii(header[8..12]))) return ImageFormat.Heic;
        return null;
    }

    public static (string MimeType, string Extension) Describe(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ("image/jpeg", "jpg"),
        ImageFormat.Png => ("image/png", "png"),
        ImageFormat.WebP => ("image/webp", "webp"),
        _ => ("image/heic", "heic"),
    };

    /// <summary>The stored width and height (before any orientation is applied), or null if the headers don't say.</summary>
    public static (int Width, int Height)? Size(Stream stream, ImageFormat format)
    {
        try
        {
            return format switch
            {
                ImageFormat.Png => PngSize(stream),
                ImageFormat.Jpeg => JpegSize(stream),
                ImageFormat.WebP => WebPSize(stream),
                _ => HeicSize(stream),
            };
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static (int, int)? PngSize(Stream stream)
    {
        var header = Read(stream, 0, 24);
        if (Ascii(header.AsSpan(12, 4)) != "IHDR") return null;
        return Valid(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)));
    }

    private static (int, int)? JpegSize(Stream stream)
    {
        stream.Position = 2;
        var two = new byte[2];
        while (true)
        {
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xFF);
            if (marker < 0) return null;
            // Markers without a length.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) continue;
            if (marker is 0xD9 or 0xDA) return null; // End of image, or the scan began with no frame header.
            stream.ReadExactly(two);
            var length = BinaryPrimitives.ReadUInt16BigEndian(two);
            if (length < 2) return null;
            var isFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isFrame)
            {
                var frame = new byte[5];
                stream.ReadExactly(frame);
                return Valid(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3)), BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1)));
            }
            stream.Seek(length - 2, SeekOrigin.Current);
            // A marker must start with 0xFF.
            var next = stream.ReadByte();
            if (next != 0xFF) return null;
        }
    }

    private static (int, int)? WebPSize(Stream stream)
    {
        var header = Read(stream, 0, 30);
        var chunk = Ascii(header.AsSpan(12, 4));
        switch (chunk)
        {
            case "VP8 ":
                if (header[23] != 0x9D || header[24] != 0x01 || header[25] != 0x2A) return null;
                return Valid((uint)(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26)) & 0x3FFF),
                             (uint)(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28)) & 0x3FFF));
            case "VP8L":
                if (header[20] != 0x2F) return null;
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(21));
                return Valid((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
            case "VP8X":
                return Valid(UInt24(header.AsSpan(24)) + 1, UInt24(header.AsSpan(27)) + 1);
            default:
                return null;
        }
    }

    /// <summary>
    /// HEIC keeps sizes in 'ispe' properties inside meta › iprp › ipco. The
    /// largest is the full image; smaller ones are tiles and thumbnails.
    /// </summary>
    private static (int, int)? HeicSize(Stream stream)
    {
        (uint W, uint H)? best = null;
        void Visit(long start, long end, int depth)
        {
            var position = start;
            while (position + 8 <= end && depth < 8)
            {
                var header = Read(stream, position, 8);
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Ascii(header.AsSpan(4, 4));
                var headerLength = 8L;
                if (size == 1)
                {
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(Read(stream, position + 8, 8));
                    headerLength = 16;
                }
                else if (size == 0)
                {
                    size = end - position;
                }
                if (size < headerLength || position + size > end) return;
                var body = position + headerLength;
                switch (type)
                {
                    case "meta":
                        Visit(body + 4, position + size, depth + 1); // A full box: skip version and flags.
                        break;
                    case "iprp":
                    case "ipco":
                        Visit(body, position + size, depth + 1);
                        break;
                    case "ispe":
                        var data = Read(stream, body + 4, 8);
                        var w = BinaryPrimitives.ReadUInt32BigEndian(data);
                        var h = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
                        if (best is null || (ulong)w * h > (ulong)best.Value.W * best.Value.H) best = (w, h);
                        break;
                }
                position += size;
            }
        }
        Visit(0, stream.Length, 0);
        return best is { } size ? Valid(size.W, size.H) : null;
    }

    private static (int, int)? Valid(uint width, uint height) =>
        width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue ? ((int)width, (int)height) : null;

    private static uint UInt24(ReadOnlySpan<byte> bytes) => (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16);

    private static byte[] Read(Stream stream, long offset, int count)
    {
        stream.Position = offset;
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static string Ascii(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes);
}
