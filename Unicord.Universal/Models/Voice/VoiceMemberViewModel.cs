using CommunityToolkit.Mvvm.Messaging;
using DSharpPlus.Entities;
using Unicord.Universal.Models.User;

namespace Unicord.Universal.Models.Voice
{
    /// <summary>
    /// A user shown under a voice channel in the channel list. Carries the parts of
    /// <see cref="DiscordVoiceState"/> the list needs; <see cref="UserViewModel"/> has no
    /// concept of voice state on its own.
    /// </summary>
    public class VoiceMemberViewModel : UserViewModel
    {
        private readonly ulong? _channelId;
        private bool _isSpeaking;

        internal VoiceMemberViewModel(DiscordVoiceState state, ulong? guildId, ViewModelBase parent = null)
            : base(state.Member ?? state.User, guildId, parent)
        {
            _channelId = state.ChannelId;
            IsMuted = state.IsSelfMuted || state.IsServerMuted;
            IsDeafened = state.IsSelfDeafened || state.IsServerDeafened;
            IsStreaming = state.IsSelfStream;
            IsVideo = state.IsSelfVideo;

            // rows are rebuilt on every voice state change, so the current state is read up front
            // and the message only carries changes from here on
            _isSpeaking = VoiceSpeakingTracker.IsSpeaking(Id);
            WeakReferenceMessenger.Default.Register<VoiceMemberViewModel, VoiceSpeakingMessage>(
                this, static (r, m) => r.OnSpeakingChanged(m));
            WeakReferenceMessenger.Default.Register<VoiceMemberViewModel, VoiceSessionChangedMessage>(
                this, static (r, m) => r.OnVoiceSessionChanged());
        }

        public bool IsMuted { get; }
        public bool IsDeafened { get; }
        public bool IsStreaming { get; }
        public bool IsVideo { get; }

        /// <summary>
        /// Whether this member is talking right now, driven by arriving audio rather than by the
        /// speaking opcode alone, which does not reliably report stopping.
        /// </summary>
        public bool IsSpeaking
        {
            get => _isSpeaking;
            private set => OnPropertySet(ref _isSpeaking, value);
        }

        /// <summary>
        /// Deafened already implies muted, so only one icon is shown, matching the
        /// official client.
        /// </summary>
        public bool ShowMuted => IsMuted && !IsDeafened;

        /// <summary>
        /// True when this row is us, sitting in the call from a different client. The account is
        /// in the channel but this app is not the one connected, which the official client shows
        /// by dimming the row.
        /// </summary>
        public bool IsOnAnotherClient
            => IsCurrent && VoiceSessionTracker.ConnectedChannelId != _channelId;

        public double MemberOpacity => IsOnAnotherClient ? 0.5 : 1.0;

        private void OnVoiceSessionChanged()
            => syncContext.Post(_ =>
            {
                InvokePropertyChanged(nameof(IsOnAnotherClient));
                InvokePropertyChanged(nameof(MemberOpacity));
            }, null);

        private void OnSpeakingChanged(VoiceSpeakingMessage message)
        {
            if (message.UserId != Id)
                return;

            syncContext.Post(_ => IsSpeaking = message.Speaking, null);
        }
    }
}
