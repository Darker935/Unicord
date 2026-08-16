---
tags:
  - "#features"
  - index
aliases:
  - Feature Index
---

# 06 — Feature Index

| Feature | Status | Notes |
|---------|--------|-------|
| Login / user token | Works | PasswordVault; Hello optional |
| Guild + DM navigation | Works | `DiscordNavigationService` |
| Messaging | Works | ChannelPage + MessageViewModels |
| Voice member list | Implemented (`fb39c21`) | Re-verify after 4006 fix |
| Voice connect / listen / talk | Handshake fixed `8f6ccf6`; **hear/speak not user-verified yet** | See [[Voice]] |
| DAVE E2EE | Implemented (`5b9a3bf`) | DLL at package root |
| Notifications / toasts | Exists | Shared + background tasks |
| Themes | Exists | `ThemeService` + roaming |
| Settings overlay | Exists | Accounts, messaging, voice devices, security |
| Windows 10 Mobile | Compatibility goal | Avoid APIs newer than 1703 when possible |

---

## Links
- [[Messaging]]
- [[Voice]]
- [[Notifications]]
