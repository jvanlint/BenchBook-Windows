using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Tests;

public class StoreTests
{
    [Fact]
    public void ANewStoreHasTheLatestSchemaWithWalAndForeignKeys()
    {
        using var library = new TestLibrary();
        using var store = library.OpenStore();
        Assert.Equal(Migrations.Latest, store.SchemaStep);
        Assert.Equal("wal", Scalar(store, "PRAGMA journal_mode"));
        Assert.Equal(1L, Scalar(store, "PRAGMA foreign_keys"));
    }

    [Fact]
    public void ASnapshotReadsBackExactly()
    {
        using var library = new TestLibrary();
        var original = Sample.Install(library);
        using var store = library.OpenStore();
        Assert.Equal(Json(original), Json(store.ReadSnapshot()));
    }

    [Fact]
    public void DeletingAProjectDeletesItsRecordsButNotPaintsOrTags()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        Execute(store, $"DELETE FROM projects WHERE id = '{sample.Projects[0].Id}'");
        var after = store.ReadSnapshot();
        Assert.Empty(after.References);
        Assert.Empty(after.ReferenceTags);
        Assert.Empty(after.VideoMoments);
        Assert.Empty(after.Markups);
        Assert.Empty(after.ProjectPaints);
        Assert.Empty(after.AftermarketItems);
        Assert.Empty(after.BuildPhotos);
        Assert.Empty(after.BuildPhotoTags);
        // Kits stay in the catalogue, in no project; mixes and their recipes stay.
        Assert.Equal(2, after.Kits.Count);
        Assert.All(after.Kits, k => Assert.Null(k.ProjectId));
        Assert.Equal(3, after.Paints.Count);
        Assert.Equal(2, after.PaintMixParts.Count);
        Assert.Single(after.Tags);
        Assert.Single(after.OwnedPaints);
        Assert.Equal(5, after.Assets.Count);
    }

    [Fact]
    public void APaintUsedInAProjectCantBeDeleted()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var error = Assert.Throws<SqliteException>(() => Execute(store, $"DELETE FROM paints WHERE id = '{sample.Paints[0].Id}'"));
        Assert.Equal(19, error.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public void TagNamesAndPaintKeysAreUnique()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        var tag = sample.Tags[0];
        var copy = new LibrarySnapshot
        {
            Tags = [new Tag { Id = Ids.New(), CreatedAt = tag.CreatedAt, UpdatedAt = tag.UpdatedAt, Name = "WHEEL WELL", NormalizedName = tag.NormalizedName }],
        };
        Assert.Throws<SqliteException>(() => store.InsertSnapshot(copy));
        Assert.Single(store.ReadSnapshot().Tags);
    }

    [Fact]
    public void AStoreFromANewerBenchBookIsLeftAlone()
    {
        using var library = new TestLibrary();
        using (var store = library.OpenStore()) Execute(store, $"PRAGMA user_version = {Migrations.Latest + 5}");
        var before = File.ReadAllBytes(library.Location.StorePath);
        var error = Assert.Throws<StoreOpenException>(() => library.OpenStore());
        Assert.Contains("newer version", error.Message);
        Assert.Equal(before, File.ReadAllBytes(library.Location.StorePath));
    }

    [Fact]
    public void AFileThatIsntAStoreIsReportedAndLeftAlone()
    {
        using var library = new TestLibrary();
        Directory.CreateDirectory(library.Location.AppFolder);
        var junk = "This is not a database, but it is the user's file."u8.ToArray();
        File.WriteAllBytes(library.Location.StorePath, junk);
        Assert.Throws<StoreOpenException>(() => library.OpenStore());
        Assert.Equal(junk, File.ReadAllBytes(library.Location.StorePath));
    }

    [Fact]
    public void UnusedAssetsAreThoseNothingPointsTo()
    {
        using var library = new TestLibrary();
        var sample = Sample.Install(library);
        using var store = library.OpenStore();
        Assert.Empty(store.UnusedAssets());

        // The aftermarket item keeps its picture in use; deleting it frees the asset.
        Execute(store, $"DELETE FROM aftermarket_items");
        var unused = Assert.Single(store.UnusedAssets());
        Assert.Equal(sample.AftermarketItems[0].ImageAssetId, unused.Id);
        Assert.False(store.DeleteAssetIfUnused(sample.Assets[0].Id));
        Assert.True(store.DeleteAssetIfUnused(unused.Id));
    }

    internal static string Json(LibrarySnapshot snapshot) =>
        System.Text.Json.JsonSerializer.Serialize(snapshot, Backup.BackupJson.Options);

    private static object? Scalar(LibraryStore store, string sql)
    {
        using var command = store.Connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(LibraryStore store, string sql)
    {
        using var command = store.Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
