namespace BenchBook.Core.Store;

/// <summary>
/// The store's schema as numbered steps. PRAGMA user_version holds the last
/// step run. Never edit a released step: add a new one.
/// </summary>
internal static class Migrations
{
    public static readonly IReadOnlyList<string> Steps =
    [
        // 1: the tables of library.json schema 1.0.0, with the delete rules in behaviour.md.
        """
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
        """,
    ];

    public static int Latest => Steps.Count;
}
