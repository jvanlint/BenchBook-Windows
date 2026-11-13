using BenchBook.Core.Markups;

namespace BenchBook.Core.Tests;

public class ImageNoteTests
{
    [Fact]
    public void BoxesAreStoredAsFractionsFromTheTopLeft()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var image = sample.References[0];

        var note = ImageNotes.Add(store, image.Id, new PixelRect(400, 300, 200, 150), 4000, 3000)!;
        Assert.Equal((0.1, 0.1, 0.05, 0.05), (note.X, note.Y, note.Width, note.Height));
        Assert.Equal(("note", "yellow", 0), (note.Kind, note.Color, note.PageIndex));
        Assert.Equal(new PixelRect(400, 300, 200, 150), NoteGeometry.ToPixels(note, 4000, 3000));
        Assert.Equal(2, store.Markups(image.Id).Count);
    }

    [Fact]
    public void ASmallBoxIsAClickAndBoxesStayInsideTheImage()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        Assert.Null(ImageNotes.Add(store, sample.References[0].Id, new PixelRect(10, 10, 7, 40), 100, 100));

        // A box dragged past the edge is kept inside, keeping its size where it fits.
        Assert.Equal(new PixelRect(80, 0, 20, 30), NoteGeometry.Clamp(new PixelRect(90, -10, 20, 30), 100, 100));
        Assert.Equal(new PixelRect(0, 0, 100, 100), NoteGeometry.Clamp(new PixelRect(-50, -50, 300, 300), 100, 100));
        Assert.Equal(new PixelRect(20, 10, 60, 90), NoteGeometry.FromDrag(80, 10, 20, 150, 100, 100));
    }

    [Fact]
    public void CornersResizeAgainstTheOppositeCornerAndMovesStayInside()
    {
        var box = new PixelRect(20, 20, 40, 30);
        Assert.Equal(Corner.BottomRight, NoteGeometry.CornerAt(61, 49, box, 4));
        Assert.Null(NoteGeometry.CornerAt(40, 35, box, 4));
        Assert.Equal(new PixelRect(10, 10, 10, 40), NoteGeometry.Resize(box, Corner.TopRight, 10, 10, 100, 100));
        Assert.Equal(new PixelRect(60, 70, 40, 30), NoteGeometry.Move(box, 70, 70, 100, 100));
    }

    [Fact]
    public void NotesNumberInTheOrderTheyWereAdded()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var id = sample.References[0].Id;
        var first = ImageNotes.Add(store, id, new PixelRect(0, 0, 20, 20), 100, 100)!;
        Thread.Sleep(5);
        var second = ImageNotes.Add(store, id, new PixelRect(0, 0, 20, 20), 100, 100)!;
        ImageNotes.SetText(store, second, "Discolouration on the gear door");
        var sorted = ImageNotes.Sorted(store.Markups(id)).Where(m => m.Id == first.Id || m.Id == second.Id).ToList();
        Assert.Equal([first.Id, second.Id], sorted.Select(m => m.Id));
        Assert.Equal("Discolouration on the gear door", sorted[1].Text);

        ImageNotes.Move(store, second, new PixelRect(50, 50, 20, 20), 100, 100);
        Assert.Equal((0.5, 0.5), (store.Markups(id).Single(m => m.Id == second.Id).X, store.Markups(id).Single(m => m.Id == second.Id).Y));
        store.DeleteMarkups([second.Id]);
        Assert.Equal(2, store.Markups(id).Count);
    }
}
