---
tags:
  - "#ui"
aliases:
  - Discord Parity UI Spec
---

# Discord Parity UI Spec (2026-08-14)

Owner decision set for the `redesign` branch UI pass. Goal is **usability parity** with the official
client — padding, spacing, hierarchy — **not** colour parity. Mica / accent theming stays Unicord's.

Scope rule agreed with the owner: **only differences visible in the two reference screenshots**
(Unicord 2.0.11 vs Discord stable + Equicord). No hover-state work, since hover is not visible in a
screenshot.

## Metrics

| Surface | Today | Target |
|---------|-------|--------|
| Guild rail slot | 55px slot / 38px icon | 50px slot / 38px icon |
| Guild unread badge | top-right | bottom-right |
| Category header margin | `10,8,8,2` | 16px above, 4px below |
| Category name | `TitleCaseConverter` | original casing + chevron |
| Voice member avatar | 16px | 20px |
| Member list avatar | 38px | 32px |
| Message gutter | left 8 / avatar-to-text 8 / right 16 | 16 / 16 / 16 |
| Message avatar | 36px | unchanged (owner rejected 40px) |
| Body text size | default | unchanged (owner rejected larger) |
| Inline media cap | 480px | unchanged (owner rejected 400px) |

## Behaviour

- **Categories** collapse on click; collapsed state persists per guild across restarts.
- **Channel labels**: read = secondary foreground (~60%), unread = full foreground. No weight change.
- **Guild rail** left-edge marker: dot = unread, tall bar = open guild. Reuses the existing
  `TreeViewItem` selection visual (`UnicordListViewItemStyle`), not a new element.
- **Member groups**: role name left, count right-aligned and dim, hairline rule beneath. Explicitly
  *not* the `CORE — 6` inline form.
- **Replies**: elbow spine rising from the avatar column into the quoted row. Neutral grey, accent
  when the parent message is the current user's. Replaces the barely-visible `E97A` arrow.
- **Bare attachment URLs** are suppressed when the message content is only the link that produced
  its own attachment/embed.

## Voice footer

Controls were duplicated across the voice panel and the user pill. Split:

| Surface | Controls |
|---------|----------|
| Voice panel | signal indicator, two-line status, audio routing, hangup (red) |
| User pill | mic, deafen, settings |

- Status is two lines: existing `ConnectionStatus` string on top (text unchanged), channel / guild
  below in dim smaller type.
- Signal bars are driven by `VoiceConnectionModel.UdpPing`: `<100ms` green, `<250ms` yellow,
  `>250ms` red, no data grey. **Colour applies to the bars icon only**, not the status text.
- Both converters must be written — `VoicePingGlyphConverter` and `VoicePingConverter` are
  referenced by the dead root-level control but **do not exist as classes anywhere in the repo**.
- Panel becomes a rounded card, keeping the existing Mica treatment.
- Mute / deafen keep the plain toggle look; owner rejected Discord's red-slash treatment.

## Live control vs dead copy

`Controls/Voice/VoiceConnectionControl.xaml` is the real one — it is in the csproj and is what
`DiscordPage.xaml` instantiates. `Controls/VoiceConnectionControl.xaml` is **not compiled** and is a
stale duplicate. Delete it; edits landing there are invisible at runtime.

## Untouched by owner decision

Channel header (no inline search box), composer, media cap, avatar and body text sizes, relative
timestamp format, member panel top bar (title + close + search stay).

## Beyond layout — shipped 2026-08-14

**Voice channel status and call start time.** Both are **ephemeral**: `status` and
`voice_start_time` (unix seconds) never arrive on the channel object. They are read with gateway
command **op 43** Request Channel Info (`{ guild_id, fields: ["status","voice_start_time"] }`),
answered by `CHANNEL_INFO` = `{ guild_id, channels: [ { id, status?, voice_start_time? } ] }`, and
changes are pushed afterwards as `VOICE_CHANNEL_STATUS_UPDATE` and
`VOICE_CHANNEL_START_TIME_UPDATE` (`{ id, guild_id, voice_start_time? }`).

> **Opcode:** docs.discord.food says 39; the official docs PR
> [#6400](https://github.com/discord/discord-api-docs/pull/6400) says **43**, which is what we send.
> Check this first if channel info never arrives. Whether Discord answers op 43 for a *user* token
> is still unverified — `OnChannelInfoAsync` logs a warning on an unexpected shape.

Op 43 is the only outbound traffic this UI work added: once per guild, when its channel list is
first shown, only if the guild has voice channels, reset per gateway connection — the same shape as
the existing op 14 sync. Nothing polls. Setting a status (`PUT /channels/{id}/voice-status`) is
deliberately **not** implemented. See [[Account Safety]].

**Call timer.** `ChannelListViewModel.CallDuration` prefers `Channel.VoiceStartTime`, falling back to
when this session first saw somebody in the channel, which reads short for a call that predates
launch. One `DispatcherTimer` on `GuildChannelListViewModel`, running only while a voice channel has
members.

**Emoji.** Unicode and custom emoji are built by one function, `MarkdownRenderer.CreateEmojiInline`,
as an `EmojiControl` in an identical box. They used to be a text `Run` and an `InlineUIContainer`
image respectively — two layout engines, which no amount of metric matching reconciled. Do not
reintroduce a second path.

> A literal emoji character does **not** reach `RenderEmoji`. `EmojiInline` only trips on `:`, so
> `:shortcode:` is the only thing it parses, and Discord stores what was typed. Literal emoji are
> pulled out of text runs by `MarkdownRenderer.SplitEmoji`, matched longest-first against
> `DiscordEmoji.DiscordNameLookup`. If emoji ever look inconsistent again, check that first.

Box geometry is constants on `EmojiControl`, taken from `seguiemj.ttf` itself: every emoji glyph has
a 2812/2048 em advance and fills a **2162/2048 em square** sitting **359/2048 em below the baseline**,
in a line box 2210 up and 514 down. Segoe UI states the same 2210/514, so the text descent is a
constant too.

> **The glyph must sit on a `Canvas`.** A `TextBlock` draws its text inside its arranged rectangle
> and clips the rest, and a glyph's line box is always bigger than the square — wider by its side
> bearings, taller by its leading. Any fixed-size parent clamps that rectangle and cuts the artwork
> at draw time, which no alignment, margin or transform can undo; they only move whichever edge
> survives. A `Canvas` measures children unconstrained. Position by the bearings.
>
> A `Viewbox` also avoids the clip, by scaling the line box instead — but that draws the artwork at
> 79% of the square an image fills, which is the bug it was replaced for.

`EmojiControl.Size` is the side of that square, so reactions, the picker and the channel list get
the same parity for free.

**Huge emoji** scale `MarkdownRenderer.EmojiSize`, never `FontSize`. Scaling the block scaled the
spaces between the emoji, which is what put wide gaps between them. Discord scales only the emoji.

Inline emoji stay at 1.0557 em. Discord uses 1.375 em; raising ours is a separate decision, not a
bug.

**Speaking ring.** `DiscordVoiceSession` tracks talkers from packets that already arrive: op 5 marks
the edges, arriving RTP refreshes a 350ms hangover, and the Opus silence frame counts as not talking.
op 5's stop event alone is unreliable, which is why the hangover exists. State reaches the rows through
`VoiceSpeakingTracker`, which also holds a snapshot because voice member rows are rebuilt from scratch
on every voice state change.

## Deferred — needs wire work, not XAML

| Item | Blocker |
|------|---------|
| Offline member group | needs op 14 member-list subscription; op 8 is bot-only |
| Guild tags / fonts / decorations | no `primary_guild`, `collectibles`, `avatar_decoration` in the fork |

---

## Links
- [[Channel List]]
- [[Navigation]]
- [[Pages and ViewModels]]
