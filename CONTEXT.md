# AI context — nAlbum

Single-project Telegram bot (`nAlbum.csproj`, `net10.0`, nullable disabled). UI is one `AlbumsForm` session per private chat; persistence is SQLite via `AlbumService`. No tests, no EF migrations, no web host.

## Repository map

```text
/Program.cs              -> bootstrap, DI, TelegramBotBase builder, EnsureCreated
/Source/AlbumsForm.cs    -> only form; all user interaction
/Source/AlbumService.cs  -> all DB + in-process code throttle
/Source/AlbumDb.cs       -> EF model / indexes
/Source/Entities.cs      -> Album, MediaItem, AlbumAccess, AlbumListItem, MediaKind
/Source/BotInfo.cs       -> mutable static Username set after GetMe
/Dockerfile              -> runtime user uid 10001, VOLUME /data
/docker-compose.yml      -> env_file .env, DB_PATH=/data/albums.db, volume bot_data
```

## Architecture constraints

```text
Program -> AlbumsForm -> AlbumService -> IDbContextFactory<AlbumDb>
```

- Only form type registered: `WithServiceProvider<AlbumsForm>(services)`.
- `AlbumService` is a **singleton**. Every DB method must use a short-lived context from the factory (do not inject `AlbumDb` into the form or the singleton).
- `AlbumsForm.Render()` is empty on purpose; do not move logic there. Handlers: `Load` (text/commands), `SentData` (photo/video), `Action` (callbacks).
- Groups/channels are ignored (`IsPrivate`). `UserId` is `Device.DeviceId`.

## Non-obvious behavior

`Program.cs` `BotBaseBuilder`
- `.NoSerialization()` — `_mode`, `_albumId`, add/remove counters, `_viewIds` live only in the in-memory form. Process restart drops mid-flow state; albums themselves are in SQLite.
- `.UseThreadPool()` — in-repo comment: keep media upload order. Do not swap the message loop without checking TelegramBotBase + media groups.
- `.DefaultMessageLoop()` + `.NoProxy()`; compose publishes no ports (no webhook setup in this repo).
- `.CustomCommands` + `UploadBotCommands()` — `/start`, `/menu`, `/new`, `/join`, `/cancel`.
- `BotInfo.Username` assigned only after `GetMe`; share links need it.

`AlbumsForm` session machine
- `OnCommand` always sets `_mode = Idle` before handling — `/cancel` and any command abort NewTitle/Rename/Adding/Removing.
- Access codes in chat are accepted **only in Idle**. In `NewTitle`/`Rename` the same text is a title.
- Deep link: `/start CODE` → `TryJoin(args[0])`. `/join CODE` and `/start CODE` count a failed throttle attempt when the code is invalid **or** fails `LooksLikeCode` (plain chat text only reaches `TryJoin` after `LooksLikeCode`).
- Live callbacks:

```text
menu | new | mine:<page> | shared:<page> | al:<album>
view:<album>:<offset> | vp:<album>:<offset>
rnd:<album> | rp:<album>:<seed>:<offset>:<flags>      flags: 1 = numbers, 2 = delete mode (3 = both)
add:<album> | done:<album>
rmlist:<album>:<offset>
toggle:<album> | ren:<album> | del:<album> | delok:<album>
leave:<album>
```

- `done:` is used for **both** add-media and remove-media.
- Live remove: `rmlist:<album>:<skip>` → `OnRemoveView` (offset into `ListMedia`, page size `ViewPage`) + typed numbers.

Media add (`SentData`)
- `FileId` / `FileUniqueId` only (no files on disk).
- Media-group reaction: once per `MediaGroupId` (`FirstOfGroup`). Ungrouped: 👍 vs 🤔; grouped: always 👍 on the first item even if later items are duplicates.

View paging (`ShowViewPage`)
- `ViewPage = 10` matches Telegram’s media-group size.
- Prev/Next on the **nav** message tries in-place `EditMessageMedia` only if `_viewEditable` and `_viewIds.Count == items.Count`.
- Otherwise deletes the old media+nav (`DeleteMessages`) and resends. Failed sends mark the page non-editable.
- `SendBatch`: 429 retried up to 4 times (`RetryAfter` capped at 30s + 1). Other send failures split the group to isolate a bad `FileId`. After 4 flood retries the whole group is counted failed.
- Captions are an explicit `IReadOnlyList<string>` (`#n` = real album position from `AlbumService.GetPositions`). Ordered remove uses consecutive `#offset+i`; random view looks up each item.

Remove-by-number
- Numbers are **1-based positions in the whole album ordered by `MediaItem.Id`**, not the current page.
- `GetMediaIdsAtPositions` must run **before** `DeleteMedia` (positions shift after delete).
- `ParsePositions`: `"12"`, `"12 15 18"`, `"12-15"`, `"3, 7-9"`. Any bad token → entire parse `null`. Range must have `lo <= hi` and `hi - lo < 100`. More than 100 numbers → `null`.
- Typed user message is deleted (`TryDelete`) to keep the chat clean.
- Also available inside the random view: `Mode.Removing` with `_removeSeed != 0`; `OnRemoveNumbers` then refreshes via `RenderRandomPage` instead of `RenderRemovePage`. `_removeSeed` is reset to 0 in `OnRemoveView`.

`AlbumService.ListMediaShuffled`
- Order: `(Id * seed) % 2147483647` then `Id` (modulus is prime; seed must be non-zero).
- `rnd:` picks `Random.Shared.NextInt64(1, 2147483646)` (**upper bound exclusive** → `1..2147483645`).
- `rp:` reuses seed via `Math.Clamp(..., 1, 2147483646)` and offset in callback_data. Changing the modulus/seed range breaks stable Next/Prev.

Owner checks
- Owner writes (`add`/`done`/`rmlist`/`toggle`/`ren`/`del`/`delok`) go through `OwnedAlbum`. Views go through `ViewableAlbum`. `leave:` does **not** — it deletes `AlbumAccess` for `UserId` and shows the shared list. `AlbumService` methods generally do **not** re-check ownership.

`Action` errors
- Non-`ApiRequestException` → log + “Something went wrong”. `ApiRequestException` is **not** caught there (can bubble).
- Unknown callback prefix: `ConfirmAction()` only.

## Critical business rules

- Closed album: `IsOpen = false`, `AccessCode = null`, all `AlbumAccess` rows deleted (`CloseAlbum`). Viewers vanish from “Shared with me” (`ListShared` requires `a.IsOpen`).
- Opening always mints a **new** 10-char code (`OpenAlbum`). Old code does not survive close/reopen.
- Code alphabet `ABCDEFGHJKLMNPQRSTUVWXYZ23456789` (no `0/O/1/I`), length 10 (`LooksLikeCode` is case-sensitive; callers uppercase first).
- Viewers cannot add/remove/rename/open/close/delete. Owner always `CanView` even when closed.
- `JoinByCode` grants `AlbumAccess` for non-owners; owner joining by code does not insert access.
- Duplicate media in an album: same `FileUniqueId` → skip (`AddMedia` false). Unique index `(AlbumId, FileUniqueId)`.
- Deleting an album is hard delete of album + media rows + access (`DeleteAlbum`). Media files on Telegram are not deleted (bot never stored them).
- Title: whitespace-collapsed, max 100 (`CleanTitle` + EF `HasMaxLength(100)`).

## Data model traps

- **No migrations.** `EnsureCreatedAsync()` only creates a missing database. Changing entities will **not** update an existing `albums.db`.
- SQLite unique index on `Album.AccessCode` allows multiple NULLs (closed albums).
- FKs: `MediaItem` and `AlbumAccess` cascade on album delete; `DeleteAlbum` still deletes children explicitly inside a transaction.
- `AlbumAccess` has no surrogate key: composite `(AlbumId, UserId)`.
- `GetMediaIdsAtPositions` / `DeleteMedia` use sync `CreateDbContext()`; everything else uses `CreateDbContextAsync()`. Missing id at a position is `0` (skipped).
- `ListOwned` order: `Id` desc. `ListShared` order: `GrantedAt` desc, then album `Id` desc. Media list order: `Id` asc (except shuffle).

## External integrations

```text
Service: Telegram Bot API (via TelegramBotBase 8.0.0-preview.2 + Telegram.Bot)
Used by: Program.cs, AlbumsForm
Authentication: BOT_TOKEN env var (required; no default)
Configuration: DefaultMessageLoop, NoProxy; compose publishes no ports
Important behavior: media replayed by FileId; expired/broken ids fail send and are isolated in SendBatch
Failure behavior: reactions ignored; view delete/edit "not modified" ignored; 429 backed off in SendBatch/EditMedia
```

## Configuration that affects code

```text
BOT_TOKEN
- Missing → process throws at startup.

DB_PATH
- Default `/data/albums.db`. Local `dotnet run` without this env uses that same default (not a project-local file).
- Parent directory is created at startup.
```

Do not commit `.env` (gitignored). Compose expects `env_file: .env`.

## Background processing / concurrency

- No queued jobs. `Task.Delay(Timeout.Infinite)` keeps the process alive.
- Failed-code throttle is **in-memory** on the singleton (`_failedCodes`, lock): 5 failures / 10 minutes / user. Lost on restart; **not** shared across multiple containers.
- `AddMedia` check-then-insert; unique-index race → `DbUpdateException` → treat as duplicate (`false`).
- `OpenAlbum` retries code generation up to 10 times on unique collision.
- `UseThreadPool` + form fields (`_mode`, `_added`, `_lastGroupId`, `_viewIds`) are per-session mutable state; media-group updates rely on that.

## Docker / runtime traps

- Image user `bot` uid **10001**; `/data` must be writable by that uid (Dockerfile `chown`).
- Compose: `restart: unless-stopped`, named volume `bot_data` → `/data`.
- WAL enabled every start: `PRAGMA journal_mode=WAL`.
- Package versions: `Microsoft.EntityFrameworkCore.Sqlite` and `Microsoft.Extensions.DependencyInjection` are `10.0.*` (floating). `TelegramBotBase` is pinned to preview `8.0.0-preview.2`.

## Testing requirements

No test project.

## Dangerous areas

- `AlbumDb` / `Program.EnsureCreatedAsync` — schema edits need a manual plan for existing SQLite files; EnsureCreated will not migrate.
- `AlbumService.CloseAlbum` / `OpenAlbum` — close wipes viewers and the code; open replaces the code.
- `AlbumService.GetMediaIdsAtPositions` — resolve positions before delete; 1-based `Id` order.
- `AlbumsForm.Action` — owner/view checks are only here for most writes.
- `AlbumsForm.SendBatch` / `EditMedia` — flood-wait loops; do not add unbounded retries around Telegram send.
- `AlbumsForm.OnRemoveNumbers` — `_removeSeed != 0` refreshes the random page; `OnRemoveView` must set `_removeSeed = 0`.
- `AlbumService._failedCodes` — assuming this is durable or multi-instance-safe is wrong.

## AI modification rules

- Do not add a second form type without changing `WithServiceProvider<AlbumsForm>`.
- Do not inject `AlbumDb` into `AlbumService` or `AlbumsForm`; keep `IDbContextFactory`.
- Keep `Render()` empty.
- Preserve HTML encoding (`H`) for titles; messages use `ParseMode.Html`.
- New owner mutations must use `OwnedAlbum` (or equivalent server-side owner check). New viewer reads must use `CanView` / `ViewableAlbum`.
- Do not persist form state expecting `NoSerialization` to keep it.
- Do not introduce EF migrations unless also replacing `EnsureCreatedAsync` (today they would not run).
- Do not store downloaded media; `FileId` is the source of truth.
- `nAlbum.csproj` already asks to pin floating `10.0.*` packages after a successful restore if the csproj is edited.

## Commands

```text
dotnet run
  (requires BOT_TOKEN; set DB_PATH for a local sqlite file)

docker compose up --build
  (requires .env with BOT_TOKEN)
```

No test command in-repo.

---

# Source dump

Full repository source (excludes `.env`, `bot_data.tgz`, `bin/`, `obj/`, this file).

## `nAlbum.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <RootNamespace>nAlbum</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <!-- Floating version: after the first successful build, pin it to the resolved one. -->
    <PackageReference Include="TelegramBotBase" Version="8.0.0-preview.2" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.*" />
  </ItemGroup>

</Project>
```

## `Program.cs`

```csharp
using nAlbum.Source;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using TelegramBotBase.Builder;
using TelegramBotBase.Commands;

var token = Environment.GetEnvironmentVariable("BOT_TOKEN")
            ?? throw new InvalidOperationException("BOT_TOKEN is not set");
var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "/data/albums.db";

var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);

var services = new ServiceCollection()
    .AddDbContextFactory<AlbumDb>(o => o.UseSqlite($"Data Source={dbPath}"))
    .AddSingleton<AlbumService>()
    .BuildServiceProvider();

// Create the schema on first start and switch SQLite to WAL.
await using (var db = await services.GetRequiredService<IDbContextFactory<AlbumDb>>().CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

var bot = BotBaseBuilder
    .Create()
    .WithAPIKey(token)
    .DefaultMessageLoop()
    .WithServiceProvider<AlbumsForm>(services) // forms are created through DI
    .NoProxy()
    .UseDefaultRequestDispatcher()
    .CustomCommands(a =>
    {
        a.Start("Main menu");
        a.Add("menu", "Main menu");
        a.Add("new", "Create an album");
        a.Add("join", "Open an album by code");
        a.Add("cancel", "Cancel the current action");
    })
    .NoSerialization()   // form state is transient; albums live in SQLite
    .DefaultLanguage()
    .UseThreadPool()   // keeps media in upload order
    .Build();

BotInfo.Username = (await bot.Client.TelegramClient.GetMe()).Username;

await bot.UploadBotCommands();
await bot.Start();
Console.WriteLine($"Bot @{BotInfo.Username} started.");
await Task.Delay(Timeout.Infinite);
```

## `Source/BotInfo.cs`

```csharp
namespace nAlbum.Source;

public static class BotInfo
{
    public static string Username;
}
```

## `Source/Entities.cs`

```csharp
namespace nAlbum.Source;

public enum MediaKind
{
    Photo = 0,
    Video = 1
}

public class Album
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public string Title { get; set; }
    public bool IsOpen { get; set; }

    /// <summary>Random code that gives view access. Null while the album is closed.</summary>
    public string AccessCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A reference to media stored on Telegram's servers (file ids only, no files).</summary>
public class MediaItem
{
    public long Id { get; set; }
    public long AlbumId { get; set; }
    public MediaKind Kind { get; set; }
    public string FileId { get; set; }
    public string FileUniqueId { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A user who entered a valid code of an open album.</summary>
public class AlbumAccess
{
    public long AlbumId { get; set; }
    public long UserId { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
}

public class AlbumListItem
{
    public Album Album { get; set; }
    public int MediaCount { get; set; }
}
```

## `Source/AlbumDb.cs`

```csharp
using Microsoft.EntityFrameworkCore;

namespace nAlbum.Source;

public class AlbumDb : DbContext
{
    public AlbumDb(DbContextOptions<AlbumDb> options) : base(options)
    {
    }

    public DbSet<Album> Albums => Set<Album>();
    public DbSet<MediaItem> Media => Set<MediaItem>();
    public DbSet<AlbumAccess> Access => Set<AlbumAccess>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Album>(e =>
        {
            e.Property(x => x.Title).IsRequired().HasMaxLength(100);
            e.HasIndex(x => new { x.OwnerId, x.Id });
            e.HasIndex(x => x.AccessCode).IsUnique(); // SQLite allows many NULLs in a unique index
        });

        b.Entity<MediaItem>(e =>
        {
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.FileId).IsRequired();
            e.Property(x => x.FileUniqueId).IsRequired();
            e.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AlbumId, x.FileUniqueId }).IsUnique(); // no duplicates inside an album
            e.HasIndex(x => new { x.AlbumId, x.Id });
        });

        b.Entity<AlbumAccess>(e =>
        {
            e.HasKey(x => new { x.AlbumId, x.UserId });
            e.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });
    }
}
```

## `Source/AlbumService.cs`

```csharp
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace nAlbum.Source;

/// <summary>All database operations. Registered as a singleton; one short-lived DbContext per call.</summary>
public class AlbumService
{
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    public const int CodeLength = 10;

    private const int MaxFailedCodes = 5;
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(10);

    private readonly IDbContextFactory<AlbumDb> _factory;
    private readonly Dictionary<long, List<DateTime>> _failedCodes = new();
    private readonly object _lock = new();

    public AlbumService(IDbContextFactory<AlbumDb> factory)
    {
        _factory = factory;
    }

    // ------------------------------------------------------------ codes
    public static string GenerateCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(chars);
    }

    public static bool LooksLikeCode(string s) =>
        s != null && s.Length == CodeLength && s.All(c => CodeAlphabet.Contains(c));

    public bool IsThrottled(long userId)
    {
        lock (_lock)
        {
            if (!_failedCodes.TryGetValue(userId, out var list)) return false;
            list.RemoveAll(t => DateTime.UtcNow - t > FailWindow);
            return list.Count >= MaxFailedCodes;
        }
    }

    public void RegisterFailedAttempt(long userId)
    {
        lock (_lock)
        {
            if (!_failedCodes.TryGetValue(userId, out var list))
            {
                _failedCodes[userId] = list = new List<DateTime>();
            }

            list.Add(DateTime.UtcNow);
        }
    }

    // ----------------------------------------------------------- albums
    public async Task<Album> CreateAlbum(long ownerId, string title)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var album = new Album { OwnerId = ownerId, Title = title };
        db.Albums.Add(album);
        await db.SaveChangesAsync();
        return album;
    }

    public async Task<Album> GetAlbum(long id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Albums.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
    }

    public async Task<bool> CanView(Album album, long userId)
    {
        if (album.OwnerId == userId) return true;
        if (!album.IsOpen) return false;
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Access.AnyAsync(x => x.AlbumId == album.Id && x.UserId == userId);
    }

    public async Task<(List<AlbumListItem> Items, int Total)> ListOwned(long ownerId, int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var query = db.Albums.AsNoTracking().Where(a => a.OwnerId == ownerId);
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.Id)
            .Skip(skip).Take(take)
            .Select(a => new AlbumListItem
            {
                Album = a,
                MediaCount = db.Media.Count(m => m.AlbumId == a.Id)
            })
            .ToListAsync();
        return (items, total);
    }

    public async Task<(List<AlbumListItem> Items, int Total)> ListShared(long userId, int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var query = from x in db.Access.AsNoTracking()
                    join a in db.Albums.AsNoTracking() on x.AlbumId equals a.Id
                    where x.UserId == userId && a.IsOpen
                    select new { Album = a, x.GrantedAt };
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(r => r.GrantedAt).ThenByDescending(r => r.Album.Id)
            .Skip(skip).Take(take)
            .Select(r => new AlbumListItem
            {
                Album = r.Album,
                MediaCount = db.Media.Count(m => m.AlbumId == r.Album.Id)
            })
            .ToListAsync();
        return (items, total);
    }

    public async Task Rename(long albumId, string title)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Albums.Where(a => a.Id == albumId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Title, title));
    }

    public async Task DeleteAlbum(long albumId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Media.Where(m => m.AlbumId == albumId).ExecuteDeleteAsync();
        await db.Access.Where(x => x.AlbumId == albumId).ExecuteDeleteAsync();
        await db.Albums.Where(a => a.Id == albumId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    // -------------------------------------------- open / close / access
    /// <summary>Opens the album and issues a fresh random access code.</summary>
    public async Task<string> OpenAlbum(long albumId)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var code = GenerateCode();
            await using var db = await _factory.CreateDbContextAsync();
            var album = await db.Albums.FirstOrDefaultAsync(a => a.Id == albumId);
            if (album == null) throw new InvalidOperationException("Album not found");
            album.IsOpen = true;
            album.AccessCode = code;
            try
            {
                await db.SaveChangesAsync();
                return code;
            }
            catch (DbUpdateException)
            {
                // unique-index collision on the code (practically impossible) -> try another one
            }
        }

        throw new InvalidOperationException("Could not generate a unique access code");
    }

    /// <summary>Closes the album: the code stops working and every viewer loses access.</summary>
    public async Task CloseAlbum(long albumId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Albums.Where(a => a.Id == albumId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.IsOpen, false)
                .SetProperty(a => a.AccessCode, (string)null));
        await db.Access.Where(x => x.AlbumId == albumId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    /// <summary>Returns the album if the code is valid (and grants access), otherwise null.</summary>
    public async Task<Album> JoinByCode(long userId, string code)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var album = await db.Albums.AsNoTracking().FirstOrDefaultAsync(a => a.AccessCode == code && a.IsOpen);
        if (album == null) return null;

        if (album.OwnerId != userId &&
            !await db.Access.AnyAsync(x => x.AlbumId == album.Id && x.UserId == userId))
        {
            db.Access.Add(new AlbumAccess { AlbumId = album.Id, UserId = userId });
            await db.SaveChangesAsync();
        }

        return album;
    }

    public async Task Leave(long albumId, long userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Access.Where(x => x.AlbumId == albumId && x.UserId == userId).ExecuteDeleteAsync();
    }

    // ------------------------------------------------------------ media
    /// <summary>Returns false when the same media is already in the album.</summary>
    public async Task<bool> AddMedia(long albumId, MediaKind kind, string fileId, string fileUniqueId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (await db.Media.AnyAsync(m => m.AlbumId == albumId && m.FileUniqueId == fileUniqueId))
        {
            return false;
        }

        db.Media.Add(new MediaItem
        {
            AlbumId = albumId,
            Kind = kind,
            FileId = fileId,
            FileUniqueId = fileUniqueId
        });

        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            return false; // lost a race with an identical insert
        }
    }

    public async Task<int> MediaCount(long albumId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Media.CountAsync(m => m.AlbumId == albumId);
    }

    public async Task<List<MediaItem>> ListMedia(long albumId, int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Media.AsNoTracking().Where(m => m.AlbumId == albumId)
            .OrderBy(m => m.Id).Skip(skip).Take(take).ToListAsync();
    }
    
    /// <summary>
    /// Random order that is stable for a given seed, so Next/Prev never repeat items.
    /// (id * seed) mod a prime is a permutation of the ids, i.e. a cheap deterministic shuffle.
    /// </summary>
    public async Task<List<MediaItem>> ListMediaShuffled(long albumId, long seed, int skip, int take)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Media.AsNoTracking().Where(m => m.AlbumId == albumId)
            .OrderBy(m => (m.Id * seed) % 2147483647L).ThenBy(m => m.Id)
            .Skip(skip).Take(take).ToListAsync();
    }

    /// <summary>Resolves 1-based album positions (ordered by id) to media ids. Call BEFORE deleting.</summary>
    public async Task<List<long>> GetMediaIdsAtPositions(long albumId, IEnumerable<int> positions)
    {
        await using var db = _factory.CreateDbContext();
        var ids = new List<long>();
        foreach (var pos in positions)
        {
            var id = await db.Media.AsNoTracking().Where(m => m.AlbumId == albumId)
                .OrderBy(m => m.Id).Skip(pos - 1).Take(1)
                .Select(m => m.Id).FirstOrDefaultAsync();
            if (id != 0) ids.Add(id);
        }

        return ids;
    }

    /// <summary>1-based position of each media item in its album (ordered by Id): the "real" numbers shown as #n.</summary>
    public async Task<Dictionary<long, int>> GetPositions(long albumId, IEnumerable<long> mediaIds)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var result = new Dictionary<long, int>();
        foreach (var id in mediaIds)
        {
            result[id] = await db.Media.CountAsync(m => m.AlbumId == albumId && m.Id <= id);
        }

        return result;
    }

    public async Task DeleteMedia(IEnumerable<long> mediaIds)
    {
        var list = mediaIds.ToList();
        await using var db = _factory.CreateDbContext();
        await db.Media.Where(m => list.Contains(m.Id)).ExecuteDeleteAsync();
    }
}
```

## `Source/AlbumsForm.cs`

```csharp
using System.Net;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotBase.Base;
using TelegramBotBase.Form;
using TelegramBotBase.Sessions;

namespace nAlbum.Source;

/// <summary>
/// The only form of the bot (one instance per user session). All rendering is done explicitly in
/// Load / SentData / Action, so Render() is intentionally empty.
///
/// Callback data:
///   menu | new | mine:&lt;page&gt; | shared:&lt;page&gt; | al:&lt;album&gt;
///   view:&lt;album&gt;:&lt;offset&gt; | vp:&lt;album&gt;:&lt;offset&gt;
///   rnd:&lt;album&gt; | rp:&lt;album&gt;:&lt;seed&gt;:&lt;offset&gt;:&lt;flags&gt;      flags: 1 = numbers, 2 = delete mode (3 = both)
///   add:&lt;album&gt; | done:&lt;album&gt;
///   rmlist:&lt;album&gt;:&lt;offset&gt;
///   toggle:&lt;album&gt; | ren:&lt;album&gt; | del:&lt;album&gt; | delok:&lt;album&gt;
///   leave:&lt;album&gt;
/// Every owner action re-checks ownership on the server side.
/// </summary>
public class AlbumsForm : FormBase
{
    private const int ListPage = 8;    // albums per page in lists
    private const int ViewPage = 10;   // media per page when watching (= one Telegram media group)

    private const string Welcome =
        "👋 <b>Albums bot</b>\n\n" +
        "Create albums of photos and videos and share them with a secret code.\n" +
        "Got a code from a friend? Just send it to me.";

    private enum Mode { Idle, NewTitle, Rename, Adding, Removing }

    private readonly AlbumService _albums;

    private Mode _mode = Mode.Idle;
    private long _albumId;          // album being renamed / filled
    private int _added, _skipped;   // counters of the current "add media" session
    private string _lastGroupId;    // last media-group id we already reacted to
    private readonly List<int> _viewIds = new(); // message ids of the album currently shown (in order)
    private int _viewNavId;                      // id of its navigation message
    private bool _viewEditable;                  // ids are known and line up with the items
    private int _removeOffset;   // first item (0-based) of the page shown in remove mode
    private string _removeNote;  // one-off message shown in the remove navigation text
    private long _removeSeed;   // 0 = ordered remove page; > 0 = delete-by-number inside the random view (the seed)

    public AlbumsForm(AlbumService albums)
    {
        _albums = albums;
    }

    private long UserId => Device.DeviceId; // private chat: chat id == user id
    private bool IsPrivate => !Device.IsGroup && !Device.IsChannel;

    public override Task Render(MessageResult message) => Task.CompletedTask;

    // =============================================================== text
    public override async Task Load(MessageResult message)
    {
        if (message.IsAction || !IsPrivate) return;
        if (message.UpdateData.Type != UpdateType.Message) return; // ignore edits etc.

        if (message.IsBotCommand)
        {
            await OnCommand(message);
            return;
        }

        var text = message.MessageText?.Trim();
        if (string.IsNullOrEmpty(text)) return; // media messages are handled in SentData

        switch (_mode)
        {
            case Mode.NewTitle:
            {
                var title = CleanTitle(text);
                if (title.Length == 0)
                {
                    await Say("Title can't be empty. Try again or /cancel.");
                    return;
                }

                _mode = Mode.Idle;
                var album = await _albums.CreateAlbum(UserId, title);
                var (card, bf) = await BuildCard(album);
                await Say("✅ Album created.\n\n" + card, bf);
                return;
            }

            case Mode.Rename:
            {
                var title = CleanTitle(text);
                if (title.Length == 0)
                {
                    await Say("Title can't be empty. Try again or /cancel.");
                    return;
                }

                _mode = Mode.Idle;
                var album = await _albums.GetAlbum(_albumId);
                if (album == null || album.OwnerId != UserId) return;
                await _albums.Rename(album.Id, title);
                album.Title = title;
                var (card, bf) = await BuildCard(album);
                await Say(card, bf);
                return;
            }

            case Mode.Adding:
                await Say("Only photos and videos can be added. Press Done when finished.");
                return;
            
            case Mode.Removing:
                await OnRemoveNumbers(message, text);
                return;

            default:
                var code = text.ToUpperInvariant();
                if (AlbumService.LooksLikeCode(code))
                {
                    await TryJoin(code);
                }
                else
                {
                    await Say("Send me an access code to open an album, or use the menu.", MenuButtons());
                }

                return;
        }
    }

    private async Task OnCommand(MessageResult m)
    {
        _mode = Mode.Idle;
        var args = m.BotCommandParameters;

        switch (m.BotCommand)
        {
            case "/start" when args.Count > 0: // deep link: t.me/<bot>?start=<code>
                await TryJoin(args[0]);
                break;

            case "/new":
                await StartNew();
                break;

            case "/join":
                if (args.Count == 0) await Say("Usage: /join CODE");
                else await TryJoin(args[0]);
                break;

            case "/cancel":
                await Say("Cancelled.", MenuButtons());
                break;

            default: // /start, /menu and anything unknown
                await Say(Welcome, MenuButtons());
                break;
        }
    }

    private async Task StartNew()
    {
        _mode = Mode.NewTitle;
        await Say("Send the album title (or /cancel).");
    }

    private async Task TryJoin(string raw)
    {
        if (_albums.IsThrottled(UserId))
        {
            await Say("⏳ Too many wrong codes. Please try again in a few minutes.");
            return;
        }

        var code = raw.Trim().ToUpperInvariant();
        var album = AlbumService.LooksLikeCode(code) ? await _albums.JoinByCode(UserId, code) : null;
        if (album == null)
        {
            _albums.RegisterFailedAttempt(UserId);
            await Say("❌ Wrong code, or the album is closed.");
            return;
        }

        var (card, bf) = await BuildCard(album);
        await Say("✅ Access granted.\n\n" + card, bf);
    }

    // ============================================================== media
    public override async Task SentData(DataResult data)
    {
        if (!IsPrivate) return;

        var msg = data.Message;
        MediaKind kind;
        string fileId, uniqueId;

        if (data.Type == MessageType.Photo && msg.Photo is { Length: > 0 })
        {
            var photo = msg.Photo[^1]; // largest size
            (kind, fileId, uniqueId) = (MediaKind.Photo, photo.FileId, photo.FileUniqueId);
        }
        else if (data.Type == MessageType.Video && msg.Video != null)
        {
            (kind, fileId, uniqueId) = (MediaKind.Video, msg.Video.FileId, msg.Video.FileUniqueId);
        }
        else
        {
            if (_mode == Mode.Adding) await Say("Only photos and videos can be added. Press Done when finished.");
            return;
        }

        if (_mode != Mode.Adding)
        {
            if (FirstOfGroup(msg)) await Say("To add media, open one of your albums and press ➕ Add media.", MenuButtons());
            return;
        }

        var album = await _albums.GetAlbum(_albumId);
        if (album == null || album.OwnerId != UserId)
        {
            _mode = Mode.Idle;
            return;
        }

        var added = await _albums.AddMedia(album.Id, kind, fileId, uniqueId);
        if (added) _added++;
        else _skipped++;

        // Telegram applies a reaction on any item of a media group to the first message of the group,
        // so react once per group.
        if (msg.MediaGroupId == null) await React(msg.MessageId, added ? "👍" : "🤔");
        else if (FirstOfGroup(msg)) await React(msg.MessageId, "👍");
    }

    private bool FirstOfGroup(Message msg)
    {
        if (msg.MediaGroupId == null) return true;
        if (msg.MediaGroupId == _lastGroupId) return false;
        _lastGroupId = msg.MediaGroupId;
        return true;
    }

    private async Task React(int messageId, string emoji)
    {
        try
        {
            await Device.Api(a => a.SetMessageReaction(
                Device.DeviceId, messageId, new ReactionType[] { new ReactionTypeEmoji { Emoji = emoji } }));
        }
        catch (ApiRequestException)
        {
            // reactions are a nicety; ignore failures
        }
    }

    // ============================================================ buttons
    public override async Task Action(MessageResult m)
    {
        var data = m.RawData;
        if (string.IsNullOrEmpty(data) || !IsPrivate) return;
        m.Handled = true;

        var p = data.Split(':');
        try
        {
            switch (p[0])
            {
                case "menu":
                    _mode = Mode.Idle;
                    await m.ConfirmAction();
                    await Show(m, Welcome, MenuButtons());
                    break;

                case "new":
                    await m.ConfirmAction();
                    await StartNew();
                    break;

                case "mine":
                case "shared":
                    _mode = Mode.Idle;
                    await m.ConfirmAction();
                    await ShowList(m, p[0], Int(p, 1));
                    break;

                case "al":
                {
                    _mode = Mode.Idle;
                    var album = await ViewableAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction();
                    await ClearView(m.MessageId);
                    var (card, bf) = await BuildCard(album);
                    await Show(m, card, bf);
                    break;
                }

                case "view":
                case "vp":
                    await OnView(m, p);
                    break;
                
                case "rnd":
                case "rp":
                    await OnRandomView(m, p);
                    break;
                
                case "add":
                {
                    var album = await OwnedAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction();
                    _mode = Mode.Adding;
                    _albumId = album.Id;
                    _added = _skipped = 0;
                    var bf = new ButtonForm();
                    bf.AddButtonRow("✅ Done", $"done:{album.Id}");
                    await Say(
                        $"📥 Adding to <b>{H(album.Title)}</b>.\n" +
                        "Send or forward photos and videos — as many as you like. " +
                        "Duplicates are skipped automatically.\nPress <b>Done</b> when finished.", bf);
                    break;
                }

                case "done":
                {
                    var album = await OwnedAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction();
                    await ClearView(m.MessageId);
                    var summary = _mode == Mode.Adding && _albumId == album.Id
                        ? $"✅ Saved: {_added} added" + (_skipped > 0 ? $", {_skipped} duplicates skipped" : "") + ".\n\n"
                        : "";
                    _mode = Mode.Idle;
                    var (card, bf) = await BuildCard(album);
                    await Show(m, summary + card, bf);
                    break;
                }

                case "rmlist":
                    await OnRemoveView(m, p);
                    break;

                case "toggle":
                    await OnToggle(m, p);
                    break;

                case "ren":
                {
                    var album = await OwnedAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction();
                    _mode = Mode.Rename;
                    _albumId = album.Id;
                    await Say("Send the new title (or /cancel).");
                    break;
                }

                case "del":
                {
                    var album = await OwnedAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction();
                    var bf = new ButtonForm();
                    bf.AddButtonRow(new ButtonBase("Yes, delete", $"delok:{album.Id}"), new ButtonBase("Cancel", $"al:{album.Id}"));
                    await Show(m, $"Delete <b>{H(album.Title)}</b> and all its media references? This can't be undone.", bf);
                    break;
                }

                case "delok":
                {
                    var album = await OwnedAlbum(m, p);
                    if (album == null) return;
                    await m.ConfirmAction("Album deleted.");
                    await _albums.DeleteAlbum(album.Id);
                    await ShowList(m, "mine", 0);
                    break;
                }

                case "leave":
                    await m.ConfirmAction();
                    await _albums.Leave(Long(p, 1), UserId);
                    await ShowList(m, "shared", 0);
                    break;

                default:
                    await m.ConfirmAction();
                    break;
            }
        }
        catch (Exception ex) when (ex is not ApiRequestException)
        {
            Console.Error.WriteLine($"Action '{data}' failed: {ex}");
            await Say("⚠️ Something went wrong. Please try again.");
        }
    }

    private async Task OnView(MessageResult m, string[] p)
    {
        var album = await ViewableAlbum(m, p);
        if (album == null) return;

        var offset = Math.Max(Int(p, 2), 0);
        var total = await _albums.MediaCount(album.Id);
        var items = await _albums.ListMedia(album.Id, offset, ViewPage);
        if (items.Count == 0)
        {
            await m.ConfirmAction("This album is empty.", true);
            return;
        }

        await m.ConfirmAction();

        var bf = new ButtonForm();
        var row = new List<ButtonBase>();
        if (offset > 0) row.Add(new ButtonBase("◀ Prev", $"vp:{album.Id}:{Math.Max(offset - ViewPage, 0)}"));
        if (offset + ViewPage < total) row.Add(new ButtonBase("Next ▶", $"vp:{album.Id}:{offset + ViewPage}"));
        if (row.Count > 0) bf.AddButtonRow(row.ToArray());
        bf.AddButtonRow(new ButtonBase("🎲 Random", $"rnd:{album.Id}"), new ButtonBase("⬅ Back", $"al:{album.Id}"));

        await ShowViewPage(m.MessageId, items,
            failed => $"{H(album.Title)}: items {offset + 1}–{offset + items.Count} of {total}" +
                      (failed > 0 ? $"\n⚠️ {failed} item(s) could not be sent." : ""),
            bf);
    }

    private async Task OnRandomView(MessageResult m, string[] p)
    {
        var album = await ViewableAlbum(m, p);
        if (album == null) return;

        var isPage = p[0] == "rp";
        var seed = isPage ? Math.Clamp(Long(p, 2), 1, 2147483646) : Random.Shared.NextInt64(1, 2147483646);
        var offset = isPage ? Math.Max(Int(p, 3), 0) : 0;
        var flags = isPage ? Int(p, 4) : 0;                          // bit 1 = show numbers, bit 2 = delete mode
        if ((flags & 2) != 0 && album.OwnerId != UserId) flags &= 1; // only the owner can delete
        if ((flags & 2) != 0) flags |= 1;                            // delete mode always shows numbers

        if (await _albums.MediaCount(album.Id) == 0)
        {
            await m.ConfirmAction("This album is empty.", true);
            return;
        }

        await m.ConfirmAction();

        if ((flags & 2) != 0)
        {
            _mode = Mode.Removing;
            _albumId = album.Id;
            _removeSeed = seed;
            _removeOffset = offset;
        }
        else if (_mode == Mode.Removing)
        {
            _mode = Mode.Idle;   // left delete mode (Done deleting / Reshuffle)
            _removeSeed = 0;
        }

        await RenderRandomPage(album, seed, offset, flags, m.MessageId, null);
    }

    private async Task RenderRandomPage(Album album, long seed, int offset, int flags, int clickedMessageId, string note)
    {
        var total = await _albums.MediaCount(album.Id);
        if (total == 0)
        {
            await ClearView();
            _mode = Mode.Idle;
            _removeSeed = 0;
            var (card, cardButtons) = await BuildCard(album);
            await Say("✅ The album is now empty.\n\n" + card, cardButtons);
            return;
        }

        if (offset >= total) offset = (total - 1) / ViewPage * ViewPage; // the page vanished after deletions
        if ((flags & 2) != 0) _removeOffset = offset;

        var items = await _albums.ListMediaShuffled(album.Id, seed, offset, ViewPage);

        List<string> captions = null;
        if ((flags & 1) != 0)
        {
            var pos = await _albums.GetPositions(album.Id, items.Select(i => i.Id));
            captions = items.Select(i => $"#{pos[i.Id]}").ToList();
        }

        var (text, nav) = RandomNav(album, seed, offset, total, items.Count, flags, note);
        await ShowViewPage(
            clickedMessageId,
            items,
            failed => text + (failed > 0 ? $"\n⚠️ {failed} item(s) could not be sent." : ""),
            nav,
            captions);
    }

    private (string Text, ButtonForm Buttons) RandomNav(Album album, long seed, int offset, int total, int count,
        int flags, string note)
    {
        var deleting = (flags & 2) != 0;
        var numbers = (flags & 1) != 0;

        var text = $"🎲 <b>{H(album.Title)}</b>: random {offset + 1}–{offset + count} of {total}";
        if (numbers) text += "\n#n = the item's number in the album";
        if (deleting) text += "\n🗑 Type the number(s) to delete, e.g. <code>12</code>, <code>12 15 18</code> or <code>12-15</code>.";
        if (note != null) text = note + "\n\n" + text;

        var bf = new ButtonForm();
        var row = new List<ButtonBase>();
        if (offset > 0)
            row.Add(new ButtonBase("◀ Prev", $"rp:{album.Id}:{seed}:{Math.Max(offset - ViewPage, 0)}:{flags}"));
        if (offset + ViewPage < total)
            row.Add(new ButtonBase("Next ▶", $"rp:{album.Id}:{seed}:{offset + ViewPage}:{flags}"));
        if (row.Count > 0) bf.AddButtonRow(row.ToArray());

        if (deleting)
        {
            bf.AddButtonRow("✅ Done deleting", $"rp:{album.Id}:{seed}:{offset}:1");
        }
        else
        {
            var toggle = numbers
                ? new ButtonBase("🔢 Hide numbers", $"rp:{album.Id}:{seed}:{offset}:{flags & ~1}")
                : new ButtonBase("🔢 Show numbers", $"rp:{album.Id}:{seed}:{offset}:{flags | 1}");
            var buttons = new List<ButtonBase> { toggle };
            if (album.OwnerId == UserId)
                buttons.Add(new ButtonBase("🗑 Delete by number", $"rp:{album.Id}:{seed}:{offset}:3"));
            bf.AddButtonRow(buttons.ToArray());
        }

        bf.AddButtonRow(new ButtonBase("🎲 Reshuffle", $"rnd:{album.Id}"), new ButtonBase("⬅ Back", $"al:{album.Id}"));
        return (text, bf);
    }

    private async Task OnToggle(MessageResult m, string[] p)
    {
        var album = await OwnedAlbum(m, p);
        if (album == null) return;

        string code = null;
        if (album.IsOpen)
        {
            await _albums.CloseAlbum(album.Id);
            await m.ConfirmAction("Album closed. Everyone else lost access.", true);
        }
        else
        {
            code = await _albums.OpenAlbum(album.Id);
            await m.ConfirmAction("Album opened.");
        }

        album = await _albums.GetAlbum(album.Id);
        var (card, bf) = await BuildCard(album);
        await Show(m, card, bf);

        if (code != null)
        {
            await Say(
                $"🔓 <b>{H(album.Title)}</b> is open.\n\n" +
                $"Access code: <code>{code}</code>\n" +
                $"Link: https://t.me/{BotInfo.Username}?start={code}\n\n" +
                "Anyone with the code can watch the album (but not edit it). " +
                "Closing the album invalidates the code and removes all viewers.");
        }
    }

    // ============================================================ screens
    private static ButtonForm MenuButtons()
    {
        var bf = new ButtonForm();
        bf.AddButtonRow("➕ New album", "new");
        bf.AddButtonRow("📁 My albums", "mine:0");
        bf.AddButtonRow("📥 Shared with me", "shared:0");
        return bf;
    }

    private async Task ShowList(MessageResult m, string kind, int page)
    {
        page = Math.Max(page, 0);
        var (items, total) = kind == "mine"
            ? await _albums.ListOwned(UserId, page * ListPage, ListPage)
            : await _albums.ListShared(UserId, page * ListPage, ListPage);

        if (items.Count == 0 && total > 0) // page is out of range after deletions
        {
            page = (total - 1) / ListPage;
            (items, total) = kind == "mine"
                ? await _albums.ListOwned(UserId, page * ListPage, ListPage)
                : await _albums.ListShared(UserId, page * ListPage, ListPage);
        }

        var text = kind == "mine" ? "📁 <b>My albums</b>" : "📥 <b>Shared with me</b>";
        if (total == 0)
        {
            text += "\n\nNothing here yet." + (kind == "shared" ? " Send me an access code to get started." : "");
        }

        var bf = new ButtonForm();
        foreach (var it in items)
        {
            var icon = kind == "mine" ? (it.Album.IsOpen ? "🔓" : "🔒") : "📁";
            bf.AddButtonRow($"{icon} {Truncate(it.Album.Title, 40)} ({it.MediaCount})", $"al:{it.Album.Id}");
        }

        var nav = new List<ButtonBase>();
        if (page > 0) nav.Add(new ButtonBase("◀", $"{kind}:{page - 1}"));
        if ((page + 1) * ListPage < total) nav.Add(new ButtonBase("▶", $"{kind}:{page + 1}"));
        if (nav.Count > 0) bf.AddButtonRow(nav.ToArray());
        if (kind == "mine") bf.AddButtonRow("➕ New album", "new");
        bf.AddButtonRow("⬅ Menu", "menu");

        await Show(m, text, bf);
    }

    private async Task<(string Text, ButtonForm Buttons)> BuildCard(Album album)
    {
        var count = await _albums.MediaCount(album.Id);
        var isOwner = album.OwnerId == UserId;

        var sb = new StringBuilder();
        sb.Append($"📁 <b>{H(album.Title)}</b>\n🖼 Media: {count}");

        var bf = new ButtonForm();
        if (!isOwner)
        {
            bf.AddButtonRow(new ButtonBase("👀 View", $"view:{album.Id}:0"), new ButtonBase("🎲 Random", $"rnd:{album.Id}"));
            bf.AddButtonRow(new ButtonBase("🚪 Leave", $"leave:{album.Id}"), new ButtonBase("⬅ Back", "shared:0"));
            return (sb.ToString(), bf);
        }

        if (album.IsOpen)
        {
            sb.Append($"\n🔓 Open for others\nCode: <code>{album.AccessCode}</code>");
            sb.Append($"\nLink: https://t.me/{BotInfo.Username}?start={album.AccessCode}");
        }
        else
        {
            sb.Append("\n🔒 Closed — only you can see it");
        }

        bf.AddButtonRow(new ButtonBase("👀 View", $"view:{album.Id}:0"), new ButtonBase("🎲 Random", $"rnd:{album.Id}"));
        bf.AddButtonRow(new ButtonBase("➕ Add media", $"add:{album.Id}"), new ButtonBase("🗑 Remove media", $"rmlist:{album.Id}:0"));
        bf.AddButtonRow("✏️ Rename", $"ren:{album.Id}");
        bf.AddButtonRow(album.IsOpen ? "🔒 Close album" : "🔓 Open for others", $"toggle:{album.Id}");
        bf.AddButtonRow("❌ Delete album", $"del:{album.Id}");
        bf.AddButtonRow("⬅ My albums", "mine:0");
        return (sb.ToString(), bf);
    }

    // ============================================================ helpers
    /// <summary>Sends a new HTML message.</summary>
    private Task<Message> Say(string html, ButtonForm bf = null) =>
        Device.Send(html, bf, parseMode: ParseMode.Html);

    /// <summary>Edits the message the button belongs to; falls back to a new message.</summary>
    private async Task Show(MessageResult m, string html, ButtonForm bf)
    {
        if (m.IsAction)
        {
            try
            {
                await Device.Edit(m.MessageId, html, bf, ParseMode.Html);
                return;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
            {
                return;
            }
            catch (ApiRequestException)
            {
                // message too old / not editable -> send a fresh one
            }
        }

        await Say(html, bf);
    }

    private async Task TryDelete(int messageId)
    {
        try
        {
            await Device.DeleteMessage(messageId);
        }
        catch (ApiRequestException)
        {
        }
    }

    private async Task<int> SendOne(MediaItem item, ButtonForm bf, string caption = null)
    {
        var msg = item.Kind == MediaKind.Photo
            ? await Device.SendPhoto(InputFile.FromFileId(item.FileId), caption, buttons: bf)
            : await Device.SendVideo(InputFile.FromFileId(item.FileId), caption, buttons: bf);
        return msg?.MessageId ?? 0;
    }

    private static string CaptionAt(IReadOnlyList<string> captions, int i) =>
        captions != null && i < captions.Count ? captions[i] : null;

    private static InputMedia ToInput(MediaItem i, string caption) =>
        i.Kind == MediaKind.Photo
            ? new InputMediaPhoto(InputFile.FromFileId(i.FileId)) { Caption = caption }
            : new InputMediaVideo(InputFile.FromFileId(i.FileId)) { Caption = caption };

    private async Task<(List<int> Ids, int Failed)> SendBatch(List<MediaItem> items,
        IReadOnlyList<string> captions = null)
    {
        var ids = new List<int>();
        if (items.Count == 0) return (ids, 0);

        if (items.Count == 1)
        {
            try
            {
                var id = await SendOne(items[0], null, CaptionAt(captions, 0));
                if (id != 0) ids.Add(id);
                return (ids, 0);
            }
            catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
            {
                await Console.Error.WriteLineAsync($"Media {items[0].Id} could not be sent: {ex.Message}");
                return (ids, 1);
            }
        }

        var group = items
            .Select((item, idx) => (IAlbumInputMedia)ToInput(item, CaptionAt(captions, idx)))
            .ToList();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var sent = await Device.Dispatch(a => a.SendMediaGroup(Device.DeviceId, group));
                ids.AddRange(sent.Select(s => s.MessageId));
                return (ids, 0);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            {
                var wait = Math.Min(ex.Parameters?.RetryAfter ?? 3, 30) + 1;
                await Console.Error.WriteLineAsync($"Flood limit on group of {items.Count}, resending in {wait}s");
                await Task.Delay(TimeSpan.FromSeconds(wait));
            }
            catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
            {
                await Console.Error.WriteLineAsync(
                    $"Media group of {items.Count} rejected: {ex.Message}. Splitting to isolate the bad item.");
                var mid = items.Count / 2;
                var left = await SendBatch(items.Take(mid).ToList(), captions?.Take(mid).ToList());
                var right = await SendBatch(items.Skip(mid).ToList(), captions?.Skip(mid).ToList());
                left.Ids.AddRange(right.Ids);
                return (left.Ids, left.Failed + right.Failed);
            }
        }

        await Console.Error.WriteLineAsync($"Gave up on group of {items.Count} after repeated flood limits");
        return (ids, items.Count);
    }

    private async Task<bool> TryEditGroup(List<MediaItem> items, IReadOnlyList<string> captions)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (!await EditMedia(_viewIds[i], ToInput(items[i], CaptionAt(captions, i)))) return false;
        }

        return true;
    }

    /// <summary>
    /// Shows one page. clickedMessageId = the message the user clicked (card or navigation message),
    /// or the navigation message id when a typed text triggered the refresh. Edits in place when possible.
    /// </summary>
    private async Task ShowViewPage(int clickedMessageId, List<MediaItem> items, Func<int, string> textFor,
        ButtonForm nav, IReadOnlyList<string> captions = null)
    {
        var clickedNav = _viewNavId != 0 && clickedMessageId == _viewNavId;

        if (clickedNav && _viewEditable && _viewIds.Count == items.Count && await TryEditGroup(items, captions))
        {
            await EditNav(textFor(0), nav);
            return;
        }

        await ClearView();
        if (!clickedNav && clickedMessageId != 0) await TryDelete(clickedMessageId); // the card / a stale nav

        var (ids, failed) = await SendBatch(items, captions);
        _viewIds.AddRange(ids);
        _viewEditable = failed == 0 && ids.Count == items.Count;
        var navMsg = await Say(textFor(failed), nav);
        _viewNavId = navMsg?.MessageId ?? 0;
    }

    /// <summary>Edits the navigation message of the current view (or sends one if there is none).</summary>
    private async Task EditNav(string html, ButtonForm bf)
    {
        if (_viewNavId == 0)
        {
            _viewNavId = (await Say(html, bf))?.MessageId ?? 0;
            return;
        }

        try
        {
            await Device.Edit(_viewNavId, html, bf, ParseMode.Html);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
        {
        }
        catch (ApiRequestException ex)
        {
            Console.Error.WriteLine($"Could not edit navigation message: {ex.Message}");
        }
    }

    private async Task ClearView(int keepMessageId = 0)
    {
        var all = new List<int>(_viewIds);
        if (_viewNavId != 0 && _viewNavId != keepMessageId) all.Add(_viewNavId);
        _viewIds.Clear();
        _viewNavId = 0;
        _viewEditable = false;
        if (all.Count == 0) return;

        try
        {
            await Device.Raw(a => a.DeleteMessages(Device.DeviceId, all));
        }
        catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Could not delete old view: {ex.Message}");
        }
    }

    private async Task<bool> EditMedia(int messageId, InputMedia media)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await Device.Raw(a => a.EditMessageMedia(Device.DeviceId, messageId, media));
                return true;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(ex.Parameters?.RetryAfter ?? 3, 30) + 1));
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("not modified"))
            {
                return true; // same media is already there
            }
            catch (Exception ex) when (ex is RequestException or HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"Edit of message {messageId} failed: {ex.Message}");
                return false; // caller falls back to rebuilding the view
            }
        }

        return false;
    }
    
    private async Task OnRemoveView(MessageResult m, string[] p)
    {
        var album = await OwnedAlbum(m, p);
        if (album == null) return;

        var total = await _albums.MediaCount(album.Id);
        if (total == 0)
        {
            await m.ConfirmAction("Nothing to remove.", true);
            return;
        }

        await m.ConfirmAction();
        _mode = Mode.Removing;
        _albumId = album.Id;
        _removeSeed = 0;   // ordered remove page, not the random one
        _removeOffset = Math.Max(Int(p, 2), 0);
        _removeNote = null;
        await RenderRemovePage(album, m.MessageId);
    }

    private async Task RenderRemovePage(Album album, int clickedMessageId = 0)
    {
        var total = await _albums.MediaCount(album.Id);
        if (total == 0)
        {
            await ClearView();
            _mode = Mode.Idle;
            var (card, cardButtons) = await BuildCard(album);
            await Say("✅ The album is now empty.\n\n" + card, cardButtons);
            return;
        }

        if (_removeOffset >= total) _removeOffset = (total - 1) / ViewPage * ViewPage; // page vanished
        var items = await _albums.ListMedia(album.Id, _removeOffset, ViewPage);
        var (text, bf) = RemoveNav(album, total, _removeOffset, items.Count);
        _removeNote = null;

        await ShowViewPage(
            clickedMessageId == 0 ? _viewNavId : clickedMessageId,
            items,
            failed => text + (failed > 0 ? $"\n⚠️ {failed} item(s) could not be shown." : ""),
            bf,
            captions: items.Select((_, i) => $"#{_removeOffset + 1 + i}").ToList());
    }

    private (string Text, ButtonForm Buttons) RemoveNav(Album album, int total, int offset, int count)
    {
        var text = $"🗑 <b>{H(album.Title)}</b>: items {offset + 1}–{offset + count} of {total}\n" +
                   "Type the number(s) to delete, e.g. <code>12</code>, <code>12 15 18</code> or <code>12-15</code>.";
        if (_removeNote != null) text = _removeNote + "\n\n" + text;

        var bf = new ButtonForm();
        var row = new List<ButtonBase>();
        if (offset > 0) row.Add(new ButtonBase("◀ Prev", $"rmlist:{album.Id}:{Math.Max(offset - ViewPage, 0)}"));
        if (offset + ViewPage < total) row.Add(new ButtonBase("Next ▶", $"rmlist:{album.Id}:{offset + ViewPage}"));
        if (row.Count > 0) bf.AddButtonRow(row.ToArray());
        bf.AddButtonRow("✅ Done", $"done:{album.Id}");
        return (text, bf);
    }

    private async Task OnRemoveNumbers(MessageResult message, string text)
    {
        var album = await _albums.GetAlbum(_albumId);
        if (album == null || album.OwnerId != UserId)
        {
            _mode = Mode.Idle;
            return;
        }

        await TryDelete(message.MessageId); // keep the chat tidy: remove the typed message

        var positions = ParsePositions(text);
        var total = await _albums.MediaCount(album.Id);
        var deleted = false;

        if (positions == null || positions.Count == 0)
        {
            _removeNote = "⚠️ I couldn't read that.";
        }
        else
        {
            var valid = positions.Where(n => n >= 1 && n <= total).ToList();
            var ids = await _albums.GetMediaIdsAtPositions(album.Id, valid); // resolve before deleting
            if (ids.Count == 0)
            {
                _removeNote = "⚠️ No items with those numbers.";
            }
            else
            {
                await _albums.DeleteMedia(ids);
                _removeNote = $"✅ Deleted {ids.Count} item(s)" +
                              (valid.Count < positions.Count ? " (some numbers were out of range)." : ".");
                deleted = true;
            }
        }

        if (_removeSeed != 0) // delete-by-number inside the random view
        {
            var note = _removeNote;
            _removeNote = null;
            if (deleted)
            {
                await RenderRandomPage(album, _removeSeed, _removeOffset, 3, _viewNavId, note);
            }
            else
            {
                var shown = Math.Min(ViewPage, Math.Max(total - _removeOffset, 0));
                var (rndNavText, navButtons) = RandomNav(album, _removeSeed, _removeOffset, total, shown, 3, note);
                await EditNav(rndNavText, navButtons);
            }

            return;
        }

        if (deleted)
        {
            await RenderRemovePage(album); // refreshes the album in place
            return;
        }

        // nothing deleted: only update the navigation text
        var count = Math.Min(ViewPage, Math.Max(total - _removeOffset, 0));
        var (navText, bf) = RemoveNav(album, total, _removeOffset, count);
        _removeNote = null;
        await EditNav(navText, bf);
    }

    /// <summary>"12", "12 15 18", "12-15", "3, 7-9" -> set of numbers; null if unreadable.</summary>
    private static SortedSet<int> ParsePositions(string text)
    {
        var set = new SortedSet<int>();
        foreach (var token in text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('-');
            if (parts.Length == 1 && int.TryParse(parts[0], out var one))
            {
                set.Add(one);
            }
            else if (parts.Length == 2 && int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi)
                     && lo <= hi && hi - lo < 100)
            {
                for (var n = lo; n <= hi; n++) set.Add(n);
            }
            else
            {
                return null;
            }

            if (set.Count > 100) return null;
        }

        return set;
    }

    private async Task<Album> OwnedAlbum(MessageResult m, string[] p)
    {
        var album = await _albums.GetAlbum(Long(p, 1));
        if (album != null && album.OwnerId == UserId) return album;
        await m.ConfirmAction("Album not found or you are not its owner.", true);
        return null;
    }

    private async Task<Album> ViewableAlbum(MessageResult m, string[] p)
    {
        var album = await _albums.GetAlbum(Long(p, 1));
        if (album != null && await _albums.CanView(album, UserId)) return album;
        await m.ConfirmAction("This album is unavailable.", true);
        return null;
    }

    private static string H(string s) => WebUtility.HtmlEncode(s ?? "");

    private static string CleanTitle(string s) =>
        Truncate(string.Join(' ', s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)), 100);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static int Int(string[] p, int i) => p.Length > i && int.TryParse(p[i], out var v) ? v : 0;

    private static long Long(string[] p, int i) => p.Length > i && long.TryParse(p[i], out var v) ? v : 0;
}
```

## `Dockerfile`

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nAlbum.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
RUN useradd --create-home --uid 10001 bot && mkdir /data && chown bot /data
COPY --from=build /app .
USER bot
ENV DB_PATH=/data/albums.db
VOLUME /data
ENTRYPOINT ["dotnet", "nAlbum.dll"]
```

## `docker-compose.yml`

```yaml
services:
  bot:
    build: .
    restart: unless-stopped
    env_file:
      - .env
    environment:
      DB_PATH: /data/albums.db
    volumes:
      - bot_data:/data

volumes:
  bot_data:
```

## `.dockerignore`

```text
bin/
obj/
.env
.git
*.db*
```
