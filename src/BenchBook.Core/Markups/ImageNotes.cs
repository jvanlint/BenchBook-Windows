using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Markups;

/// <summary>A rectangle in image pixels, origin at the top left, y going down.</summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Note boxes on images (behaviour.md, "Image notes"): stored as fractions of
/// the image from its top-left corner, kept inside the image, and treated as a
/// click when smaller than 8 × 8 image pixels.
/// </summary>
public static class NoteGeometry
{
    public const double MinimumSide = 8;

    public static PixelRect ToPixels(Markup note, double imageWidth, double imageHeight) =>
        new(note.X * imageWidth, note.Y * imageHeight, note.Width * imageWidth, note.Height * imageHeight);

    /// <summary>Pixels to fractions, kept inside the image.</summary>
    public static (double X, double Y, double Width, double Height) ToFractions(PixelRect rect, double imageWidth, double imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return (0, 0, 0, 0);
        var r = Clamp(rect, imageWidth, imageHeight);
        return (r.X / imageWidth, r.Y / imageHeight, r.Width / imageWidth, r.Height / imageHeight);
    }

    /// <summary>Keeps the rectangle inside the image, keeping its size where it fits.</summary>
    public static PixelRect Clamp(PixelRect rect, double imageWidth, double imageHeight)
    {
        var x = Math.Min(rect.X, rect.Right);
        var y = Math.Min(rect.Y, rect.Bottom);
        var width = Math.Min(Math.Abs(rect.Width), imageWidth);
        var height = Math.Min(Math.Abs(rect.Height), imageHeight);
        x = Math.Min(Math.Max(x, 0), imageWidth - width);
        y = Math.Min(Math.Max(y, 0), imageHeight - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>The rectangle a drag spans, with both ends kept inside the image.</summary>
    public static PixelRect FromDrag(double x1, double y1, double x2, double y2, double imageWidth, double imageHeight)
    {
        static double Inside(double v, double max) => Math.Min(Math.Max(v, 0), max);
        var (ax, ay, bx, by) = (Inside(x1, imageWidth), Inside(y1, imageHeight), Inside(x2, imageWidth), Inside(y2, imageHeight));
        return new PixelRect(Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(ax - bx), Math.Abs(ay - by));
    }

    public static bool IsLargeEnough(PixelRect rect) => rect.Width >= MinimumSide && rect.Height >= MinimumSide;

    public static (double X, double Y) Point(Corner corner, PixelRect rect) => corner switch
    {
        Corner.TopLeft => (rect.X, rect.Y),
        Corner.TopRight => (rect.Right, rect.Y),
        Corner.BottomLeft => (rect.X, rect.Bottom),
        _ => (rect.Right, rect.Bottom),
    };

    /// <summary>The corner within <paramref name="tolerance"/> of the point, if any.</summary>
    public static Corner? CornerAt(double x, double y, PixelRect rect, double tolerance)
    {
        foreach (var corner in Enum.GetValues<Corner>())
        {
            var (cx, cy) = Point(corner, rect);
            if (Math.Abs(cx - x) <= tolerance && Math.Abs(cy - y) <= tolerance) return corner;
        }
        return null;
    }

    /// <summary>The rectangle with one corner dragged to the point; the opposite corner stays put.</summary>
    public static PixelRect Resize(PixelRect rect, Corner corner, double x, double y, double imageWidth, double imageHeight)
    {
        var (ax, ay) = corner switch
        {
            Corner.TopLeft => (rect.Right, rect.Bottom),
            Corner.TopRight => (rect.X, rect.Bottom),
            Corner.BottomLeft => (rect.Right, rect.Y),
            _ => (rect.X, rect.Y),
        };
        return FromDrag(ax, ay, x, y, imageWidth, imageHeight);
    }

    /// <summary>The rectangle moved by an offset, kept inside the image.</summary>
    public static PixelRect Move(PixelRect rect, double dx, double dy, double imageWidth, double imageHeight) =>
        Clamp(rect with { X = rect.X + dx, Y = rect.Y + dy }, imageWidth, imageHeight);
}

/// <summary>Creating, moving, editing and deleting an image's note boxes.</summary>
public static class ImageNotes
{
    /// <summary>An image's notes in the order they were added, which is also their number (the first is 1).</summary>
    public static List<Markup> Sorted(IEnumerable<Markup> markups) => markups.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();

    /// <summary>Adds a note for a box drawn in image pixels, on a reference or a build photo. Null for a box too small to be deliberate.</summary>
    public static Markup? Add(LibraryStore store, string? referenceId, PixelRect drawn, double imageWidth, double imageHeight, string? buildPhotoId = null)
    {
        var rect = NoteGeometry.Clamp(drawn, imageWidth, imageHeight);
        if (!NoteGeometry.IsLargeEnough(rect)) return null;
        var (x, y, w, h) = NoteGeometry.ToFractions(rect, imageWidth, imageHeight);
        var now = IsoDate.Now();
        var note = new Markup
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ReferenceId = referenceId, BuildPhotoId = buildPhotoId,
            PageIndex = 0, Kind = "note", Color = "yellow", X = x, Y = y, Width = w, Height = h, Points = [], Text = "",
        };
        store.InsertMarkup(note);
        return note;
    }

    public static void Move(LibraryStore store, Markup note, PixelRect rect, double imageWidth, double imageHeight)
    {
        var (x, y, w, h) = NoteGeometry.ToFractions(rect, imageWidth, imageHeight);
        if ((note.X, note.Y, note.Width, note.Height) == (x, y, w, h)) return;
        (note.X, note.Y, note.Width, note.Height) = (x, y, w, h);
        note.UpdatedAt = IsoDate.Now();
        store.UpdateMarkup(note);
    }

    public static void SetText(LibraryStore store, Markup note, string text)
    {
        if (note.Text == text) return;
        note.Text = text;
        note.UpdatedAt = IsoDate.Now();
        store.UpdateMarkup(note);
    }
}
