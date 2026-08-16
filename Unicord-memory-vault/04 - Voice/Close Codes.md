---
tags:
  - "#voice"
aliases:
  - Close Codes
---

# Voice WebSocket close codes

Source: Discord developer docs + docs.discord.food (voice).

| Code | Meaning | Typical Unicord cause |
|------|---------|------------------------|
| 4003 | Not authenticated | Payload before Identify |
| 4004 | Authentication failed | Wrong token |
| 4005 | Already authenticated | Identify twice |
| **4006** | **Session no longer valid** | **Wrong WS port (stripped endpoint), stale token after second Voice Server Update, or leave/join race** |
| 4009 | Session timeout | Idle / missed heartbeats |
| 4014 | Disconnected | Kicked / main gateway drop — do not resume |
| 4015 | Voice server crashed | Resume ok |
| 4016 | Unknown encryption mode | Not offering a live mode |
| **4017** | **E2EE required** | Missing / zero `max_dave_protocol_version` |
| 4022 | Disconnected (all clients) | Channel deleted / call ended |

4004 = credentials were never valid. 4006 = session existed then died **or** identify hit a gateway that does not own that session (wrong port).

Do not “retry identify” on 4006 with the same dead pair. Need a new join or a newer Voice Server Update.

---

## Links
- [[Voice Handshake]]
