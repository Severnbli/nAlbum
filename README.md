# nAlbum

nAlbum is a Telegram bot for creating photo and video albums and sharing them with an access code. Media numbers are permanent: deleting an item does not renumber the others.

## Features

- Create, rename, and delete albums.
- Use the bot in English, Russian, or Belarusian, with automatic Telegram-language detection or a saved manual choice.
- Add photos and videos; duplicate media in an album is skipped.
- View albums in order or shuffled, with optional permanent-number captions.
- Share albums with a code or private link; revoke access by closing the album.
- Remove items by number with a preview, or directly from a view.
- See public album, view, and watched-page totals. Configured admins can access detailed statistics.

## Get started

### Requirements

- Docker with the Compose plugin, or .NET 10 SDK for local development.
- A Telegram bot token from [@BotFather](https://t.me/BotFather).

### Run with Docker Compose

1. Copy `.env.example` to `.env`.
2. Set `BOT_TOKEN` in `.env`. Optionally set `ADMIN_IDS` to comma-separated Telegram user IDs for admin statistics.
3. Start the bot:

   ```sh
   docker compose up --build -d
   ```

The SQLite database is stored in the persistent `bot_data` volume. To stop the bot, run `docker compose down`; the volume is retained.

### Run locally

Set `BOT_TOKEN` and optionally `DB_PATH`, then run:

```sh
dotnet run
```

`DB_PATH` defaults to `/data/albums.db`. The parent directory is created when the bot starts.

## Bot commands

| Command | Description |
| --- | --- |
| `/start`, `/menu` | Open the main menu |
| `/new` | Create an album |
| `/join CODE` | Join an album using its access code |
| `/language` | Choose a language or Telegram-language detection |
| `/cancel` | Cancel the current action |
| `/stats` | Show detailed statistics (admins only) |

You can also send an access code directly to the bot while no other action is in progress.
The bot detects your language from Telegram by default (English, Russian, and Belarusian are included). Use `/language` or the **Language** menu button to choose a language or return to automatic detection; your choice is saved for future chats.

## Configuration

| Variable | Required | Description |
| --- | --- | --- |
| `BOT_TOKEN` | Yes | Telegram bot token |
| `DB_PATH` | No | SQLite database path; defaults to `/data/albums.db` |
| `ADMIN_IDS` | No | Comma-separated Telegram user IDs allowed to view detailed statistics |
| `LOCALES_DIR` | No | Directory with language files; defaults to `Locales` next to the application |

Do not commit `.env`; it contains the bot token. `.env.example` is a safe template.

## Adding a language

Each language is one JSON file in `Locales/`; no code changes are needed.

1. Copy `Locales/en.json` to `Locales/<code>.json`, where `<code>` is the Telegram language code in lowercase (for example `de`, `pl`, `pt-br`).
2. Set `name` (shown in the language menu) and optionally `flag`.
3. Translate the values in `translations`. Keep the keys unchanged, keep placeholders such as `{0}` and HTML tags such as `<b>`, and leave a value empty or remove the entry to fall back to English.
4. Restart the bot. The language appears in `/language` and is detected automatically for users whose Telegram language matches.

Files are copied to the build output, so rebuild after adding one. To add languages without rebuilding, point `LOCALES_DIR` at a mounted directory (for Docker, add a volume and set `LOCALES_DIR` in `docker-compose.yml`). Invalid files are skipped with a message in the log.

## Statistics and data

Public totals show the current number of albums and lifetime album views and watched pages. Album cards show that album's views and pages to everyone who can access it. Detailed user, album, media, and code statistics are restricted to `ADMIN_IDS`; statistics never show user IDs or usernames.

The bot stores Telegram media file references, not downloaded media. Schema upgrades use SQLite `user_version` and create a database backup beside the database file before upgrading. In Docker, backups are retained in `bot_data` as `albums.db.bak-v*`; remove them only after confirming the upgrade succeeded.

## Development

Build the project with:

```sh
dotnet build
```

There is currently no automated test project. The application targets .NET 10 and uses TelegramBotBase, EF Core, and SQLite.

## License

This project is distributed under the [MIT License](LICENSE).
