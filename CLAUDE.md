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

- **Minimum button width**: Telegram shrinks inline buttons to the width of a short message. `BotUi.SayNav` / `EditText` append `Text.MinWidth` (invisible Braille blanks) to every message that has buttons, so send menus, cards and views through `Ctx.Ui` (`Say`, `Show`, `ShowMenu`, `EditNav`). If you call `SendMessage` / `EditMessageText` directly with an inline keyboard, wrap the text in `Text.WithMinWidth(html, buttons)`. Never add the filler by hand elsewhere.
- **Texts**: never hardcode user-visible strings. Add a `const` to `LocKey` and the key to all three locale files (English falls back, but translate ru and by too). Messages are HTML: escape user data with `Text.H`. Keep texts short and plain.
- **Track message ids**: every *transient* bot message (menu, prompt, card, album view, song, preview) has its id in the session (`MenuMessageId`, `View.Ids` / `View.NavId`, `SongMessageId`, `Add.PromptMessageId`, `Removal.PromptMsgId`, ...). Before sending a new one of such a kind, delete or edit the old one (`Ctx.Ui.Say` / `ShowMenu` / `EditNav` do this for the menu); never leave two live copies (two menus, two prompts, a card under a stale nav). Clear the id when the message is deleted. Any new tracked id must also be added to `SessionSnapshot` (`From` and `ApplyTo`), including the ids of album media (`View.Ids`), or it is orphaned after a restart.
- **Durable messages persist**: messages that are a record for the user (support conversation messages and notices, ban / data deletion notices, "added to album" notices, broadcasts) are sent with `SendMessage` / `Ctx.Ui.Notify` and are never deleted or replaced by the bot, so the chat keeps the history. Do not track them as the menu. Their buttons may go stale, so every handler must answer a closed or missing target politely. Use `Ctx.Ui.Notify` for plain notices to other users.
- **Telegram calls**: never use `Device.Api` or `Device.Raw`. Use the `Device` helpers (`DeleteMessage`, ...); when `Device` has no method for what you need, call the API through `Ctx.Device.Dispatch(a => a.Method(...))`, which also handles rate limits.
- **Callback data** is `prefix:arg:arg`, max 64 bytes. Add a new prefix to the screen's `Callbacks`, and to `SongScreen.AlbumCallbacks` if it stays inside an album (otherwise the album song is removed).
- **Bans and support**: `BotContext.Banned` is set in `PreLoad`; banned users get only a notice. Support conversations live in `SupportService` / `SupportScreen`; bot messages about a conversation are stored as `ConversationNotice` so a Telegram reply to them continues it. Admin user tools are in `AdminUsersScreen`; `UserAdminService.DeleteUserDataAsync` must be extended when a new table stores per-user data.
- **Authorization** is re-checked server-side in every callback and text handler (`ViewableAlbum`, `ManagedAlbum`, `OwnedAlbum`, `CanManage`). Never trust callback data.
- **SQL injection**: use EF LINQ or `ExecuteSqlInterpolatedAsync` / `SqlQuery` with interpolated parameters (EF binds `{value}` as a parameter). Never concatenate, `string.Format` or `$""`-build values into SQL passed to `ExecuteSqlRawAsync` / `FromSqlRaw`; raw SQL is allowed only for static strings in `SchemaUpgrader`. This covers everything user-controlled: callback data, typed text, names, file ids, support messages. Review every new query for this before committing (`grep -rn "SqlRaw" Source`). PRAGMAs go through `SchemaUpgrader.SetVersion` (int only). Strip control characters from stored user text (`Text.CleanTitle`, `Text.StripControl`).
- **Enums stored in sessions** (`Mode`) have explicit numbers: never renumber, leave gaps.
- **Schema changes**: add `UpgradeToN` in `SchemaUpgrader` and bump `LatestVersion`; no EF migrations. A backup is taken automatically.
- Handlers catch Telegram API errors (`RequestException`, `ApiRequestException`) instead of crashing the session; use `Ctx.Ui.TryDelete` / `EditNav` helpers.
- **Dashes**: use the en dash `–` instead of the em dash `—` in texts, comments and docs, except where a format or quoted data requires `—`.
- Match surrounding style: comment density, naming, `Ctx.T` / `Ctx.F` for texts.
