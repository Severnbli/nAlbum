---
name: add-screen-text
description: Add a new user-visible text or button label to the bot (LocKey constant plus en/ru/by entries). Use whenever a UI string is needed.
---

1. Add `public const string Name = nameof(Name);` to `Source/Localization/LocKey.cs`, next to related keys.
2. Add `"Name": "..."` to `Locales/en.json`, `ru.json` and `by.json`, near related keys. Keep placeholders (`{0}`), HTML tags and emoji identical across languages.
3. Use it as `Ctx.T(LocKey.Name)` or `Ctx.F(LocKey.Name, args)`; wrap user data with `Text.H(...)`.
4. Verify JSON parses and `dotnet build` passes.
