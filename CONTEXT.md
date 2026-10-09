# Agent context

Use this file as a short orientation and a list of invariants, not as a substitute
for the implementation. **When details matter, inspect the current source and
README first**; update this file when a change makes its guidance inaccurate.

## Project at a glance

nAlbum is a single-project .NET 10 Telegram bot for private photo/video albums.
It stores Telegram file references and album data in SQLite; it does not download
or host media. The bot runs as one process and has no web server or job queue.

## Source map

```text
Program.cs                         composition root, database startup, bot startup
Source/Config/AppConfig.cs         BOT_TOKEN, DB_PATH, ADMIN_IDS, LOCALES_DIR
Source/Localization/               LocalizationService: loads Locales/*.json, T/F lookups
Locales/                           one JSON file per language (en, ru, be)
Source/Data/Entities.cs            EF entities and domain data
Source/Data/AlbumDb.cs             EF model, keys, indexes, relationships
Source/Data/SchemaUpgrader.cs       SQLite user_version upgrades and backups
Source/Services/AlbumService.cs    albums, access codes, grants, ownership data
Source/Services/MediaService.cs    adding, listing, numbering, deleting media
Source/Services/CodeThrottle.cs    in-memory invalid-code throttle
Source/Services/StatsService.cs    usage tracking, aggregates, lifetime counters
Source/Services/UserPreferenceService.cs  saved per-user language choice
Source/Bot/AlbumsForm.cs            sole Telegram form and update router
Source/Bot/BotContext.cs            per-form dependencies and UI helpers
Source/Bot/UserSession.cs           transient per-user conversation/view state
Source/Bot/Screens/                 menu, album, join, media, view, removal, stats flows
Source/Bot/Ui/                      Telegram rendering, album cards, media views, text helpers
README.md                           user-facing setup and feature documentation
Dockerfile, docker-compose.yml      single-container runtime and persistent SQLite volume
```

## Architecture and request flow

```text
Program -> AlbumsForm -> Screens -> BotContext -> services / UI helpers
Services -> IDbContextFactory<AlbumDb> -> SQLite
```

- `AlbumsForm` is the only `FormBase`; the router dispatches text, commands,
  media, and callbacks to screens. Duplicate command, mode, or callback
  registrations intentionally fail during construction.
- Keep screens independent of each other. Share behavior through `BotContext`,
  a UI helper, or a service.
- Services are singletons. Create a short-lived context from
  `IDbContextFactory<AlbumDb>` per database operation; do not inject a
  `AlbumDb` into a singleton.
- `AlbumsForm.Render()` is intentionally empty. Keep update handling in
  `PreLoad`, `Load`, `SentData`, and `Action`.
- `BotContext.Device` is assigned by the framework after form construction;
  do not access it in constructors. Group and channel updates are ignored.
  In private chats `UserId` is `Device.DeviceId`.
- Commands reset the current conversation mode before dispatch. Plain-text
  access codes are accepted only while idle; `/start CODE` is a deep link.
- `.NoSerialization()` makes `UserSession` state transient. A restart loses
  in-progress flows and view message tracking, but not persisted albums.

## Invariants to preserve

### Authorization and sharing

- Callback payloads are untrusted input. Keep server-side access checks on every
  path: use `BotUi.OwnedAlbum` for mutations and `BotUi.ViewableAlbum` (or an
  equivalent `CanView` check) for album reads.
- Owners can view their albums even when closed. Viewers cannot mutate albums.
- Closing an album clears its access code and all viewer grants. Reopening
  issues a fresh code; do not preserve a prior code across close/reopen.
- Joining by code grants access to non-owners. Leaving removes only the
  current user's grant.
- Access codes are 10 characters from
  `ABCDEFGHJKLMNPQRSTUVWXYZ23456789` (excludes easily confused characters).
  Normalize user input at the call site as appropriate; `LooksLikeCode` itself
  is case-sensitive.

### Media and numbering

- Persist only Telegram `FileId` and `FileUniqueId`; never add downloaded media
  files without an explicit product requirement.
- A `FileUniqueId` can occur only once per album. Preserve the unique index and
  duplicate handling.
- `Album.NextNumber` allocates permanent `MediaItem.Number` values. Numbers
  never decrease or get reused; gaps after deletion are expected. Display and
  pagination order is by media `Id`, not by number.
- Route additions through `MediaService.AddMedia`; its process-local semaphore
  protects allocation under concurrent media-group updates. Multiple bot
  processes sharing one database are unsupported.
- Media sent in `Mode.Adding` is only pending (`AddState.Pending`, keyed by message id) until the user presses Done; nothing is written before that. `AlbumsForm.Edited` updates or drops pending items when their message is edited. Telegram sends bots no event for user deletions, so `AddMediaScreen.Commit` probes each pending message on Done by forwarding it to the chat and deleting the forward (400 = gone, skipped; other errors keep the item). Leaving the flow without Done discards pending items.
- Album deletion removes its database rows, not Telegram-hosted media.

### Database and schema

- There are no EF migrations. `SchemaUpgrader` uses SQLite `PRAGMA user_version`;
  a new database is created from the current EF model, while existing databases
  take explicit SQL upgrade steps.
- **Every model change requires both fresh-database and upgrade-path support.**
  Bump `SchemaUpgrader.LatestVersion`, add the matching upgrade step, and keep
  the resulting schema (columns, defaults, indexes, keys, and foreign keys)
  aligned with `AlbumDb`.
- Upgrades make a `VACUUM INTO` backup beside the database. Do not remove a
  backup until the upgraded database has been confirmed healthy.
- SQLite allows multiple `NULL` values in the unique `Album.AccessCode` index.
- `AlbumAccess` and `AlbumViewStat` use composite keys; `Counter` is keyed by
  `Name`; `UserPreference` by `UserId` (schema version 3). Keep raw SQL in `StatsService` synchronized with EF table/column names.
- Album-dependent media, access, and view-stat rows must be removed on album
  deletion. Preserve transaction boundaries and cascade behavior.

### Localization

- All user-visible bot text goes through `Ctx.T(text)` / `Ctx.F(format, args)`.
  The English text is the lookup key and the fallback; never concatenate
  translatable sentences, use `{0}` placeholders instead.
- Languages are `Locales/<code>.json` files (`name`, optional `flag`,
  `translations`), loaded once at startup by `LocalizationService`; the
  language menu is built from the loaded files, so adding a language must not
  require code changes. Keep `en.json` complete when adding or changing a key,
  and keep placeholders and HTML tags identical in every translation.
- `BotContext.Language` is resolved per update in `AlbumsForm.PreLoad`: saved
  preference (`UserPreference`, null = automatic) if the language still exists,
  otherwise detected from the Telegram language code, otherwise `en`.
- Bot command descriptions in `Program.cs` are not localized.

### Telegram UI, concurrency, and privacy

- Preserve HTML escaping with `Text.H` for user-provided text rendered with
  `ParseMode.Html`.
- `MediaView` owns view message tracking and pagination. When leaving or
  replacing a view, keep `UserSession.View` state consistent and clear the
  page key when the view is cleared.
- Ordered/random view opens and newly displayed pages are counted separately;
  toggles and refreshes are not new pages. Per-album statistics are deleted with
  their album; lifetime counters survive deletion.
- Detailed statistics are admin-only. Keep authorization on both command and
  callback routes; public statistics must not expose user IDs or usernames.
- Preserve bounded handling of Telegram send/edit failures and flood waits. Do
  not add unbounded retries or silently treat unexpected errors as success.
- The failed-code throttle is in-memory (5 failures per 10 minutes per user),
  so it resets on restart and is not shared across processes.

## Configuration and running

| Variable | Behavior |
| --- | --- |
| `BOT_TOKEN` | Required; startup fails if unset. |
| `DB_PATH` | Optional; defaults to `/data/albums.db`. Parent directory is created. |
| `ADMIN_IDS` | Optional comma-, semicolon-, or space-separated Telegram user IDs. |
| `LOCALES_DIR` | Optional; defaults to `Locales` beside the executable. |

Do not commit `.env`; Compose loads it and it may contain the bot token. The
Compose service mounts the persistent `bot_data` volume at `/data`. The image
runs as uid `10001`, which must be able to write to that directory.

```text
dotnet run                         local run (requires BOT_TOKEN)
dotnet build                       compile
docker compose up --build          build and run with Compose (requires .env)
```

There is currently no automated test project. Use the narrowest relevant
validation available; `dotnet build` is the repository's general compile check.

## Agent change workflow

1. Read the implementation and nearby call sites before changing behavior; this
   document can lag behind code.
2. Follow existing naming, async, dependency-injection, and error-handling
   patterns. Keep changes focused and do not add dependencies without need.
3. For UI behavior, trace the complete path through router, screen, helper, and
   service; retain authorization checks on callbacks.
4. For data changes, update the EF model and versioned upgrade path together.
   Consider fresh and existing databases, data preservation, constraints, and
   backup behavior.
5. Update `README.md` for user-facing behavior or setup changes. Update this
   context only for lasting architecture or invariant changes.
6. Run the applicable existing build or test command and report any limitation.
