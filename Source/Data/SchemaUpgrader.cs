using Microsoft.EntityFrameworkCore;

namespace nAlbum.Data;

/// <summary>
/// Versioned schema upgrades via PRAGMA user_version (no EF migrations in this project).
/// RULE: every future model change = bump LatestVersion + add an UpgradeToN step + keep fresh-DB and upgraded-DB schemas identical.
/// </summary>
public static class SchemaUpgrader
{
    public const int LatestVersion = 2;

    public static async Task RunAsync(IDbContextFactory<AlbumDb> factory, string dbPath)
    {
        await using var db = await factory.CreateDbContextAsync();

        if (!await TableExists(db, "Albums"))
        {
            // Brand-new database: EF creates the full current model.
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync($"PRAGMA user_version = {LatestVersion}");
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            return;
        }

        var version = await GetVersion(db); // 0 = legacy DB created by EnsureCreated
        if (version < LatestVersion)
        {
            await Backup(db, dbPath, version);
            if (version < 2) await UpgradeTo2(db);
        }

        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }

    private static async Task<bool> TableExists(AlbumDb db, string name) =>
        await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {name}")
            .SingleAsync() > 0;

    private static async Task<int> GetVersion(AlbumDb db) =>
        await db.Database.SqlQuery<int>($"SELECT user_version AS Value FROM pragma_user_version").SingleAsync();

    /// <summary>Consistent copy of the database (works with WAL). Kept next to the DB file.</summary>
    private static async Task Backup(AlbumDb db, string dbPath, int fromVersion)
    {
        var target = $"{dbPath}.bak-v{fromVersion}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", target);
        Console.WriteLine($"Database backup written: {target}");
    }

    private static async Task UpgradeTo2(AlbumDb db)
    {
        string[] steps =
        {
            // --- permanent numbers --------------------------------------------------------------
            "ALTER TABLE \"Albums\" ADD COLUMN \"NextNumber\" INTEGER NOT NULL DEFAULT 1",
            "ALTER TABLE \"Media\" ADD COLUMN \"Number\" INTEGER NOT NULL DEFAULT 0",
            // today's rank by Id becomes the permanent number (nothing changes visibly)
            "UPDATE \"Media\" SET \"Number\" = (SELECT COUNT(*) FROM \"Media\" m2 WHERE m2.\"AlbumId\" = \"Media\".\"AlbumId\" AND m2.\"Id\" <= \"Media\".\"Id\")",
            "UPDATE \"Albums\" SET \"NextNumber\" = COALESCE((SELECT MAX(\"Number\") FROM \"Media\" WHERE \"Media\".\"AlbumId\" = \"Albums\".\"Id\"), 0) + 1",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Media_AlbumId_Number\" ON \"Media\" (\"AlbumId\", \"Number\")",

            // --- statistics tables --------------------------------------------------------------
            "CREATE TABLE IF NOT EXISTS \"BotUsers\" (" +
            " \"UserId\" INTEGER NOT NULL CONSTRAINT \"PK_BotUsers\" PRIMARY KEY," +
            " \"FirstSeenAt\" TEXT NOT NULL," +
            " \"LastSeenAt\" TEXT NOT NULL)",

            "CREATE TABLE IF NOT EXISTS \"AlbumViewStats\" (" +
            " \"AlbumId\" INTEGER NOT NULL," +
            " \"UserId\" INTEGER NOT NULL," +
            " \"Views\" INTEGER NOT NULL DEFAULT 0," +
            " \"RandomViews\" INTEGER NOT NULL DEFAULT 0," +
            " \"Pages\" INTEGER NOT NULL DEFAULT 0," +
            " \"FirstViewedAt\" TEXT NOT NULL," +
            " \"LastViewedAt\" TEXT NOT NULL," +
            " CONSTRAINT \"PK_AlbumViewStats\" PRIMARY KEY (\"AlbumId\", \"UserId\")," +
            " CONSTRAINT \"FK_AlbumViewStats_Albums_AlbumId\" FOREIGN KEY (\"AlbumId\") REFERENCES \"Albums\" (\"Id\") ON DELETE CASCADE)",
            "CREATE INDEX IF NOT EXISTS \"IX_AlbumViewStats_UserId\" ON \"AlbumViewStats\" (\"UserId\")",

            "CREATE TABLE IF NOT EXISTS \"Counters\" (" +
            " \"Name\" TEXT NOT NULL CONSTRAINT \"PK_Counters\" PRIMARY KEY," +
            " \"Value\" INTEGER NOT NULL DEFAULT 0)",

            // --- one-time seeds so the lifetime counters are not misleading right after the upgrade ----
            "INSERT OR IGNORE INTO \"Counters\" (\"Name\", \"Value\") SELECT 'albums_created', COUNT(*) FROM \"Albums\"",
            "INSERT OR IGNORE INTO \"Counters\" (\"Name\", \"Value\") SELECT 'media_added', COUNT(*) FROM \"Media\"",
            // known users: album owners and viewers with access (approximate first/last seen)
            "INSERT OR IGNORE INTO \"BotUsers\" (\"UserId\", \"FirstSeenAt\", \"LastSeenAt\") SELECT \"OwnerId\", MIN(\"CreatedAt\"), MAX(\"CreatedAt\") FROM \"Albums\" GROUP BY \"OwnerId\"",
            "INSERT OR IGNORE INTO \"BotUsers\" (\"UserId\", \"FirstSeenAt\", \"LastSeenAt\") SELECT \"UserId\", MIN(\"GrantedAt\"), MAX(\"GrantedAt\") FROM \"Access\" GROUP BY \"UserId\"",
        };

        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var sql in steps) await db.Database.ExecuteSqlRawAsync(sql);
        await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 2");
        await tx.CommitAsync();
    }
}
