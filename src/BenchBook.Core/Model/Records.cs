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
        CreatedAt = IsoDate.RoundToMilliseconds(CreatedAt);
        UpdatedAt = IsoDate.RoundToMilliseconds(UpdatedAt);
    }

    internal static string? TidyId(string? id) => id is null ? null : Ids.Normalize(id) ?? id;

    internal static DateTimeOffset? TidyDate(DateTimeOffset? date) =>
        date is { } value ? IsoDate.RoundToMilliseconds(value) : null;
}

public static class ProjectStatus
{
    public const string Researching = "researching";
    public const string InProgress = "inProgress";
    public const string Paused = "paused";
    public const string Completed = "completed";

    public static readonly IReadOnlyList<string> All = [Researching, InProgress, Paused, Completed];
}

/// <summary>
/// A build. Since schema 4 its kits are the kits whose projectId is its id; the
/// manufacturer, scale and kit number it used to have went into its notes.
/// </summary>
public sealed class Project : LibraryRecord
{
    public required string Name { get; set; }
    public required string Subject { get; set; }
    public string? MarkingsScheme { get; set; }
    public string? Notes { get; set; }
    /// <summary>Missing means researching.</summary>
    public string? Status { get; set; }
    /// <summary>A build photo shown in the header. One that no longer exists shows none. Schema 2.</summary>
    public string? ProjectPhotoId { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        MarkingsScheme = Optional(MarkingsScheme);
        Notes = Optional(Notes);
        Status = Optional(Status);
        ProjectPhotoId = TidyId(Optional(ProjectPhotoId));
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
        CapturedAt = IsoDate.RoundToMilliseconds(CapturedAt);
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

/// <summary>
/// A note, highlight or arrow on a PDF, or a note box on an image or a build
/// photo. Exactly one of <see cref="ReferenceId"/> and <see cref="BuildPhotoId"/> is set.
/// </summary>
public sealed class Markup : LibraryRecord
{
    public string? ReferenceId { get; set; }
    /// <summary>A build photo's notes are always "note"s on page 0, placed like an image's. Schema 2.</summary>
    public string? BuildPhotoId { get; set; }
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
        ReferenceId = TidyId(Optional(ReferenceId));
        BuildPhotoId = TidyId(Optional(BuildPhotoId));
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
    /// <summary>A standard system's raw value, e.g. "fs595"; empty for none. Schema 5.</summary>
    public string StandardSystem { get; set; } = "";
    /// <summary>The code within the system, e.g. "36270"; for Multiple and Custom, the whole text.</summary>
    public required string StandardCode { get; set; }
    /// <summary>"none", "closest" or "exact"; empty for Not Set, and when there's no system. Schema 5.</summary>
    public string Match { get; set; } = "";
    /// <summary>A paint type's raw value, e.g. "lacquer"; empty for Not Set. Schema 5.</summary>
    public string Type { get; set; } = "";
    /// <summary>A custom mix, made from other paints by its recipe. Its code may be empty. Schema 6.</summary>
    public bool IsMix { get; set; }
    public required string IdentityKey { get; set; }

    /// <summary>The paint's standard, or null for none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PaintStandard? Standard => PaintStandard.From(StandardSystem, StandardCode);

    internal override void Tidy()
    {
        base.Tidy();
        Notes = Optional(Notes);
        SwatchHex = Optional(SwatchHex);
        StandardSystem ??= "";
        StandardCode ??= "";
        Match ??= "";
        Type ??= "";
    }
}

/// <summary>One line of a mix's recipe. Schema 6.</summary>
public sealed class PaintMixPart : LibraryRecord
{
    /// <summary>A paint with IsMix set.</summary>
    public required string MixId { get; set; }
    /// <summary>A paint that isn't a mix, once per mix.</summary>
    public required string PaintId { get; set; }
    /// <summary>More than zero, e.g. 3 or 1.5.</summary>
    public required double Parts { get; set; }
    /// <summary>The line's place in the recipe, from 0.</summary>
    public required int Position { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        MixId = TidyId(MixId)!;
        PaintId = TidyId(PaintId)!;
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
    /// <summary>The guide colour's standard system, as for paints; empty for none. Schema 5.</summary>
    public string StandardSystem { get; set; } = "";
    /// <summary>The guide colour's code, e.g. "36270". Null when there's no system.</summary>
    public string? Standard { get; set; }
    public string? UsedFor { get; set; }
    public string? Notes { get; set; }

    /// <summary>The guide colour, or null for none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PaintStandard? GuideStandard => PaintStandard.From(StandardSystem, Standard);

    internal override void Tidy()
    {
        base.Tidy();
        ProjectId = TidyId(ProjectId)!;
        PaintId = TidyId(Optional(PaintId));
        StandardSystem ??= "";
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

public static class BuildPhotoStage
{
    public const string Wip = "wip";
    public const string Completed = "completed";
}

/// <summary>The user's own photo of a build, kept apart from its references. Schema 2.</summary>
public sealed class BuildPhoto : LibraryRecord
{
    public required string ProjectId { get; set; }
    /// <summary>Always an image.</summary>
    public required string AssetId { get; set; }
    /// <summary>The first line is its caption.</summary>
    public string? Notes { get; set; }
    /// <summary>From EXIF when it was added, else when it was added. The user can change it.</summary>
    public required DateTimeOffset TakenAt { get; set; }
    /// <summary>"wip" or "completed". Unknown values show as WIP and are kept.</summary>
    public required string Stage { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        ProjectId = TidyId(ProjectId)!;
        AssetId = TidyId(AssetId)!;
        Notes = Optional(Notes);
        TakenAt = IsoDate.RoundToMilliseconds(TakenAt);
    }
}

public sealed class BuildPhotoTag : LibraryRecord
{
    public required string BuildPhotoId { get; set; }
    public required string TagId { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        BuildPhotoId = TidyId(BuildPhotoId)!;
        TagId = TidyId(TagId)!;
    }
}

public static class KitStatus
{
    public const string Bnib = "bnib";
    public const string Opened = "opened";
    public const string Wip = "wip";
    public const string Built = "built";

    public static readonly IReadOnlyList<string> All = [Bnib, Opened, Wip, Built];
}

/// <summary>One physical model kit in the library's catalogue. Schema 3.</summary>
public sealed class Kit : LibraryRecord
{
    public required string Brand { get; set; }
    /// <summary>The product's name as on the box.</summary>
    public required string Name { get; set; }
    public string? Scale { get; set; }
    /// <summary>The maker's number, as text so leading zeros and dashes stay.</summary>
    public string? Number { get; set; }
    /// <summary>False for a kit the user wants but hasn't got.</summary>
    public required bool Owned { get; set; }
    /// <summary>"bnib", "opened", "wip" or "built". Unknown values show as BNIB and are kept.</summary>
    public required string Status { get; set; }
    /// <summary>1 to 5. Other values read as no rating.</summary>
    public int? Rating { get; set; }
    public string? Link { get; set; }
    /// <summary>In the library's one currency, which isn't stored.</summary>
    public decimal? PricePaid { get; set; }
    public required bool ForSale { get; set; }
    /// <summary>Kept when ForSale is false.</summary>
    public decimal? SalePrice { get; set; }
    public string? Notes { get; set; }
    /// <summary>Box art. Copies of a kit can share one asset.</summary>
    public string? PictureAssetId { get; set; }
    /// <summary>The project building it; missing for none. Schema 4.</summary>
    public string? ProjectId { get; set; }

    internal override void Tidy()
    {
        base.Tidy();
        Scale = Optional(Scale);
        Number = Optional(Number);
        Rating = Rating is >= 1 and <= 5 ? Rating : null;
        Link = Optional(Link);
        Notes = Optional(Notes);
        PictureAssetId = TidyId(Optional(PictureAssetId));
        ProjectId = TidyId(Optional(ProjectId));
    }
}
