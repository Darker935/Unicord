---
tags:
  - "#convention"
aliases:
  - Quality Gates
---

# Quality Gates

## No band-aids (mandatory)

1. Trace the full call chain (UI → VM → gateway / voice WS).
2. Prove the root cause (payload, close code, code path, working client).
3. Fix the origin.
4. No silent null fallbacks that hide missing protocol fields.
5. No `if (special) return` in three files for one design bug.

## Commits

Conventional: `fix(voice):`, `feat(...)`, `docs(...)`, `chore(...)`.

English only. No emojis. Stage only task-owned files.

## Account safety (MANDATORY — never skip)

Unicord is a **human-driven** client. Full research and hard rules: [[Account Safety]].

Before any REST, gateway, voice WS, UDP, or DAVE change:

1. Name the user gesture that causes the request.
2. Confirm official desktop would send the same opcode/route for that gesture.
3. No tight loops, no extra IDENTIFY, no 4006 re-identify, no RTP/speaking floods.
4. Honor 429. Prefer RESUME over a fresh IDENTIFY.
5. No self-bot features (auto-send, auto-join, scrape, mass DM, token tools).

If a fix “needs” more Discord chatter or a second connection: **stop and ask**.

## Discord protocol

Never invent opcodes, close codes, or identify fields. Follow [[Discord Protocol Lookup]].

## Packaging

- Voice/DLL experiments → Canary
- `unicord_dave.dll` must be at AppX root
- Bump Canary version when contents change

## Secrets

No tokens, no PFX passwords in git or vault.

---

## Links
- [[CSharp Conventions]]
- [[Account Safety]]
- [[Building]]
