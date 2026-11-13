using System.Buffers.Binary;
using System.Text;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Projects;
using BenchBook.Core.Store;

namespace BenchBook.Core.Tests;

/// <summary>Store access for tests: one store, used directly.</summary>
internal sealed class DirectStore(LibraryStore store) : IStoreAccess
{
    public T With<T>(Func<LibraryStore, T> work) => work(store);
}

/// <summary>Small, valid image headers, built by hand.</summary>
internal static class Images
{
    public static byte[] Png(uint width, uint height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    /// <summary>SOI, an APP0 segment, then a baseline frame header.</summary>
    public static byte[] Jpeg(ushort width, ushort height)
    {
        var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        stream.Write([0xFF, 0xE0, 0x00, 0x10]);
        stream.Write("JFIF\0"u8);
        stream.Write(new byte[9]);
        stream.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        stream.Write([(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width]);
        stream.Write(new byte[12]);
        stream.Write([0xFF, 0xD9]);
        return stream.ToArray();
    }

    public static byte[] WebPLossless(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8L"u8.CopyTo(bytes.AsSpan(12));
        bytes[20] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), (uint)((width - 1) | ((height - 1) << 14)));
        return bytes;
    }

    /// <summary>ftyp, then meta › iprp › ipco with a thumbnail ispe and the full-size one.</summary>
    public static byte[] Heic(uint width, uint height)
    {
        static byte[] Box(string type, params byte[][] body)
        {
            var content = body.SelectMany(b => b).ToArray();
            var box = new byte[8 + content.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            content.CopyTo(box, 8);
            return box;
        }
        static byte[] Ispe(uint w, uint h)
        {
            var data = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), w);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), h);
            return Box("ispe", data);
        }
        var ftyp = Box("ftyp", "heic"u8.ToArray(), new byte[4], "mif1heic"u8.ToArray());
        var meta = Box("meta", new byte[4], Box("hdlr", new byte[20]), Box("iprp", Box("ipco", Ispe(320, 240), Ispe(width, height))));
        return ftyp.Concat(meta).ToArray();
    }
}

public class FileCheckTests
{
    [Fact]
    public void ImagesAreRecognisedByContentAndMeasuredFromTheirHeaders()
    {
        using var library = new TestLibrary();
        (string Name, byte[] Bytes, string Mime, string Ext, int W, int H)[] cases =
        [
            ("a.png", Images.Png(1200, 819), "image/png", "png", 1200, 819),
            ("b.jpg", Images.Jpeg(4032, 3024), "image/jpeg", "jpg", 4032, 3024),
            ("c.webp", Images.WebPLossless(640, 480), "image/webp", "webp", 640, 480),
            ("d.heic", Images.Heic(4032, 3024), "image/heic", "heic", 4032, 3024),
            ("misnamed.txt", Images.Png(10, 20), "image/png", "png", 10, 20),
        ];
        foreach (var (name, bytes, mime, ext, w, h) in cases)
        {
            var path = Write(library, name, bytes);
            var file = FileInspector.Inspect(path, name, _ => true);
            Assert.Equal(ReferenceKind.Image, file.Kind);
            Assert.Equal(mime, file.MimeType);
            Assert.Equal(ext, file.Extension);
            Assert.Equal((w, h), (file.PixelWidth, file.PixelHeight));
            Assert.Equal(Sample.Hash(bytes), file.Sha256);
            Assert.Equal(bytes.Length, file.ByteCount);
        }
    }

    [Fact]
    public void PdfsNeedTheSignatureAndAPage()
    {
        using var library = new TestLibrary();
        var path = Write(library, "kit.pdf", Encoding.ASCII.GetBytes(new string(' ', 100) + "%PDF-1.7\n"));
        Assert.Equal(ReferenceKind.Pdf, FileInspector.Inspect(path, "kit.pdf", _ => true).Kind);
        Assert.Contains("couldn't be read", Assert.Throws<CaptureException>(() => FileInspector.Inspect(path, "kit.pdf", _ => false)).Message);

        var late = Write(library, "late.pdf", Encoding.ASCII.GetBytes(new string(' ', 1030) + "%PDF-1.7\n"));
        Assert.Contains("isn't a supported file", Assert.Throws<CaptureException>(() => FileInspector.Inspect(late, "late.pdf", _ => true)).Message);
    }

    [Fact]
    public void MarkdownIsRecognisedByExtensionAndMustBeUtf8()
    {
        using var library = new TestLibrary();
        var good = Write(library, "notes.markdown", "# Decals\nSee *page 3* – “soak” 10s."u8.ToArray());
        var file = FileInspector.Inspect(good, "notes.markdown", _ => true);
        Assert.Equal(("text/markdown", "md"), (file.MimeType, file.Extension));

        var bad = Write(library, "bad.md", [0x23, 0x20, 0xC3, 0x28]);
        Assert.Contains("couldn't be read", Assert.Throws<CaptureException>(() => FileInspector.Inspect(bad, "bad.md", _ => true)).Message);

        var text = Write(library, "notes.txt", "plain text"u8.ToArray());
        Assert.Equal(
            "“notes.txt” isn't a supported file. BenchBook accepts JPEG, PNG, WebP and HEIC images, PDFs and Markdown files.",
            Assert.Throws<CaptureException>(() => FileInspector.Inspect(text, "notes.txt", _ => true)).Message);
    }

    [Fact]
    public void EmptyBrokenAndOversizedFilesAreRefused()
    {
        using var library = new TestLibrary();
        var empty = Write(library, "empty.jpg", []);
        Assert.Equal("“empty.jpg” couldn't be read. It may be damaged or empty.",
            Assert.Throws<CaptureException>(() => FileInspector.Inspect(empty, "empty.jpg", _ => true)).Message);

        var broken = Write(library, "broken.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00]);
        Assert.Contains("couldn't be read", Assert.Throws<CaptureException>(() => FileInspector.Inspect(broken, "broken.jpg", _ => true)).Message);

        var huge = Write(library, "huge.png", Images.Png(20000, 20000));
        Assert.Equal("“huge.png” has too many pixels. Images can be up to 300 megapixels.",
            Assert.Throws<CaptureException>(() => FileInspector.Inspect(huge, "huge.png", _ => true)).Message);

        var bigMarkdown = Path.Combine(library.Root, "big.md");
        using (var stream = File.Create(bigMarkdown)) stream.SetLength(FileInspector.MaxMarkdownBytes + 1);
        Assert.Contains("too large", Assert.Throws<CaptureException>(() => FileInspector.Inspect(bigMarkdown, "big.md", _ => true)).Message);

        var folder = Path.Combine(library.Root, "folder.jpg");
        Directory.CreateDirectory(folder);
        Assert.Contains("couldn't be read", Assert.Throws<CaptureException>(() => FileInspector.Inspect(folder, "folder.jpg", _ => true)).Message);
    }

    internal static string Write(TestLibrary library, string name, byte[] bytes)
    {
        var folder = Path.Combine(library.Root, "Incoming");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

public class ImportTests
{
    [Fact]
    public void AFileIsCopiedByteForByteAndSavedWithItsTagsAndNote()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "F-16C" });
        var bytes = Images.Jpeg(800, 600);
        var source = FileCheckTests.Write(library, "wheel-well.jpg", bytes);
        var importer = new ReferenceImporter(new DirectStore(store), library.Location, _ => true);

        var outcome = Assert.Single(importer.Import([new CapturedItem.File(source)], project.Id, ["Wheel Well", "cockpit"], "  From the walkaround.  "));
        var added = Assert.IsType<CaptureOutcome.Added>(outcome).Reference;

        var entry = Assert.Single(store.ProjectReferences(project.Id));
        Assert.Equal(added.Id, entry.Reference.Id);
        Assert.Equal(ReferenceKind.Image, entry.Reference.Kind);
        Assert.Equal("From the walkaround.", entry.Reference.Note);
        Assert.Equal("wheel-well.jpg", entry.Asset!.OriginalFilename);
        Assert.Equal($"Originals/{entry.Asset.Id}.jpg", entry.Asset.OriginalRelativePath);
        Assert.Equal((800, 600), (entry.Asset.PixelWidth, entry.Asset.PixelHeight));
        Assert.Equal(bytes, File.ReadAllBytes(library.Location.FullPath(entry.Asset.OriginalRelativePath)!));
        Assert.Equal(["cockpit", "Wheel Well"], entry.Tags.Select(t => t.Name));
        Assert.Empty(Directory.EnumerateFiles(library.Location.OriginalsFolder, ".partial-*"));
        Assert.True(File.Exists(source)); // The user's file is left where it was.
    }

    [Fact]
    public void TheSameFileTwiceInAProjectIsADuplicateButAnotherProjectGetsItsOwnCopy()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var first = ProjectService.Create(store, new ProjectDraft { Name = "First" });
        var second = ProjectService.Create(store, new ProjectDraft { Name = "Second" });
        var source = FileCheckTests.Write(library, "a.png", Images.Png(10, 10));
        var copy = FileCheckTests.Write(library, "renamed.png", Images.Png(10, 10));
        var importer = new ReferenceImporter(new DirectStore(store), library.Location, _ => true);

        importer.Import([new CapturedItem.File(source)], first.Id);
        var duplicate = Assert.IsType<CaptureOutcome.Duplicate>(Assert.Single(importer.Import([new CapturedItem.File(copy)], first.Id)));
        Assert.Equal("a.png", duplicate.ExistingTitle);
        Assert.IsType<CaptureOutcome.Added>(Assert.Single(importer.Import([new CapturedItem.File(copy)], second.Id)));

        var assets = store.ReadSnapshot().Assets;
        Assert.Equal(2, assets.Count);
        Assert.Equal(2, Directory.GetFiles(library.Location.OriginalsFolder).Length);
    }

    [Fact]
    public void LinksBecomeWebpagesOrVideosWithAMomentForATimestamp()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "P" });
        var importer = new ReferenceImporter(new DirectStore(store), library.Location, _ => true);

        var outcomes = importer.Import(
        [
            new CapturedItem.Link("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1m30s", "  "),
            new CapturedItem.Link("https://www.britmodeller.com/forums/topic/123", "Walkaround"),
            new CapturedItem.Link("ftp://example.com/file"),
            new CapturedItem.Link("not a link"),
        ], project.Id);

        Assert.IsType<CaptureOutcome.Added>(outcomes[0]);
        Assert.IsType<CaptureOutcome.Added>(outcomes[1]);
        Assert.Equal("“ftp://example.com/file” isn't a web link BenchBook can save.", Assert.IsType<CaptureOutcome.Failed>(outcomes[2]).Message);
        Assert.IsType<CaptureOutcome.Failed>(outcomes[3]);

        var entries = store.ProjectReferences(project.Id);
        var video = entries.Single(e => e.Reference.Kind == ReferenceKind.VideoLink);
        Assert.Null(video.Reference.Title);
        Assert.Equal(90, Assert.Single(video.Moments).OffsetSeconds);
        var page = entries.Single(e => e.Reference.Kind == ReferenceKind.Webpage);
        Assert.Equal("Walkaround", page.Reference.Title);
        Assert.Null(page.Reference.LinkPreviewFetchedAt);
    }

    [Fact]
    public void TextIsTrimmedCutAndRefusedWhenEmpty()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "P" });
        var importer = new ReferenceImporter(new DirectStore(store), library.Location, _ => true);

        var outcomes = importer.Import(
        [
            new CapturedItem.Text("\n  Use XF-54 for the upper surfaces.  \n", "https://example.com/guide"),
            new CapturedItem.Text(new string('a', 100_050)),
            new CapturedItem.Text("   \n\t"),
        ], project.Id);

        Assert.Equal("The shared text was empty.", Assert.IsType<CaptureOutcome.Failed>(outcomes[2]).Message);
        var texts = store.ProjectReferences(project.Id).Select(e => e.Reference).ToList();
        Assert.Contains(texts, r => r.Text == "Use XF-54 for the upper surfaces." && r.SourcePageUrl == "https://example.com/guide");
        Assert.Contains(texts, r => r.Text!.Length == 100_000);
    }

    [Fact]
    public void ABatchReportsFailuresAndDuplicatesTogether()
    {
        var item = new CapturedItem.Link("x");
        var existing = new Reference { Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, ProjectId = Ids.New(), Kind = "image", CapturedAt = Sample.Date };
        Assert.Null(ReferenceImporter.Report([]));
        var duplicates = ReferenceImporter.Report([new CaptureOutcome.Duplicate(existing, "Main Gear")])!;
        Assert.Equal(("Already Saved to This Project", "“Main Gear”"), (duplicates.Title, duplicates.Message));
        var both = ReferenceImporter.Report([new CaptureOutcome.Failed(item, "Bad."), new CaptureOutcome.Failed(item, "Worse."), new CaptureOutcome.Duplicate(existing, "Main Gear")])!;
        Assert.Equal("2 Items Couldn't Be Added", both.Title);
        Assert.Equal("Bad.\nWorse.\n“Main Gear” is already saved to this project.", both.Message);
        Assert.Equal("An Item Couldn't Be Added", ReferenceImporter.Report([new CaptureOutcome.Failed(item, "Bad.")])!.Title);
    }

    [Fact]
    public void ANoteNeedsATitleOrTextAndKeepsItsIndentation()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "P" });
        Assert.Equal("Write a title or some text for the note.",
            Assert.Throws<CaptureException>(() => ReferenceService.CreateNote(store, project.Id, " ", "\n", "")).Message);

        var note = ReferenceService.CreateNote(store, project.Id, "", "\n  Step 1\n    - mask canopy\n\n", "Cockpit, cockpit");
        var entry = Assert.Single(store.ProjectReferences(project.Id));
        Assert.Equal("Step 1\n    - mask canopy", entry.Reference.Text);
        Assert.Equal(ReferenceKind.Note, entry.Reference.Kind);
        Assert.Equal("Cockpit", Assert.Single(entry.Tags).Name);
        Assert.Equal("Step 1", ReferenceText.FallbackTitle(entry));

        ReferenceService.UpdateNote(store, note, "Masking", "Step 1", "COCKPIT, decals");
        entry = Assert.Single(store.ProjectReferences(project.Id));
        Assert.Equal("Masking", entry.Reference.Title);
        // Typing an existing tag in any case reuses it and keeps its spelling.
        Assert.Equal(["Cockpit", "decals"], entry.Tags.Select(t => t.Name));
        Assert.Equal(2, store.TagsByUsage().Count);
    }

    [Fact]
    public void DeletingAReferenceKeepsAFileAnotherReferenceShares()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var image = sample.References[0];
        var asset = sample.Assets[0];
        // Another reference shares the image's asset, as a copy would.
        var other = ProjectService.Create(store, new ProjectDraft { Name = "Other" });
        var copy = ReferenceImporter.NewReference(other.Id, ReferenceKind.Image, Sample.Date);
        copy.AssetId = asset.Id;
        store.InsertReference(copy, null, [], []);

        ReferenceService.Delete(store, library.Location, [image.Id]);
        Assert.NotNull(store.Asset(asset.Id));
        Assert.True(File.Exists(library.Location.FullPath(asset.OriginalRelativePath)));

        ReferenceService.Delete(store, library.Location, [copy.Id]);
        Assert.Null(store.Asset(asset.Id));
        Assert.False(File.Exists(library.Location.FullPath(asset.OriginalRelativePath)));
        // Its tag stays, unused.
        Assert.Equal(0, Assert.Single(store.TagsByUsage()).References);
    }
}

public class TextRuleTests
{
    [Theory]
    [InlineData(" wheel   well, Cockpit ,, WHEEL WELL ,decals ", new[] { "wheel well", "Cockpit", "decals" })]
    [InlineData("", new string[0])]
    [InlineData(" , ,", new string[0])]
    [InlineData("Wheel\tWell\n", new[] { "Wheel Well" })]
    public void TagFieldsAreParsed(string text, string[] expected) => Assert.Equal(expected, TagNames.Parse(text));

    [Fact]
    public void TagNamesNormalize()
    {
        Assert.Equal("wheel well", TagNames.Normalize("  Wheel   Well "));
        Assert.Equal("décals", TagNames.Normalize("DÉCALS"));
    }

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=90", VideoProvider.YouTube, "dQw4w9WgXcQ", 90.0)]
    [InlineData("https://www.youtube.com/watch?v=abc-_1&start=1:02:03", VideoProvider.YouTube, "abc-_1", 3723.0)]
    [InlineData("https://m.youtube.com/shorts/xyz", VideoProvider.YouTube, "xyz", null)]
    [InlineData("https://www.youtube-nocookie.com/embed/xyz#t=1m30s", VideoProvider.YouTube, "xyz", 90.0)]
    [InlineData("https://music.youtube.com/watch?v=xyz&t=1h2m3s", VideoProvider.YouTube, "xyz", 3723.0)]
    [InlineData("https://vimeo.com/channels/staffpicks/76979871#t=45s", VideoProvider.Vimeo, "76979871", 45.0)]
    [InlineData("https://player.vimeo.com/video/76979871", VideoProvider.Vimeo, "76979871", null)]
    public void VideoLinksAreRecognised(string url, VideoProvider provider, string id, double? start)
    {
        var video = VideoLink.Parse(url)!;
        Assert.Equal(provider, video.Provider);
        Assert.Equal(id, video.VideoId);
        Assert.Equal(start, video.StartSeconds);
    }

    [Theory]
    [InlineData("https://www.youtube.com/channel/abc")]
    [InlineData("https://www.youtube.com/watch?v=has%20space")]
    [InlineData("https://vimeo.com/about")]
    [InlineData("https://example.com/watch?v=abc")]
    public void OtherLinksAreNotVideos(string url) => Assert.Null(VideoLink.Parse(url));

    [Theory]
    [InlineData("90", 90.0)]
    [InlineData("90s", 90.0)]
    [InlineData("1m30s", 90.0)]
    [InlineData("1h2m3s", 3723.0)]
    [InlineData("1:30", 90.0)]
    [InlineData("1:02:03", 3723.0)]
    [InlineData("abc", null)]
    [InlineData("1:2:3:4", null)]
    [InlineData("5x", null)]
    [InlineData("", null)]
    public void TimestampsAreParsed(string text, double? seconds) => Assert.Equal(seconds, VideoLink.ParseTimestamp(text));

    [Fact]
    public void PlaybackLinksAndTimesAreFormatted()
    {
        Assert.Equal("https://www.youtube.com/watch?v=xyz&t=90s", VideoLink.PlaybackUrl("https://youtu.be/xyz", 90.7));
        Assert.Equal("https://vimeo.com/123#t=45s", VideoLink.PlaybackUrl("https://vimeo.com/123", 45));
        Assert.Equal("0:45", VideoLink.Format(45));
        Assert.Equal("12:03", VideoLink.Format(723));
        Assert.Equal("1:02:03", VideoLink.Format(3723));
    }
}
