---
tags:
  - index
  - "#project"
aliases:
  - Project Identity
  - Unicord Intro
---

# 01 — Project Identity

## What is Unicord?

Unicord is a **free, open-source Discord client for Windows 10 / Windows 10 Mobile** (UWP). It is a native C# / XAML app on a **forked DSharpPlus** (`Libraries/DSharpPlus`, user-token capable, Discord API v9).

It is **not** the official Discord client and **not** a bot framework. Login uses a **user token** (`TokenType.User`) stored in Windows `PasswordVault`.

Product bar: a **human-driven**, lightweight native client for low-end Windows 10 / Windows 10 Mobile. It must not spam Discord endpoints or grow self-bot features. Mandatory research and rules: [[Account Safety]].

Upstream / origin: [UnicordDev/Unicord](https://github.com/UnicordDev/Unicord). Local work in this tree is on branch **`redesign`**.

## Identity Card

| Property | Value |
|----------|-------|
| **Name** | Unicord |
| **Upstream org** | UnicordDev / Wan Kerr Co. Ltd. (WamWooWam) |
| **Local working branch** | `redesign` |
| **Language** | C# (LangVersion 12) + XAML |
| **UI** | UWP / WinUI 2.7 |
| **Discord library** | DSharpPlus fork (submodule under `Libraries/DSharpPlus`) |
| **Token** | User token, not bot |
| **API** | Discord HTTP/Gateway v9 |
| **Platform** | Windows 10/11 UWP; keep 1703-safe APIs for Phone |
| **License** | MIT |
| **Solution** | `Unicord.sln` |

## Two installed apps (do not mix)

| App | Package name | Typical version | Role |
|-----|--------------|-----------------|------|
| **Canary** (this tree) | `24101WamWooWamRD.UnicordCanary` | 2.0.4.0 | Local deploy from `Unicord.Universal/Package.appxmanifest` |
| **Store / production** | `24101WamWooWamRD.Unicord` | 2.0.51.0 | `Unicord.Universal.Package`; **cannot overwrite without original PFX** |

Publisher for local Canary: `CN=0F22111D-EDF0-42F0-B58D-26C4C5C5054B`. Settings and data are separate, so both can be installed side by side.

## Directory Layout

| Path | Content |
|------|---------|
| `Unicord.Universal/` | Main UWP app (pages, models, voice, themes) |
| `Unicord.Universal.Package/` | Store/production packaging project |
| `Unicord.Universal.Shared/` | Toasts, tiles, shared constants |
| `Unicord.Universal.Background/` | Desktop tray / notification host |
| `Unicord.Universal.Background.Tasks/` | Background tasks (WinMD) |
| `Libraries/DSharpPlus/` | Forked Discord library (submodule) |
| `Unicord.Universal/Voice/` | In-process voice session + DAVE native |
| `Unicord.Universal/Voice/native/unicord_dave/` | Rust `cdylib` wrapping `davey 0.1.4` |
| `Research/` | Clean-room Discord notes (not a protocol spec) |
| `Unicord-memory-vault/` | This vault |

## Key Files (First to Read)

- `INITIAL_SESSION_PROMPT.md` — Forkable first message
- `Unicord.Universal/Package.appxmanifest` — Canary identity + version
- `Unicord.Universal/Services/DiscordManager.cs` — Login / `DiscordClient`
- `Unicord.Universal/Services/DiscordNavigationService.cs` — Channel / voice navigation
- `Unicord.Universal/Models/Voice/VoiceConnectionModel.cs` — Gateway voice handshake
- `Unicord.Universal/Voice/DiscordVoiceSession.cs` — Voice WS + UDP + DAVE
- `Libraries/DSharpPlus/DSharpPlus/Net/Rest/Endpoints.cs` — `API_VERSION = "9"`

---

## Links
- [[Tech Stack]]
- [[Architecture Overview]]
- [[Package Identities]]
