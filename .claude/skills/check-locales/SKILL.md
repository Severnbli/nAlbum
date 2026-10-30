---
name: check-locales
description: Verify that LocKey constants and the three locale files agree (no missing, unused or unknown keys, valid JSON).
---

Run from the repo root:

```sh
python3 - <<'PY'
import json,re
keys=set(re.findall(r'const string (\w+) =',open('Source/Localization/LocKey.cs',encoding='utf8').read()))
for l in ['en','ru','by']:
    t=json.load(open(f'Locales/{l}.json',encoding='utf8'))['translations']
    print(l,'missing:',sorted(keys-t.keys()),'unknown:',sorted(t.keys()-keys))
src=''.join(open(p,encoding='utf8').read() for p in __import__('glob').glob('Source/**/*.cs',recursive=True) if not p.endswith('LocKey.cs'))
print('unused:',sorted(k for k in keys if f'LocKey.{k}' not in src))
PY
```

Fix anything reported (add translations, delete dead keys from LocKey and all locale files), then `dotnet build`.
