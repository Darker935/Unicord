---
tags:
  - "#voice"
aliases:
  - Audio Quality
---

# Audio Quality

Encoder policy lives in `Voice/VoiceQualityOptions.cs`; it is built once per connect from
`App.LocalSettings` plus `DiscordChannel.Bitrate`, so the sender loop never reads settings.

## Modes

| Mode | Opus application | Signal | DTX | Inband FEC | Channels | Gate |
|------|------------------|--------|-----|------------|----------|------|
| `Voice` (default) | `OPUS_APPLICATION_VOIP` | `OPUS_SIGNAL_VOICE` | on | on (setting) | per `StereoPreference` | VAD + hangover |
| `Music` | `OPUS_APPLICATION_AUDIO` | `OPUS_SIGNAL_MUSIC` | off | off | **forced stereo** | none, continuous |

**Stereo is real and stays real.** Opus carries the channel count in its own bitstream and the
voice server just forwards the payload, so a stereo Opus frame decodes as stereo on every client —
this is how music bots deliver stereo today. Two caveats:

- Opus will collapse L/R into mid-side mono when it decides bits are tight. `ForceChannels = 2`
  prevents that.
- `OPUS_APPLICATION_VOIP` biases toward SILK, which is mono-ish at low bitrate. True stereo
  fidelity needs `OPUS_APPLICATION_AUDIO`, hence the separate mode.

## Stereo preference (Voice mode)

`StereoPreference` — a preference, not a switch between mono and stereo:

| Setting | Behaviour |
|---------|-----------|
| `Auto` (default) | `ForceChannels` is never assigned. Opus decides per frame. |
| `Prefer` | Locked stereo above `StereoLockBitrate` (64 kbps), released below `StereoReleaseBitrate` (48 kbps), re-locked when the bitrate recovers. Hysteresis stops it flapping. |
| `Always` | Locked at any bitrate. |

Music mode ignores this and is always locked stereo.

`ApplyStereoLock` restores `_defaultForceChannels`, captured from the fresh encoder, rather than
hardcoding `OpusConstants.OPUS_AUTO` — the release value is then correct by construction.

The setting key is `VoiceStereoMode`. `VoicePreserveStereo` from 2.0.10.0 held a bool and is dead;
do not reuse it or `LocalSettings.Read` will type-mismatch.

**Inband FEC only exists in SILK.** Music mode runs CELT, so requesting FEC there would only cost
bitrate. It is disabled in that mode on purpose, not by omission.

## Bitrate

`ResolveInitialBitrate()` = `clamp(channel bitrate, min, max)`, except Music mode which starts at
the ceiling. `DiscordChannel.Bitrate` is the channel's own value, so boosted guilds get their
higher quality without any extra setting.

Adaptive backoff (`UpdateAdaptiveQuality`, every 2 s, sender loop only):

- loss >= 5% → bitrate * 0.75
- loss <= 1% → bitrate * 1.125
- always clamped to the user's min/max
- `PacketLossPercent` is fed to Opus so it sizes FEC redundancy

**Loss is measured inbound**, from RTP sequence gaps, because the voice gateway sends no RTCP
receiver reports. It is a proxy for the outbound path, not a measurement of it.

## Transmit pacing (the periodic cut)

**The RTP packet rate must never be slaved to the AudioGraph capture callback.** Measured on real
hardware that callback delivers ~48 frames/s, and under UI load as low as 37/s, against a required
50/s. A rate deficit drains the far end's jitter buffer no matter how deep it is, which is what the
repeated "voice cuts for a few milliseconds" was. See [[voice-pacing-2026-08-14]].

Wall clock is the authority: `ExpectedFrames = _clockBaseFrame + elapsedMs / 20`. When the capture
stream falls behind, `FillClockGapAsync` repeats the previous Opus frame to keep the stream at 50/s.
While idle the clock is realigned rather than caught up.

Verify with the pacing log line — `capture` may sit at 48, but `sent` must read 50:

```text
Voice pacing capture=48/s sent=50/s repeats=2 (target 50/s)
```

## Playback queue (why every speaker glitched at the same instant)

`AudioFrameInputNode` keeps its own queue. Adding exactly `args.RequiredSamples` each callback
drains that queue to nothing every quantum, so **one missed graph callback silences every source
simultaneously**. Independent network jitter cannot do that; simultaneous glitching across two
speakers is the signature of a shared local starve.

The same graph loses roughly 4% of quanta (measured on the capture side: 48 of 50), so without a
cushion that is a shared dropout several times a second.

`OnPlaybackQuantumStarted` now tops the node up to `PlaybackQueueTargetQuanta` (4 quanta, ~40 ms)
using `sender.QueuedSampleCount`, instead of handing over exactly one quantum. A missed callback is
then absorbed by the node's own queue. All mixer buffers are preallocated and reused —
three arrays plus an `AudioFrame` per 10 ms quantum was itself a cause of missed callbacks.

Verify with:

```text
Voice playback quanta=100/s queued=1920 underruns=0
```

`quanta` at ~100/s and `queued` well above zero means the cushion is holding.

## Latency

Three separate things used to ratchet latency and never give it back:

1. Capture queue trimmed only at its cap, so any stall pinned ~160 ms permanently. The sender loop
   now drains fully and skips frames while more than `CaptureBacklogFrames` are behind, still
   calling `NextTimestamp()` so the RTP clock stays true.
2. `ToggleMuteAsync` / `ToggleDeafenAsync` awaited a gateway round-trip. They now apply locally and
   let the Voice State Update run detached.
3. The playback jitter buffer was fixed-depth. `SourceBuffer.Prebuffer` now starts at 2 frames
   (40 ms), grows by one per underrun up to 6, and decays back down after 500 clean quanta.

## Settings

`Models/Settings/VoiceSettingsModel.cs` → `Pages/Settings/VoiceSettingsPage.xaml`:
audio mode, min/max bitrate sliders, adapt-to-connection, forward error correction, stereo preference.

`NoiseSuppression`, `EchoCancellation`, `AutomaticGainControl` and `VoiceActivity` are still
**write-only** — the controls exist and persist, but nothing in the voice path reads them. The old
native `Unicord.Universal.Voice` package did this with WebRTC APM and the C# rewrite has no
replacement yet.

---

## Links
- [[Voice Overview]]
- [[Voice Handshake]]
