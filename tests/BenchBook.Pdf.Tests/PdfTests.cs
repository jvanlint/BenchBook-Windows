using System.Text;
using BenchBook.Core.Markups;
using BenchBook.Core.Model;

namespace BenchBook.Pdf.Tests;

/// <summary>PDFs made with PDFium itself, so the tests need no files.</summary>
internal static class TestPdfs
{
    /// <summary>One page per entry, each with a line of text at (72, 700) in 24 pt Helvetica.</summary>
    public static byte[] Make(params (string Text, int Rotation, float[]? Crop)[] pages)
    {
        Pdfium.EnsureInitialised();
        lock (Pdfium.Lock)
        {
            var doc = Pdfium.FPDF_CreateNewDocument();
            try
            {
                for (var i = 0; i < pages.Length; i++)
                {
                    var page = Pdfium.FPDFPage_New(doc, i, 612, 792);
                    var text = Pdfium.FPDFPageObj_NewTextObj(doc, "Helvetica", 24);
                    Pdfium.FPDFText_SetText(text, pages[i].Text);
                    Pdfium.FPDFPageObj_Transform(text, 1, 0, 0, 1, 72, 700);
                    Pdfium.FPDFPage_InsertObject(page, text);
                    if (pages[i].Crop is { } c) Pdfium.FPDFPage_SetCropBox(page, c[0], c[1], c[2], c[3]);
                    Pdfium.FPDFPage_SetRotation(page, pages[i].Rotation);
                    Pdfium.FPDFPage_GenerateContent(page);
                    Pdfium.FPDF_ClosePage(page);
                }
                return PdfExporter.Save(doc);
            }
            finally
            {
                Pdfium.FPDF_CloseDocument(doc);
            }
        }
    }

    /// <summary>Roughly Arial's widths: about half the font size per character.</summary>
    public static double Measure(string text, double size, bool bold) => text.Length * size * (bold ? 0.56 : 0.5);

    public static byte[] Font(string name) => File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), name));

    public static Markup Markup(string kind, int page, double x, double y, double w, double h, double[] points, string text, string color = "yellow", int order = 0) => new()
    {
        Id = Ids.New(), CreatedAt = new DateTimeOffset(2026, 11, 13, 9, 0, order, TimeSpan.Zero), UpdatedAt = DateTimeOffset.UtcNow,
        ReferenceId = Ids.New(), PageIndex = page, Kind = kind, Color = color, X = x, Y = y, Width = w, Height = h, Points = points, Text = text,
    };
}

public class PdfTests
{
    [Fact]
    public void PagesReportTheirCropBoxAndRotation()
    {
        var bytes = TestPdfs.Make(("Cockpit", 0, null), ("Gear", 1, [36, 72, 576, 720]));
        using var pdf = PdfDocument.Open(bytes);
        Assert.Equal(2, pdf.Pages.Count);
        Assert.Equal((new PageRect(0, 0, 612, 792), 0), (pdf.Pages[0].Crop, pdf.Pages[0].Rotation));
        Assert.Equal((new PageRect(36, 72, 540, 648), 1), (pdf.Pages[1].Crop, pdf.Pages[1].Rotation));
        // A quarter-turned page is shown on its side.
        Assert.Equal((648.0, 540.0), (pdf.Pages[1].ShownWidth, pdf.Pages[1].ShownHeight));
    }

    [Fact]
    public void APageRendersWithItsText()
    {
        using var pdf = PdfDocument.Open(TestPdfs.Make(("Main gear well", 0, null)));
        var pixels = pdf.Render(0, 306, 396);
        Assert.Equal(306 * 396 * 4, pixels.Length);
        var dark = 0;
        for (var i = 0; i < pixels.Length; i += 4) if (pixels[i] < 128) dark++;
        Assert.InRange(dark, 50, 306 * 396 / 4); // Some text, on a mostly white page.
    }

    [Fact]
    public void FindReturnsMatchesWithTheirRectanglesOnEachPage()
    {
        using var pdf = PdfDocument.Open(TestPdfs.Make(("Main gear well", 0, null), ("No match here", 0, null), ("GEAR doors", 0, null)));
        var matches = pdf.Find("gear");
        Assert.Equal([0, 2], matches.Select(m => m.Page));
        var rect = Assert.Single(matches[0].Rects);
        Assert.InRange(rect.X, 100, 200);
        Assert.InRange(rect.Y, 690, 730);
        Assert.Empty(pdf.Find("spitfire"));
        Assert.Empty(pdf.Find("  "));
    }

    [Fact]
    public void DamagedFilesAreRefused()
    {
        Assert.Throws<PdfOpenException>(() => PdfDocument.Open("%PDF-1.7 not really"u8.ToArray()));
    }

    [Fact]
    public void ExportDrawsMarkupsAndListsTheOnesWithText()
    {
        var original = TestPdfs.Make(("Instructions step 1", 0, null), ("Step 2", 1, null));
        var copy = original.ToArray();
        Markup[] markups =
        [
            TestPdfs.Markup("note", 0, 300, 600, 180, 46, [], "Fit the seat after painting", order: 1),
            TestPdfs.Markup("highlight", 0, 65, 693, 214, 14, [72, 700, 272, 700], "Check the step order", order: 2),
            TestPdfs.Markup("arrow", 1, 93, 93, 114, 64, [100, 100, 200, 150], "", "red", order: 3),
        ];
        var exported = PdfExporter.Export(original, markups, TestPdfs.Font("arial.ttf"), TestPdfs.Font("arialbd.ttf"), TestPdfs.Measure);

        Assert.Equal(copy, original); // The original's bytes are untouched.
        using var pdf = PdfDocument.Open(exported);
        Assert.Equal(3, pdf.Pages.Count); // Two pages, then Markup Notes.
        Assert.Equal(1, pdf.Pages[1].Rotation); // Pages keep their rotation.
        Assert.Contains(pdf.Find("Fit the seat"), m => m.Page == 0); // A note's text is on its page.
        var list = pdf.Find("Markup Notes");
        Assert.Equal(2, Assert.Single(list).Page);
        // The heading's middle dot draws correctly, but PDFium's text map for an embedded
        // font reads it back as a look-alike character, so search for the parts around it.
        Assert.Contains(pdf.Find("2. Page 1"), m => m.Page == 2);
        Assert.Contains(pdf.Find("Highlight"), m => m.Page == 2);
        Assert.Single(pdf.Find("Check the step order"));
        Assert.Empty(pdf.Find("Page 2 · Arrow")); // An arrow without text isn't listed.
    }

    [Fact]
    public void WithNothingToListThereAreNoExtraPages()
    {
        var original = TestPdfs.Make(("Page", 0, null));
        var exported = PdfExporter.Export(original, [TestPdfs.Markup("note", 0, 10, 10, 180, 32, [], "Only a note")],
            TestPdfs.Font("arial.ttf"), TestPdfs.Font("arialbd.ttf"), TestPdfs.Measure);
        using var pdf = PdfDocument.Open(exported);
        Assert.Single(pdf.Pages);
    }

    [Fact]
    public void TextWrapsAtSpacesAndBreaksLongWords()
    {
        // At 12 pt, each character is 6 wide here, so 60 fits 10 characters.
        Assert.Equal(["Paint the", "wheel", "wells", "first"], PdfExporter.Wrap("Paint the wheel wells first", 60, 12, false, TestPdfs.Measure));
        Assert.Equal(["ABCDEFGHIJ", "KLMNO"], PdfExporter.Wrap("ABCDEFGHIJKLMNO", 60, 12, false, TestPdfs.Measure));
        Assert.Equal(["one", "", "two"], PdfExporter.Wrap("one\r\n\ntwo", 60, 12, false, TestPdfs.Measure));
    }
}
