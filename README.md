# nAlbum

Telegram bot for creating photo and video albums and sharing them with an access code. Media numbers stay permanent when items are deleted.

## Run

Set `BOT_TOKEN` in `.env`, then start with `docker compose up --build`. Compose stores the SQLite database in the persistent `bot_data` volume.

Set `ADMIN_IDS` in `.env` to comma-separated Telegram user IDs to enable the private `/stats` command and detailed-statistics menu button. Public album/view/page totals are shown to everyone.

For local development, set `BOT_TOKEN` and optionally `DB_PATH` (defaults to `/data/albums.db`), then run `dotnet run`.
