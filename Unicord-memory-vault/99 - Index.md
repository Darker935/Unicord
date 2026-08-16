---
tags:
  - index
  - root
aliases:
  - Home
  - Start Here
---

# Unicord Memory Vault

> **Purpose:** Single source of truth for any AI agent working on Unicord. Designed to prevent hallucination by providing precise, organized context about every subsystem, convention, and architectural boundary.

## Vault Map

| Section | Purpose | Priority |
|---------|---------|----------|
| [[01 - Project Identity]] | Who we are, what we build, package identities | READ FIRST |
| [[02 - Architecture]] | UWP layers, data flow, service boundaries | READ FIRST |
| [[03 - DSharpPlus & Gateway]] | Forked library, user token, API v9, events | Core |
| [[04 - Voice]] | Voice WS v8, RTP, DAVE, handshake, close codes | Core (hot) |
| [[05 - UI Shell]] | Navigation, pages, channel list, view models | Core |
| [[06 - Feature Registry]] | Messaging, voice UI, notifications, settings | Reference |
| [[07 - Config & Persistence]] | Settings, PasswordVault, Canary vs Store | Core |
| [[08 - Coding Conventions]] | C# / XAML style, quality gates | Must Follow |
| [[09 - AI Agents]] | Session rules, Discord protocol lookup, **[[Account Safety]] (MANDATORY)** | System |
| [[10 - Workflows]] | Build, debug, deploy Canary | Process |
| [[AI-logs]] | Session history and decisions | Archive |

## Quick Links (Most Referenced)

- **Initial session prompt (forkable):** repo root `INITIAL_SESSION_PROMPT.md`
- [[Architecture Overview]] — Pages / ViewModels / Services / DSharpPlus
- [[Voice Handshake]] — Join, Voice Server Update, endpoint port, 4006
- [[DAVE]] — Mandatory E2EE since 2026-03-01, `unicord_dave.dll`
- [[Close Codes]] — Voice WS 4006 / 4017 and what they actually mean
- [[Package Identities]] — Canary vs production; do not overwrite Store
- [[Building]] — MSBuild x64 Debug + signing thumbprint
- [[Discord Protocol Lookup]] — Docs, discord.js-selfbot voice, local GhoSty (protocol only)
- [[Discord Client Source]] — Discord is not source-available; DAVE whitepaper + libdave are
- [[Account Safety]] — **MANDATORY.** Human-driven client only. No endpoint spam, no self-bot features, no identify/RTP storms. Read before any REST / gateway / voice change.

## Live pins (re-check before assuming)

| Pin | Value (as of 2026-08-13) |
|-----|--------------------------|
| Branch | `redesign` (ahead of `origin/redesign`) |
| Remote | `https://github.com/UnicordDev/Unicord.git` |
| Discord API | v9 (`Libraries/DSharpPlus/.../Endpoints.cs`) |
| Token type | `TokenType.User` |
| Voice WS | `/?v=8` |
| Transport crypto | `aead_aes256_gcm_rtpsize` |
| DAVE | identify `max_dave_protocol_version: 1`, crate `davey 0.1.4` |
| Canary identity | `24101WamWooWamRD.UnicordCanary` **2.0.20.0** |
| Store identity | `24101WamWooWamRD.Unicord` **2.0.51.0** |

---

*Last updated: 2026-08-14*
