using BenchBook.Core.Markups;

namespace BenchBook.Core.Tests;

public class PdfMarkupTests
{
    private static readonly PageRect Page = new(0, 0, 612, 792);

    [Fact]
    public void ANoteStartsAtTheClickAndStaysOnThePage()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var pdf = sample.References[1].Id;

        var note = PdfMarkups.AddNote(store, pdf, 0, new PagePoint(100, 700), Page, "yellow", emptyTextHeight: 15);
        Assert.Equal((100, 668, 180, 32), (note.X, note.Y, note.Width, note.Height)); // Top-left at the click.

        var corner = PdfMarkups.AddNote(store, pdf, 0, new PagePoint(600, 10), Page, "blue", 15);
        Assert.Equal((432, 0), (corner.X, corner.Y)); // Moved to stay on the page.
    }

    [Fact]
    public void ANoteGrowsToFitItsTextKeepingItsTopEdge()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var note = PdfMarkups.AddNote(store, sample.References[1].Id, 0, new PagePoint(100, 700), Page, "yellow", 15);
        PdfMarkups.SetText(store, note, "Paint the wheel wells XF-2 before fitting the doors.", textHeight: 44.2);
        Assert.Equal((61.0, 639.0, 700.0), (note.Height, note.Y, note.Y + note.Height));
        PdfMarkups.SetText(store, note, "", 15);
        Assert.Equal((32.0, 700.0), (note.Height, note.Y + note.Height));
    }

    [Fact]
    public void HighlightsAreSimplifiedAndBoundedByHalfTheirWidth()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var points = new List<PagePoint> { new(100, 500), new(100.5, 500), new(101, 500), new(102, 500), new(150, 500), new(150.2, 500) };
        var highlight = PdfMarkups.AddHighlight(store, sample.References[1].Id, 1, points, Page, "yellow")!;
        Assert.Equal([100, 500, 102, 500, 150, 500, 150.2, 500], highlight.Points);
        Assert.Equal((93.0, 493.0, 64.2, 14.0), (highlight.X, highlight.Y, Math.Round(highlight.Width, 6), highlight.Height));

        Assert.Null(PdfMarkups.AddHighlight(store, sample.References[1].Id, 1, [new(10, 10), new(15, 10)], Page, "yellow"));
    }

    [Fact]
    public void ArrowsKeepTwoPointsAndAreBoundedByHalfTheirHead()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var arrow = PdfMarkups.AddArrow(store, sample.References[1].Id, 0, new PagePoint(100, 100), new PagePoint(200, 150), Page, "red")!;
        Assert.Equal([100, 100, 200, 150], arrow.Points);
        Assert.Equal((93.0, 93.0, 114.0, 64.0), (arrow.X, arrow.Y, arrow.Width, arrow.Height));
        Assert.Null(PdfMarkups.AddArrow(store, sample.References[1].Id, 0, new PagePoint(1, 1), new PagePoint(4, 4), Page, "red"));

        // Points off the page are pulled back onto it.
        var clamped = PdfMarkups.AddArrow(store, sample.References[1].Id, 0, new PagePoint(-50, 100), new PagePoint(700, 100), Page, "red")!;
        Assert.Equal([0, 100, 612, 100], clamped.Points);
    }

    [Fact]
    public void MovingKeepsAMarkupOnThePageUnlessItsBigger()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var arrow = PdfMarkups.AddArrow(store, sample.References[1].Id, 0, new PagePoint(100, 100), new PagePoint(200, 150), Page, "red")!;
        PdfMarkups.Move(store, arrow, -500, 10, Page);
        Assert.Equal((0.0, 103.0), (arrow.X, arrow.Y));
        Assert.Equal([7, 110, 107, 160], arrow.Points);

        Assert.Equal((-50.0, 0.0), MarkupGeometry.ClampedOffset(-50, 0, new PageRect(10, 10, 700, 20), Page));
    }

    [Fact]
    public void ClicksHitNotesByAreaAndStrokesByDistance()
    {
        Assert.True(MarkupGeometry.Hits(new PagePoint(5, 5), MarkupKinds.Note, new PageRect(10, 10, 20, 20), [], 6));
        Assert.False(MarkupGeometry.Hits(new PagePoint(0, 0), MarkupKinds.Note, new PageRect(10, 10, 20, 20), [], 6));
        PagePoint[] line = [new(0, 0), new(100, 0)];
        Assert.True(MarkupGeometry.Hits(new PagePoint(50, 9), MarkupKinds.Highlight, default, line, 2));
        Assert.False(MarkupGeometry.Hits(new PagePoint(50, 9), MarkupKinds.Arrow, default, line, 2));
    }

    [Fact]
    public void MarkupsNumberByPageThenTimeAndOnlyStrokesWithTextAreListed()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var pdf = sample.References[1].Id; // Already has a highlight on page 3 (index 2).
        var first = PdfMarkups.AddNote(store, pdf, 0, new PagePoint(10, 700), Page, "yellow", 15);
        var sorted = PdfMarkups.Sorted(store.Markups(pdf));
        Assert.Equal([first.Id, sample.Markups[0].Id], sorted.Select(m => m.Id));
        Assert.True(PdfMarkups.IsListed(sorted[1]));
        PdfMarkups.SetText(store, first, "A note", 15);
        Assert.False(PdfMarkups.IsListed(first));
        Assert.Equal(new PagePoint(10, 700), MarkupGeometry.BadgeCentre(first));
        Assert.Equal(new PagePoint(72.5, 647.25), MarkupGeometry.BadgeCentre(sorted[1]));
    }
}
