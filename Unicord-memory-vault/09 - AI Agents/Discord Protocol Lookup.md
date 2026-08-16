---
tags:
  - "#protocol"
  - "#voice"
aliases:
  - Discord Protocol Lookup
---

# Discord Protocol Lookup

Never invent gateway/voice opcodes, identify fields, or close-code meanings.

## Order

1. **This vault** (`04 - Voice/*`, this page)
2. **This repo’s code** (`DiscordVoiceSession`, `VoiceConnectionModel`, DSharpPlus dispatch)
3. **Official docs** — `https://docs.discord.com/developers/topics/voice-connections` and opcodes table. For DAVE: [daveprotocol.com](https://daveprotocol.com) + [github.com/discord/libdave](https://github.com/discord/libdave)
4. **Userdoccers** — `https://docs.discord.food/topics/voice-connections` (client-accurate extras; mark v8 vs v9)
5. **Working library source** — `GhoSty-Music-SelfBot-v1/node_modules/@discordjs/voice` (protocol only)
6. **Web search** — last, and quote the page; do not treat 2024 training data as current (DAVE deadline is 2026-03-01)

Discord’s **product** (web / Electron / native) is **not** source-available. Do not unpack `app.asar` or copy their client. See [[Discord Client Source]].

## GhoSty folder

Path: `C:\Users\Admin\Documents\Github\GhoSty-Music-SelfBot-v1`

Use for: voice WS v8, AEAD, DAVE identify, `addServerPacket` reconnect.

Do **not** use for: tokens, selfbot features, or copying JS into C#. If `index.js` contains a token, ignore it. Never repeat or commit it.

Local GhoSty edits vs GitHub were **YouTube / play-dl**, not DAVE. DAVE lives in `@discordjs/voice` + `davey`.

## Cache new facts here

When a close code, opcode, or handshake rule is proven, write it into `04 - Voice/` the same day.

---

## Links
- [[Account Safety]]
- [[Discord Client Source]]
- [[Voice Handshake]]
- [[Close Codes]]
