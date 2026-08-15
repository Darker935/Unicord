using System;

namespace Unicord.Universal.Voice
{
    internal enum VoiceAudioMode
    {
        /// <summary>Speech. Matches what the official client does: VOIP, DTX, inband FEC.</summary>
        Voice = 0,
        /// <summary>Full-band stereo. Keeps L/R separated for dual-channel inputs.</summary>
        Music = 1
    }

    internal enum StereoPreference
    {
        /// <summary>Opus decides per frame. Never pinned.</summary>
        Auto = 0,
        /// <summary>Stereo whenever the bitrate can carry it, released when it cannot.</summary>
        Prefer = 1,
        /// <summary>Stereo at any bitrate, even when mono would sound better.</summary>
        Always = 2
    }

    /// <summary>
    /// Encoder settings for a single voice session. Built once per connect so the sender
    /// loop never has to touch <see cref="App.LocalSettings"/>.
    /// </summary>
    internal sealed class VoiceQualityOptions
    {
        public const string ModeSetting = "VoiceAudioMode";
        public const string MinBitrateSetting = "VoiceMinBitrate";
        public const string MaxBitrateSetting = "VoiceMaxBitrate";
        public const string AdaptiveSetting = "VoiceAdaptiveBitrate";
        public const string FecSetting = "VoiceInbandFec";
        // New key on purpose: 2.0.10.0 stored a bool here under "VoicePreserveStereo".
        public const string StereoSetting = "VoiceStereoMode";

        // Discord allows 8k-96k on a normal guild and up to 384k when boosted.
        public const int MinSupportedBitrate = 8000;
        public const int MaxSupportedBitrate = 384000;
        public const int DefaultChannelBitrate = 64000;
        public const int DefaultMinBitrate = 24000;
        public const int DefaultMaxBitrate = 128000;

        public VoiceAudioMode Mode { get; set; } = VoiceAudioMode.Voice;
        public int ChannelBitrate { get; set; } = DefaultChannelBitrate;
        public int MinBitrate { get; set; } = DefaultMinBitrate;
        public int MaxBitrate { get; set; } = DefaultMaxBitrate;
        public bool AdaptiveBitrate { get; set; } = true;
        public bool ForwardErrorCorrection { get; set; } = true;
        public StereoPreference Stereo { get; set; } = StereoPreference.Auto;

        public bool IsMusic => Mode == VoiceAudioMode.Music;

        /// <summary>
        /// The channel's own bitrate is the target, clamped into the user's range. Music mode
        /// starts at the ceiling because the point of it is fidelity, not bandwidth.
        /// </summary>
        public int ResolveInitialBitrate()
            => Clamp(IsMusic ? MaxBitrate : ChannelBitrate, MinBitrate, MaxBitrate);

        public static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        public static VoiceQualityOptions FromSettings(int? channelBitrate)
        {
            var min = Clamp(
                (int)App.LocalSettings.Read(MinBitrateSetting, (uint)DefaultMinBitrate),
                MinSupportedBitrate,
                MaxSupportedBitrate);
            var max = Clamp(
                (int)App.LocalSettings.Read(MaxBitrateSetting, (uint)DefaultMaxBitrate),
                MinSupportedBitrate,
                MaxSupportedBitrate);

            if (max < min)
                max = min;

            return new VoiceQualityOptions
            {
                Mode = App.LocalSettings.Read(ModeSetting, (uint)VoiceAudioMode.Voice) == (uint)VoiceAudioMode.Music
                    ? VoiceAudioMode.Music
                    : VoiceAudioMode.Voice,
                ChannelBitrate = Clamp(
                    channelBitrate ?? DefaultChannelBitrate,
                    MinSupportedBitrate,
                    MaxSupportedBitrate),
                MinBitrate = min,
                MaxBitrate = max,
                AdaptiveBitrate = App.LocalSettings.Read(AdaptiveSetting, true),
                ForwardErrorCorrection = App.LocalSettings.Read(FecSetting, true),
                Stereo = ReadStereoPreference()
            };
        }

        private static StereoPreference ReadStereoPreference()
        {
            switch (App.LocalSettings.Read(StereoSetting, (uint)StereoPreference.Auto))
            {
                case (uint)StereoPreference.Always: return StereoPreference.Always;
                case (uint)StereoPreference.Prefer: return StereoPreference.Prefer;
                default: return StereoPreference.Auto;
            }
        }

        public override string ToString()
            => "mode=" + Mode + " channel=" + ChannelBitrate + " range=" + MinBitrate + ".." + MaxBitrate +
               " adaptive=" + AdaptiveBitrate + " fec=" + ForwardErrorCorrection + " stereo=" + Stereo;
    }
}
