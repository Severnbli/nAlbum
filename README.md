# nAlbum

nAlbum is a Telegram bot for creating photo and video albums and sharing them with an access code. Media numbers are permanent: deleting an item does not renumber the others.

## Features

- Create, rename, and delete albums.
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
| `/cancel` | Cancel the current action |
| `/stats` | Show detailed statistics (admins only) |

You can also send an access code directly to the bot while no other action is in progress.

## Configuration

| Variable | Required | Description |
| --- | --- | --- |
| `BOT_TOKEN` | Yes | Telegram bot token |
| `DB_PATH` | No | SQLite database path; defaults to `/data/albums.db` |
| `ADMIN_IDS` | No | Comma-separated Telegram user IDs allowed to view detailed statistics |

Do not commit `.env`; it contains the bot token. `.env.example` is a safe template.

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
