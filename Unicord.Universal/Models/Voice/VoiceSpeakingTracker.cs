using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;

namespace Unicord.Universal.Models.Voice
{
    /// <summary>
    /// Sent when this client joins or leaves a voice channel, so member rows can tell an
    /// "I am here" from an "I am here on my phone".
    /// </summary>
    public sealed class VoiceSessionChangedMessage
    {
        public VoiceSessionChangedMessage(ulong? channelId)
        {
            ChannelId = channelId;
        }

        public ulong? ChannelId { get; }
    }

    /// <summary>
    /// Sent whenever a member starts or stops talking in the current call.
    /// </summary>
    public sealed class VoiceSpeakingMessage
    {
        public VoiceSpeakingMessage(ulong userId, bool speaking)
        {
            UserId = userId;
            Speaking = speaking;
        }

        public ulong UserId { get; }

        public bool Speaking { get; }
    }

    /// <summary>
    /// Who is currently talking. Voice member rows are rebuilt from scratch on every voice state
    /// change, so a new row needs somewhere to read the current state from; the message alone
    /// would only tell it about the next change.
    /// </summary>
    /// <summary>
    /// Which voice channel this client is connected to, if any. A voice state for our own user in
    /// a channel we are not connected to means the account is in that call from another device,
    /// which is what the official client dims the row for.
    /// </summary>
    internal static class VoiceSessionTracker
    {
        private static ulong? _connectedChannelId;

        public static ulong? ConnectedChannelId => _connectedChannelId;

        internal static void Set(ulong? channelId)
        {
            if (_connectedChannelId == channelId)
                return;

            _connectedChannelId = channelId;
            WeakReferenceMessenger.Default.Send(new VoiceSessionChangedMessage(channelId));
        }
    }

    internal static class VoiceSpeakingTracker
    {
        private static readonly HashSet<ulong> _speaking = new HashSet<ulong>();

        public static bool IsSpeaking(ulong userId)
        {
            lock (_speaking)
                return _speaking.Contains(userId);
        }

        internal static void Set(ulong userId, bool speaking)
        {
            lock (_speaking)
            {
                var changed = speaking ? _speaking.Add(userId) : _speaking.Remove(userId);
                if (!changed)
                    return;
            }

            WeakReferenceMessenger.Default.Send(new VoiceSpeakingMessage(userId, speaking));
        }

        /// <summary>
        /// Called when the call ends, so nobody is left ringed after a disconnect.
        /// </summary>
        internal static void Clear()
        {
            ulong[] stale;
            lock (_speaking)
            {
                if (_speaking.Count == 0)
                    return;

                stale = new ulong[_speaking.Count];
                _speaking.CopyTo(stale);
                _speaking.Clear();
            }

            foreach (var userId in stale)
                WeakReferenceMessenger.Default.Send(new VoiceSpeakingMessage(userId, false));
        }
    }
}
