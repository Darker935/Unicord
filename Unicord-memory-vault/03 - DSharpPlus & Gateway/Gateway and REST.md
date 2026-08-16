---
tags:
  - "#dsharpplus"
aliases:
  - Gateway and REST
---

# Gateway and REST

## Versions

- REST base: `https://discord.com/api/v9`
- Main gateway query includes `v=9` (`DiscordClient.WebSocket.cs` + `Endpoints.API_VERSION`)
- Voice gateway is a **different** socket: v8 (see [[Voice Handshake]])

## Events Unicord cares about for voice

| Dispatch | Handler | Used for |
|----------|---------|----------|
| `VOICE_STATE_UPDATE` | `OnVoiceStateUpdateEventAsync` | session_id, channel membership, member list |
| `VOICE_SERVER_UPDATE` | `OnVoiceServerUpdateEventAsync` | endpoint + token |

`VoiceConnectionModel` must **stay subscribed** for the life of the call. Discord can send a second `VOICE_SERVER_UPDATE` with a new token. First-packet-then-unsubscribe is a 4006.

## Sending voice state (join/leave)

Unicord sends gateway opcode 4 via `DiscordClient.SendPayloadAsync(GatewayOpCode.VoiceStateUpdate, payload)` with `VoiceStateUpdatePayload`:

- `guild_id`, `channel_id` (null = leave)
- `self_mute`, `self_deaf`

## Cache notes

- Guild voice states live in `DiscordGuild._voiceStates` keyed by user id
- A user is in a channel when `DiscordVoiceState.ChannelId` is non-null / non-zero
- UI member lists must read **ids**, not `DiscordChannel` reference equality

---

## Links
- [[DSharpPlus Overview]]
- [[Voice Handshake]]
