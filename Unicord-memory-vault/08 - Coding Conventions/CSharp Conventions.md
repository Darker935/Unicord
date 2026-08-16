---
tags:
  - "#csharp"
  - "#convention"
aliases:
  - CSharp Conventions
  - C# Conventions
---

# C# / XAML Conventions

Match **this repo**, not AzrylClient Java/C++ rules.

## Naming

- Types: `PascalCase`
- Methods / properties: `PascalCase`
- Fields: `_camelCase` (existing VM / session style)
- Namespaces: `Unicord.Universal.*`

## Comments

Unicord already has comments. Do **not** impose Azryl’s “zero comments” rule here.

- Short comments for non-obvious protocol constraints are OK
- Do not narrate implementation steps
- Do not leave “TODO later” placeholders for unrelated work

## Style

- Async: `Async` suffix; `ConfigureAwait(false)` on voice/library paths that already do
- WinRT: prefer existing `Windows.*` types; watch min-version (1703 / Phone)
- JSON: Newtonsoft attributes like surrounding DSharpPlus / voice payloads
- Do not add new abstractions when a local method matches the file

## English

Code, logs, commits, vault: English. Exception strings shown to the user may stay as they are in `.resw`.

---

## Links
- [[Quality Gates]]
