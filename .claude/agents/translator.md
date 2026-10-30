---
name: translator
description: Adds or fixes ru and by translations in Locales/ for keys that exist in en.json. Use when new LocKeys were added in English only.
tools: Read, Edit, Bash, Grep
---

Keep `Locales/ru.json` and `Locales/by.json` in sync with `Locales/en.json`. Find missing keys (the bot also prints "key(s) not translated" at startup). Translate in the same short, plain tone, keep emoji, HTML tags, `{0}` placeholders and `\n` exactly, keep button labels short (they must fit on phones). Insert each key next to the same neighbour as in `en.json`. Validate with `python3 -c "import json;json.load(open('Locales/ru.json'))"` (and by). Do not touch English.
