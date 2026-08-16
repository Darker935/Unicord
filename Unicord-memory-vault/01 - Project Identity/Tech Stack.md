---
tags:
  - "#project"
  - "#csharp"
aliases:
  - Tech Stack
---

# Tech Stack

## App

| Component | Version / pin | Role |
|-----------|---------------|------|
| **C#** | LangVersion 12 | Language |
| **UWP** | Target 10.0.22621, min 10.0.16299 (manifest min 10.0.15063) | App model |
| **WinUI** | 2.7 (package dependency) | Controls |
| **CommunityToolkit.Mvvm** | WeakReferenceMessenger | Event fan-out to VMs |
| **Newtonsoft.Json** | DSharpPlus + voice payloads | JSON |
| **Concentus** | Opus encode/decode | Voice codec |
| **Windows.Networking.Sockets** | MessageWebSocket + DatagramSocket | Voice WS / UDP |
| **Windows.Media.Audio** | AudioGraph | Capture / playback |
| **Windows.ApplicationModel.Calls** | VoipCallCoordinator | VoIP reservation |

## Discord

| Component | Pin | Role |
|-----------|-----|------|
| **DSharpPlus fork** | `Libraries/DSharpPlus` | Gateway + REST |
| **API version** | 9 | HTTP + main gateway `v` query |
| **TokenType** | User | No `Bot ` prefix |
| **Voice gateway** | v8 | `wss://{endpoint}/?v=8` |
| **Transport AEAD** | `aead_aes256_gcm_rtpsize` | RTP |
| **DAVE** | protocol 1 | E2EE required 2026-03-01 |
| **davey** | 0.1.4 | Rust MLS / DAVE |
| **unicord_dave.dll** | packaged at AppX root | P/Invoke from `DaveNative.cs` |

## Build / IDE

| Tool | Role |
|------|------|
| **Visual Studio 2022+** (VS 18 MSBuild also works) | UWP workload |
| **MSBuild** | `Unicord.Universal.csproj` x64 Debug |
| **Rust / cargo** | Build `unicord_dave` when the crate changes |
| **Local PFX** | `Unicord.Universal_TemporaryKey.pfx` (gitignored) |
| **Store cert thumbprint (this machine)** | `442FA34278685B740D5D1C0279A757B71D9B1C68` |

## AI / reference (not in-repo)

| Tool | Role |
|------|------|
| **Obsidian vault** | This folder |
| **GhoSty-Music-SelfBot-v1** | Local voice *protocol* reference (`@discordjs/voice`). **Never a token store.** |
| **docs.discord.com / docs.discord.food** | Voice opcodes and close codes |

---

## Links
- [[Overview]]
- [[Building]]
- [[Discord Protocol Lookup]]
