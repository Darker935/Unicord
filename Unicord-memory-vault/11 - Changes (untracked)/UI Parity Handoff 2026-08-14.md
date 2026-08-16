---
tags:
  - "#handoff"
  - "#ui"
aliases:
  - UI Parity Handoff
---

# Handoff — UI parity work in progress (2026-08-14)

State at the moment the chat was compacted. Full reasoning in
[[ui-parity-round-two-2026-08-14]] and [[Discord Parity UI Spec]].

## Where things stand

Canary manifest is at **2.0.36.0**, which is also the last package built and signed. Check
`Package.appxmanifest` and `Unicord.Universal/AppPackages/` before the next build — the owner bumps
the version too, so any remembered number is stale.

## Committed

| Commit | What |
|--------|------|
| `2dff862` | feat(ui): shell spacing and unread signals |
| `f0f85f5` | feat(voice): call folded into the user pill, real latency indicator |
| `2bafa5a` | fix(messages): reply ping toggle |
| `4adef01` | docs(vault): parity spec + first AI log |
| `61b83fd` | feat(voice): channel status + call duration |
| `86ba9d4` | feat(voice): speaking ring |
| `4efebe9` | docs(vault): limits of the three features |
| `0b44bce7` | submodule: voice channel status field + dispatch |

## Uncommitted — two rounds of work in the tree

**Unicord.Universal**
- `Controls/Markdown/Render/MarkdownRenderer.Inlines.cs` — unified emoji path
  (`CreateEmojiInline`, `GetEmojiMetrics`)
- `Models/Emoji/EmojiViewModel.cs` — unicode ctor, CDN `size` parameter
- `Themes/Controls/Emoji.xaml` — square box, centred image
- `Models/User/UserViewModel.cs` — `AvatarUri`
- `Models/Voice/VoiceMemberViewModel.cs`, `VoiceSpeakingTracker.cs` — `VoiceSessionTracker`,
  `IsOnAnotherClient`, dimming
- `Models/Channels/ChannelListViewModel.cs`, `Models/Guild/GuildChannelListViewModel.cs` —
  `voice_start_time`, op 43 request gate
- `Models/Messaging/DiscordClientMessenger.cs` — start-time event forwarding
- `Pages/DiscordPage.xaml` + `.cs` — `UserPanel_SizeChanged` bottom reservation, avatar binding
- `Pages/Subpages/GuildChannelListPage.xaml` + `.cs` — scroll-preserving selection restore,
  padding 68 → 4
- `Pages/Subpages/DMChannelsPage.xaml` — padding 68 → 4
- `Themes/Controls/Messages.xaml` + `.cs` — hover layer, markdown left-aligned, flyout
- `Themes/Templates.xaml` — voice row avatar, status line, duration, speaking ring, dimming

**Libraries/DSharpPlus** (submodule, detached HEAD at `0b44bce7`)
- `GatewayOpCode.cs` — `RequestChannelInfo = 43`
- `DiscordChannel.cs` — `VoiceStartTime`
- `DiscordClient.cs` — `RequestChannelInfoAsync`
- `DiscordClient.Events.cs` / `.Dispatch.cs` — `VoiceChannelStartTimeUpdated`, `CHANNEL_INFO`
- new `EventArgs/Channel/VoiceChannelStartTimeUpdateEventArgs.cs`

**Not ours, do not absorb:** `Unicord.Universal/Voice/VoiceAudioEngine.cs` (dirty from other work),
`discord-fix-conversation.md` (untracked, owner's).

> The submodule commits sit on a **detached HEAD** and are not on `unicord-current`. They are
> GC-eligible. Offer to fast-forward that branch.

## Open / unverified

1. **Emoji parity** — root cause found and fixed in 2.0.36.0. The earlier "unification" only covered
   `:shortcode:` emoji, which real messages never contain; literal emoji were still plain `Run`s.
   Full account in [[emoji-one-path-2026-08-14]]. Unverified by the owner.
2. **Op 43 answered for user tokens?** Unknown. If the call timer reads short, read the log for the
   `CHANNEL_INFO` warning before touching code.
3. **Sidebar bottom reservation** — new `SizeChanged` behaviour, unverified by the owner.
4. **Reactions, emoji picker and channel-list emoji** all share `EmojiControl` and all had the same
   79% glyph mismatch. Unicode emoji in those places should now look slightly larger, matching the
   custom emoji beside them. Unverified.

## Still deferred

| Item | Blocker |
|------|---------|
| Offline members group | needs op 14 lazy member-list subscription with ranges; op 8 is bot-only |
| Guild tags, name fonts, avatar decorations | `primary_guild`, `collectibles`, `avatar_decoration` are not deserialised in the fork |

## Working agreement the owner set

- Community sources (docs.discord.food, discord.js-selfbot, Equicord) are leads, **not proof**.
  Confirm against official docs or a real payload. The op 39 vs 43 conflict is the example.
- When something is still wrong after a couple of attempts, stop tuning constants and go read the
  code that produces it. The emoji fix only landed once both kinds shared one path.
