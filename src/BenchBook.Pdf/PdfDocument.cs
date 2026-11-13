using System.Runtime.InteropServices;
using BenchBook.Core.Markups;

namespace BenchBook.Pdf;

/// <summary>One page's geometry: its crop box in page space, how it turns, and its size as shown.</summary>
/// <param name="Crop">The visible area, in the page's own coordinates (points, y up).</param>
/// <param name="Rotation">Clockwise quarter turns: 0 to 3.</param>
public sealed record PdfPageInfo(int Index, PageRect Crop, int Rotation)
{
    /// <summary>The size as shown, after turning, in points.</summary>
    public double ShownWidth => Rotation % 2 == 0 ? Crop.Width : Crop.Height;
    public double ShownHeight => Rotation % 2 == 0 ? Crop.Height : Crop.Width;
}

/// <summary>A search match: its page, and the rectangles it covers in page coordinates.</summary>
public sealed record PdfMatch(int Page, IReadOnlyList<PageRect> Rects);

public sealed class PdfPasswordException() : Exception("This PDF is protected with a password, so BenchBook can't show it. Use Open in Default App instead.");

public sealed class PdfOpenException(string message) : Exception(message);

/// <summary>
/// A PDF opened with PDFium from a copy of its bytes in memory, so the file
/// itself is only read, never written or locked.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private readonly GCHandle _bytes;
    private IntPtr _document;

    private PdfDocument(GCHandle bytes, IntPtr document, List<PdfPageInfo> pages)
    {
        _bytes = bytes;
        _document = document;
        Pages = pages;
    }

    public IReadOnlyList<PdfPageInfo> Pages { get; }

    public IntPtr Handle => _document;

    public static PdfDocument Open(string path) => Open(File.ReadAllBytes(path));

    public static PdfDocument Open(byte[] data)
    {
        Pdfium.EnsureInitialised();
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        lock (Pdfium.Lock)
        {
            var document = Pdfium.FPDF_LoadMemDocument64(pinned.AddrOfPinnedObject(), (nuint)data.Length, null);
            if (document == IntPtr.Zero)
            {
                var error = Pdfium.FPDF_GetLastError();
                pinned.Free();
                if (error == Pdfium.ErrorPassword) throw new PdfPasswordException();
                throw new PdfOpenException("This PDF couldn't be opened. It may be damaged.");
            }
            var pages = new List<PdfPageInfo>();
            var count = Pdfium.FPDF_GetPageCount(document);
            for (var i = 0; i < count; i++)
            {
                var page = Pdfium.FPDF_LoadPage(document, i);
                if (page == IntPtr.Zero) { pages.Add(new PdfPageInfo(i, new PageRect(0, 0, 612, 792), 0)); continue; }
                try
                {
                    if (!Pdfium.FPDFPage_GetCropBox(page, out var l, out var b, out var r, out var t)
                        && !Pdfium.FPDFPage_GetMediaBox(page, out l, out b, out r, out t))
                    {
                        // No box at all: the page's size from PDFium, from the origin.
                        (l, b, r, t) = (0, 0, Pdfium.FPDF_GetPageWidthF(page), Pdfium.FPDF_GetPageHeightF(page));
                    }
                    var crop = new PageRect(Math.Min(l, r), Math.Min(b, t), Math.Abs(r - l), Math.Abs(t - b));
                    pages.Add(new PdfPageInfo(i, crop, ((Pdfium.FPDFPage_GetRotation(page) % 4) + 4) % 4));
                }
                finally
                {
                    Pdfium.FPDF_ClosePage(page);
                }
            }
            return new PdfDocument(pinned, document, pages);
        }
    }

    /// <summary>The page drawn as shown (crop box and rotation), as BGRA pixels on white.</summary>
    public byte[] Render(int index, int width, int height)
    {
        lock (Pdfium.Lock)
        {
            ObjectDisposedException.ThrowIf(_document == IntPtr.Zero, this);
            var page = Pdfium.FPDF_LoadPage(_document, index);
            if (page == IntPtr.Zero) return new byte[width * height * 4];
            var bitmap = Pdfium.FPDFBitmap_Create(width, height, 1);
            try
            {
                Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                Pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, Pdfium.RenderAnnotations | Pdfium.RenderLcdText);
                var stride = Pdfium.FPDFBitmap_GetStride(bitmap);
                var buffer = Pdfium.FPDFBitmap_GetBuffer(bitmap);
                var pixels = new byte[width * height * 4];
                for (var y = 0; y < height; y++) Marshal.Copy(buffer + y * stride, pixels, y * width * 4, width * 4);
                return pixels;
            }
            finally
            {
                Pdfium.FPDFBitmap_Destroy(bitmap);
                Pdfium.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Every place the text appears, page by page, ignoring case.</summary>
    public List<PdfMatch> Find(string text, CancellationToken cancellation = default)
    {
        var matches = new List<PdfMatch>();
        if (string.IsNullOrWhiteSpace(text)) return matches;
        for (var index = 0; index < Pages.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (Pdfium.Lock)
            {
                var page = Pdfium.FPDF_LoadPage(_document, index);
                if (page == IntPtr.Zero) continue;
                var textPage = Pdfium.FPDFText_LoadPage(page);
                try
                {
                    if (textPage == IntPtr.Zero) continue;
                    var search = Pdfium.FPDFText_FindStart(textPage, text, 0, 0);
                    try
                    {
                        while (Pdfium.FPDFText_FindNext(search))
                        {
                            var start = Pdfium.FPDFText_GetSchResultIndex(search);
                            var count = Pdfium.FPDFText_GetSchCount(search);
                            var rects = new List<PageRect>();
                            var rectCount = Pdfium.FPDFText_CountRects(textPage, start, count);
                            for (var r = 0; r < rectCount; r++)
                                if (Pdfium.FPDFText_GetRect(textPage, r, out var left, out var top, out var right, out var bottom))
                                    rects.Add(new PageRect(left, bottom, right - left, top - bottom));
                            if (rects.Count > 0) matches.Add(new PdfMatch(index, rects));
                        }
                    }
                    finally
                    {
                        Pdfium.FPDFText_FindClose(search);
                    }
                }
                finally
                {
                    if (textPage != IntPtr.Zero) Pdfium.FPDFText_ClosePage(textPage);
                    Pdfium.FPDF_ClosePage(page);
                }
            }
        }
        return matches;
    }

    public void Dispose()
    {
        lock (Pdfium.Lock)
        {
            if (_document == IntPtr.Zero) return;
            Pdfium.FPDF_CloseDocument(_document);
            _document = IntPtr.Zero;
        }
        _bytes.Free();
    }
}
