<img width="400" height="275" alt="logo" src="https://github.com/user-attachments/assets/08d268eb-681c-443a-aa94-fc09eda3442c" />

# nAlbum

nAlbum is a Telegram bot for creating photo and video albums and sharing them with invite links. Media numbers are permanent: deleting an item does not renumber the others. Try it [here](https://t.me/nAlbum_bot?start=TUMH975GM4).

## Features

- Create, rename, and delete albums.
- Use the bot in English, Russian, or Belarusian, with automatic Telegram-language detection or a saved manual choice.
- Add photos and videos.
- View albums in order or shuffled, with optional permanent-number captions.
- Invite people with links that can expire and have a limited number of uses, or by forwarding a message from them.
- Share management through roles: members view, managers manage, the owner rules everyone.
- Set an album song that plays alongside the album without being interrupted by menu changes.
- Remove items by number with a preview, or directly from a view.
- See public album, view, and watched-page totals. Configured admins get an admin panel with detailed statistics, album removal, and broadcasts.
- Read the privacy policy in the bot (`/privacy`).

## Roles

| Role | Can |
| --- | --- |
| Member | View the album and leave it |
| Manager | Everything a member can, plus add and remove media, rename the album, set the album song, create and delete invite links, invite by forwarded message, and remove members |
| Owner | Everything a manager can, plus remove managers, promote members to managers and demote them, delete the album, and transfer ownership to another member |

Managers cannot act on other managers or the owner. Management actions are in the album's **⚙️ Manage** submenu; the album card shows your role. After transferring ownership, the previous owner becomes a manager.

Invite links are created in **🔗 Invite links** with an expiry (1 hour to 30 days, or none) and a use limit (1 to 50, or none). Anyone who opens a valid link becomes a member. Inviting by forward works only if the person's privacy settings allow linking forwarded messages to their account.

## Get started

### Requirements

- Docker with the Compose plugin, or .NET 10 SDK for local development.
- A Telegram bot token from [@BotFather](https://t.me/BotFather).

### Run with Docker Compose

1. Copy `.env.example` to `.env`.
2. Set `BOT_TOKEN` in `.env`. Optionally set `ADMIN_IDS` to comma-separated Telegram user IDs for admin statistics.
3. Start the bot using the published image:

   ```sh
   docker compose up --no-build -d
   ```
   
   To build and run the image from the local source instead, use:

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
| `/albums` | Show all your albums: owned and shared with you |
| `/new` | Create an album |
| `/join CODE` | Join an album using an invite code |
| `/language` | Choose a language or Telegram-language detection |
| `/help` | Show help |
| `/privacy` | Show the privacy policy |
| `/cancel` | Cancel the current action |
| `/admin` | Open the admin panel (admins only) |
| `/stats` | Show detailed statistics (admins only) |

You can also send an invite code directly to the bot while no other action is in progress.
The bot detects your language from Telegram by default (English, Russian, and Belarusian are included). Use `/language` or the **Language** menu button to choose a language or return to automatic detection; your choice is saved for future chats.

## Configuration

| Variable | Required | Description |
| --- | --- | --- |
| `BOT_TOKEN` | Yes | Telegram bot token |
| `DB_PATH` | No | SQLite database path; defaults to `/data/albums.db` |
| `ADMIN_IDS` | No | Comma-separated Telegram user IDs allowed to use the admin panel (detailed statistics, album removal, broadcasts) |
| `LOCALES_DIR` | No | Directory with language files; defaults to `Locales` next to the application |

Do not commit `.env`; it contains the bot token. `.env.example` is a safe template.

## Adding a language

Each language is one JSON file in `Locales/`; no code changes are needed.

1. Copy `Locales/en.json` to `Locales/<code>.json`, where `<code>` is the Telegram language code in lowercase (for example `de`, `pl`, `pt-br`).
2. Set `name` (shown in the language menu) and optionally `flag`.
3. Translate the values in `translations`. Keep the keys (PascalCase names such as `MenuNewAlbum`) unchanged, keep placeholders such as `{0}` and HTML tags such as `<b>`, and leave a value empty or remove the entry to fall back to English. Startup logs list keys that are not translated yet.
4. Restart the bot. The language appears in `/language` and is detected automatically for users whose Telegram language matches.

Files are copied to the build output, so rebuild after adding one. To add languages without rebuilding, point `LOCALES_DIR` at a mounted directory (for Docker, add a volume and set `LOCALES_DIR` in `docker-compose.yml`). Invalid files are skipped with a message in the log.

## Statistics and data

Public totals show the current number of albums and lifetime album views and watched pages. Album cards show that album's views and pages to everyone who can access it. Detailed user, album, media, and invite statistics are restricted to `ADMIN_IDS`; statistics never show user IDs or usernames.

The bot stores Telegram media and song file references, not downloaded media, plus each user's Telegram display name so album managers can recognize members. The in-bot privacy policy (`PrivacyPolicy` in `Locales/*.json`) describes this; if you run your own instance, review it and add your contact details. You can also register a privacy policy link with [@BotFather](https://t.me/BotFather).

Upgrading to schema version 4 replaces open/closed albums with roles: the access code of each open album becomes an invite link without expiry or use limit, and existing viewers become members. Schema upgrades use SQLite `user_version` and create a database backup beside the database file before upgrading. In Docker, backups are retained in `bot_data` as `albums.db.bak-v*`; remove them only after confirming the upgrade succeeded.

## Development

Build the project with:

```sh
dotnet build
```

There is currently no automated test project. The application targets .NET 10 and uses TelegramBotBase, EF Core, and SQLite.

## License

This project is distributed under the [GNU AGPL v3.0 license](LICENSE).
