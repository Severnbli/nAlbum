# nAlbum

nAlbum is a .NET 10 Telegram bot for sharing photo and video albums. Copy the AGPL-3.0 license header from an existing file when adding a C# file. Use the en dash (–) in project text, comments, and docs.

## Workflow

- Work on `dev`. Release PRs go from `dev` to `main`. Never force-push.
- Commit only when asked. Make one commit per issue or logical change. Include `Closes #N` in the body when the change closes a GitHub issue. Use `gh issue list` to find tracked issues. The issue labels are `fix` and `feature`.
- Run `dotnet build` from the repository root with the .NET 10 SDK. The repository has no automated test project.

## Project guidance

- Read [README.md](README.md) for setup, configuration, Docker, and language support. Running the bot requires `BOT_TOKEN`.
- Read [the development guide](docs/development.md) before editing application code or locales. It covers screen routing, authorization, message lifecycle, localization, session state, and schema upgrades.
- Use `add-screen-text` when adding UI text and `check-locales` when changing locale keys. The skills live under `.agents/skills/`.
- Use the development guide's review checklist before committing a feature or fix. It also describes how to complete Russian and Belarusian translations from English. The `reviewer` and `translator` roles are available for delegated work.
