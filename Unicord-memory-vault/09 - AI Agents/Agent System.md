---
tags:
  - "#system"
  - "#ai"
  - index
aliases:
  - AI Agents
---

# 09 — AI Agent System

Unicord does **not** use Azryl’s orchestrator / Java / C++ / Minecraft subagents.

## Session start

1. `user-working-style`
2. `unicord-session-rules` (when cwd is this repo)
3. Vault [[99 - Index]]
4. **[[Account Safety]]** — mandatory before any REST / gateway / voice / login work
5. Task-relevant vault pages only

## Where project truth lives

| Kind | Place |
|------|--------|
| Architecture, protocol, deploy pins | This vault |
| How the user works | `~/.grok/skills/user-working-style` |
| Unicord operating rules | `~/.grok/skills/unicord-session-rules` |
| Compact / new thread | `session-compact` / `project-handoff` |

## After significant work

Update the relevant vault note (especially [[Voice Overview]], [[Feature Index]], [[AI-logs]]). Do not leave the only copy of a root cause in chat.

## Tools

- Repo read/grep/edit
- MSBuild + `Add-AppxPackage` for Canary
- Official Discord docs / discord.food / local `@discordjs/voice` for protocol
- Browser verification only applies to web UIs — this is UWP; verify by install + user test

---

## Links
- [[Account Safety]]
- [[Discord Protocol Lookup]]
- [[Quality Gates]]
