---
tags:
  - "#safety"
  - "#mandatory"
  - "#tos"
aliases:
  - Account Safety
  - Endpoint Discipline
  - Ban Risk
---

# Account Safety and Endpoint Discipline — MANDATORY

> **This page is a standing rule, not a suggestion.**
>
> Every agent, every session, every change that talks to Discord (REST, main gateway, voice WS, UDP, DAVE) **must** follow this file. It is never optional, never deferred, and never "just this once." If a change would violate it, **stop and ask**. Do not ship the change.

**Read this before writing voice, gateway, REST, login, presence, typing, or message code.**

Product intent (owner + project): Unicord is a **lightweight, human-driven native Discord client** for Windows 10 / Windows 10 Mobile, because official Discord does not support those devices. It exists to give a real person a native UI. It is **not** a self-bot, not an automator, not a scraper, and not a raid tool.

---

## 1. Two layers of truth (do not collapse them)

These are **not** the same thing. Agents must keep them separate.

### Layer A — What Discord writes (policy)

As of the documents retrieved **2026-08-14**:

| Source | What it actually says |
|--------|------------------------|
| [Community Guidelines](https://discord.com/guidelines) §13 (effective 2025-09-29) | Do not send unsolicited bulk messages (spam). Do not sell spam/raid/account-creation/token/CAPTCHA tools. |
| Guidelines §14 | **“Do not use self-bots or user-bots. Each account must be associated with a human, not a bot.”** |
| [Platform Manipulation Policy Explainer](https://discord.com/safety/platform-manipulation-policy-explainer) (2024-03-15, still current) | Spam includes automated accounts, humans doing spam, **and** “user accounts modified to perform automated actions (self-bots).” Also: **“Making modifications to the Discord client for the purpose of spam or any other reason is not allowed under this policy.”** Avoidance list: “Don’t modify the Discord client for any reason — including automating account actions or altering the appearance or layout of Discord.” |
| [Automated User Accounts (Self-Bots)](https://support.discord.com/hc/en-us/articles/115002192352-Automated-User-Accounts-Self-Bots) | Bot accounts exist for automation. **“Automating normal user accounts (generally called ‘self-bots’) outside of the OAuth2/bot API is forbidden, and can result in an account termination if found.”** |
| [Terms of Service](https://discord.com/terms) | Do not intentionally overburden Discord’s systems; do not scrape; do not use unauthorized software **designed to modify the services**. Discord may suspend/terminate with or without notice. |
| DSharpPlus fork itself (`DiscordClient.ConnectAsync`) | Logs: *“You are logging in with a token that is not a bot token. This is not officially supported by Discord, and can result in your account being terminated if you aren't careful.”* |

**Written policy does forbid unofficial clients and user-token API use.** Unicord is a standalone UWP client (not a patch of Discord.exe), but it still logs in with a **user token** on API v9. That is unofficial. Do not tell users “Discord allows this.” Discord’s lawyers do not.

### Layer B — What Discord actually enforces (practice)

This is the owner’s operating thesis, and public history largely agrees **as an enforcement pattern**, not as permission:

- Discord’s public ban reasons for this class of abuse are almost always **automation / spam / raids / token abuse**, not “you opened a third-party window.”
- Guideline 13 and the Platform Manipulation explainer spend their energy on **bulk unsolicited actions**, fake accounts, join-for-join, sold tokens, raid tools.
- Guideline 14 is written as “account must be a human, not a bot.” A person tapping Mute in Unicord is still a human. A script sending 400 DMs is not.
- Custom clients that **behave like a human using the official app** (one presence, one gateway, user-initiated messages, official voice cadence) have existed for years. Enforcement volume tracks **endpoint spam and automation**, not “XAML instead of Electron.”
- Hitting **429**, **invalid-request Cloudflare bans**, or **IDENTIFY storms** is what turns a quiet client into a Trust & Safety event.

**Unicord’s safety bar (this project, mandatory):**

1. Behave like **one human on one official-shaped session**.
2. Never add automation, bulk actions, or extra chatter Discord did not ask for.
3. Treat rate limits as hard walls.
4. Do not pretend Layer A does not exist — just do not make Layer B fire.

If an agent “helps” by adding auto-reply, mass-join, message scrape loops, auto-react, or reconnect-spam “to be robust,” that agent has **failed this rule** even if the feature works.

---

## 2. What Unicord is allowed to be

| Allowed (human client) | Forbidden (self-bot / abuse) |
|------------------------|------------------------------|
| User opens a channel → fetch a small page of messages | Background poll of every channel / guild |
| User types → typing indicator at most every 10s | Auto-type, auto-reply, auto-react |
| User clicks Connect → one Voice State Update, one voice WS identify | Retry storms, identify loops, join/leave flaps |
| User speaks → ~50 Opus packets/s, speaking opcode on edge only | UDP flood, speaking opcode every frame |
| User mutes → one gateway Voice State Update | Binding loops that emit opcode 4 in a tight loop |
| Guild not yet lazy-synced → one opcode 14 `LazyRequest` | Re-sync every navigation, request all members |
| Honor HTTP 429 / gateway 120/60s | Ignore 429, busy-retry, parallel duplicate requests |
| One `DiscordClient` via `DiscordManager` | Second hidden gateway, extra shards, token clone |

Unicord must remain **user-initiated**. The computer does not act unless the human did.

---

## 3. What actually harms accounts

Ranked by how Discord’s systems actually see them. This is the research, not folklore.

### 3.1 REST spam (highest practical risk)

Official ([Rate Limits](https://docs.discord.com/developers/topics/rate-limits), retrieved 2026-08-14):

| Limit | Number | If you blow it |
|-------|--------|----------------|
| Global HTTP | **50 requests / second** per token (also per IP if unauthenticated) | HTTP 429, `X-RateLimit-Global: true` |
| Per-route | Varies; read `X-RateLimit-*` headers. **Do not hardcode.** | 429 + `retry_after` |
| Invalid-request Cloudflare | **10,000** of **401 / 403 / 429** in **10 minutes** | Temporary IP ban from the API |

DSharpPlus `RestClient` already buckets, waits, and retries 429. **New Unicord code must not bypass it** (no raw `HttpClient` to `discord.com/api`, no fire-and-forget retry loops around `CreateMessageAsync`).

Typical Unicord REST that is **fine** when user-driven:

- `GetMessagesAsync(15–25)` on open; `GetMessagesBeforeAsync` on scroll (`ChannelPageViewModel`, `INITIAL_LOAD_LIMIT`).
- `TriggerTypingAsync` gated to **> 10 seconds** since last send.
- Message send / edit / delete when the user did that.

Typical Unicord REST that would **not** be fine:

- Prefetching every guild’s history at login.
- Retrying a failed send in a `while (true)` without `retry_after`.
- Acknowledging / marking-read every channel in a tight loop.
- Searching or listing members across many guilds unattended.

### 3.2 Main-gateway spam (session killer)

Official ([Gateway](https://docs.discord.com/developers/events/gateway), retrieved 2026-08-14):

| Limit | Number | If you blow it |
|-------|--------|----------------|
| Gateway commands | **120 events / connection / 60 seconds** (~2/s) | Immediate disconnect; repeat offenders lose API access |
| IDENTIFY | **1000 / 24 hours** (documented for apps; treat user tokens as **at least this strict**) | All sessions killed; token reset + email on the bot path. User path is not kinder. |
| IDENTIFY concurrency | `max_concurrency` per 5s (usually 1 for a user) | Opcode 9 Invalid Session |
| Payload size | **4096 bytes** | Close **4002** |

Unicord sends user-gateway opcodes for: Identify/Resume (library), Heartbeat (library), Voice State Update (join/leave/mute/deafen), LazyRequest op 14 (`SyncGuildsAsync`, once per unsynced guild), presence requests for missing authors.

**Dangerous patterns:**

- `ReconnectIndefinitely = true` **without** backoff that prefers **RESUME** over a fresh IDENTIFY. Fresh IDENTIFY is the expensive one.
- Re-sending Voice State Update on every property-changed flicker.
- Calling `SyncAsync()` on a guild that is already synced (current code gates on `!IsSynced` — keep that).
- Opening a second `DiscordClient` (background tasks already construct their own — do not add a third, and do not let background + foreground both IDENTIFY the same token at once if it can be avoided).

### 3.3 Voice-gateway and UDP (this is the DAVE work)

Voice is a **second** WebSocket plus UDP. Official clients:

1. One gateway opcode 4 (join).
2. Wait for `VOICE_STATE_UPDATE` + `VOICE_SERVER_UPDATE`.
3. One voice WS, one Identify (op 0).
4. One UDP IP discovery, one Select Protocol.
5. Heartbeat at the server’s `heartbeat_interval`.
6. Speaking (op 5) on **edges** (start / stop), not per packet.
7. RTP at **50 packets/s** (20 ms Opus frames).
8. UDP keepalive every few seconds.
9. DAVE MLS only when the server sends 21–31.

**What looks like abuse on voice:**

| Pattern | Why it is bad |
|---------|----------------|
| Identify retry on 4006 with the same dead token | Close-code storm + extra identifies. Need a new join or a newer Voice Server Update. |
| Leave/join loop (`channel_id` null then set) in a reconnect handler | Gateway opcode 4 spam + session churn. |
| Speaking opcode every RTP packet | Voice WS flood (op 5). |
| RTP well above 50/s with no clock | UDP flood. |
| Sending plaintext Opus while `dave_protocol_version > 0` | Not a ban by itself; other clients hear **noise**. Still forbidden here because it is protocol-wrong. |
| Re-init DAVE + key package on every failed commit in a tight server loop | MLS chatter. Protocol allows **one** op 31 + re-key per bad commit, not a local while-loop. |
| Extra REST around voice (polling `/voice-states`, etc.) | There is no such client REST. Do not invent it. |

### 3.4 Automation features (instant Guideline 14)

Never implement, even as “debug,” “admin,” or “hidden”:

- Auto-send, scheduled send, auto-reply, auto-react, auto-add-friend
- Mass DM / mass ping / mention bombs
- Token login farms, token checkers, token generators
- Auto-join servers / auto-accept invites / raid helpers
- Scrapers (members, messages, invites) that run without a user scroll/click
- CAPTCHA solvers that are not the user completing Unicord’s own captcha dialog
- Anything copied from selfbot libraries **as a feature** (GhoSty is protocol-only)

### 3.5 Fingerprint / impersonation (secondary, real)

The DSharpPlus fork already:

- Sets REST `User-Agent` to a **synthetic Chrome** string (`Utilities.GetUserAgent`).
- Sends `X-Super-Properties` from `ClientProperties`: `browser = "Chrome"`, `release_channel = "stable"`, **`client_build_number = 325421`** (stale vs live Discord desktop), computed `browser_version`.

That is **not** something the DAVE work added. It is a pre-existing library choice.

Honest assessment:

- Pretending to be official desktop Discord with a **wrong/stale build number** can look more like a cheap self-bot than identifying as “Unicord.”
- Identifying honestly as Unicord is more detectable as unofficial, but more consistent.
- **Do not casually flip this.** Changing super-properties is a product/security decision. Do not “fix” it in a voice PR. Do not invent new fake Discord build numbers from memory.

### 3.6 Token handling (account theft, not Discord ban)

A stolen token **is** the account. Rules already in [[Token and Login]]:

- Never log, commit, or print a user token.
- Never read GhoSty (or any other repo) as a token store.
- `PasswordVault` is the store. `App.LocalSettings.Save("Token", …)` exists for the background host — do not add more plaintext copies.
- Voice WS `SendJsonAsync` currently logs the **full identify JSON**, which includes the **voice token**. That is a **local secret leak** (log files), not Discord spam. Do not add more of that. Prefer logging opcode + lengths, not payloads that carry `token` / `session_id` / `secret_key`.

### 3.7 Other account-harm (not endpoint spam)

These are real, but they are **user behavior** or **content policy**, not Unicord protocol:

- Spam, scams, raids, CSAM, hate, etc. (Guidelines 1–27). The client must not help.
- Sharing the token, installing malware, phishing.
- Ban evasion after a Discord-level ban (Guideline 19).

Unicord must not grow features that make those easier.

---

## 4. Official numbers agents must not invent past

Copy these; do not “remember” different ones.

**HTTP** — [docs.discord.com/developers/topics/rate-limits](https://docs.discord.com/developers/topics/rate-limits)

- 50 req/s global
- Per-route from headers
- 429 body: `retry_after` (seconds), `global`
- 10k invalid (401/403/429) / 10 min → Cloudflare

**Gateway** — [docs.discord.com/developers/events/gateway](https://docs.discord.com/developers/events/gateway)

- 120 sent events / 60 s / connection
- 1000 IDENTIFY / 24 h (RESUME does not count)
- Prefer RESUME after drop; IDENTIFY only when the session is dead
- Heartbeat at server interval (with jitter on first beat). Do not heartbeat faster “to be safe.”

**Voice** — vault [[Voice Handshake]] + [[Close Codes]] + official voice-connections

- One identify per voice session
- 4006 → **do not** re-identify the same pair; wait for new credentials or a user-initiated rejoin
- 4017 → DAVE required (`max_dave_protocol_version: 1`), not “retry harder”
- 4005 Already authenticated → do not identify twice on one socket
- RTP clock 20 ms / 960 samples @ 48 kHz
- Speaking opcode on state change only

---

## 5. Mandatory rules (never violate)

These are executable constraints. If a PR / edit breaks one, it is invalid.

### R1 — Human in the loop

Every Discord-visible action except heartbeats, session resume, 429 backoff, and protocol-required DAVE replies must be **caused by a user gesture** (click, tap, type, hardware mute, navigating to a channel, connecting to voice).

### R2 — No new chatter

Do not add REST routes, gateway opcodes, voice opcodes, or UDP packet types that a current official desktop client would not send for the same user action. Protocol lookup: [[Discord Protocol Lookup]].

### R3 — One session

One foreground `DiscordClient`. One voice session per guild voice/call. Tear down the old voice WS before opening a new one. No parallel identifies.

### R4 — Honor limits

Never bypass DSharpPlus rate-limit buckets. On 429, wait `retry_after`. Never tight-loop a Discord call. Never swallow 429 and immediately retry.

### R5 — Connect / reconnect discipline

- Main gateway: RESUME if `session_id` is live; IDENTIFY only when it is not. Backoff on failure (library already 7.5s doubling).
- Voice: at most **one** `ConnectAsync` handshake per credential generation. The existing 3-attempt loop may retry **only when `_credentialVersion` changed** (new Voice Server Update). Do not raise that cap. Do not add a fourth outer retry.
- 4006 / 4004: leave or wait for new credentials. Do not hammer Identify.
- User disconnect: one opcode 4 with `channel_id: null`, then stop.

### R6 — Voice cadence

- RTP target **50/s**. Catch-up repeats stay capped (`MaxClockCatchUpFrames` is 2 today — do not raise it into double-digits).
- Drop capture backlog rather than bursting late packets.
- Speaking op 5 only when `_speaking` flips.
- Heartbeat only on the Hello interval (`Math.Max(interval, 1000)`).
- UDP keepalive ~5 s, one 8-byte packet.
- Do not send media until AES is up. If `dave_protocol_version > 0`, do not send non-silence Opus until `EncryptOpus` succeeds.

### R7 — DAVE is protocol, not a retry toy

MLS messages (ops 23, 26, 28, 31) fire **in response to server 21/22/24/25/27/29/30**. One key package after session description. One op 31 + re-init per failed commit/welcome. No local “keep sending key packages until ready” loop.

### R8 — No self-bot surface

No commands, hidden pages, debug buttons, or library helpers that send messages / joins / friends / reactions without the user composing them. Do not import GhoSty/selfbot **features**. Protocol only.

### R9 — Secrets

No tokens, voice tokens, `secret_key`, or session ids in git, vault examples, commit messages, or chat. Prefer not to log them locally either.

### R10 — Stop and ask

If a fix “needs” more requests, a polling loop, a second connection, or a fake official-client header change to “look less banned,” **stop**. Ask the owner. Do not decide.

---

## 6. Audit of the current voice / DAVE code (2026-08-14)

Scope: `VoiceConnectionModel`, `DiscordVoiceSession`, `DaveNative`, `VoiceAudioEngine`, `DiscordManager`, recent `fix(voice)` / `feat(voice)` commits through `f1d119f`.

### 6.1 Verdict

**The DAVE / voice reimplementation does not introduce endpoint spam and does not, by itself, violate the owner’s “no spam → no automation ban” principle.**

It talks to Discord the way a single official client talks when one human joins a voice channel: one join, one voice identify, one UDP discovery, server-paced heartbeats, edge-triggered speaking, ~50 RTP/s, DAVE only when the server drives MLS.

It is **not** a self-bot. It does not send chat, does not scrape, does not mass-connect.

**It does not make Unicord “ToS-legal.”** No unofficial user-token client is. That was already true before DAVE. This work does not make that worse.

### 6.2 What the current code does (measured against R1–R7)

| Check | Result | Evidence |
|-------|--------|----------|
| Join is user-initiated | Pass | `ConnectAsync` from navigation / call UI |
| Opcode 4 count on join | Pass (1 + optional leave) | `LeaveExistingVoiceAsync` then one join. Leave only if already in a channel in that guild. Waits up to 2s. |
| Voice WS identify | Pass (once per session) | `OnWsMessage` Hello → `SendIdentifyAsync` once |
| Handshake retries | Pass | Max **3** attempts, and only continues if `_credentialVersion` changed |
| 4006 handling | Pass | Failed connect → `ForceLeaveAsync` (one leave) + throw. No silent identify retry |
| Mid-call Voice Server Update | Pass (official-shaped) | Tear down + one new session (`ReconfigureNetworkingAsync`). Discord-driven, not a timer |
| Heartbeat | Pass | Server `heartbeat_interval`, floor 1s |
| Speaking | Pass | `SendSpeakingAsync` returns if `_speaking == speaking` |
| RTP rate | Pass | Wall-clock 20 ms, `MaxClockCatchUpFrames = 2`, capture queue max 4, drop backlog |
| UDP keepalive | Pass | 5 s |
| DAVE send | Pass | Encrypt only; skip send if not ready (no plaintext Opus on a DAVE channel) |
| DAVE MLS | Pass | Reactive to server ops; one op 31 + `InitializeDave` on bad commit |
| Silence frames | Pass | Five `F8 FF FE` after ready so the SFU unmutes others — official clients do this |
| Mute/deafen gateway | Mostly pass | One opcode 4 per mute. **Deafen also notifies `Muted`**, so **two** opcode 4s per deafen click. Wasteful, not a flood. |
| Main gateway reconnect | Pre-existing | `ReconnectIndefinitely = true` with 7.5s doubling backoff on **initial** connect. Disconnect path AutoReconnect is library default. Not introduced by DAVE. |
| REST from voice | Pass | Voice path does **not** hit HTTP for media |

### 6.3 Not spam, but do not regress

These are quality / privacy, not ban-storms:

1. **Voice WS logger prints full JSON** (`Voice WS >>` / `<<`), including identify `token` and session description `secret_key`. Local log leak.
2. **Deafen → two Voice State Updates** (`OnPropertyChanged` fires both `Muted` and `Deafened`).
3. **Chrome / stale `client_build_number`** in the library — pre-existing; leave it out of voice diffs.
4. **Foreground + background `DiscordClient`** both set `ReconnectIndefinitely` — pre-existing dual-session risk if both are alive. Do not add a third.
5. If DAVE `ProcessCommit` / `ProcessWelcome` were stuck failing, Discord could keep sending commits and we would answer with op 31 + a new key package **once per server message**. That is protocol. A local `while` around it would not be.

### 6.4 What would make this code unsafe later

Agents must not “harden” voice by:

- Auto-rejoin on every close code
- Re-identify on 4006
- Raising handshake attempts above 3
- Raising `MaxClockCatchUpFrames` to “fix lag”
- Sending speaking=true every packet “so Discord notices”
- Polling REST for voice members
- Opening a second voice WS “just in case”
- Sending plaintext “so people can hear us” when DAVE is not ready

Those “fixes” are how a quiet client becomes a spam client.

---

## 7. How to apply this in a session

Before any Discord-facing edit:

1. Name the user gesture that causes the request.
2. Name the official opcode / route (vault → this repo → docs.discord.com → discord.food).
3. Count: is this 1:1 with what official desktop would send for that gesture?
4. If reconnect/retry: what stops the loop? What is the cap? Is it RESUME or IDENTIFY?
5. If you cannot answer, do not write the code.

After the edit: grep for new `SendPayloadAsync`, `CreateMessageAsync`, `TriggerTypingAsync`, `ConnectAsync`, `SendIdentify`, `HttpClient`, `while`, `Retry`. If a new loop talks to Discord, it is guilty until proven user-gated and 429-safe.

---

## 8. Paste block for other agents’ standing memory

The owner may paste this into Claude / other tools. Do not shorten away the hard rules.

```
Unicord account safety (MANDATORY, never violate):

Unicord is a human-driven UWP Discord client (user token, API v9). It is not a self-bot.
Discord's written rules forbid unofficial clients and self-bots. Enforcement in practice
targets automation and endpoint spam (429 storms, IDENTIFY storms, bulk messages, raids).
Unicord's job is to look like one human on one session. Never add automation.

Rules:
- Every Discord-visible action except heartbeats, RESUME, 429 waits, and server-driven
  DAVE replies must come from a user gesture.
- Do not add REST/gateway/voice/UDP Discord does not send for that same gesture.
- One DiscordClient, one voice session. No parallel identifies.
- Honor 429 / rate-limit headers. Never tight-loop Discord I/O.
- Gateway: RESUME if the session is live; IDENTIFY only when it is dead.
  Voice: one identify per credential generation; max 3 connect attempts and only if
  credentials changed. Never re-identify on 4006 with the same pair.
- Voice RTP ~50/s. Speaking opcode on edge only. No UDP flood. No plaintext Opus
  when dave_protocol_version > 0.
- DAVE MLS only in response to server ops. No key-package while-loop.
- No auto-send, auto-join, scrape, mass DM, token tools, raid helpers.
- Never log or commit tokens / voice tokens / secret_key.
- If a fix "needs" more requests or a second connection, stop and ask.

Canonical research: Unicord-memory-vault/09 - AI Agents/Account Safety.md
```

---

## Sources (retrieved 2026-08-14)

- https://discord.com/guidelines (effective 2025-09-29)
- https://discord.com/terms (effective 2025-09-29)
- https://discord.com/safety/platform-manipulation-policy-explainer (2024-03-15)
- https://support.discord.com/hc/en-us/articles/115002192352-Automated-User-Accounts-Self-Bots
- https://docs.discord.com/developers/topics/rate-limits
- https://docs.discord.com/developers/events/gateway (IDENTIFY 1000/24h; 120 events/60s)
- This repo: `VoiceConnectionModel.cs`, `DiscordVoiceSession.cs`, `DaveNative.cs`, `DiscordManager.cs`, `RestClient.cs`, `ClientProperties.cs`, `ChannelPageViewModel.cs`
- Vault: [[Voice Handshake]], [[Close Codes]], [[DAVE]], [[Token and Login]]

---

## Links

- [[Agent System]]
- [[Quality Gates]]
- [[Discord Protocol Lookup]]
- [[Voice Overview]]
- [[Token and Login]]
