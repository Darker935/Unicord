---
tags:
  - "#voice"
aliases:
  - Voice Handshake
---

# Voice Handshake

## Official sequence

1. Gateway opcode 4 Voice State Update (`guild_id`, `channel_id`, `self_mute`, `self_deaf`)
2. Wait for **both** `VOICE_STATE_UPDATE` (session_id) and `VOICE_SERVER_UPDATE` (endpoint, token)
3. Open `wss://{endpoint}/?v=8` — endpoint may be `host:port`
4. Hello (op 8) → Identify (op 0)
5. Ready (op 2) → UDP discover → Select Protocol (op 1)
6. Session Description (op 4)

Working clients (`@discordjs/voice`, discord.py after 2025-06): **keep the endpoint string including port** and **reconfigure on every Voice Server Update**.

## Identify payload (Unicord)

```json
{
  "op": 0,
  "d": {
    "server_id": "<guild id string>",
    "user_id": "<user id string>",
    "session_id": "<from VOICE_STATE_UPDATE>",
    "token": "<from VOICE_SERVER_UPDATE>",
    "max_dave_protocol_version": 1
  }
}
```

Do not send extra `video: false` (harmless if default, omitted to match discord.js).

`channel_id` on identify is required only on **voice gateway v9**. Unicord is on **v8**.

## The 4006 bug (fixed 8f6ccf6)

Unicord used to strip `:port` and connect to `wss://host/?v=8` (implicit 443). Discord no longer always serves voice WS on 443. Identifying on the wrong host/port closes **4006 Session is no longer valid**.

discord.py PR 10210 is the same root cause: stop `rpartition(':')` on the endpoint.

## Current Unicord rules (`VoiceConnectionModel`)

1. If the current user already has a voice state in this guild, **leave and wait** (up to 2s) before join.
2. Subscribe to VoiceState + VoiceServer for the **life of the model**.
3. Require non-empty `SessionId` + endpoint + token.
4. Use the **latest** token/endpoint; increment a credential generation when they change.
5. If credentials change after handshake, tear down and `DiscordVoiceSession` again.
6. On connect failure: force leave, then throw (so retries are not racing a late leave).

`DiscordNavigationService` still disconnects any existing `VoiceModel` before a new connect. Disconnect now waits for leave.

## Reconnect discipline (the 4006 / 4014 storm)

A single call once produced **9 websocket connects, 12 Voice Server Updates, 4x 4006 and
4x 4014**. It was a self-sustaining loop, not bad luck:

1. Discord sends several `VOICE_SERVER_UPDATE`s; a rotated token counts as changed.
2. `ReconfigureNetworkingAsync` was fire-and-forget with no gate, so two updates opened two
   voice websockets against one session.
3. Discord killed one with **4006**.
4. The failure path did force-leave then rejoin, which made Discord send another
   `VOICE_SERVER_UPDATE` — back to step 1.

The reference client (`@discordjs/voice` `addServerPacket`) also reconfigures on every server
update, but it is single threaded and cannot race itself. Rules here:

- **One attempt at a time.** All connect work is behind a single gate. If an attempt is already
  in flight a new update is *skipped*, not queued: the running attempt reads the newest endpoint
  and token when it builds its session.
- **4014 is terminal.** Channel deleted, kicked, moved, or the main gateway session dropped.
  `onNetworkingClose` in the reference explicitly does not reconnect on 4014, and neither do we.
- **Never re-identify on 4006 with the same pair** — needs a newer Voice Server Update.
- **A click on the channel you are already in is a no-op.** Rebuilding the model tore down a
  half-finished join and started a second one, which is a duplicate identify.

This is also an [[Account Safety]] requirement, not only a bug fix: parallel identifies and
rejoin loops are precisely the signalling pattern to avoid.

## Media must not fail the handshake

`ConnectAsync` waits on `_ready`, which `OnWsMessage` / `OnUdpMessage` used to complete with **any** exception.

After session description, `_aes` is set and `StartAsync` yields. Other users’ RTP is **DAVE-encrypted Opus**. Concentus `OpusDecoder.Decode` throws `An error occurred during decoding: buffer too small`. That used to `_ready.TrySetException` and show the connect dialog.

Rules:

- After UDP discovery, UDP exceptions only log.
- Do not Opus-decode DAVE ciphertext. Decrypt first; skip if DAVE is not ready or decrypt fails.
- SFU silence `F8 FF FE` is allowed through without DAVE.

## RTP receive layout (`_rtpsize`)

The AEAD `..._rtpsize` modes authenticate **only** the fixed 12-byte RTP header plus, when the `X`
bit is set, the 4-byte `0xBEDE` profile + length word. Everything after that is ciphertext.

After decryption the plaintext is **not** yet Opus:

1. if `packet[0] & 0x20` (padding), drop the last `plaintext[^1]` bytes
2. if `X` is set and `packet[12..13] == 0xBE 0xDE`, drop the leading `4 * ((packet[14] << 8) | packet[15])` bytes — this is the extension **body**
3. only then DAVE-decrypt, then Opus-decode

Discord's own clients set that extension; most bots do not. Skipping step 2 therefore looks exactly
like "I can hear bots but no people". Reference: `@discordjs/voice` `VoiceReceiver.parsePacket`.

## RTP transmit cadence

The RTP timestamp is a **clock**, not a counter of sent packets. Every 20 ms slot advances it by 960
whether or not a packet goes out, otherwise remote clients splice non-adjacent speech together and
hear noise. `@discordjs/voice` sends `F8 FF FE` for idle slots and always does
`timestamp += TIMESTAMP_INC`.

Unicord derives the timestamp from a monotonic frame index, keeps a 5-frame silence tail after
speech, and uses attack/release hysteresis + a 500 ms hangover so the VAD gates content only.

Encoding, DAVE and UDP must not run on the `AudioGraph` `QuantumStarted` thread; they starve the
playback quantum. `DiscordVoiceSession` queues captured PCM to a sender loop instead.

`MessageWebSocket` carries one `Control.MessageType` for the whole socket, so text and binary sends
are serialised through a single semaphore. Racing it sends MLS key packages as text frames, which
Discord silently drops.

## IP discovery endianness

Discord: all numeric fields **big-endian**. `@discordjs/voice` `parseLocalPacket` uses `readUInt16BE`. Unicord now reads the discovered port as big-endian. Little-endian here selects the wrong UDP port (listen/talk fail after a “successful” connect).

---

## Links
- [[Close Codes]]
- [[DAVE]]
- [[Discord Protocol Lookup]]
