using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BenchBook.Core.Capture;

/// <summary>A photo whose metadata couldn't be read safely, so location data can't be removed.</summary>
public sealed class PhotoMetadataException(string message) : Exception(message);

/// <summary>
/// Reads and edits a photo's metadata in its own bytes, without decoding it:
/// the date it was taken (EXIF DateTimeOriginal, else DateTimeDigitized), and
/// removing location data for export. JPEG, PNG, WebP and HEIC, as references
/// accept. Nothing here needs a codec.
/// </summary>
public static partial class PhotoMetadata
{
    // MARK: Date taken

    /// <summary>When the photo was taken, as written in its EXIF (local time, no zone), if it says.</summary>
    public static DateTime? DateTaken(byte[] file)
    {
        if (ImageHeaders.Recognise(file) is not { } format) return null;
        try
        {
            foreach (var (start, length) in ExifBlocks(file, format))
            {
                var tiff = new Tiff(file, start, length);
                if (tiff.DateTaken() is { } date) return date;
            }
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException or PhotoMetadataException) { }
        return null;
    }

    /// <summary>The date taken as a moment: EXIF dates have no zone, so they're read as this PC's local time.</summary>
    public static DateTimeOffset? DateTakenLocal(byte[] file) =>
        DateTaken(file) is { } local ? new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)) : null;

    /// <summary>"2026:11:19 14:05:00".</summary>
    public static DateTime? ParseExifDate(string text) =>
        DateTime.TryParseExact(text.Trim('\0', ' '), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    // MARK: Removing location

    /// <summary>
    /// A copy of the photo without its GPS data: the EXIF GPS directory is taken
    /// out and its values blanked, and GPS properties in XMP are blanked. The file
    /// keeps its size and every other byte, so the pixels aren't re-compressed.
    /// Throws when the metadata can't be followed, rather than export a copy that
    /// might still say where it was taken.
    /// </summary>
    public static byte[] WithoutLocation(byte[] original)
    {
        var file = (byte[])original.Clone();
        var format = ImageHeaders.Recognise(file) ?? throw new PhotoMetadataException("This isn't a photo.");
        var changed = new List<(int Start, int Length)>();
        try
        {
            foreach (var (start, length) in ExifBlocks(file, format))
                changed.AddRange(new Tiff(file, start, length).RemoveGps());
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new PhotoMetadataException("The photo's metadata is damaged.");
        }
        changed.AddRange(BlankXmpGps(file));
        if (format == ImageFormat.Png) FixPngChecksums(file, changed);
        return file;
    }

    // MARK: Finding EXIF

    /// <summary>Where each EXIF (TIFF) block starts in the file, and how long it is.</summary>
    private static List<(int Start, int Length)> ExifBlocks(byte[] file, ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => JpegExif(file),
        ImageFormat.Png => PngChunks(file).Where(c => c.Type == "eXIf").Select(c => (c.Data, c.Length)).ToList(),
        ImageFormat.WebP => WebPChunks(file).Where(c => c.Type == "EXIF").Select(c => SkipExifHeader(file, c.Data, c.Length)).ToList(),
        _ => HeicExif(file),
    };

    /// <summary>Some writers start the block with "Exif\0\0", as in JPEG.</summary>
    private static (int, int) SkipExifHeader(byte[] file, int start, int length) =>
        length >= 6 && Ascii(file, start, 6) == "Exif\0\0" ? (start + 6, length - 6) : (start, length);

    private static List<(int, int)> JpegExif(byte[] file)
    {
        var blocks = new List<(int, int)>();
        var position = 2;
        while (position + 4 <= file.Length)
        {
            if (file[position] != 0xFF) break;
            var marker = file[position + 1];
            if (marker == 0xFF) { position++; continue; }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { position += 2; continue; }
            if (marker is 0xD9 or 0xDA) break; // The metadata comes before the image data.
            var length = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(position + 2));
            if (length < 2 || position + 2 + length > file.Length) break;
            var data = position + 4;
            var dataLength = length - 2;
            if (marker == 0xE1 && dataLength >= 6 && Ascii(file, data, 6) == "Exif\0\0") blocks.Add((data + 6, dataLength - 6));
            position += 2 + length;
        }
        return blocks;
    }

    private static IEnumerable<(string Type, int Data, int Length)> PngChunks(byte[] file)
    {
        var position = 8;
        while (position + 12 <= file.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(position));
            if (length < 0 || position + 12L + length > file.Length) yield break;
            var type = Ascii(file, position + 4, 4);
            yield return (type, position + 8, length);
            if (type == "IEND") yield break;
            position += 12 + length;
        }
    }

    private static IEnumerable<(string Type, int Data, int Length)> WebPChunks(byte[] file)
    {
        var position = 12;
        while (position + 8 <= file.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(position + 4));
            if (length < 0 || position + 8L + length > file.Length) yield break;
            yield return (Ascii(file, position, 4), position + 8, length);
            position += 8 + length + (length & 1);
        }
    }

    /// <summary>
    /// HEIC keeps EXIF as an item of type "Exif": 'iinf' names it and 'iloc'
    /// says where its bytes are. The item starts with the offset to its TIFF header.
    /// </summary>
    private static List<(int, int)> HeicExif(byte[] file)
    {
        var blocks = new List<(int, int)>();
        if (FindBox(file, 0, file.Length, "meta") is not { } meta) return blocks;
        var (metaStart, metaEnd) = (meta.Body + 4, meta.End); // A full box: skip version and flags.
        var exifItems = new HashSet<uint>();
        if (FindBox(file, metaStart, metaEnd, "iinf") is { } iinf)
        {
            var version = file[iinf.Body];
            var position = iinf.Body + 4 + (version == 0 ? 2 : 4);
            foreach (var infe in Boxes(file, position, iinf.End).Where(b => b.Type == "infe"))
            {
                var infeVersion = file[infe.Body];
                if (infeVersion < 2) continue;
                var at = infe.Body + 4;
                uint id;
                if (infeVersion == 2) { id = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at)); at += 2; }
                else { id = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at)); at += 4; }
                at += 2; // Protection index.
                if (Ascii(file, at, 4) == "Exif") exifItems.Add(id);
            }
        }
        if (exifItems.Count == 0 || FindBox(file, metaStart, metaEnd, "iloc") is not { } iloc) return blocks;
        var idat = FindBox(file, metaStart, metaEnd, "idat");

        var v = file[iloc.Body];
        var p = iloc.Body + 4;
        var offsetSize = file[p] >> 4;
        var lengthSize = file[p] & 0xF;
        var baseOffsetSize = file[p + 1] >> 4;
        var indexSize = v is 1 or 2 ? file[p + 1] & 0xF : 0;
        p += 2;
        uint count;
        if (v < 2) { count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(p)); p += 2; }
        else { count = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p)); p += 4; }
        for (var i = 0; i < count; i++)
        {
            uint id;
            if (v < 2) { id = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(p)); p += 2; }
            else { id = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p)); p += 4; }
            var construction = 0;
            if (v is 1 or 2) { construction = file[p + 1] & 0xF; p += 2; }
            p += 2; // Data reference index.
            var baseOffset = ReadSized(file, ref p, baseOffsetSize);
            var extents = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(p));
            p += 2;
            for (var e = 0; e < extents; e++)
            {
                if (indexSize > 0) ReadSized(file, ref p, indexSize);
                var offset = ReadSized(file, ref p, offsetSize);
                var length = ReadSized(file, ref p, lengthSize);
                // One extent is all EXIF needs; split items are left alone.
                if (!exifItems.Contains(id) || extents != 1) continue;
                long start = construction switch
                {
                    0 => (long)(baseOffset + offset),
                    1 when idat is { } data => data.Body + (long)(baseOffset + offset),
                    _ => -1,
                };
                if (start < 0 || length < 4 || start + (long)length > file.Length) continue;
                // The item begins with the offset from just after it to the TIFF header.
                var header = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)start));
                var tiff = start + 4 + header;
                var end = start + (long)length;
                if (tiff < end) blocks.Add(((int)tiff, (int)(end - tiff)));
            }
        }
        return blocks;
    }

    private static ulong ReadSized(byte[] file, ref int position, int size)
    {
        ulong value = size switch
        {
            0 => 0,
            4 => BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position)),
            8 => BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(position)),
            _ => throw new PhotoMetadataException("The photo's metadata is damaged."),
        };
        position += size;
        return value;
    }

    private readonly record struct Box(string Type, int Body, int End);

    private static IEnumerable<Box> Boxes(byte[] file, int start, int end)
    {
        var position = start;
        while (position + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position));
            var type = Ascii(file, position + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (position + 16 > end) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(position + 8));
                header = 16;
            }
            else if (size == 0) size = end - position;
            if (size < header || position + size > end) yield break;
            yield return new Box(type, position + header, (int)(position + size));
            position += (int)size;
        }
    }

    private static Box? FindBox(byte[] file, int start, int end, string type)
    {
        foreach (var box in Boxes(file, start, end))
            if (box.Type == type) return box;
        return null;
    }

    // MARK: TIFF

    /// <summary>An EXIF block: a TIFF header, IFD0, and the directories it points to.</summary>
    private sealed class Tiff
    {
        private const ushort ExifPointer = 0x8769, GpsPointer = 0x8825, DateTimeOriginal = 0x9003, DateTimeDigitized = 0x9004;
        private static readonly int[] TypeSizes = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4];

        private readonly byte[] _file;
        private readonly int _start, _length;
        private readonly bool _little;

        public Tiff(byte[] file, int start, int length)
        {
            (_file, _start, _length) = (file, start, length);
            if (length < 8) throw new PhotoMetadataException("The photo's metadata is damaged.");
            _little = Ascii(file, start, 2) switch
            {
                "II" => true,
                "MM" => false,
                _ => throw new PhotoMetadataException("The photo's metadata is damaged."),
            };
            if (U16(2) != 42) throw new PhotoMetadataException("The photo's metadata is damaged.");
        }

        private int At(long offset)
        {
            if (offset < 0 || offset >= _length) throw new ArgumentOutOfRangeException(nameof(offset));
            return _start + (int)offset;
        }

        private ushort U16(long offset) { var at = At(offset + 1) - 1; return _little ? BinaryPrimitives.ReadUInt16LittleEndian(_file.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(_file.AsSpan(at)); }

        private uint U32(long offset) { var at = At(offset + 3) - 3; return _little ? BinaryPrimitives.ReadUInt32LittleEndian(_file.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(_file.AsSpan(at)); }

        private void W16(long offset, ushort value) { var at = At(offset + 1) - 1; if (_little) BinaryPrimitives.WriteUInt16LittleEndian(_file.AsSpan(at), value); else BinaryPrimitives.WriteUInt16BigEndian(_file.AsSpan(at), value); }

        private void W32(long offset, uint value) { var at = At(offset + 3) - 3; if (_little) BinaryPrimitives.WriteUInt32LittleEndian(_file.AsSpan(at), value); else BinaryPrimitives.WriteUInt32BigEndian(_file.AsSpan(at), value); }

        private uint Ifd0 => U32(4);

        /// <summary>Each entry's position (from the TIFF start), tag, type, count and value or offset.</summary>
        private List<(long Position, ushort Tag, ushort Type, uint Count, uint Value)> Entries(long ifd)
        {
            var count = U16(ifd);
            var entries = new List<(long, ushort, ushort, uint, uint)>(count);
            for (var i = 0; i < count; i++)
            {
                var position = ifd + 2 + 12L * i;
                entries.Add((position, U16(position), U16(position + 2), U32(position + 4), U32(position + 8)));
            }
            return entries;
        }

        private long ValueSize(ushort type, uint count) => type < TypeSizes.Length ? (long)TypeSizes[type] * count : 0;

        public DateTime? DateTaken()
        {
            var exif = Entries(Ifd0).FirstOrDefault(e => e.Tag == ExifPointer);
            if (exif.Tag != ExifPointer) return null;
            var entries = Entries(exif.Value);
            foreach (var tag in new[] { DateTimeOriginal, DateTimeDigitized })
            {
                var entry = entries.FirstOrDefault(e => e.Tag == tag);
                if (entry.Tag != tag || entry.Type != 2 || entry.Count == 0) continue;
                var offset = entry.Count <= 4 ? entry.Position + 8 : entry.Value;
                var at = At(offset);
                At(offset + entry.Count - 1);
                if (ParseExifDate(Encoding.ASCII.GetString(_file, at, (int)entry.Count)) is { } date) return date;
            }
            return null;
        }

        /// <summary>
        /// Takes the GPS pointer out of IFD0, moving the later entries up and
        /// zeroing the space left, then blanks the GPS directory and its values.
        /// Returns the byte ranges changed, in file positions.
        /// </summary>
        public List<(int, int)> RemoveGps()
        {
            var changed = new List<(int, int)>();
            var ifd0 = Ifd0;
            var entries = Entries(ifd0);
            var index = entries.FindIndex(e => e.Tag == GpsPointer);
            if (index < 0) return changed;
            var gps = entries[index].Value;

            // Blank the GPS directory's values, then the directory itself.
            var gpsEntries = Entries(gps);
            foreach (var entry in gpsEntries)
            {
                var size = ValueSize(entry.Type, entry.Count);
                if (size > 4) changed.Add(Zero(entry.Value, size));
            }
            changed.Add(Zero(gps, 2 + 12L * gpsEntries.Count + 4));

            // IFD0 without the pointer: the next-directory offset moves up 12 bytes.
            var count = entries.Count;
            var next = U32(ifd0 + 2 + 12L * count);
            for (var i = index + 1; i < count; i++)
                Array.Copy(_file, At(ifd0 + 2 + 12L * i), _file, At(ifd0 + 2 + 12L * (i - 1)), 12);
            W16(ifd0, (ushort)(count - 1));
            W32(ifd0 + 2 + 12L * (count - 1), next);
            changed.Add(Zero(ifd0 + 2 + 12L * (count - 1) + 4, 12));
            changed.Add((At(ifd0), 2 + 12 * count + 4));
            return changed;
        }

        private (int, int) Zero(long offset, long size)
        {
            var at = At(offset);
            At(offset + size - 1);
            Array.Clear(_file, at, (int)size);
            return (at, (int)size);
        }
    }

    // MARK: XMP

    [GeneratedRegex(@"<x:xmpmeta\b.*?</x:xmpmeta>", RegexOptions.Singleline)]
    private static partial Regex XmpPacket();

    /// <summary>GPS properties, whatever the namespace prefix: attributes, and elements with or without content.</summary>
    [GeneratedRegex(@"\s[\w.-]+:GPS\w+\s*=\s*(""[^""]*""|'[^']*')|<([\w.-]+:GPS\w+)\b[^>]*/>|<([\w.-]+:GPS\w+)\b[^>]*>.*?</\3\s*>", RegexOptions.Singleline)]
    private static partial Regex XmpGps();

    /// <summary>
    /// Blanks GPS properties in every XMP packet with spaces of the same length,
    /// so no offset in the file moves. XMP is plain text in every format here.
    /// </summary>
    private static List<(int, int)> BlankXmpGps(byte[] file)
    {
        var changed = new List<(int, int)>();
        // Latin-1 keeps one character per byte, so positions are file positions.
        var text = Encoding.Latin1.GetString(file);
        foreach (Match packet in XmpPacket().Matches(text))
            foreach (Match gps in XmpGps().Matches(packet.Value))
            {
                var start = packet.Index + gps.Index;
                Array.Fill(file, (byte)' ', start, gps.Length);
                changed.Add((start, gps.Length));
            }
        return changed;
    }

    // MARK: PNG checksums

    /// <summary>Recomputes the CRC of each PNG chunk that was changed.</summary>
    private static void FixPngChecksums(byte[] file, List<(int Start, int Length)> changed)
    {
        if (changed.Count == 0) return;
        foreach (var (_, data, length) in PngChunks(file).ToList())
        {
            if (!changed.Any(c => c.Start < data + length && data < c.Start + c.Length)) continue;
            var crc = Crc32(file.AsSpan(data - 4, length + 4));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(data + length), crc);
        }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static string Ascii(byte[] file, int start, int length) =>
        start >= 0 && start + length <= file.Length ? Encoding.ASCII.GetString(file, start, length) : "";
}
