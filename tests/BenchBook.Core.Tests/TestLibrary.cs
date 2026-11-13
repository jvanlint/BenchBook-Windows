using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Tests;

/// <summary>A library in its own temporary folder, deleted afterwards. Never the user's.</summary>
public sealed class TestLibrary : IDisposable
{
    public TestLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "BenchBook Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Location = new LibraryLocation(Path.Combine(Root, "App"));
    }

    public string Root { get; }
    public LibraryLocation Location { get; }

    public LibraryStore OpenStore() => LibraryStore.Open(Location.StorePath);

    /// <summary>Writes an original for the asset, at its path under the library root.</summary>
    public void WriteOriginal(Asset asset, byte[] bytes)
    {
        var path = Location.FullPath(asset.OriginalRelativePath)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>A small library with one of every record type.</summary>
public static class Sample
{
    public static readonly DateTimeOffset Date = new(2026, 11, 13, 9, 41, 7, 250, TimeSpan.Zero);

    public static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];
    public static readonly byte[] PdfBytes = "%PDF-1.7\n% sample\n"u8.ToArray();
    public static readonly byte[] PosterBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9];

    public static (LibrarySnapshot Snapshot, Dictionary<string, byte[]> Files) Make()
    {
        var project = new Project
        {
            Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date,
            Name = "F-16CJ 50th Anniversary", Manufacturer = "Tamiya", Subject = "F-16CJ Block 50", Scale = "1:48",
            KitNumber = "61098", Status = ProjectStatus.InProgress, Notes = "Wheel wells first.\n  Then the cockpit.",
        };
        var image = new Asset
        {
            Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, MimeType = "image/jpeg", ByteCount = JpegBytes.Length,
            Sha256 = Hash(JpegBytes), PixelWidth = 1200, PixelHeight = 819,
            OriginalFilename = "wheel-well é.jpg", OriginalRelativePath = "",
        };
        image.OriginalRelativePath = $"Originals/{image.Id}.jpg";
        var pdf = new Asset
        {
            Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, MimeType = "application/pdf", ByteCount = PdfBytes.Length,
            Sha256 = Hash(PdfBytes), OriginalRelativePath = "",
        };
        pdf.OriginalRelativePath = $"Originals/{pdf.Id}.pdf";
        var partPicture = new Asset
        {
            Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, MimeType = "image/jpeg", ByteCount = JpegBytes.Length,
            Sha256 = image.Sha256, PixelWidth = 10, PixelHeight = 10, OriginalRelativePath = "",
        };
        partPicture.OriginalRelativePath = $"Originals/{partPicture.Id}.jpg";

        var imageRef = new Reference
        {
            Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, ProjectId = project.Id, Kind = ReferenceKind.Image,
            Title = "Main Gear and Bay", CapturedAt = Date, AssetId = image.Id, Note = "“Quoted” note",
        };
        var pdfRef = new Reference
        {
            Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, ProjectId = project.Id, Kind = ReferenceKind.Pdf,
            CapturedAt = Date.AddSeconds(1), AssetId = pdf.Id,
        };
        var video = new Reference
        {
            Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, ProjectId = project.Id, Kind = ReferenceKind.VideoLink,
            CapturedAt = Date.AddSeconds(2), SourcePageUrl = "https://www.youtube.com/watch?v=abc_123&t=90s",
            VideoChannel = "Scale Channel", VideoDurationSeconds = 1234.5, VideoPublishedAt = Date.AddDays(-30),
            VideoInfoFetchedAt = Date,
        };
        var unknownKind = new Reference
        {
            Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, ProjectId = project.Id, Kind = "hologram",
            CapturedAt = Date.AddSeconds(3), Text = "A kind from a newer BenchBook.",
        };
        var tag = new Tag { Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, Name = "Wheel Well", NormalizedName = "wheel well", Color = "teal" };
        var paint = new Paint
        {
            Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, Brand = "Tamiya", Range = "Acrylic XF", Code = "XF-54",
            Name = "Dark Sea Grey", StandardCode = "BS 640", IdentityKey = "tamiya|acrylic xf|xf54", SwatchHex = "#6B7378",
        };

        var snapshot = new LibrarySnapshot
        {
            Projects = [project],
            Assets = [image, pdf, partPicture],
            References = [imageRef, pdfRef, video, unknownKind],
            Tags = [tag],
            ReferenceTags = [new ReferenceTag { Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, ReferenceId = imageRef.Id, TagId = tag.Id }],
            VideoMoments = [new VideoMoment { Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, ReferenceId = video.Id, OffsetSeconds = 90, Title = "Gear doors" }],
            Markups =
            [
                new Markup
                {
                    Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, ReferenceId = pdfRef.Id, PageIndex = 2, Kind = "highlight",
                    Color = "yellow", X = 72.5, Y = 640.25, Width = 200, Height = 14, Points = [72.5, 647.25, 272.5, 647.25], Text = "Check this",
                },
                new Markup
                {
                    Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, ReferenceId = imageRef.Id, PageIndex = 0, Kind = "note",
                    Color = "orange", X = 0.1, Y = 0.2, Width = 0.3, Height = 0.15, Text = "",
                },
            ],
            Paints = [paint],
            OwnedPaints = [new OwnedPaint { Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, PaintId = paint.Id }],
            ProjectPaints =
            [
                new ProjectPaint { Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, ProjectId = project.Id, PaintId = paint.Id, UsedFor = "Upper surfaces" },
                new ProjectPaint { Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, ProjectId = project.Id, Standard = "FS 36270" },
            ],
            AftermarketItems =
            [
                new AftermarketItem
                {
                    Id = Ids.New(), CreatedAt = Date, UpdatedAt = Date, ProjectId = project.Id, ProductName = "50th Anniversary 2015",
                    Manufacturer = "Galaxy Models", Type = "decals", Status = "inStash", ImageAssetId = partPicture.Id,
                    ProductUrl = "https://example.com/decals",
                },
            ],
        };

        var files = new Dictionary<string, byte[]>
        {
            [image.OriginalRelativePath] = JpegBytes,
            [pdf.OriginalRelativePath] = PdfBytes,
            [partPicture.OriginalRelativePath] = JpegBytes,
            [$"Posters/{video.Id}.jpg"] = PosterBytes,
        };
        return (snapshot, files);
    }

    /// <summary>A library on disk holding the sample.</summary>
    public static LibrarySnapshot Install(TestLibrary library)
    {
        var (snapshot, files) = Make();
        using (var store = library.OpenStore()) store.InsertSnapshot(snapshot);
        foreach (var (path, bytes) in files)
        {
            var full = library.Location.FullPath(path)!;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
        return snapshot;
    }

    public static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}
