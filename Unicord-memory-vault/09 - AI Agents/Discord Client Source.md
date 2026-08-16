---
tags:
  - "#protocol"
  - "#research"
aliases:
  - Discord Client Source
  - Is Discord source-available
---

# Is Discord source-available?

**No.** Discord does not publish the web app, the desktop app, or the REST/gateway servers. There is no official “here is how the client works” tree you can clone.

What you *can* read is a mix of **things Discord published on purpose**, **minified JS they ship to every browser**, and **community notes already extracted from that**. For Unicord voice, the first group is enough to stop guessing. Dumping the installed app is the worst option.

Written 2026-08-14. Lookup order for live protocol work is still [[Discord Protocol Lookup]].

---

## What Discord actually is

| Piece | Open? | What you get |
|---|---|---|
| Web app (`discord.com/app`) | No | Webpack bundles in the browser. Minified, not encrypted. Readable after beautify, painful as a spec. |
| Desktop (Windows / macOS / Linux) | No | **Electron**. Same JS as the website, packed in `app.asar`. ASAR is a tar-like archive, **not encryption**. Native bits (voice, overlay, updates) are compiled `.node` / DLLs — not source. |
| iOS / Android | No | Real native apps. Not useful for Unicord. |
| API servers | No | Closed. You only see the wire. |
| Bot / HTTP / Gateway docs | Yes (bot-shaped) | [docs.discord.com](https://docs.discord.com) — incomplete for **user** clients. |
| **DAVE** | **Yes** | This is the exception that matters for calls. |

Desktop looking “native” is mostly Chromium + JS. A smaller C++ layer does capture, encode, and DAVE. That native layer is **not** source-available.

---

## The part that *is* open (use this for voice)

Discord published the call crypto on purpose (2024, still current):

| What | Where |
|---|---|
| Protocol whitepaper (DAVE v1.1) | [github.com/discord/dave-protocol](https://github.com/discord/dave-protocol) / [daveprotocol.com](https://daveprotocol.com) |
| Code their own clients use (JS + C++, MIT) | [github.com/discord/libdave](https://github.com/discord/libdave) |
| Official voice WS docs (including new opcodes) | [Voice connections](https://docs.discord.com/developers/topics/voice-connections) |
| Announcement | [Meet DAVE](https://discord.com/blog/meet-dave-e2ee-for-audio-video) |

Unicord already sits on that stack via `davey` 0.1.4 / `unicord_dave.dll`. You do **not** need Discord.exe to understand MLS, ops 21–31, or why 4017 exists. That is written down.

RTP/AES (`aead_aes256_gcm_rtpsize`), silence `F8 FF FE`, endpoint port, speaking opcode — those are in official docs + [docs.discord.food](https://docs.discord.food/topics/voice-connections) + `@discordjs/voice`. That is why the voice bugs were fixable without Discord’s UI source.

---

## “Can I just read the installed / web JS?”

**Technically:** the JS they send you is sitting on disk / in DevTools. It is obfuscated (short names, giant webpack chunks), not encrypted. Desktop `app.asar` unpacks the same kind of files. Native voice is still a binary.

**Practically:** that is a terrible spec. You will spend days in `e.exports = function(t,n){…}` to rediscover something `discord.food` or `libdave` already states.

**Policy:** Discord’s Terms say not to reverse-engineer or use unauthorized software to modify the service. The Platform Manipulation explainer says not to modify the Discord client. See [[Account Safety]]. Do not unpack their install or copy their client into Unicord.

Watching **your own** official session in the browser Network tab (opcodes, URLs, when they send Voice State Update) is ordinary debugging of a service you are talking to. That is different from ripping their app and pasting it.

---

## How to stop guessing (this repo’s order)

Already in [[Discord Protocol Lookup]]. Faster than Discord’s webpack:

1. This vault (`04 - Voice/`, this page)
2. This tree: `DiscordVoiceSession`, `VoiceConnectionModel`, DSharpPlus dispatch
3. Official: voice-connections, rate limits, gateway, **libdave + dave-protocol**
4. [docs.discord.food](https://docs.discord.food) — user-client extras; mark v8 vs v9
5. Local `@discordjs/voice` in GhoSty — **protocol only**, never tokens
6. Web last

`discord.food` exists because people already did the webpack archaeology. You inherit that. You do not need to redo it.

---

## Direct answers

- **Is Discord source-available?** No.
- **Is the web JS readable?** Minified, yes. Encrypted, no. Usable as a spec, barely.
- **Is the installed app better?** Same JS in an ASAR, plus closed native modules. Not a cleaner source tree.
- **Is anything actually open that helps voice?** Yes: **DAVE whitepaper + libdave**, plus official voice opcode docs.
- **Will dumping Discord.exe speed Unicord up?** Almost never, compared to food + libdave + `@discordjs/voice`. It also steps on ToS we do not play games with.

If a *specific* voice/API unknown is stuck (for example “what does voice op 22 look like on the wire”), look it up in libdave / food / the working library — not by unpacking Discord.

---

## Links

- [[Discord Protocol Lookup]]
- [[DAVE]]
- [[Voice Handshake]]
- [[Account Safety]]
