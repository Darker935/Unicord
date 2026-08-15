using System;

namespace Unicord.Universal.Voice
{
    /// <summary>
    /// Raised when a member starts or stops talking in the current call.
    /// </summary>
    internal sealed class VoiceSpeakingEventArgs : EventArgs
    {
        public VoiceSpeakingEventArgs(ulong userId, bool speaking)
        {
            UserId = userId;
            Speaking = speaking;
        }

        public ulong UserId { get; }

        public bool Speaking { get; }
    }
}
