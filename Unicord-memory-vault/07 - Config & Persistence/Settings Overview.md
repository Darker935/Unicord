---
tags:
  - "#config"
aliases:
  - Settings Overview
---

# Settings Overview

- `App.LocalSettings` / `App.RoamingSettings` (WinRT ApplicationData)
- Voice devices: `InputDevice` / `OutputDevice` local keys
- Settings UI: `SettingsService` → overlay `SettingsPage`
- Security: Windows Hello gates (`VERIFY_LOGIN`, `VERIFY_SETTINGS`, `VERIFY_NSFW`)

Token is **not** in ApplicationData. See [[Token and Login]].

---

## Links
- [[Package Identities]]
