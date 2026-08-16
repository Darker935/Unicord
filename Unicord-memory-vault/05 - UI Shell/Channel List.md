---
tags:
  - "#ui"
aliases:
  - Channel List
---

# Channel List (voice members)

`ChannelListViewModel` for voice/stage channels:

- `VoiceMembers` bound from `Channel.Users` / guild voice states
- Registers `VoiceStateUpdateEventArgs` on the messenger
- Template: `ChannelListVoiceTemplate` in `Themes/Templates.xaml`

DSharpPlus must keep `DiscordChannel.Users` sourced from **voice states by channel id**. Object-identity maps (`Channel` instance as key) miss updates.

---

## Links
- [[DSharpPlus Overview]]
- [[Voice Overview]]
