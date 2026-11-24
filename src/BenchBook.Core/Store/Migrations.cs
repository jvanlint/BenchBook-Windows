using BenchBook.Core.Model;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Store;

/// <summary>One step: its SQL, then any work done in code, in the same transaction.</summary>
/// <param name="SchemaVersion">The library schema version the store holds after it.</param>
internal sealed record Migration(string SchemaVersion, string Sql, Action<SqliteConnection, SqliteTransaction>? Code = null);

/// <summary>
/// The store's schema as numbered steps. PRAGMA user_version holds the last
/// step run. Never edit a released step: add a new one.
/// </summary>
internal static class Migrations
{
    public static readonly IReadOnlyList<Migration> Steps =
    [
        // 1: the tables of library.json schema 1.0.0, with the delete rules in behaviour.md.
        new("1.0.0", """
        CREATE TABLE projects (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            name TEXT NOT NULL,
            manufacturer TEXT NOT NULL,
            subject TEXT NOT NULL,
            scale TEXT NOT NULL,
            kitNumber TEXT,
            markingsScheme TEXT,
            notes TEXT,
            status TEXT
        );

        CREATE TABLE assets (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            originalRelativePath TEXT NOT NULL,
            originalFilename TEXT,
            mimeType TEXT NOT NULL,
            byteCount INTEGER NOT NULL,
            sha256 TEXT NOT NULL,
            pixelWidth INTEGER,
            pixelHeight INTEGER
        );

        CREATE TABLE reference_items (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            projectId TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            kind TEXT NOT NULL,
            title TEXT,
            note TEXT,
            text TEXT,
            sourcePageUrl TEXT,
            originalResourceUrl TEXT,
            capturedAt TEXT NOT NULL,
            assetId TEXT REFERENCES assets(id) ON DELETE RESTRICT,
            videoChannel TEXT,
            videoChannelUrl TEXT,
            videoDurationSeconds REAL,
            videoPublishedAt TEXT,
            videoDescription TEXT,
            videoInfoFetchedAt TEXT,
            linkSiteName TEXT,
            linkDescription TEXT,
            linkPreviewFetchedAt TEXT
        );
        CREATE INDEX reference_items_project ON reference_items(projectId);
        CREATE INDEX reference_items_asset ON reference_items(assetId);

        CREATE TABLE tags (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            name TEXT NOT NULL,
            normalizedName TEXT NOT NULL UNIQUE,
            color TEXT
        );

        CREATE TABLE reference_tags (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            referenceId TEXT NOT NULL REFERENCES reference_items(id) ON DELETE CASCADE,
            tagId TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            UNIQUE (referenceId, tagId)
        );
        CREATE INDEX reference_tags_tag ON reference_tags(tagId);

        CREATE TABLE video_moments (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            referenceId TEXT NOT NULL REFERENCES reference_items(id) ON DELETE CASCADE,
            offsetSeconds REAL NOT NULL,
            title TEXT,
            note TEXT
        );
        CREATE INDEX video_moments_reference ON video_moments(referenceId);

        CREATE TABLE markups (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            referenceId TEXT NOT NULL REFERENCES reference_items(id) ON DELETE CASCADE,
            pageIndex INTEGER NOT NULL,
            kind TEXT NOT NULL,
            color TEXT NOT NULL,
            x REAL NOT NULL,
            y REAL NOT NULL,
            width REAL NOT NULL,
            height REAL NOT NULL,
            points TEXT NOT NULL,
            text TEXT NOT NULL
        );
        CREATE INDEX markups_reference ON markups(referenceId);

        CREATE TABLE paints (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            brand TEXT NOT NULL,
            range TEXT NOT NULL,
            code TEXT NOT NULL,
            name TEXT NOT NULL,
            notes TEXT,
            swatchHex TEXT,
            standardCode TEXT NOT NULL,
            identityKey TEXT NOT NULL UNIQUE
        );

        CREATE TABLE owned_paints (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            paintId TEXT NOT NULL UNIQUE REFERENCES paints(id) ON DELETE CASCADE
        );

        CREATE TABLE project_paints (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            projectId TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            paintId TEXT REFERENCES paints(id) ON DELETE RESTRICT,
            standard TEXT,
            usedFor TEXT,
            notes TEXT
        );
        CREATE INDEX project_paints_project ON project_paints(projectId);
        CREATE INDEX project_paints_paint ON project_paints(paintId);

        CREATE TABLE aftermarket_items (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            projectId TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            productName TEXT NOT NULL,
            manufacturer TEXT NOT NULL,
            productNumber TEXT,
            type TEXT NOT NULL,
            status TEXT NOT NULL,
            scaleCompatibility TEXT,
            productUrl TEXT,
            notes TEXT,
            imageAssetId TEXT REFERENCES assets(id) ON DELETE RESTRICT
        );
        CREATE INDEX aftermarket_items_project ON aftermarket_items(projectId);
        CREATE INDEX aftermarket_items_asset ON aftermarket_items(imageAssetId);
        """),

        // 2: schema 6.0.0, the Mac's 2.0.0 to 6.0.0 at once: build photos, kits,
        // kits in projects, paint standards with match and type, and mixes.
        new("6.0.0", """
        CREATE TABLE build_photos (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            projectId TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            assetId TEXT NOT NULL REFERENCES assets(id) ON DELETE RESTRICT,
            notes TEXT,
            takenAt TEXT NOT NULL,
            stage TEXT NOT NULL
        );
        CREATE INDEX build_photos_project ON build_photos(projectId);
        CREATE INDEX build_photos_asset ON build_photos(assetId);

        CREATE TABLE build_photo_tags (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            buildPhotoId TEXT NOT NULL REFERENCES build_photos(id) ON DELETE CASCADE,
            tagId TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            UNIQUE (buildPhotoId, tagId)
        );
        CREATE INDEX build_photo_tags_tag ON build_photo_tags(tagId);

        -- A markup now belongs to a reference or a build photo, so referenceId
        -- becomes optional. SQLite can't change a column, so the table is rebuilt.
        CREATE TABLE markups_v2 (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            referenceId TEXT REFERENCES reference_items(id) ON DELETE CASCADE,
            buildPhotoId TEXT REFERENCES build_photos(id) ON DELETE CASCADE,
            pageIndex INTEGER NOT NULL,
            kind TEXT NOT NULL,
            color TEXT NOT NULL,
            x REAL NOT NULL,
            y REAL NOT NULL,
            width REAL NOT NULL,
            height REAL NOT NULL,
            points TEXT NOT NULL,
            text TEXT NOT NULL,
            CHECK ((referenceId IS NULL) <> (buildPhotoId IS NULL))
        );
        INSERT INTO markups_v2 (id, createdAt, updatedAt, referenceId, pageIndex, kind, color, x, y, width, height, points, text)
            SELECT id, createdAt, updatedAt, referenceId, pageIndex, kind, color, x, y, width, height, points, text FROM markups ORDER BY rowid;
        DROP TABLE markups;
        ALTER TABLE markups_v2 RENAME TO markups;
        CREATE INDEX markups_reference ON markups(referenceId);
        CREATE INDEX markups_build_photo ON markups(buildPhotoId);

        CREATE TABLE kits (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            brand TEXT NOT NULL,
            name TEXT NOT NULL,
            scale TEXT,
            number TEXT,
            owned INTEGER NOT NULL,
            status TEXT NOT NULL,
            rating INTEGER,
            link TEXT,
            pricePaid TEXT,
            forSale INTEGER NOT NULL,
            salePrice TEXT,
            notes TEXT,
            pictureAssetId TEXT REFERENCES assets(id) ON DELETE RESTRICT,
            -- Deleting the project leaves the kit, in no project.
            projectId TEXT REFERENCES projects(id) ON DELETE SET NULL
        );
        CREATE INDEX kits_project ON kits(projectId);
        CREATE INDEX kits_picture ON kits(pictureAssetId);

        ALTER TABLE projects ADD COLUMN projectPhotoId TEXT;

        ALTER TABLE paints ADD COLUMN standardSystem TEXT NOT NULL DEFAULT '';
        ALTER TABLE paints ADD COLUMN match TEXT NOT NULL DEFAULT '';
        ALTER TABLE paints ADD COLUMN type TEXT NOT NULL DEFAULT '';
        ALTER TABLE paints ADD COLUMN isMix INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE project_paints ADD COLUMN standardSystem TEXT NOT NULL DEFAULT '';

        CREATE TABLE paint_mix_parts (
            id TEXT PRIMARY KEY NOT NULL,
            createdAt TEXT NOT NULL,
            updatedAt TEXT NOT NULL,
            mixId TEXT NOT NULL REFERENCES paints(id) ON DELETE CASCADE,
            -- A paint in a recipe can't be deleted.
            paintId TEXT NOT NULL REFERENCES paints(id) ON DELETE RESTRICT,
            parts REAL NOT NULL,
            position INTEGER NOT NULL,
            UNIQUE (mixId, paintId)
        );
        CREATE INDEX paint_mix_parts_paint ON paint_mix_parts(paintId);
        """, UpgradeTo6),
    ];

    public static int Latest => Steps.Count;

    /// <summary>
    /// The data the 4.0.0 and 5.0.0 upgrades move: each project's manufacturer,
    /// kit number and scale go to the end of its notes, then those columns go;
    /// each paint's and guide colour's standard text is split into a system and
    /// a code (behaviour.md, "Upgrading from schema 3" and "from schema 4").
    /// </summary>
    private static void UpgradeTo6(SqliteConnection connection, SqliteTransaction transaction)
    {
        var projects = new List<(string Id, string? Notes)>();
        Read(connection, transaction, "SELECT id, manufacturer, kitNumber, scale, notes FROM projects", reader =>
        {
            var line = KitDetailsNote.Line(Text(reader, 1), Text(reader, 2), Text(reader, 3));
            var notes = Text(reader, 4);
            var upgraded = KitDetailsNote.Notes(notes, line);
            if (upgraded != notes) projects.Add((reader.GetString(0), upgraded));
        });
        foreach (var (id, notes) in projects)
            Run(connection, transaction, "UPDATE projects SET notes = $v WHERE id = $id", ("$v", notes), ("$id", id));
        Run(connection, transaction, "ALTER TABLE projects DROP COLUMN manufacturer");
        Run(connection, transaction, "ALTER TABLE projects DROP COLUMN scale");
        Run(connection, transaction, "ALTER TABLE projects DROP COLUMN kitNumber");

        var paints = new List<(string Id, PaintStandard? Standard)>();
        Read(connection, transaction, "SELECT id, standardCode FROM paints WHERE standardSystem = ''", reader =>
        {
            var text = Text(reader, 1);
            if (!string.IsNullOrEmpty(text)) paints.Add((reader.GetString(0), PaintStandardParser.Parse(text)));
        });
        foreach (var (id, standard) in paints)
            Run(connection, transaction, "UPDATE paints SET standardSystem = $s, standardCode = $c WHERE id = $id",
                ("$s", standard?.SystemRaw ?? ""), ("$c", standard?.Code ?? ""), ("$id", id));

        var rows = new List<(string Id, PaintStandard? Standard)>();
        Read(connection, transaction, "SELECT id, standard FROM project_paints WHERE standardSystem = ''", reader =>
        {
            if (Text(reader, 1) is { } text) rows.Add((reader.GetString(0), PaintStandardParser.Parse(text)));
        });
        foreach (var (id, standard) in rows)
            Run(connection, transaction, "UPDATE project_paints SET standardSystem = $s, standard = $c WHERE id = $id",
                ("$s", standard?.SystemRaw ?? ""), ("$c", standard is { Code.Length: > 0 } ? standard.Code : null), ("$id", id));
    }

    private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static void Read(SqliteConnection connection, SqliteTransaction transaction, string sql, Action<SqliteDataReader> row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read()) row(reader);
    }

    private static void Run(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
