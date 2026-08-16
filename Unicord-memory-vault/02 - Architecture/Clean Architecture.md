---
tags:
  - "#architecture"
  - "#convention"
aliases:
  - Clean Architecture
---

# Clean Architecture (as this repo actually is)

Unicord is a UWP MVVM app, not a DDD template. Do **not** invent Domain/Application folders. Keep the existing split:

```
Pages (XAML) → ViewModels → Services → DSharpPlus / OS APIs
```

## Layers in this tree

| Layer | Lives in | May depend on | Must not own |
|-------|----------|---------------|--------------|
| Presentation | `Pages/`, `Controls/`, `Themes/` | ViewModels, converters | Gateway payloads, crypto |
| ViewModels | `Models/` | DSharpPlus entities, Services | Raw voice sockets |
| Services | `Services/` | DiscordClient, WinRT | XAML trees |
| Library | `Libraries/DSharpPlus/` | Discord wire format | Unicord UI |
| Voice transport | `Voice/` | WinRT sockets, Concentus, DAVE | Navigation |

## Hard boundaries

- **Voice transport** stays in `Unicord.Universal/Voice/` + `Models/Voice/`. Pages only bind `VoiceConnectionModel`.
- **Gateway Voice State Update** is sent only through `VoiceConnectionModel.SendVoiceStateUpdateAsync` (or an equivalent single owner). Do not sprinkle opcode 4 from random pages.
- **DSharpPlus fork changes** are for library truth (entities, dispatch). Unicord-specific UX stays in Unicord.Universal.
- **Canary vs Store packaging** are different identities. Deploy experiments to Canary.

## JNI / native equivalent

The only native ABI in-tree is `DaveNative` → `unicord_dave.dll`. Keep P/Invoke in that one file. Rust crate is `Voice/native/unicord_dave`.

---

## Links
- [[Architecture Overview]]
- [[CSharp Conventions]]
