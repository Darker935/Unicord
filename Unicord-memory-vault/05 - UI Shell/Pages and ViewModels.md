---
tags:
  - "#ui"
aliases:
  - Pages and ViewModels
---

# Pages and ViewModels

Classic UWP MVVM. `ViewModelBase` captures `DiscordManager.Discord` and `SynchronizationContext`.

## Shell

| Page | VM | Role |
|------|----|------|
| `MainPage` | — | Connecting overlay, root frame |
| `DiscordPage` | `DiscordPageViewModel` | Guild rail, sidebars, `VoiceModel` |
| `LoginPage` | — | Token / login UI |
| `ChannelPage` | `ChannelPageViewModel` | Message list + composer |

## Sidebars

| Page | Role |
|------|------|
| `GuildChannelListPage` | Guild channels + voice member rows |
| `DMChannelsPage` | DM list |
| `FriendsPage` | Friends |

## Overlays / settings

`OverlayService` + `SettingsService.OpenAsync`. Settings pages under `Pages/Settings/`.

## Messenger

`DiscordClientMessenger` → `WeakReferenceMessenger`. Channel list registers for `VoiceStateUpdateEventArgs` to refresh `VoiceMembers`.

---

## Links
- [[Navigation]]
- [[Channel List]]
