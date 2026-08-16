---
tags:
  - "#ui"
aliases:
  - Navigation
---

# Navigation

Owner: `DiscordNavigationService` (`Services/DiscordNavigationService.cs`).

## Text vs voice

- Text / forum / threads: `MainFrame.Navigate` to `ChannelPage` / `ForumChannelPage` / `AgeGatePage`.
- Voice / stage: **does not** change `CurrentChannel` the same way. Creates `VoiceConnectionModel` and `ConnectAsync`. Errors show `UIUtilities.ShowErrorDialogAsync("Failed to connect to voice!", ex.Message)`.

## Guild list

`GuildChannelListPage.channelsList_SelectionChanged`:

- Non-text types restore the previous selection (voice rows do not stay highlighted).
- Voice still calls `NavigateAsync`.

Do not “fix” voice by navigating to `ChannelPage` for a voice channel.

## VoiceModel lifetime

`DiscordPageViewModel.VoiceModel` holds the active call. A new voice navigate disconnects the previous model first. `Disconnected` clears `VoiceModel`.

## Call controls

- `Controls/Voice/VoiceConnectionControl` sits in the channel-list column, **above** the floating user pill (`Padding 0,0,0,68` so the pill does not cover it).
- Mute / deafen / disconnect are also on the user pill while `VoiceModel` is set.
- `Muted` / `Deafened` are real flags, not XOR toggles (TwoWay `IsChecked` needs assign).

---

## Links
- [[Pages and ViewModels]]
- [[Channel List]]
- [[Voice Handshake]]
