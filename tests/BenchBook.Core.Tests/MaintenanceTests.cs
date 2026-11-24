using BenchBook.Core.Model;
using BenchBook.Core.Settings;

namespace BenchBook.Core.Tests;

public class MaintenanceTests
{
    [Fact]
    public void CleanUpRemovesLeftoversAndKeepsEverythingInUse()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        var location = library.Location;
        var picture = sample.Assets.Single(a => a.Id == sample.AftermarketItems[0].ImageAssetId);

        using var store = library.OpenStore();
        // The aftermarket item goes, so its picture is unused.
        using (var command = store.Connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM aftermarket_items";
            command.ExecuteNonQuery();
        }
        Directory.CreateDirectory(location.ThumbnailsFolder);
        File.WriteAllBytes(location.ThumbnailPath(picture.Id), [1]);
        File.WriteAllBytes(location.ThumbnailPath(sample.Assets[0].Id), [1]);

        var strayOriginal = Path.Combine(location.OriginalsFolder, Ids.New() + ".jpg");
        var partial = Path.Combine(location.OriginalsFolder, ".partial-" + Ids.New());
        var strayThumbnail = location.ThumbnailPath(Ids.New());
        var strayPoster = location.PosterPath(Ids.New());
        var oddPoster = Path.Combine(location.PostersFolder, "notes.txt");
        var userFile = Path.Combine(location.OriginalsFolder, "Read me.txt");
        foreach (var path in new[] { strayOriginal, partial, strayThumbnail, strayPoster, oddPoster, userFile })
            File.WriteAllBytes(path, [1]);

        var removed = LibraryMaintenance.CleanUp(store, location);

        // The unused asset counts once (its files go with it, as on the Mac), plus five stray files.
        Assert.Equal(6, removed);
        Assert.DoesNotContain(store.ReadSnapshot().Assets, a => a.Id == picture.Id);
        Assert.False(File.Exists(location.FullPath(picture.OriginalRelativePath)));
        foreach (var path in new[] { location.ThumbnailPath(picture.Id), strayOriginal, partial, strayThumbnail, strayPoster, oddPoster })
            Assert.False(File.Exists(path), path);

        // Files in use, and names that aren't ids, stay.
        Assert.True(File.Exists(userFile));
        Assert.True(File.Exists(location.ThumbnailPath(sample.Assets[0].Id)));
        Assert.True(File.Exists(location.PosterPath(sample.References[2].Id)));
        foreach (var asset in sample.Assets.Where(a => a.Id != picture.Id))
            Assert.True(File.Exists(location.FullPath(asset.OriginalRelativePath)));

        Assert.Equal(0, LibraryMaintenance.CleanUp(store, location));
    }

    [Fact]
    public void ClearThumbnailsLeavesOriginalsAndPosters()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        Directory.CreateDirectory(library.Location.ThumbnailsFolder);
        File.WriteAllBytes(library.Location.ThumbnailPath(sample.Assets[0].Id), new byte[100]);

        var before = LibraryMaintenance.MeasureUsage(library.Location);
        Assert.Equal(100, before.Thumbnails);
        Assert.Equal(Sample.PosterBytes.Length, before.Posters);
        // Three JPEGs, the PDF and the kit's box art.
        Assert.Equal(3 * Sample.JpegBytes.Length + Sample.PdfBytes.Length + Sample.PosterBytes.Length, before.Originals);

        LibraryMaintenance.ClearThumbnails(library.Location);
        var after = LibraryMaintenance.MeasureUsage(library.Location);
        Assert.Equal(0, after.Thumbnails);
        Assert.Equal(before.Originals + before.Posters, after.Total);
    }

    [Fact]
    public void SettingsHaveTheSpecDefaultsAndSurviveASave()
    {
        using var library = new TestLibrary();
        var path = library.Location.SettingsPath;
        var defaults = AppSettings.Load(path);
        Assert.True(defaults.FetchLinkDetails);
        Assert.False(defaults.OpenImagesAtActualSize);
        Assert.True(defaults.ShowNoteBoxes);
        Assert.Equal(ProjectStatus.Researching, defaults.NewProjectStatus);
        Assert.Null(defaults.LastBackup);

        defaults.FetchLinkDetails = false;
        defaults.LastBackup = Sample.Date;
        defaults.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.False(loaded.FetchLinkDetails);
        Assert.Equal(Sample.Date, loaded.LastBackup);
    }

    [Fact]
    public void UnreadableSettingsFallBackToDefaultsWithoutOverwritingThem()
    {
        using var library = new TestLibrary();
        Directory.CreateDirectory(library.Location.AppFolder);
        File.WriteAllText(library.Location.SettingsPath, "{ broken");
        Assert.True(AppSettings.Load(library.Location.SettingsPath).FetchLinkDetails);
        Assert.Equal("{ broken", File.ReadAllText(library.Location.SettingsPath));
    }
}
