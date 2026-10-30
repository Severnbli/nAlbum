# nAlbum

Telegram bot (C# / .NET 10, TelegramBotBase, EF Core + SQLite) for sharing photo and video albums via invite links. AGPL-3.0; keep the license header on new `.cs` files (copy it from any existing one).

## Commands

- Build: `dotnet build` (must end with 0 errors; there is no test project)
- Run: `BOT_TOKEN=... dotnet run` (optional `DB_PATH`, `ADMIN_IDS`; see `.env.example`)
- Docker: `docker compose up --build -d`

## Workflow

- Work on the `dev` branch; `main` is the release branch merged via PRs.
- One commit per issue or logical change, with `Closes #N` in the body. Issues are tracked on GitHub (`gh issue list`); labels `fix` / `feature`.
- Commit only when asked. Never force-push.

## Layout

- `Program.cs` – DI, schema upgrade, bot setup. **Every user command must also be registered in `CustomCommands` here** (Telegram's command menu).
- `Source/Bot/AlbumsForm.cs` – thin router, one instance per user. Dispatches callbacks (by prefix before the first `:`), typed text (by `Mode`) and commands to a `Screen`.
- `Source/Bot/Screens/*` – one class per feature area, deriving from `Screen` (`Callbacks`, `TextModes`, `Commands`). Duplicate prefixes/modes/commands throw at startup.
- `Source/Bot/Ui/*` – shared UI helpers (`BotUi`, `MediaView`, `AlbumCard`, `Text`, `InviteInput`).
- `Source/Bot/UserSession.cs` + `SessionSnapshot.cs` – per-user state, saved to JSON on shutdown.
- `Source/Services/*` – database access; `Source/Data/*` – entities, `AlbumDb`, `SchemaUpgrader`.
- `Source/Localization/LocKey.cs` + `Locales/{en,ru,by}.json` – all user-visible text.

## Rules

- **Texts**: never hardcode user-visible strings. Add a `const` to `LocKey` and the key to all three locale files (English falls back, but translate ru and by too). Messages are HTML: escape user data with `Text.H`. Keep texts short and plain.
- **Callback data** is `prefix:arg:arg`, max 64 bytes. Add a new prefix to the screen's `Callbacks`, and to `SongScreen.AlbumCallbacks` if it stays inside an album (otherwise the album song is removed).
- **Authorization** is re-checked server-side in every callback and text handler (`ViewableAlbum`, `ManagedAlbum`, `OwnedAlbum`, `CanManage`). Never trust callback data.
- **SQL**: use EF LINQ or `ExecuteSqlInterpolatedAsync` / `SqlQuery` with interpolated parameters. Never concatenate or `string.Format` values into SQL. PRAGMAs go through `SchemaUpgrader.SetVersion` (int only). Strip control characters from stored user text (`Text.CleanTitle`, `Text.StripControl`).
- **Enums stored in sessions** (`Mode`) have explicit numbers: never renumber, leave gaps.
- **Schema changes**: add `UpgradeToN` in `SchemaUpgrader` and bump `LatestVersion`; no EF migrations. A backup is taken automatically.
- Handlers catch Telegram API errors (`RequestException`, `ApiRequestException`) instead of crashing the session; use `Ctx.Ui.TryDelete` / `EditNav` helpers.
- Match surrounding style: comment density, naming, `Ctx.T` / `Ctx.F` for texts.
