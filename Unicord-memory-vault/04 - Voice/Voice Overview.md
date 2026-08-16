---
tags:
  - "#voice"
  - index
aliases:
  - Voice Overview
---

# 04 — Voice Overview

In-process voice client. Not VoiceNext. Not the old `Unicord.Universal.Voice` AppService.

## Files

| File | Role |
|------|------|
| `Models/Voice/VoiceConnectionModel.cs` | Gateway join/leave, credentials, session lifecycle |
| `Models/Voice/Transport/VoiceStateUpdatePayload.cs` | Opcode 4 payload |
| `Voice/DiscordVoiceSession.cs` | Voice WS + UDP + Opus + speaking |
| `Voice/VoiceAesGcm.cs` | `aead_aes256_gcm_rtpsize` |
| `Voice/VoiceAudioEngine.cs` | AudioGraph capture/playback |
| `Voice/DaveNative.cs` | P/Invoke `unicord_dave.dll` |
| `Voice/VoiceQualityOptions.cs` | Encoder policy: mode, bitrate range, FEC, stereo ([[Audio Quality]]) |
| `Voice/native/unicord_dave/` | Rust crate (`davey 0.1.4`) |
| `Voice/native/unicord_dave.dll` | Built DLL, Content-linked to AppX root |

## Stack

1. Gateway: Voice State Update + Voice Server Update
2. `wss://{endpoint}/?v=8` — **keep the port** (see [[Voice Handshake]])
3. Hello → Identify (`session_id`, `token`, `max_dave_protocol_version: 1`)
4. Ready → UDP IP discovery (ports **big-endian**)
5. Select protocol `aead_aes256_gcm_rtpsize`
6. Session Description → secret key + `dave_protocol_version`
7. DAVE MLS via binary opcodes 25–31
8. Speaking + five silence frames (`F8 FF FE`) so Discord delivers others’ audio

## What was broken (redesign, 2026-08)

| Symptom | Cause | Commit |
|---------|-------|--------|
| Voice list empty | States keyed / members NRE; no messenger refresh | `fb39c21` + DSharpPlus cache |
| Official shows you in channel, Unicord deaf | No real WS/UDP / no silence frames | `fb39c21`, `6aca192` |
| 4017 | Identify `max_dave` 0 / missing DAVE | `5b9a3bf` |
| 4006 every connect | Endpoint **port stripped** → identify on :443 | `8f6ccf6` |
| `An error occurred during decoding: buffer too small` | Concentus Opus decode of DAVE frames aborted `_ready` | `b9ad3c0` / 2.0.5.0 |
| Robotic / laggy audio | Playback discarded quantum remainder; all SSRCs in one FIFO; 500ms queue; own SSRC looped | Canary 2.0.6.0 |
| Mute/deafen/disconnect missing | User pill overlaid the call bar; control collapsed itself; Muted used XOR | 2.0.6.0 |
| Outgoing noise / left-channel static; mute ignored | AudioGraph is float32; we read/write int16. DAVE encrypt sent plaintext when not ready. Mute was TwoWay-only. | Canary 2.0.7.0 |
| **Only bot audio audible, never real users** | RTP header **extension body** never stripped from the decrypted payload — Discord clients set it, bots do not | Canary 2.0.8.0 |
| Our mic reached others as noise | RTP timestamp only advanced on sent packets while a hard VAD gate skipped slots | Canary 2.0.8.0 |
| Incoming chopped a few times a second | Opus encode + DAVE + AES + UDP ran on the `AudioGraph` realtime thread | Canary 2.0.8.0 |
| MLS never completed | `MessageWebSocket.Control.MessageType` raced between text and binary sends | Canary 2.0.8.0 |
| DAVE version stuck | op 22 execute-transition was a no-op; no pending-transition map | Canary 2.0.8.0 |
| **Outgoing audio pure noise; kept playing after mute** | `AudioGraph.EncodingProperties` reports the 16-bit PCM we requested, but `AudioFrameOutputNode` hands out **32-bit float**. Reading float as int16 is noise *and* double the samples, so we sent 100 Opus frames/s and the far end built a backlog | Canary 2.0.9.0 |
| Incoming still cut occasionally | No Opus packet-loss concealment and no RTP sequence handling | Canary 2.0.9.0 |
| **Voice cut for tens of ms, repeatedly, both directions** | RTP paced off the AudioGraph capture callback, which delivers 48 frames/s (37 under load) instead of 50. A rate deficit drains the far end's jitter buffer however deep it is | Canary 2.0.12.0 |
| Mute/deafen not shown on voice member cards | Channel list bound plain `UserViewModel`, which has no voice state | Canary 2.0.12.0 |

---

Voice I/O is still bound by [[Account Safety]]: one human join, one identify, no retry storms, ~50 RTP/s.

## Links
- [[Account Safety]]
- [[Voice Handshake]]
- [[Audio Quality]]
- [[DAVE]]
- [[Close Codes]]
