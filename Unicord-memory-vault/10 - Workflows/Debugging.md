---
tags:
  - "#workflow"
aliases:
  - Debug
---

# Debugging

## Voice

| Symptom | First checks |
|---------|----------------|
| Dialog: 4017 | Identify `max_dave_protocol_version` |
| Dialog: 4006 | Endpoint port kept? Latest Voice Server token? Leave race? |
| Official sees you, Unicord silent | Session Description + silence frames + IP discovery BE port |
| Member list empty | `Channel.Users` / voice states by id; messenger registered |
| DAVE DllNotFound | `unicord_dave.dll` at package root |

## Logs

`Logger.Log` / `Logger.LogError` (`Misc/Logger.cs`) — Debug + WinRT `FileLoggingSession`. Voice WS lines start with `Voice WS`.

ETW buffers in memory. A `.etl` only appears when a session is closed and saved — there is no per-event flush. Save points, in order of preference:

1. **Settings > Developer > Save logs now** (sideloaded builds only). Writes the trace without closing Unicord.
2. Minimising the window (`EnteredBackground`).
3. Suspend / close.

Files land in `%LOCALAPPDATA%\Packages\24101WamWooWamRD.UnicordCanary_g9xp2jqbzr3wg\LocalState\Logs` as `Log-Unicord-<yyyyMMdd-HHmmss>-NN-0.etl`. Newest sorts last by name. Decode:

```powershell
tracerpt <file>.etl -o dump.xml -of XML -y
```

Events carry typed fields, not just `Message`: `Timestamp` (the caller's clock — the ETW stamp is when the background writer ran), `ThreadId`, the message template's named holes, and `ExceptionType` / `ExceptionMessage` / `StackTrace`.

A `LogEntriesDropped` event means the writer queue overflowed and that many events were lost.

## Login

`DiscordManager` + PasswordVault. Captcha handler on client.

## UI

UWP — no Playwright. Closest verify: install Canary, user exercise, or inspect logs.

---

## Links
- [[Close Codes]]
- [[Voice Handshake]]
