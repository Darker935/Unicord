using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Unicord.Universal
{
    /// <summary>
    /// A named stream of high-volume diagnostic logging that can be switched on per feature.
    /// </summary>
    /// <remarks>
    /// <see cref="Logger"/> stays the right place for lifecycle and failure logging, which is
    /// low-volume and always wanted. This is for the opposite kind: per-window metrics that
    /// are indispensable while a fault is being chased and pure cost afterwards, both in CPU
    /// spent formatting them and in the size of the trace that has to be decoded to read
    /// anything else.
    ///
    /// Channels are independent on purpose. Chasing an audio fault should not bury the trace
    /// in emoji layout measurements, and vice versa, so each feature owns a channel and a
    /// setting rather than everything sharing one "verbose" flag.
    ///
    /// Cost when a channel is off is one volatile read. That only holds if callers keep the
    /// work behind <see cref="IsEnabled"/> rather than passing an already-built string, so
    /// anything that concatenates, formats or walks a collection to produce its message
    /// belongs inside an <c>if (channel.IsEnabled)</c> block. <see cref="Log"/> re-checks so
    /// a cheap constant message needs no guard at the call site.
    /// </remarks>
    internal sealed class DiagnosticLog
    {
        private static readonly List<DiagnosticLog> Registry = new List<DiagnosticLog>();

        /// <summary>Voice call audio: pacing, jitter, render clock, collection stalls.</summary>
        public static readonly DiagnosticLog Voice = new DiagnosticLog("Voice", "DiagnosticsVoice");

        /// <summary>Emoji and inline-image layout measurement.</summary>
        public static readonly DiagnosticLog Emoji = new DiagnosticLog("Emoji", "DiagnosticsEmoji");

        private readonly string _prefix;
        private bool _enabled;
        private bool _resolved;

        private DiagnosticLog(string name, string settingKey)
        {
            Name = name;
            SettingKey = settingKey;
            _prefix = "[" + name + "] ";

            lock (Registry)
                Registry.Add(this);
        }

        /// <summary>Display name, and the tag every message from this channel carries.</summary>
        public string Name { get; }

        /// <summary>Key this channel's switch is stored under in local settings.</summary>
        public string SettingKey { get; }

        /// <summary>
        /// Whether this channel is currently recording. Resolved from settings once and then
        /// cached, because it is read from audio callbacks running a hundred times a second.
        /// </summary>
        public bool IsEnabled
        {
            get
            {
                if (!_resolved)
                {
                    // Settings are unavailable before the app has initialised, and a
                    // diagnostic switch is never worth failing a caller over.
                    try { _enabled = App.LocalSettings.Read(SettingKey, false); }
                    catch { _enabled = false; }
                    _resolved = true;
                }

                return _enabled;
            }
        }

        /// <summary>
        /// Writes to the trace if this channel is on. Callers that have to build their
        /// message should test <see cref="IsEnabled"/> first; this check only avoids the
        /// write itself.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Log(object message, [CallerMemberName] string source = "General")
        {
            if (!IsEnabled)
                return;

            Logger.Log(_prefix + message, source);
        }

        /// <summary>
        /// Sets this channel's switch and stores it. Takes effect immediately, so a channel
        /// can be turned on mid-call without restarting.
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            App.LocalSettings.Save(SettingKey, enabled);
            _enabled = enabled;
            _resolved = true;
        }

        /// <summary>
        /// Drops every cached switch so the next read comes from settings again. For callers
        /// that change the stored value by some route other than <see cref="SetEnabled"/>.
        /// </summary>
        public static void InvalidateAll()
        {
            lock (Registry)
            {
                foreach (var channel in Registry)
                    channel._resolved = false;
            }
        }

        /// <summary>Every registered channel, for a settings screen that lists them.</summary>
        public static IReadOnlyList<DiagnosticLog> All
        {
            get
            {
                lock (Registry)
                    return Registry.ToArray();
            }
        }
    }
}
