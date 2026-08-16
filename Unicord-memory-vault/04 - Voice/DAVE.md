---
tags:
  - "#voice"
  - "#dave"
aliases:
  - DAVE
---

# DAVE (Discord Audio/Video E2EE)

Mandatory for voice/video since **2026-03-01**. Identify with `max_dave_protocol_version: 0` (or omit) → voice WS **4017 E2EE/DAVE protocol required**.

## Implementation

- Rust crate `unicord_dave` depends on `davey = "0.1.4"`
- Built `cdylib` copied to `Voice/native/unicord_dave.dll`
- `Unicord.Universal.csproj` Content + `<Link>unicord_dave.dll</Link>` so DllImport finds it at AppX **root**
- C# wrapper: `DaveNative`

If the DLL is packaged under `Voice/native/` only, `DllImport("unicord_dave.dll")` fails. Package root is required.

## Opcodes (voice WS)

| Op | Format | Direction | Role |
|----|--------|-----------|------|
| 21 | JSON | recv | Prepare transition |
| 22 | JSON | recv | Execute transition |
| 23 | JSON | send | Transition ready |
| 24 | JSON | recv | Prepare epoch (`epoch == 1` → new MLS group) |
| 25 | binary | recv | External sender |
| 26 | binary | send | Key package |
| 27 | binary | recv | Proposals |
| 28 | binary | send | Commit + welcome |
| 29 | JSON/bin | recv | Announce commit |
| 30 | binary | recv | Welcome |
| 31 | JSON | send | Invalid commit/welcome |

Server→client binary frames: 2-byte BE sequence + 1-byte opcode + payload. Client→server binary: opcode + payload (no sequence).

## Session Description

`dave_protocol_version` > 0 → `InitializeDave` → send key package (26).

External sender (25) may arrive before session description; Unicord stashes it in `_pendingExternalSender`.

---

Official write-up: [daveprotocol.com](https://daveprotocol.com) and [github.com/discord/libdave](https://github.com/discord/libdave). Discord’s app itself is not source-available — [[Discord Client Source]].

## Links
- [[Voice Overview]]
- [[Discord Client Source]]
- [[Close Codes]]
