using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Markups;

/// <summary>A point in a PDF page's own coordinates: points (1/72 inch), y going up.</summary>
public readonly record struct PagePoint(double X, double Y);

/// <summary>A rectangle in page coordinates. Y is its bottom edge.</summary>
public readonly record struct PageRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Top => Y + Height;

    public PageRect Inflate(double by) => new(X - by, Y - by, Width + by * 2, Height + by * 2);

    public bool Contains(PagePoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Top;
}

public static class MarkupKinds
{
    public const string Note = "note", Highlight = "highlight", Arrow = "arrow";

    /// <summary>The colours a markup can be.</summary>
    public static readonly IReadOnlyList<string> Colors = ["yellow", "orange", "red", "pink", "green", "blue"];

    public static string DefaultColor(string kind) => kind == Arrow ? "red" : "yellow";

    public static string Name(string kind) => kind switch { Note => "Note", Highlight => "Highlight", Arrow => "Arrow", _ => kind };
}

/// <summary>Sizes and hit testing for PDF markups, in page points (behaviour.md, "PDF markups").</summary>
public static class MarkupGeometry
{
    public const double NoteWidth = 180, NotePadding = 8, NoteMinimumHeight = 32, NoteFontSize = 12;
    public const double HighlightWidth = 14, ArrowWidth = 3, ArrowHeadLength = 14, BadgeDiameter = 18;
    /// <summary>Strokes shorter than this are treated as clicks.</summary>
    public const double MinimumStrokeLength = 8;
    public const double ArrowSpread = Math.PI / 7;

    /// <summary>A note's height for text whose laid-out height (at the note's text width) is <paramref name="textHeight"/>.</summary>
    public static double NoteHeight(double textHeight) => Math.Max(NoteMinimumHeight, Math.Ceiling(textHeight) + NotePadding * 2);

    public static double NoteTextWidth => NoteWidth - NotePadding * 2;

    /// <summary>The area a stroke covers, grown by half its width.</summary>
    public static PageRect Bounds(IReadOnlyList<PagePoint> points, double lineWidth)
    {
        if (points.Count == 0) return default;
        var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
        return new PageRect(minX, minY, maxX - minX, maxY - minY).Inflate(lineWidth / 2);
    }

    public static double Length(IReadOnlyList<PagePoint> points)
    {
        double total = 0;
        for (var i = 1; i < points.Count; i++) total += Distance(points[i - 1], points[i]);
        return total;
    }

    /// <summary>Drops any point closer than 1.5 to the last kept one, always keeping the end.</summary>
    public static List<PagePoint> Simplified(IReadOnlyList<PagePoint> points, double spacing = 1.5)
    {
        if (points.Count == 0) return [];
        var last = points[0];
        var result = new List<PagePoint> { last };
        foreach (var point in points.Skip(1))
        {
            if (Distance(point, last) < spacing) continue;
            result.Add(point);
            last = point;
        }
        if (points[^1] != result[^1]) result.Add(points[^1]);
        return result;
    }

    public static PagePoint Clamp(PagePoint p, PageRect page) =>
        new(Math.Min(Math.Max(p.X, page.X), page.Right), Math.Min(Math.Max(p.Y, page.Y), page.Top));

    /// <summary>The offset limited so the rectangle stays on the page, unless it's bigger than the page.</summary>
    public static (double Dx, double Dy) ClampedOffset(double dx, double dy, PageRect rect, PageRect page)
    {
        var x = Math.Min(Math.Max(dx, page.X - rect.X), page.Right - rect.Right);
        var y = Math.Min(Math.Max(dy, page.Y - rect.Y), page.Top - rect.Top);
        return (rect.Width > page.Width ? dx : x, rect.Height > page.Height ? dy : y);
    }

    /// <summary>Whether a click lands on a markup. The tolerance is in page points, so it can grow when zoomed out.</summary>
    public static bool Hits(PagePoint point, string kind, PageRect rect, IReadOnlyList<PagePoint> points, double tolerance) => kind switch
    {
        MarkupKinds.Note => rect.Inflate(tolerance).Contains(point),
        MarkupKinds.Highlight => DistanceToLine(point, points) <= HighlightWidth / 2 + tolerance,
        _ => DistanceToLine(point, points) <= ArrowWidth / 2 + tolerance,
    };

    /// <summary>The badge sits at a note's top-left corner, or at a stroke's first point.</summary>
    public static PagePoint BadgeCentre(Markup markup) =>
        markup.Kind == MarkupKinds.Note || markup.Points.Length < 2
            ? new PagePoint(markup.X, markup.Y + markup.Height)
            : new PagePoint(markup.Points[0], markup.Points[1]);

    public static List<PagePoint> PointsOf(Markup markup)
    {
        var points = new List<PagePoint>();
        for (var i = 0; i + 1 < markup.Points.Length; i += 2) points.Add(new PagePoint(markup.Points[i], markup.Points[i + 1]));
        return points;
    }

    public static double[] Flatten(IEnumerable<PagePoint> points) => points.SelectMany(p => new[] { p.X, p.Y }).ToArray();

    public static PageRect RectOf(Markup markup) => new(markup.X, markup.Y, markup.Width, markup.Height);

    /// <summary>The two lines of an arrow's head, from the tip: (tip, left, right).</summary>
    public static (PagePoint Tip, PagePoint Left, PagePoint Right, PagePoint LineEnd) ArrowHead(PagePoint start, PagePoint end)
    {
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var head = ArrowHeadLength;
        // The line stops short of the tip so it doesn't poke through the head.
        var lineEnd = new PagePoint(end.X - Math.Cos(angle) * head * 0.8, end.Y - Math.Sin(angle) * head * 0.8);
        return (end,
            new PagePoint(end.X - Math.Cos(angle - ArrowSpread) * head, end.Y - Math.Sin(angle - ArrowSpread) * head),
            new PagePoint(end.X - Math.Cos(angle + ArrowSpread) * head, end.Y - Math.Sin(angle + ArrowSpread) * head),
            lineEnd);
    }

    private static double Distance(PagePoint a, PagePoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double DistanceToLine(PagePoint p, IReadOnlyList<PagePoint> points)
    {
        if (points.Count == 0) return double.PositiveInfinity;
        if (points.Count == 1) return Distance(p, points[0]);
        var best = double.PositiveInfinity;
        for (var i = 1; i < points.Count; i++)
        {
            var (a, b) = (points[i - 1], points[i]);
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
            best = Math.Min(best, Distance(p, new PagePoint(a.X + t * dx, a.Y + t * dy)));
        }
        return best;
    }
}

/// <summary>Creating, moving, editing and deleting the markups on a PDF. The file itself is never touched.</summary>
public static class PdfMarkups
{
    /// <summary>A PDF's markups in number order: by page, then by when they were made. The first is 1.</summary>
    public static List<Markup> Sorted(IEnumerable<Markup> markups) =>
        markups.OrderBy(m => m.PageIndex).ThenBy(m => m.CreatedAt).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();

    /// <summary>An empty note with its top-left corner at the point, moved to stay on the page.</summary>
    public static Markup AddNote(LibraryStore store, string referenceId, int page, PagePoint at, PageRect pageBounds, string color, double emptyTextHeight)
    {
        var height = MarkupGeometry.NoteHeight(emptyTextHeight);
        var rect = new PageRect(at.X, at.Y - height, MarkupGeometry.NoteWidth, height);
        var (dx, dy) = MarkupGeometry.ClampedOffset(0, 0, rect, pageBounds);
        var markup = New(referenceId, page, MarkupKinds.Note, color, rect with { X = rect.X + dx, Y = rect.Y + dy }, []);
        store.InsertMarkup(markup);
        return markup;
    }

    /// <summary>A highlight along the points. Null for a stroke too short to be deliberate.</summary>
    public static Markup? AddHighlight(LibraryStore store, string referenceId, int page, IReadOnlyList<PagePoint> points, PageRect pageBounds, string color)
    {
        var simplified = MarkupGeometry.Simplified(points.Select(p => MarkupGeometry.Clamp(p, pageBounds)).ToList());
        if (MarkupGeometry.Length(simplified) < MarkupGeometry.MinimumStrokeLength) return null;
        var markup = New(referenceId, page, MarkupKinds.Highlight, color, MarkupGeometry.Bounds(simplified, MarkupGeometry.HighlightWidth), simplified);
        store.InsertMarkup(markup);
        return markup;
    }

    /// <summary>An arrow from its tail to its tip. Null for one too short to be deliberate.</summary>
    public static Markup? AddArrow(LibraryStore store, string referenceId, int page, PagePoint start, PagePoint end, PageRect pageBounds, string color)
    {
        var points = new[] { MarkupGeometry.Clamp(start, pageBounds), MarkupGeometry.Clamp(end, pageBounds) };
        if (MarkupGeometry.Length(points) < MarkupGeometry.MinimumStrokeLength) return null;
        var markup = New(referenceId, page, MarkupKinds.Arrow, color, MarkupGeometry.Bounds(points, MarkupGeometry.ArrowHeadLength), points);
        store.InsertMarkup(markup);
        return markup;
    }

    /// <summary>Moves a markup as far as its page allows.</summary>
    public static void Move(LibraryStore store, Markup markup, double dx, double dy, PageRect pageBounds)
    {
        (dx, dy) = MarkupGeometry.ClampedOffset(dx, dy, MarkupGeometry.RectOf(markup), pageBounds);
        if (dx == 0 && dy == 0) return;
        markup.X += dx;
        markup.Y += dy;
        markup.Points = MarkupGeometry.Flatten(MarkupGeometry.PointsOf(markup).Select(p => new PagePoint(p.X + dx, p.Y + dy)));
        markup.UpdatedAt = IsoDate.Now();
        store.UpdateMarkup(markup);
    }

    /// <summary>
    /// Sets the text. A note's height grows or shrinks to fit it, keeping its top
    /// edge in place; <paramref name="textHeight"/> is the text's laid-out height.
    /// </summary>
    public static void SetText(LibraryStore store, Markup markup, string text, double textHeight)
    {
        if (markup.Text == text) return;
        markup.Text = text;
        if (markup.Kind == MarkupKinds.Note)
        {
            var top = markup.Y + markup.Height;
            markup.Width = MarkupGeometry.NoteWidth;
            markup.Height = MarkupGeometry.NoteHeight(textHeight);
            markup.Y = top - markup.Height;
        }
        markup.UpdatedAt = IsoDate.Now();
        store.UpdateMarkup(markup);
    }

    public static void SetColor(LibraryStore store, Markup markup, string color)
    {
        if (markup.Color == color) return;
        markup.Color = color;
        markup.UpdatedAt = IsoDate.Now();
        store.UpdateMarkup(markup);
    }

    /// <summary>Highlights and arrows with text: they keep their number in an export, and are listed in Markup Notes.</summary>
    public static bool IsListed(Markup markup) => markup.Kind != MarkupKinds.Note && markup.Text.Trim().Length > 0;

    private static Markup New(string referenceId, int page, string kind, string color, PageRect rect, IReadOnlyList<PagePoint> points)
    {
        var now = IsoDate.Now();
        return new Markup
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ReferenceId = referenceId, PageIndex = page,
            Kind = kind, Color = color, X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height,
            Points = MarkupGeometry.Flatten(points), Text = "",
        };
    }
}
