---
tags:
  - "#dsharpplus"
  - index
aliases:
  - DSharpPlus Overview
---

# 03 — DSharpPlus Overview

Unicord vendors a **fork** at `Libraries/DSharpPlus/` (git submodule). It is not stock nuget DSharpPlus.

## Why the fork exists

- `TokenType.User` (no `Bot ` prefix)
- Discord **API v9**
- User-client events Unicord needs (read states, relationships, captcha, token update)
- Voice-state cache used by the channel list

Recent local library fixes (redesign branch work):

- Public `ChannelId` on voice state
- Null-safe `Member`
- `Channel.Users` from `VoiceStates`
- Skip `TransportMember` NRE
- Store voice states by `ChannelId`, not `Channel` object identity

## Key types

| Type | Path | Role |
|------|------|------|
| `DiscordClient` | `DSharpPlus/Clients/` | Gateway + REST |
| `DiscordConfiguration` | `DSharpPlus/DiscordConfiguration.cs` | Token, logger, reconnect |
| `Endpoints.API_VERSION` | `DSharpPlus/Net/Rest/Endpoints.cs` | `"9"` |
| `DiscordVoiceState` | `Entities/Voice/DiscordVoiceState.cs` | `channel_id`, `session_id` (session id is internal; event args expose it) |
| `VoiceStateUpdateEventArgs` | `EventArgs/Voice/` | `SessionId`, `Before`, `After` |
| `VoiceServerUpdateEventArgs` | `EventArgs/Voice/` | `Endpoint`, `VoiceToken`, `Guild` |

## Unicord construction

```csharp
new DiscordClient(new DiscordConfiguration()
{
    Token = token,
    TokenType = TokenType.User,
    LoggerFactory = Logger.LoggerFactory,
    ReconnectIndefinitely = true
});
```

`DiscordClientMessenger.Register` hooks client events and republishes through `WeakReferenceMessenger`.

## VoiceNext vs Unicord voice

`DSharpPlus.VoiceNext` is the old bot voice stack (WS v4-era identify, first-packet TCS). **Unicord does not use VoiceNext for the in-app call.** Do not “fix voice” by calling `channel.ConnectAsync()` from VoiceNext.

---

## Links
- [[Gateway and REST]]
- [[Token and Login]]
- [[Voice Handshake]]
