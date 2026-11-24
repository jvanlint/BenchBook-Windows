using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BenchBook.Core.BuildPhotos;
using BenchBook.Core.Capture;
using BenchBook.Core.Markups;
using BenchBook.Core.Model;
using BenchBook.Core.Projects;
using BenchBook.Core.Search;
using BenchBook.Core.Store;

namespace BenchBook.Core.Tests;

/// <summary>Photos with EXIF, GPS and XMP, built by hand.</summary>
internal static class ExifImages
{
    /// <summary>
    /// A little-endian TIFF block: IFD0 with Make, the EXIF and GPS pointers and
    /// one tag after them; an EXIF directory with DateTimeOriginal; a GPS
    /// directory with a latitude. Returns where the GPS values are.
    /// </summary>
    public static (byte[] Tiff, int GpsStart, int GpsEnd) Tiff(string dateTaken = "2026:11:19 14:05:00", bool gps = true)
    {
        var ifd0Count = gps ? 4 : 3;
        var ifd0 = 8;
        var make = ifd0 + 2 + 12 * ifd0Count + 4;
        var exif = make + 6;
        var dateData = exif + 2 + 12 + 4;
        var gpsIfd = dateData + 20;
        var latitude = gpsIfd + 2 + 24 + 4;
        var end = gps ? latitude + 24 : gpsIfd;
        var t = new byte[end];
        "II"u8.CopyTo(t);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), (uint)ifd0);
        void Entry(int at, ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(at), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(at + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(at + 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(at + 8), value);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(ifd0), (ushort)ifd0Count);
        var e = ifd0 + 2;
        Entry(e, 0x010F, 2, 6, (uint)make); e += 12;
        Entry(e, 0x8769, 4, 1, (uint)exif); e += 12;
        if (gps) { Entry(e, 0x8825, 4, 1, (uint)gpsIfd); e += 12; }
        Entry(e, 0xC000, 3, 1, 7); e += 12;
        "Canon\0"u8.CopyTo(t.AsSpan(make));
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(exif), 1);
        Entry(exif + 2, 0x9003, 2, 20, (uint)dateData);
        Encoding.ASCII.GetBytes(dateTaken + "\0").CopyTo(t.AsSpan(dateData));
        if (gps)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(gpsIfd), 2);
            Entry(gpsIfd + 2, 0x0001, 2, 2, 'S');
            Entry(gpsIfd + 14, 0x0002, 5, 3, (uint)latitude);
            foreach (var (i, n) in new[] { (0, 33u), (1, 51u), (2, 7u) })
            {
                BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(latitude + 8 * i), n);
                BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(latitude + 8 * i + 4), 1);
            }
        }
        return (t, gpsIfd, end);
    }

    public const string Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF><rdf:Description exif:GPSLatitude=\"33,51.1S\" exif:DateTimeOriginal=\"2026-11-19\">"
        + "<exif:GPSLongitude>151,12.0E</exif:GPSLongitude><exif:GPSAltitude/></rdf:Description></rdf:RDF></x:xmpmeta>";

    /// <summary>A JPEG with an EXIF segment and an XMP segment. Returns where the TIFF block starts.</summary>
    public static (byte[] File, int TiffStart) Jpeg(byte[] tiff)
    {
        var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        void Segment(byte marker, byte[] data)
        {
            stream.Write([0xFF, marker, (byte)((data.Length + 2) >> 8), (byte)(data.Length + 2)]);
            stream.Write(data);
        }
        var tiffStart = (int)stream.Length + 4 + 6;
        Segment(0xE1, [.. "Exif\0\0"u8, .. tiff]);
        Segment(0xE1, [.. Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0" + Xmp)]);
        stream.Write([0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x20]);
        stream.Write(new byte[12]);
        stream.Write([0xFF, 0xDA, 0x00, 0x02, 0x12, 0x34, 0xFF, 0xD9]);
        return (stream.ToArray(), tiffStart);
    }

    /// <summary>A PNG with IHDR, an eXIf chunk and IEND, with correct checksums.</summary>
    public static byte[] Png(byte[] tiff)
    {
        var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            stream.Write(length);
            byte[] body = [.. Encoding.ASCII.GetBytes(type), .. data];
            stream.Write(body);
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, PhotoMetadata.Crc32(body));
            stream.Write(crc);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 16);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 32);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        Chunk("eXIf", tiff);
        Chunk("IEND", []);
        return stream.ToArray();
    }

    /// <summary>A HEIC whose EXIF is an "Exif" item, found through iinf and iloc.</summary>
    public static byte[] Heic(byte[] tiff)
    {
        static byte[] Box(string type, params byte[][] body)
        {
            var content = body.SelectMany(b => b).ToArray();
            var bytes = new byte[8 + content.Length];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
            content.CopyTo(bytes, 8);
            return bytes;
        }
        static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];
        static byte[] U32(long v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

        byte[] item = [.. U32(6), .. "Exif\0\0"u8, .. tiff];
        var ftyp = Box("ftyp", "heic"u8.ToArray(), U32(0), "mif1heic"u8.ToArray());
        var ispe = Box("ispe", U32(0), U32(32), U32(16));
        var infe = Box("infe", [2, 0, 0, 0], U16(1), U16(0), "Exif"u8.ToArray(), [0]);
        var iinf = Box("iinf", [0, 0, 0, 0], U16(1), infe);
        byte[] Meta(long offset) => Box("meta", [0, 0, 0, 0], Box("iprp", Box("ipco", ispe)), iinf,
            Box("iloc", [0, 0, 0, 0], [0x44, 0x00], U16(1), U16(1), U16(0), U16(1), U32(offset), U32(item.Length)));
        var offset = ftyp.Length + Meta(0).Length + 8;
        return [.. ftyp, .. Meta(offset), .. Box("mdat", item)];
    }

    /// <summary>The IFD0 tags of the first TIFF block found at <paramref name="start"/>.</summary>
    public static List<ushort> Ifd0Tags(byte[] file, int start)
    {
        var ifd = start + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(start + 4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(ifd));
        return Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(ifd + 2 + 12 * i))).ToList();
    }
}

public class PhotoMetadataTests
{
    [Fact]
    public void TheDateTakenIsReadFromExifInEveryFormat()
    {
        var (tiff, _, _) = ExifImages.Tiff();
        var expected = new DateTime(2026, 11, 19, 14, 5, 0);
        Assert.Equal(expected, PhotoMetadata.DateTaken(ExifImages.Jpeg(tiff).File));
        Assert.Equal(expected, PhotoMetadata.DateTaken(ExifImages.Png(tiff)));
        Assert.Equal(expected, PhotoMetadata.DateTaken(ExifImages.Heic(tiff)));
        Assert.Null(PhotoMetadata.DateTaken(Images.Jpeg(10, 10)));
        Assert.Null(PhotoMetadata.DateTaken(ExifImages.Jpeg(ExifImages.Tiff("not a date").Tiff).File));
    }

    [Fact]
    public void RemovingLocationTakesOutGpsAndKeepsEverythingElse()
    {
        var (tiff, gpsStart, gpsEnd) = ExifImages.Tiff();
        var (original, tiffStart) = ExifImages.Jpeg(tiff);
        var copy = PhotoMetadata.WithoutLocation(original);

        // Same size, so nothing after the metadata moved and the pixels are untouched.
        Assert.Equal(original.Length, copy.Length);
        Assert.Equal(original[^30..], copy[^30..]);
        Assert.Equal([(ushort)0x010F, 0x8769, 0xC000], ExifImages.Ifd0Tags(copy, tiffStart));
        Assert.All(copy[(tiffStart + gpsStart)..(tiffStart + gpsEnd)], b => Assert.Equal(0, b));
        Assert.Equal(new DateTime(2026, 11, 19, 14, 5, 0), PhotoMetadata.DateTaken(copy));

        var xmp = Encoding.Latin1.GetString(copy);
        Assert.DoesNotContain("GPS", xmp, StringComparison.Ordinal);
        Assert.Contains("exif:DateTimeOriginal=\"2026-11-19\"", xmp, StringComparison.Ordinal);
        Assert.Contains("</rdf:Description>", xmp, StringComparison.Ordinal);
        // The original bytes are left as they were.
        Assert.Contains("GPS", Encoding.Latin1.GetString(original), StringComparison.Ordinal);
    }

    [Fact]
    public void APngKeepsValidChecksumsWithoutLocation()
    {
        var (tiff, _, _) = ExifImages.Tiff();
        var copy = PhotoMetadata.WithoutLocation(ExifImages.Png(tiff));
        var position = 8;
        while (position < copy.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(position));
            var crc = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(position + 8 + length));
            Assert.Equal(PhotoMetadata.Crc32(copy.AsSpan(position + 4, length + 4)), crc);
            position += 12 + length;
        }
        Assert.DoesNotContain((ushort)0x8825, ExifImages.Ifd0Tags(copy, 8 + 8 + 13 + 4 + 8));
    }

    [Fact]
    public void AHeicLosesItsGpsDirectory()
    {
        var (tiff, _, _) = ExifImages.Tiff();
        var original = ExifImages.Heic(tiff);
        var copy = PhotoMetadata.WithoutLocation(original);
        Assert.Equal(original.Length, copy.Length);
        var tiffStart = copy.Length - tiff.Length;
        Assert.Equal([(ushort)0x010F, 0x8769, 0xC000], ExifImages.Ifd0Tags(copy, tiffStart));
        Assert.Equal(new DateTime(2026, 11, 19, 14, 5, 0), PhotoMetadata.DateTaken(copy));
    }

    [Fact]
    public void APhotoWithoutGpsIsCopiedUnchanged()
    {
        var plain = ExifImages.Jpeg(ExifImages.Tiff(gps: false).Tiff).File;
        // Only the XMP's GPS properties are blanked.
        var copy = PhotoMetadata.WithoutLocation(plain);
        Assert.Equal(plain.Length, copy.Length);
        var jpeg = Images.Jpeg(20, 10);
        Assert.Equal(jpeg, PhotoMetadata.WithoutLocation(jpeg));
    }

    [Fact]
    public void DamagedExifIsRefusedRatherThanExportedWithLocation()
    {
        var (tiff, _, _) = ExifImages.Tiff();
        // The GPS pointer now points past the end.
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(8 + 2 + 24 + 8), 60000);
        Assert.Throws<PhotoMetadataException>(() => PhotoMetadata.WithoutLocation(ExifImages.Jpeg(tiff).File));
    }
}

public class BuildPhotoServiceTests
{
    private static string Write(TestLibrary library, string name, byte[] bytes)
    {
        var folder = Path.Combine(library.Root, "Incoming");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void PhotosAreAddedAsWipWithTheirDateTakenAndDuplicatesAreRefused()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "Spitfire" });
        var access = new DirectStore(store);
        var photo = Write(library, "cockpit.jpg", ExifImages.Jpeg(ExifImages.Tiff().Tiff).File);
        var plain = Write(library, "plain.png", Images.Png(20, 10));
        var notes = Write(library, "notes.md", "# Notes"u8.ToArray());

        var outcomes = BuildPhotoService.Add(access, library.Location, [(photo, null), (photo, "again.jpg"), (plain, null), (notes, null)], project.Id);
        // The same file twice in one go is left out quietly.
        Assert.Equal(3, outcomes.Count);
        var added = Assert.IsType<BuildPhotoOutcome.Added>(outcomes[0]).Photo;
        Assert.Equal(BuildPhotoStage.Wip, added.Stage);
        var local = new DateTime(2026, 11, 19, 14, 5, 0);
        Assert.Equal(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)), added.TakenAt);
        Assert.Equal("“notes.md” isn't a photo. Build photos can be JPEG, PNG, WebP or HEIC images.", Assert.IsType<BuildPhotoOutcome.Failed>(outcomes[2]).Message);

        var again = BuildPhotoService.Add(access, library.Location, [(photo, "cockpit.jpg")], project.Id);
        Assert.IsType<BuildPhotoOutcome.Duplicate>(Assert.Single(again));
        var report = BuildPhotoService.Report(again)!;
        Assert.Equal(("Already in Build Photos", "“cockpit.jpg”"), (report.Title, report.Message));
        // The original is copied byte for byte, location and all.
        var entry = store.ProjectBuildPhotos(project.Id).Single(e => Ids.Comparer.Equals(e.Photo.Id, added.Id));
        Assert.Equal(File.ReadAllBytes(photo), File.ReadAllBytes(library.Location.FullPath(entry.Asset!.OriginalRelativePath)!));
    }

    [Fact]
    public void ReportsListFailuresThenDuplicates()
    {
        var photo = new BuildPhoto { Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, ProjectId = "p", AssetId = "a", TakenAt = Sample.Date, Stage = "wip" };
        var report = BuildPhotoService.Report([new BuildPhotoOutcome.Failed("a.md", "“a.md” isn't a photo."), new BuildPhotoOutcome.Duplicate(photo, "b.jpg")], ["Folder"])!;
        Assert.Equal("2 Photos Couldn't Be Added", report.Title);
        Assert.Equal("“Folder” isn't a photo.\n“a.md” isn't a photo.\n“b.jpg” is already in this project's build photos.", report.Message);
        Assert.Null(BuildPhotoService.Report([]));
    }

    [Fact]
    public void DetailsStageTagsAndTheProjectPhoto()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "Spitfire" });
        var path = Write(library, "a.png", Images.Png(20, 10));
        var added = ((BuildPhotoOutcome.Added)BuildPhotoService.Add(new DirectStore(store), library.Location, [(path, null)], project.Id)[0]).Photo;

        var taken = new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.FromHours(11));
        BuildPhotoService.Update(store, added, "  Cockpit painted\r\nSeat belts next ", taken, BuildPhotoStage.Completed, "cockpit, WIP shots");
        var entry = store.ProjectBuildPhotos(project.Id).Single();
        Assert.Equal("Cockpit painted\nSeat belts next", entry.Photo.Notes);
        Assert.Equal("Cockpit painted", BuildPhotoText.Caption(entry.Photo));
        Assert.Equal(taken, entry.Photo.TakenAt);
        Assert.Equal(["cockpit", "WIP shots"], entry.Tags.Select(t => t.Name));
        Assert.Contains(store.TagsByUsage(), u => u.Tag.Name == "cockpit" && u.BuildPhotos == 1 && !u.IsUnused);

        store.SetBuildPhotoStage([added.Id], BuildPhotoStage.Wip);
        Assert.Equal(BuildPhotoStage.Wip, store.BuildPhoto(added.Id)!.Stage);

        store.SetProjectPhoto(project.Id, added.Id);
        Assert.Equal(added.Id, store.Project(project.Id)!.ProjectPhotoId);
        var note = ImageNotes.Add(store, null, new PixelRect(1, 1, 10, 10), 20, 10, buildPhotoId: added.Id)!;
        Assert.Equal(1, store.ProjectBuildPhotos(project.Id).Single().NoteCount);

        // Deleting the project photo clears it, with its notes, tags and file.
        var original = library.Location.FullPath(entry.Asset!.OriginalRelativePath)!;
        BuildPhotoService.Delete(store, library.Location, [added.Id]);
        Assert.Null(store.Project(project.Id)!.ProjectPhotoId);
        Assert.Empty(store.BuildPhotoMarkups(added.Id));
        Assert.Empty(store.ProjectBuildPhotos(project.Id));
        Assert.False(File.Exists(original));
        Assert.NotNull(note);
    }

    [Fact]
    public void GroupsAreCompletedThenEachDayNewestFirst()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("AEDT", TimeSpan.FromHours(11), "AEDT", "AEDT");
        BuildPhotoEntry Entry(string stage, int day, int hour) => new(new BuildPhoto
        {
            Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, ProjectId = "p", AssetId = "a", Stage = stage,
            TakenAt = new DateTimeOffset(2026, 11, day, hour, 0, 0, TimeSpan.FromHours(11)),
        }, null, [], 0);
        var done = Entry("completed", 1, 9);
        var early = Entry("wip", 19, 8);
        var late = Entry("wip", 19, 20);
        var other = Entry("wip", 20, 7);
        var unknown = Entry("painting", 18, 12);

        var groups = BuildPhotoText.Groups([early, done, late, other, unknown], zone, CultureInfo.GetCultureInfo("en-AU"));
        Assert.Equal(["completed", "2026-11-20", "2026-11-19", "2026-11-18"], groups.Select(g => g.Id));
        Assert.Equal("Thursday, 19 November 2026", groups[2].Title);
        Assert.Equal([late, early], groups[2].Entries);
        // An unknown stage shows as WIP.
        Assert.Equal("WIP", BuildPhotoText.StageName(unknown.Photo));
    }

    [Theory]
    [InlineData(null, "2026-11-19")]
    [InlineData("Cockpit: painted / weathered", "2026-11-19 Cockpit painted weathered")]
    [InlineData("  \nSecond line first", "2026-11-19 Second line first")]
    [InlineData("a?b*c|d\"e<f>g%h\\i", "2026-11-19 a b c d e f g h i")]
    [InlineData("???", "2026-11-19")]
    public void ExportNamesUseTheDateAndACleanCaption(string? notes, string expected)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("AEDT", TimeSpan.FromHours(11), "AEDT", "AEDT");
        var photo = new BuildPhoto
        {
            Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, ProjectId = "p", AssetId = "a", Stage = "wip", Notes = notes,
            // 19 November in Sydney, still the 18th in UTC.
            TakenAt = new DateTimeOffset(2026, 11, 18, 22, 0, 0, TimeSpan.Zero),
        };
        Assert.Equal(expected, BuildPhotoText.ExportBaseName(photo, zone));
    }

    [Fact]
    public void ExportCaptionsAreCutAt80Characters()
    {
        var photo = new BuildPhoto { Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, ProjectId = "p", AssetId = "a", Stage = "wip", Notes = new string('x', 100), TakenAt = Sample.Date };
        Assert.Equal(11 + 80, BuildPhotoText.ExportBaseName(photo, TimeZoneInfo.Utc).Length);
    }

    [Fact]
    public void ExportNeverOverwritesAndCanRemoveLocation()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "Spitfire" });
        var first = Write(library, "a.jpg", ExifImages.Jpeg(ExifImages.Tiff().Tiff).File);
        var second = Write(library, "b.jpg", ExifImages.Jpeg(ExifImages.Tiff("2026:11:19 15:00:00").Tiff).File);
        BuildPhotoService.Add(new DirectStore(store), library.Location, [(first, null), (second, null)], project.Id);
        var entries = store.ProjectBuildPhotos(project.Id);
        var folder = Path.Combine(library.Root, "Export");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "2026-11-19.jpg"), "already here");

        var written = BuildPhotoService.Export(entries, library.Location, folder, removeLocation: true);
        Assert.Equal(["2026-11-19 2.jpg", "2026-11-19 3.jpg"], written.Select(Path.GetFileName));
        Assert.Equal("already here", File.ReadAllText(Path.Combine(folder, "2026-11-19.jpg")));
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(File.ReadAllBytes(written[0])), StringComparison.Ordinal);
        // The originals in the library keep their location.
        Assert.Contains("GPS", Encoding.Latin1.GetString(File.ReadAllBytes(first)), StringComparison.Ordinal);

        var kept = BuildPhotoService.Export(entries.Take(1), library.Location, folder, removeLocation: false);
        Assert.Contains("GPS", Encoding.Latin1.GetString(File.ReadAllBytes(kept[0])), StringComparison.Ordinal);
    }
}

public class BuildPhotoSearchTests
{
    [Fact]
    public void SearchFindsPhotosByCaptionNotesBoxesAndTags()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        var project = ProjectService.Create(store, new ProjectDraft { Name = "Spitfire" });
        var folder = Path.Combine(library.Root, "Incoming");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "a.png");
        File.WriteAllBytes(path, Images.Png(20, 10));
        var photo = ((BuildPhotoOutcome.Added)BuildPhotoService.Add(new DirectStore(store), library.Location, [(path, null)], project.Id)[0]).Photo;
        BuildPhotoService.Update(store, photo, "Cockpit painted\nSeat belts from Eduard", photo.TakenAt, BuildPhotoStage.Wip, "cockpit");
        ImageNotes.SetText(store, ImageNotes.Add(store, null, new PixelRect(1, 1, 10, 8), 20, 10, buildPhotoId: photo.Id)!, "Harness buckles");

        var search = new LibrarySearch(store);
        var byCaption = search.Search(new SearchQuery("painted"));
        Assert.Equal(1, byCaption.BuildPhotoCount);
        Assert.Equal(0, byCaption.ReferenceCount);
        var hit = Assert.Single(Assert.Single(search.Search(new SearchQuery("buckles")).ReferenceGroups).Photos);
        Assert.Contains(hit.Matches, m => m.Label == "Box 1" && m.MarkupId is not null);
        Assert.Contains(Assert.Single(search.Search(new SearchQuery("eduard")).ReferenceGroups).Photos.Single().Matches, m => m.Label == "Note");
        Assert.Equal(1, search.Search(new SearchQuery("build photo wip")).BuildPhotoCount);
        // A tag token leaves only references and build photos with it.
        var tagged = search.Search(new SearchQuery("", ["cockpit"]));
        Assert.Equal(1, tagged.BuildPhotoCount);
        Assert.Empty(tagged.Projects);
    }

    [Fact]
    public void TagUsageReadsAsOnTheMac()
    {
        var tag = new Tag { Id = Ids.New(), CreatedAt = Sample.Date, UpdatedAt = Sample.Date, Name = "cockpit", NormalizedName = "cockpit" };
        Assert.Equal("12 references, 4 build photos", new TagUsage(tag, 12, 1, 4).Summary);
        Assert.Equal("1 build photo", new TagUsage(tag, 0, 1, 1).Summary);
        Assert.Equal("Not used", new TagUsage(tag, 0, 0, 0).Summary);
    }
}
