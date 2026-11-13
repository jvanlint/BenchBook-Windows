using System.Runtime.InteropServices;
using BenchBook.Core.Markups;
using BenchBook.Core.Model;

namespace BenchBook.Pdf;

/// <summary>Measures text for line wrapping: the width of a line of text at a font size, bold or not.</summary>
public delegate double MeasureText(string text, double fontSize, bool bold);

/// <summary>
/// Saves a separate copy of a PDF with its markups drawn into the pages
/// (behaviour.md, "Export marked-up copy"). The original is never changed.
/// </summary>
public static class PdfExporter
{
    private const double Margin = 54;

    /// <summary>The markup colours, as approximate sRGB (behaviour.md, "Colours").</summary>
    public static (byte R, byte G, byte B) Rgb(string color) => color switch
    {
        "orange" => (0xFF, 0x95, 0x00),
        "red" => (0xFF, 0x3B, 0x30),
        "pink" => (0xFF, 0x2D, 0x55),
        "green" => (0x34, 0xC7, 0x59),
        "blue" => (0x00, 0x7A, 0xFF),
        _ => (0xFF, 0xCC, 0x00),
    };

    /// <param name="original">The PDF's bytes.</param>
    /// <param name="markups">Its markups, in number order.</param>
    /// <param name="regularFont">A TrueType font file for text, e.g. Arial.</param>
    /// <param name="boldFont">The bold face of the same font.</param>
    public static byte[] Export(byte[] original, IReadOnlyList<Markup> markups, byte[] regularFont, byte[] boldFont, MeasureText measure)
    {
        using var document = PdfDocument.Open(original);
        var regular = GCHandle.Alloc(regularFont, GCHandleType.Pinned);
        var bold = GCHandle.Alloc(boldFont, GCHandleType.Pinned);
        try
        {
            lock (Pdfium.Lock)
            {
                var doc = document.Handle;
                var font = Pdfium.FPDFText_LoadFont(doc, regular.AddrOfPinnedObject(), (uint)regularFont.Length, Pdfium.FontTrueType, true);
                var boldHandle = Pdfium.FPDFText_LoadFont(doc, bold.AddrOfPinnedObject(), (uint)boldFont.Length, Pdfium.FontTrueType, true);
                try
                {
                    var numbers = markups.Select((m, i) => (m, i + 1)).ToDictionary(p => p.m.Id, p => p.Item2);
                    foreach (var group in markups.GroupBy(m => m.PageIndex))
                    {
                        if (group.Key < 0 || group.Key >= document.Pages.Count) continue;
                        var page = Pdfium.FPDF_LoadPage(doc, group.Key);
                        if (page == IntPtr.Zero) continue;
                        try
                        {
                            foreach (var markup in group)
                                Draw(doc, page, markup, numbers[markup.Id], font, boldHandle, measure);
                            Pdfium.FPDFPage_GenerateContent(page);
                        }
                        finally
                        {
                            Pdfium.FPDF_ClosePage(page);
                        }
                    }

                    var listed = markups.Where(PdfMarkups.IsListed).ToList();
                    if (listed.Count > 0)
                    {
                        // The list matches the PDF's pages, unless they're too small to read it on.
                        var last = document.Pages.Count > 0 ? document.Pages[^1] : null;
                        var (width, height) = last is not null && last.ShownWidth >= 612 && last.ShownHeight >= 792
                            ? (last.ShownWidth, last.ShownHeight) : (612.0, 792.0);
                        DrawList(doc, document.Pages.Count, listed.Select(m => (m, numbers[m.Id])).ToList(), width, height, font, boldHandle, measure);
                    }
                    return Save(doc);
                }
                finally
                {
                    if (font != IntPtr.Zero) Pdfium.FPDFFont_Close(font);
                    if (boldHandle != IntPtr.Zero) Pdfium.FPDFFont_Close(boldHandle);
                }
            }
        }
        finally
        {
            regular.Free();
            bold.Free();
        }
    }

    // MARK: Markups

    private static void Draw(IntPtr doc, IntPtr page, Markup markup, int number, IntPtr font, IntPtr bold, MeasureText measure)
    {
        var (r, g, b) = Rgb(markup.Color);
        switch (markup.Kind)
        {
            case MarkupKinds.Note:
                // Filled with its colour blended 60% toward white, with a one-point outline.
                var box = Pdfium.FPDFPageObj_CreateNewPath((float)markup.X, (float)markup.Y);
                Pdfium.FPDFPath_LineTo(box, (float)(markup.X + markup.Width), (float)markup.Y);
                Pdfium.FPDFPath_LineTo(box, (float)(markup.X + markup.Width), (float)(markup.Y + markup.Height));
                Pdfium.FPDFPath_LineTo(box, (float)markup.X, (float)(markup.Y + markup.Height));
                Pdfium.FPDFPath_Close(box);
                Pdfium.FPDFPageObj_SetFillColor(box, Blend(r), Blend(g), Blend(b), 255);
                Pdfium.FPDFPageObj_SetStrokeColor(box, r, g, b, 255);
                Pdfium.FPDFPageObj_SetStrokeWidth(box, 1);
                Pdfium.FPDFPath_SetDrawMode(box, Pdfium.FillWinding, true);
                Pdfium.FPDFPage_InsertObject(page, box);
                var lines = Wrap(markup.Text, MarkupGeometry.NoteTextWidth, MarkupGeometry.NoteFontSize, false, measure);
                var baseline = markup.Y + markup.Height - MarkupGeometry.NotePadding - MarkupGeometry.NoteFontSize * 0.9;
                foreach (var line in lines)
                {
                    if (baseline < markup.Y + 2) break;
                    Text(doc, page, font, line, MarkupGeometry.NoteFontSize, markup.X + MarkupGeometry.NotePadding, baseline, (0, 0, 0));
                    baseline -= MarkupGeometry.NoteFontSize * 1.2;
                }
                break;

            case MarkupKinds.Highlight:
                var points = MarkupGeometry.PointsOf(markup);
                if (points.Count == 0) return;
                var stroke = Pdfium.FPDFPageObj_CreateNewPath((float)points[0].X, (float)points[0].Y);
                foreach (var point in points.Skip(1)) Pdfium.FPDFPath_LineTo(stroke, (float)point.X, (float)point.Y);
                if (points.Count == 1) Pdfium.FPDFPath_LineTo(stroke, (float)points[0].X + 0.01f, (float)points[0].Y);
                Pdfium.FPDFPageObj_SetStrokeColor(stroke, r, g, b, 115); // 45% opacity.
                Pdfium.FPDFPageObj_SetStrokeWidth(stroke, (float)MarkupGeometry.HighlightWidth);
                Pdfium.FPDFPageObj_SetLineCap(stroke, Pdfium.LineCapRound);
                Pdfium.FPDFPageObj_SetLineJoin(stroke, Pdfium.LineJoinRound);
                Pdfium.FPDFPageObj_SetBlendMode(stroke, "Multiply");
                Pdfium.FPDFPath_SetDrawMode(stroke, Pdfium.FillNone, true);
                Pdfium.FPDFPage_InsertObject(page, stroke);
                break;

            case MarkupKinds.Arrow:
                var ends = MarkupGeometry.PointsOf(markup);
                if (ends.Count < 2 || ends[0] == ends[^1]) return;
                var (tip, left, right, lineEnd) = MarkupGeometry.ArrowHead(ends[0], ends[^1]);
                var shaft = Pdfium.FPDFPageObj_CreateNewPath((float)ends[0].X, (float)ends[0].Y);
                Pdfium.FPDFPath_LineTo(shaft, (float)lineEnd.X, (float)lineEnd.Y);
                Pdfium.FPDFPageObj_SetStrokeColor(shaft, r, g, b, 255);
                Pdfium.FPDFPageObj_SetStrokeWidth(shaft, (float)MarkupGeometry.ArrowWidth);
                Pdfium.FPDFPageObj_SetLineCap(shaft, Pdfium.LineCapRound);
                Pdfium.FPDFPath_SetDrawMode(shaft, Pdfium.FillNone, true);
                Pdfium.FPDFPage_InsertObject(page, shaft);
                var head = Pdfium.FPDFPageObj_CreateNewPath((float)tip.X, (float)tip.Y);
                Pdfium.FPDFPath_LineTo(head, (float)left.X, (float)left.Y);
                Pdfium.FPDFPath_LineTo(head, (float)right.X, (float)right.Y);
                Pdfium.FPDFPath_Close(head);
                Pdfium.FPDFPageObj_SetFillColor(head, r, g, b, 255);
                Pdfium.FPDFPath_SetDrawMode(head, Pdfium.FillWinding, false);
                Pdfium.FPDFPage_InsertObject(page, head);
                break;

            default:
                return; // A kind from a newer BenchBook isn't drawn.
        }

        // Highlights and arrows with text keep their number; the list says what it means.
        if (PdfMarkups.IsListed(markup)) Badge(doc, page, bold, number, MarkupGeometry.BadgeCentre(markup), markup.Color, measure);
    }

    private static uint Blend(byte channel) => (uint)Math.Round(channel + (255 - channel) * 0.6);

    private static void Badge(IntPtr doc, IntPtr page, IntPtr bold, int number, PagePoint centre, string color, MeasureText measure)
    {
        var (r, g, b) = Rgb(color);
        var radius = MarkupGeometry.BadgeDiameter / 2;
        var circle = Circle(centre.X, centre.Y, radius);
        Pdfium.FPDFPageObj_SetFillColor(circle, r, g, b, 255);
        Pdfium.FPDFPageObj_SetStrokeColor(circle, 255, 255, 255, 255);
        Pdfium.FPDFPageObj_SetStrokeWidth(circle, 1.5f);
        Pdfium.FPDFPath_SetDrawMode(circle, Pdfium.FillWinding, true);
        Pdfium.FPDFPage_InsertObject(page, circle);
        var label = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var width = measure(label, 10, true);
        var dark = color is "yellow" or "green";
        Text(doc, page, bold, label, 10, centre.X - width / 2, centre.Y - 3.5, dark ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
    }

    /// <summary>A circle as four Bézier curves.</summary>
    private static IntPtr Circle(double cx, double cy, double r)
    {
        const double k = 0.5522847498;
        var path = Pdfium.FPDFPageObj_CreateNewPath((float)(cx + r), (float)cy);
        Pdfium.FPDFPath_BezierTo(path, (float)(cx + r), (float)(cy + r * k), (float)(cx + r * k), (float)(cy + r), (float)cx, (float)(cy + r));
        Pdfium.FPDFPath_BezierTo(path, (float)(cx - r * k), (float)(cy + r), (float)(cx - r), (float)(cy + r * k), (float)(cx - r), (float)cy);
        Pdfium.FPDFPath_BezierTo(path, (float)(cx - r), (float)(cy - r * k), (float)(cx - r * k), (float)(cy - r), (float)cx, (float)(cy - r));
        Pdfium.FPDFPath_BezierTo(path, (float)(cx + r * k), (float)(cy - r), (float)(cx + r), (float)(cy - r * k), (float)(cx + r), (float)cy);
        Pdfium.FPDFPath_Close(path);
        return path;
    }

    private static void Text(IntPtr doc, IntPtr page, IntPtr font, string text, double size, double x, double baseline, (byte R, byte G, byte B) color)
    {
        if (text.Length == 0 || font == IntPtr.Zero) return;
        var obj = Pdfium.FPDFPageObj_CreateTextObj(doc, font, (float)size);
        if (obj == IntPtr.Zero) return;
        Pdfium.FPDFText_SetText(obj, text);
        Pdfium.FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, 255);
        Pdfium.FPDFPageObj_Transform(obj, 1, 0, 0, 1, x, baseline);
        Pdfium.FPDFPage_InsertObject(page, obj);
    }

    // MARK: Markup Notes

    /// <summary>"Markup Notes", then "3. Page 2 · Highlight" and the text for each, over as many pages as it needs.</summary>
    private static void DrawList(IntPtr doc, int insertAt, List<(Markup Markup, int Number)> items, double width, double height,
        IntPtr font, IntPtr bold, MeasureText measure)
    {
        var lines = new List<(string Text, double Size, bool Bold, double Before)>
        {
            ("Markup Notes", 16, true, 0),
        };
        foreach (var (markup, number) in items)
        {
            lines.Add(($"{number}. Page {markup.PageIndex + 1} · {MarkupKinds.Name(markup.Kind)}", 11, true, 14));
            foreach (var line in Wrap(markup.Text.Trim(), width - Margin * 2, 11, false, measure)) lines.Add((line, 11, false, 0));
        }

        var index = insertAt;
        IntPtr page = IntPtr.Zero;
        double y = 0;
        void NewPage()
        {
            if (page != IntPtr.Zero) { Pdfium.FPDFPage_GenerateContent(page); Pdfium.FPDF_ClosePage(page); }
            page = Pdfium.FPDFPage_New(doc, index++, width, height);
            y = height - Margin;
        }
        NewPage();
        foreach (var (text, size, isBold, before) in lines)
        {
            var lineHeight = size * 1.3;
            if (y - before - lineHeight < Margin) NewPage();
            else y -= before;
            y -= lineHeight;
            Text(doc, page, isBold ? bold : font, text, size, Margin, y + size * 0.25, (0, 0, 0));
        }
        Pdfium.FPDFPage_GenerateContent(page);
        Pdfium.FPDF_ClosePage(page);
    }

    /// <summary>Breaks text into lines no wider than the width, at spaces where it can, keeping its line breaks.</summary>
    public static List<string> Wrap(string text, double width, double size, bool bold, MeasureText measure)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (measure(candidate, size, bold) <= width) { line = candidate; continue; }
                if (line.Length > 0) lines.Add(line);
                // A word too long for a line is broken where it must be.
                line = word;
                while (line.Length > 1 && measure(line, size, bold) > width)
                {
                    var cut = line.Length - 1;
                    while (cut > 1 && measure(line[..cut], size, bold) > width) cut--;
                    lines.Add(line[..cut]);
                    line = line[cut..];
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    // MARK: Saving

    /// <summary>The document as PDF bytes. Call inside <see cref="Pdfium.Lock"/>.</summary>
    public static byte[] Save(IntPtr doc)
    {
        using var output = new MemoryStream();
        Pdfium.WriteBlockHandler write = (_, data, size) =>
        {
            var chunk = new byte[size];
            Marshal.Copy(data, chunk, 0, (int)size);
            output.Write(chunk);
            return 1;
        };
        var writer = new Pdfium.FileWrite { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(write) };
        var saved = Pdfium.FPDF_SaveAsCopy(doc, ref writer, 0);
        GC.KeepAlive(write);
        if (!saved) throw new PdfOpenException("The marked-up copy couldn't be made.");
        return output.ToArray();
    }
}
