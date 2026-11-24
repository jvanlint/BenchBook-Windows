using System.Globalization;
using BenchBook.Core.Model;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Store;

/// <summary>The store couldn't be opened. Nothing in it has been changed.</summary>
public sealed class StoreOpenException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The SQLite store. Write-ahead logging and foreign keys are on. Its layout
/// is private to the Windows app; only library.json is shared.
/// </summary>
public sealed partial class LibraryStore : IDisposable
{
    private static readonly TableMap<Project> ProjectsTable = new("projects");
    private static readonly TableMap<Asset> AssetsTable = new("assets");
    private static readonly TableMap<Reference> ReferencesTable = new("reference_items");
    private static readonly TableMap<Tag> TagsTable = new("tags");
    private static readonly TableMap<ReferenceTag> ReferenceTagsTable = new("reference_tags");
    private static readonly TableMap<VideoMoment> VideoMomentsTable = new("video_moments");
    private static readonly TableMap<Markup> MarkupsTable = new("markups");
    private static readonly TableMap<Paint> PaintsTable = new("paints");
    private static readonly TableMap<OwnedPaint> OwnedPaintsTable = new("owned_paints");
    private static readonly TableMap<ProjectPaint> ProjectPaintsTable = new("project_paints");
    private static readonly TableMap<AftermarketItem> AftermarketTable = new("aftermarket_items");
    private static readonly TableMap<PaintMixPart> PaintMixPartsTable = new("paint_mix_parts");
    private static readonly TableMap<BuildPhoto> BuildPhotosTable = new("build_photos");
    private static readonly TableMap<BuildPhotoTag> BuildPhotoTagsTable = new("build_photo_tags");
    private static readonly TableMap<Kit> KitsTable = new("kits");

    private readonly SqliteConnection _connection;

    private LibraryStore(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
    }

    public string Path { get; }

    /// <summary>
    /// Opens the store, creating it if there's none, and runs any migrations.
    /// An existing store is copied first, as "BenchBook Before 6.0.0.db" beside
    /// it. If anything goes wrong, the store is left as it was and a
    /// <see cref="StoreOpenException"/> says why.
    /// </summary>
    public static LibraryStore Open(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        SqliteConnection? connection = null;
        try
        {
            connection = Connect(path, SqliteOpenMode.ReadWriteCreate);
            var version = Scalar<long>(connection, "PRAGMA user_version");
            if (version > Migrations.Latest)
                throw new StoreOpenException("This library was made by a newer version of BenchBook. Update BenchBook, then open it again.");
            if (version > 0 && version < Migrations.Latest)
                CopyBeforeUpgrade(connection, path);
            Migrate(connection, version);
            return new LibraryStore(connection, path);
        }
        catch (StoreOpenException)
        {
            connection?.Dispose();
            throw;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            connection?.Dispose();
            throw new StoreOpenException(error.Message, error);
        }
    }

    /// <summary>Creates a new store holding the snapshot. The file must not exist yet.</summary>
    public static void Create(string path, LibrarySnapshot snapshot)
    {
        if (File.Exists(path)) throw new IOException($"“{path}” already exists.");
        using var store = Open(path);
        store.InsertSnapshot(snapshot);
    }

    private static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            // Without pooling, closing the store closes the file, so it can be moved.
            Pooling = false,
            ForeignKeys = true,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Migrate(SqliteConnection connection, long from)
    {
        for (var step = (int)from; step < Migrations.Latest; step++)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, Migrations.Steps[step].Sql, transaction);
            Migrations.Steps[step].Code?.Invoke(connection, transaction);
            Execute(connection, $"PRAGMA user_version = {step + 1}", transaction);
            // Every link must still hold, or nothing of the step is kept.
            if (HasForeignKeyProblems(connection, transaction))
                throw new StoreOpenException("Your library couldn't be upgraded, as some of its records don't fit together. It hasn't been changed.");
            transaction.Commit();
        }
    }

    /// <summary>
    /// Copies the store beside it as e.g. "BenchBook Before 6.0.0.db", made in a
    /// hidden folder first so a failed copy never leaves part of one. An existing
    /// copy is kept: it's from before the first try. SQLite's backup gives one
    /// consistent file, so there are no -wal or -shm files to copy. If the copy
    /// fails, the store isn't upgraded (library-format.md, "The library on disk").
    /// </summary>
    private static void CopyBeforeUpgrade(SqliteConnection connection, string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var copy = UpgradeCopyPath(path);
        if (File.Exists(copy)) return;
        var staging = System.IO.Path.Combine(folder, ".upgrade-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var staged = System.IO.Path.Combine(staging, name + ".db");
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = staged, Pooling = false }.ToString()))
            {
                destination.Open();
                connection.BackupDatabase(destination);
            }
            File.Move(staged, copy);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new StoreOpenException($"Your library needs an upgrade, but it couldn't be copied first, so it hasn't been changed. {error.Message}", error);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>e.g. "BenchBook Before 6.0.0.db", beside the store.</summary>
    public static string UpgradeCopyPath(string storePath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(storePath)!,
            $"{System.IO.Path.GetFileNameWithoutExtension(storePath)} Before {Migrations.Steps[^1].SchemaVersion}{System.IO.Path.GetExtension(storePath)}");

    private static bool HasForeignKeyProblems(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    // MARK: Snapshots

    /// <summary>Every record, read in one consistent snapshot.</summary>
    public LibrarySnapshot ReadSnapshot()
    {
        using var transaction = _connection.BeginTransaction(deferred: true);
        var snapshot = new LibrarySnapshot
        {
            Projects = ProjectsTable.ReadAll(_connection, transaction),
            References = ReferencesTable.ReadAll(_connection, transaction),
            Assets = AssetsTable.ReadAll(_connection, transaction),
            Tags = TagsTable.ReadAll(_connection, transaction),
            ReferenceTags = ReferenceTagsTable.ReadAll(_connection, transaction),
            VideoMoments = VideoMomentsTable.ReadAll(_connection, transaction),
            Markups = MarkupsTable.ReadAll(_connection, transaction),
            Paints = PaintsTable.ReadAll(_connection, transaction),
            OwnedPaints = OwnedPaintsTable.ReadAll(_connection, transaction),
            ProjectPaints = ProjectPaintsTable.ReadAll(_connection, transaction),
            AftermarketItems = AftermarketTable.ReadAll(_connection, transaction),
            PaintMixParts = PaintMixPartsTable.ReadAll(_connection, transaction),
            BuildPhotos = BuildPhotosTable.ReadAll(_connection, transaction),
            BuildPhotoTags = BuildPhotoTagsTable.ReadAll(_connection, transaction),
            Kits = KitsTable.ReadAll(_connection, transaction),
        };
        transaction.Commit();
        return snapshot;
    }

    /// <summary>Adds every record in the snapshot in one transaction: all of them, or none.</summary>
    public void InsertSnapshot(LibrarySnapshot snapshot)
    {
        snapshot.Tidy();
        using var transaction = _connection.BeginTransaction();
        // Parents before children, so the foreign keys hold at every step.
        ProjectsTable.InsertAll(_connection, transaction, snapshot.Projects);
        AssetsTable.InsertAll(_connection, transaction, snapshot.Assets);
        ReferencesTable.InsertAll(_connection, transaction, snapshot.References);
        TagsTable.InsertAll(_connection, transaction, snapshot.Tags);
        ReferenceTagsTable.InsertAll(_connection, transaction, snapshot.ReferenceTags);
        VideoMomentsTable.InsertAll(_connection, transaction, snapshot.VideoMoments);
        BuildPhotosTable.InsertAll(_connection, transaction, snapshot.BuildPhotos);
        BuildPhotoTagsTable.InsertAll(_connection, transaction, snapshot.BuildPhotoTags);
        MarkupsTable.InsertAll(_connection, transaction, snapshot.Markups);
        PaintsTable.InsertAll(_connection, transaction, snapshot.Paints);
        OwnedPaintsTable.InsertAll(_connection, transaction, snapshot.OwnedPaints);
        PaintMixPartsTable.InsertAll(_connection, transaction, snapshot.PaintMixParts);
        ProjectPaintsTable.InsertAll(_connection, transaction, snapshot.ProjectPaints);
        AftermarketTable.InsertAll(_connection, transaction, snapshot.AftermarketItems);
        KitsTable.InsertAll(_connection, transaction, snapshot.Kits);
        transaction.Commit();
    }

    // MARK: Queries

    public LibraryCounts Counts() => new(
        Scalar<long>(_connection, "SELECT count(*) FROM projects"),
        Scalar<long>(_connection, "SELECT count(*) FROM reference_items"),
        Scalar<long>(_connection, "SELECT count(*) FROM paints"));

    public List<Project> Projects() => ProjectsTable.ReadAll(_connection, null);

    public HashSet<string> AssetIds() => Strings("SELECT id FROM assets");

    public HashSet<string> ReferenceIds() => Strings("SELECT id FROM reference_items");

    /// <summary>Assets that no reference, aftermarket item, build photo or kit points to.</summary>
    public List<Asset> UnusedAssets()
    {
        var used = Strings("""
            SELECT assetId FROM reference_items WHERE assetId IS NOT NULL
            UNION SELECT imageAssetId FROM aftermarket_items WHERE imageAssetId IS NOT NULL
            UNION SELECT assetId FROM build_photos
            UNION SELECT pictureAssetId FROM kits WHERE pictureAssetId IS NOT NULL
            """);
        return AssetsTable.ReadAll(_connection, null).Where(a => !used.Contains(a.Id)).ToList();
    }

    /// <summary>Deletes an asset's record if it's still unused. False if something uses it now.</summary>
    public bool DeleteAssetIfUnused(string assetId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            DELETE FROM assets WHERE id = $id
              AND NOT EXISTS (SELECT 1 FROM reference_items WHERE assetId = $id)
              AND NOT EXISTS (SELECT 1 FROM aftermarket_items WHERE imageAssetId = $id)
              AND NOT EXISTS (SELECT 1 FROM build_photos WHERE assetId = $id)
              AND NOT EXISTS (SELECT 1 FROM kits WHERE pictureAssetId = $id)
            """;
        command.Parameters.AddWithValue("$id", assetId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Test and diagnostic access to the schema version.</summary>
    internal long SchemaStep => Scalar<long>(_connection, "PRAGMA user_version");

    internal SqliteConnection Connection => _connection;

    private HashSet<string> Strings(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var set = new HashSet<string>(Ids.Comparer);
        while (reader.Read()) set.Add(reader.GetString(0));
        return set;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

public readonly record struct LibraryCounts(long Projects, long References, long Paints);
