namespace BenchBook.Core.Model;

// The record types mirror library.json (see library-format.md). Kinds, types,
// statuses and colours are kept as raw text, so a value this version doesn't
// know is written back unchanged.

public abstract class LibraryRecord
{
    public required string Id { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optional text is missing when empty, never "".</summary>
    internal static string? Optional(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>Puts ids in canonical form and empties optional text to null.</summary>
    internal virtual void Tidy()
    {
        Id = Ids.Normalize(Id) ?? Id;
        CreatedAt = IsoDate.TruncateToMilliseconds(CreatedAt);
        UpdatedAt = IsoDate.TruncateToMilliseconds(UpdatedAt);
    }

    internal static string? TidyId(string? id) => id is null ? null : Ids.Normalize(id) ?? id;

    internal static DateTimeOffset? TidyDate(DateTimeOffset? date) =>
        date is { } value ? IsoDate.TruncateToMilliseconds(value) : null;
}

public static class ProjectStatus
{
    public const string Researching = "researching";
    public const string InProgress = "inProgress";
    public const string Paused = "paused";
    public const string Completed = "completed";

    public static readonly IReadOnlyList<string> All = [Researching, InProgress, Paused, Completed];
}

public sealed class Project : LibraryRecord
{
    public required string Name { get; set; }
    public required string Manufacturer { get; set; }
    public required string Subject { get; set; }
    public required string Scale { get; set; }
    public string? KitNumber { get; set; }
    public string? MarkingsScheme { get; set; }
    public string? Notes { get; set; }
    /// <summary>Missing means researching.</summary>
    public string? Status { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        KitNumber = Optional(KitNumber);
        MarkingsScheme = Optional(MarkingsScheme);
        Notes = Optional(Notes);
        Status = Optional(Status);
    }
}

public static class ReferenceKind
{
    public const string Image = "image";
    public const string Pdf = "pdf";
    public const string Markdown = "markdown";
    public const string Webpage = "webpage";
    public const string VideoLink = "videoLink";
    public const string TextSnippet = "textSnippet";
    public const string Note = "note";

    /// <summary>Kinds whose reference must have a stored file.</summary>
    public static bool HasAsset(string kind) => kind is Image or Pdf or Markdown;
}

public sealed class Reference : LibraryRecord
{
    public required string ProjectId { get; set; }
    public required string Kind { get; set; }
    public string? Title { get; set; }
    public string? Note { get; set; }
    public string? Text { get; set; }
    public string? SourcePageUrl { get; set; }
    public string? OriginalResourceUrl { get; set; }
    public required DateTimeOffset CapturedAt { get; set; }
    public string? AssetId { get; set; }
    public string? VideoChannel { get; set; }
    public string? VideoChannelUrl { get; set; }
    public double? VideoDurationSeconds { get; set; }
    public DateTimeOffset? VideoPublishedAt { get; set; }
    public string? VideoDescription { get; set; }
    public DateTimeOffset? VideoInfoFetchedAt { get; set; }
    public string? LinkSiteName { get; set; }
    public string? LinkDescription { get; set; }
    public DateTimeOffset? LinkPreviewFetchedAt { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        ProjectId = TidyId(ProjectId)!;
        AssetId = TidyId(Optional(AssetId));
        Title = Optional(Title);
        Note = Optional(Note);
        Text = Optional(Text);
        SourcePageUrl = Optional(SourcePageUrl);
        OriginalResourceUrl = Optional(OriginalResourceUrl);
        CapturedAt = IsoDate.TruncateToMilliseconds(CapturedAt);
        VideoChannel = Optional(VideoChannel);
        VideoChannelUrl = Optional(VideoChannelUrl);
        VideoPublishedAt = TidyDate(VideoPublishedAt);
        VideoDescription = Optional(VideoDescription);
        VideoInfoFetchedAt = TidyDate(VideoInfoFetchedAt);
        LinkSiteName = Optional(LinkSiteName);
        LinkDescription = Optional(LinkDescription);
        LinkPreviewFetchedAt = TidyDate(LinkPreviewFetchedAt);
    }
}

public sealed class Asset : LibraryRecord
{
    /// <summary>Relative to the library root, with "/" separators, e.g. Originals/&lt;id&gt;.jpg.</summary>
    public required string OriginalRelativePath { get; set; }
    public string? OriginalFilename { get; set; }
    public required string MimeType { get; set; }
    public required long ByteCount { get; set; }
    public required string Sha256 { get; set; }
    public int? PixelWidth { get; set; }
    public int? PixelHeight { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        OriginalFilename = Optional(OriginalFilename);
        Sha256 = Sha256.ToLowerInvariant();
    }
}

public sealed class Tag : LibraryRecord
{
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    /// <summary>Missing means the app's accent colour.</summary>
    public string? Color { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        Color = Optional(Color);
    }
}

public sealed class ReferenceTag : LibraryRecord
{
    public required string ReferenceId { get; set; }
    public required string TagId { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        ReferenceId = TidyId(ReferenceId)!;
        TagId = TidyId(TagId)!;
    }
}

public sealed class VideoMoment : LibraryRecord
{
    public required string ReferenceId { get; set; }
    public required double OffsetSeconds { get; set; }
    public string? Title { get; set; }
    public string? Note { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        ReferenceId = TidyId(ReferenceId)!;
        Title = Optional(Title);
        Note = Optional(Note);
    }
}

public sealed class Markup : LibraryRecord
{
    public required string ReferenceId { get; set; }
    public required int PageIndex { get; set; }
    public required string Kind { get; set; }
    public required string Color { get; set; }
    public required double X { get; set; }
    public required double Y { get; set; }
    public required double Width { get; set; }
    public required double Height { get; set; }
    /// <summary>Pairs: x1, y1, x2, y2, … Empty for notes.</summary>
    public double[] Points { get; set; } = [];
    public string Text { get; set; } = "";

    internal override void Tidy()
    {
        base.Tidy();
        ReferenceId = TidyId(ReferenceId)!;
        Points ??= [];
        Text ??= "";
    }
}

public sealed class Paint : LibraryRecord
{
    public required string Brand { get; set; }
    public required string Range { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Notes { get; set; }
    public string? SwatchHex { get; set; }
    public required string StandardCode { get; set; }
    public required string IdentityKey { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        Notes = Optional(Notes);
        SwatchHex = Optional(SwatchHex);
    }
}

public sealed class OwnedPaint : LibraryRecord
{
    public required string PaintId { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        PaintId = TidyId(PaintId)!;
    }
}

public sealed class ProjectPaint : LibraryRecord
{
    public required string ProjectId { get; set; }
    public string? PaintId { get; set; }
    public string? Standard { get; set; }
    public string? UsedFor { get; set; }
    public string? Notes { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        ProjectId = TidyId(ProjectId)!;
        PaintId = TidyId(Optional(PaintId));
        Standard = Optional(Standard);
        UsedFor = Optional(UsedFor);
        Notes = Optional(Notes);
    }
}

public sealed class AftermarketItem : LibraryRecord
{
    public required string ProjectId { get; set; }
    public required string ProductName { get; set; }
    public required string Manufacturer { get; set; }
    public string? ProductNumber { get; set; }
    public required string Type { get; set; }
    public required string Status { get; set; }
    public string? ScaleCompatibility { get; set; }
    public string? ProductUrl { get; set; }
    public string? Notes { get; set; }
    public string? ImageAssetId { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        ProjectId = TidyId(ProjectId)!;
        ProductNumber = Optional(ProductNumber);
        ScaleCompatibility = Optional(ScaleCompatibility);
        ProductUrl = Optional(ProductUrl);
        Notes = Optional(Notes);
        ImageAssetId = TidyId(Optional(ImageAssetId));
    }
}
