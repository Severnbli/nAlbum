/*
    nAlbum – telegram bot for sharing photos and videos in albums
    Copyright (C) 2026 Usevalad Buben usevaladbuben@gmail.com

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as published by
    the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
    or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with this program. If not, see <https://www.gnu.org/licenses/>.
*/

using Microsoft.EntityFrameworkCore;

namespace nAlbum.Data;

/// <summary>
/// Versioned schema upgrades via PRAGMA user_version (no EF migrations in this project).
/// RULE: every future model change = bump LatestVersion + add an UpgradeToN step + keep fresh-DB and upgraded-DB schemas identical.
/// </summary>
public static class SchemaUpgrader
{
    public const int LatestVersion = 7;

    public static async Task RunAsync(IDbContextFactory<AlbumDb> factory, string dbPath)
    {
        await using var db = await factory.CreateDbContextAsync();

        if (!await TableExists(db, "Albums"))
        {
            // Brand-new database: EF creates the full current model.
            await db.Database.EnsureCreatedAsync();
            await SetVersion(db, LatestVersion);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            return;
        }

        var version = await GetVersion(db); // 0 = legacy DB created by EnsureCreated
        if (version < LatestVersion)
        {
            await Backup(db, dbPath, version);
            if (version < 2) await UpgradeTo2(db);
            if (version < 3) await UpgradeTo3(db);
            if (version < 4) await UpgradeTo4(db);
            if (version < 5) await UpgradeTo5(db);
            if (version < 6) await UpgradeTo6(db);
            if (version < 7) await UpgradeTo7(db);
        }

        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }

    private static async Task<bool> TableExists(AlbumDb db, string name) =>
        await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {name}")
            .SingleAsync() > 0;

    /// <summary>
    /// PRAGMA statements cannot take parameters, so the value is an int (never text) and range-checked.
    /// Every other statement in the bot is parameterized; values are never concatenated into SQL.
    /// </summary>
    private static async Task SetVersion(AlbumDb db, int version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(version, LatestVersion);
        await db.Database.ExecuteSqlRawAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"PRAGMA user_version = {version}"));
    }

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
        await SetVersion(db, 2);
        await tx.CommitAsync();
    }

    private static async Task UpgradeTo3(AlbumDb db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"UserPreferences\" (\"UserId\" INTEGER NOT NULL CONSTRAINT \"PK_UserPreferences\" PRIMARY KEY, \"LanguageCode\" TEXT NULL)");
        await SetVersion(db, 3);
    }

    /// <summary>Roles and invite links replace open/closed albums. Codes of open albums become unlimited invite links.</summary>
    private static async Task UpgradeTo4(AlbumDb db)
    {
        string[] steps =
        {
            "CREATE TABLE IF NOT EXISTS \"InviteLinks\" (" +
            " \"Id\" INTEGER NOT NULL CONSTRAINT \"PK_InviteLinks\" PRIMARY KEY AUTOINCREMENT," +
            " \"AlbumId\" INTEGER NOT NULL," +
            " \"Code\" TEXT NOT NULL," +
            " \"CreatedBy\" INTEGER NOT NULL," +
            " \"CreatedAt\" TEXT NOT NULL," +
            " \"ExpiresAt\" TEXT NULL," +
            " \"MaxUses\" INTEGER NULL," +
            " \"Uses\" INTEGER NOT NULL DEFAULT 0," +
            " CONSTRAINT \"FK_InviteLinks_Albums_AlbumId\" FOREIGN KEY (\"AlbumId\") REFERENCES \"Albums\" (\"Id\") ON DELETE CASCADE)",
            "CREATE INDEX IF NOT EXISTS \"IX_InviteLinks_AlbumId\" ON \"InviteLinks\" (\"AlbumId\")",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_InviteLinks_Code\" ON \"InviteLinks\" (\"Code\")",

            // existing shared codes keep working: unlimited links created by the owner
            "INSERT INTO \"InviteLinks\" (\"AlbumId\", \"Code\", \"CreatedBy\", \"CreatedAt\", \"ExpiresAt\", \"MaxUses\", \"Uses\")" +
            " SELECT \"Id\", \"AccessCode\", \"OwnerId\", strftime('%Y-%m-%d %H:%M:%S', 'now'), NULL, NULL," +
            " (SELECT COUNT(*) FROM \"Access\" WHERE \"Access\".\"AlbumId\" = \"Albums\".\"Id\")" +
            " FROM \"Albums\" WHERE \"IsOpen\" = 1 AND \"AccessCode\" IS NOT NULL",

            "ALTER TABLE \"Access\" ADD COLUMN \"Role\" INTEGER NOT NULL DEFAULT 0", // every viewer becomes a member
            "ALTER TABLE \"BotUsers\" ADD COLUMN \"Name\" TEXT NULL",

            "DROP INDEX IF EXISTS \"IX_Albums_AccessCode\"",
            "ALTER TABLE \"Albums\" DROP COLUMN \"AccessCode\"",
            "ALTER TABLE \"Albums\" DROP COLUMN \"IsOpen\"",
        };

        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var sql in steps) await db.Database.ExecuteSqlRawAsync(sql);
        await SetVersion(db, 4);
        await tx.CommitAsync();
    }

    private static async Task UpgradeTo5(AlbumDb db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Albums\" ADD COLUMN \"SongFileId\" TEXT NULL");
        await SetVersion(db, 5);
        await tx.CommitAsync();
    }

    /// <summary>User bans and support conversations.</summary>
    private static async Task UpgradeTo6(AlbumDb db)
    {
        string[] steps =
        {
            "ALTER TABLE \"BotUsers\" ADD COLUMN \"IsBanned\" INTEGER NOT NULL DEFAULT 0",

            "CREATE TABLE IF NOT EXISTS \"Conversations\" (" +
            " \"Id\" INTEGER NOT NULL CONSTRAINT \"PK_Conversations\" PRIMARY KEY AUTOINCREMENT," +
            " \"UserId\" INTEGER NOT NULL," +
            " \"Status\" INTEGER NOT NULL," +
            " \"CreatedAt\" TEXT NOT NULL," +
            " \"UpdatedAt\" TEXT NOT NULL," +
            " \"AwaitingAdmin\" INTEGER NOT NULL)",
            "CREATE INDEX IF NOT EXISTS \"IX_Conversations_UserId_Status\" ON \"Conversations\" (\"UserId\", \"Status\")",
            "CREATE INDEX IF NOT EXISTS \"IX_Conversations_Status_UpdatedAt\" ON \"Conversations\" (\"Status\", \"UpdatedAt\")",

            "CREATE TABLE IF NOT EXISTS \"ConversationMessages\" (" +
            " \"Id\" INTEGER NOT NULL CONSTRAINT \"PK_ConversationMessages\" PRIMARY KEY AUTOINCREMENT," +
            " \"ConversationId\" INTEGER NOT NULL," +
            " \"FromAdmin\" INTEGER NOT NULL," +
            " \"Text\" TEXT NOT NULL," +
            " \"SentAt\" TEXT NOT NULL," +
            " CONSTRAINT \"FK_ConversationMessages_Conversations_ConversationId\" FOREIGN KEY (\"ConversationId\") REFERENCES \"Conversations\" (\"Id\") ON DELETE CASCADE)",
            "CREATE INDEX IF NOT EXISTS \"IX_ConversationMessages_ConversationId_Id\" ON \"ConversationMessages\" (\"ConversationId\", \"Id\")",

            "CREATE TABLE IF NOT EXISTS \"ConversationNotices\" (" +
            " \"ChatId\" INTEGER NOT NULL," +
            " \"MessageId\" INTEGER NOT NULL," +
            " \"ConversationId\" INTEGER NOT NULL," +
            " CONSTRAINT \"PK_ConversationNotices\" PRIMARY KEY (\"ChatId\", \"MessageId\")," +
            " CONSTRAINT \"FK_ConversationNotices_Conversations_ConversationId\" FOREIGN KEY (\"ConversationId\") REFERENCES \"Conversations\" (\"Id\") ON DELETE CASCADE)",
            "CREATE INDEX IF NOT EXISTS \"IX_ConversationNotices_ConversationId\" ON \"ConversationNotices\" (\"ConversationId\")",
        };

        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var sql in steps) await db.Database.ExecuteSqlRawAsync(sql);
        await SetVersion(db, 6);
        await tx.CommitAsync();
    }

    /// <summary>Telegram @username of users, shown to admins.</summary>
    private static async Task UpgradeTo7(AlbumDb db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"BotUsers\" ADD COLUMN \"Username\" TEXT NULL");
        await SetVersion(db, 7);
        await tx.CommitAsync();
    }
}
