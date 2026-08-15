using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models.Guild;
using Unicord.Universal.Models.User;
using Unicord.Universal.Models.Voice;

namespace Unicord.Universal.Models.Channels
{
    public class ChannelListViewModel : ChannelViewModel
    {
        private readonly GuildChannelListViewModel _guildChannelList;
        private DateTimeOffset? _callStartedAt;

        public ChannelListViewModel(DiscordChannel channel, GuildChannelListViewModel guildChannelList = null) 
            : base(channel.Id)
        {
            _guildChannelList = guildChannelList;
            VoiceMembers = new ObservableCollection<VoiceMemberViewModel>();

            if (channel.Type is DSharpPlus.ChannelType.Voice or DSharpPlus.ChannelType.Stage)
            {
                RefreshVoiceMembers();
                WeakReferenceMessenger.Default.Register<ChannelListViewModel, VoiceStateUpdateEventArgs>(this, static (r, m) => r.OnVoiceStateUpdated(m.Event));
                WeakReferenceMessenger.Default.Register<ChannelListViewModel, GuildCreateEventArgs>(this, static (r, m) => r.OnGuildCreated(m.Event));
                WeakReferenceMessenger.Default.Register<ChannelListViewModel, VoiceChannelStatusUpdateEventArgs>(this, static (r, m) => r.OnVoiceChannelStatusUpdated(m.Event));
                WeakReferenceMessenger.Default.Register<ChannelListViewModel, VoiceChannelStartTimeUpdateEventArgs>(this, static (r, m) => r.OnVoiceChannelStartTimeUpdated(m.Event));
            }
        }

        public ObservableCollection<VoiceMemberViewModel> VoiceMembers { get; }

        public bool HasVoiceMembers => VoiceMembers.Count > 0;

        public bool IsVoiceChannel => Channel.Type is DSharpPlus.ChannelType.Voice or DSharpPlus.ChannelType.Stage;

        public bool IsCategory => Channel.Type == DSharpPlus.ChannelType.Category;

        /// <summary>
        /// The voice channel's status line, set by members to say what a call is about. Arrives on
        /// the channel object and through VOICE_CHANNEL_STATUS_UPDATE; nothing is ever requested
        /// for it.
        /// </summary>
        public string Status => Channel?.Status;

        public bool HasStatus => IsVoiceChannel && !string.IsNullOrWhiteSpace(Status);

        /// <summary>
        /// When the call in this channel actually started. Discord sends the real value as
        /// voice_start_time, but only in answer to a Request Channel Info command or through
        /// VOICE_CHANNEL_START_TIME_UPDATE, never on the channel object. Until one of those
        /// arrives the clock falls back to when this session first saw somebody in the channel,
        /// which reads short for a call that was already running.
        /// </summary>
        private DateTimeOffset? CallStart
        {
            get
            {
                var serverStart = Channel?.VoiceStartTime;
                return serverStart != null
                    ? DateTimeOffset.FromUnixTimeSeconds(serverStart.Value)
                    : _callStartedAt;
            }
        }

        /// <summary>
        /// How long the call in this channel has been running.
        /// </summary>
        public string CallDuration
        {
            get
            {
                var start = CallStart;
                if (start == null)
                    return null;

                var elapsed = DateTimeOffset.Now - start.Value;
                if (elapsed < TimeSpan.Zero)
                    elapsed = TimeSpan.Zero;

                return elapsed.TotalHours >= 1
                    ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                    : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
            }
        }

        public bool HasCallDuration => CallStart != null;

        /// <summary>
        /// Whether this category's channels are hidden. Persisted per category id, so the state
        /// survives a restart like it does on other clients.
        /// </summary>
        public bool IsCollapsed
        {
            get => App.LocalSettings.TryRead<bool>(CollapsedSettingKey, out var collapsed) && collapsed;
            set
            {
                if (!IsCategory || IsCollapsed == value)
                    return;

                App.LocalSettings.Save(CollapsedSettingKey, value);
                InvokePropertyChanged(nameof(IsCollapsed));
                InvokePropertyChanged(nameof(CategoryGlyph));
                _guildChannelList?.ApplyCollapseState();
            }
        }

        public string CategoryGlyph => IsCollapsed ? "" : "";

        // CategoryGlyph above is ChevronRight (U+E76C) when collapsed, ChevronDown (U+E70D) when not.

        private string CollapsedSettingKey => $"CategoryCollapsed-{Id}";

        private Task OnGuildCreated(GuildCreateEventArgs e)
        {
            if (e.Guild?.Id != Channel.GuildId)
                return Task.CompletedTask;

            syncContext.Post(_ => RefreshVoiceMembers(), null);
            return Task.CompletedTask;
        }

        private Task OnVoiceStateUpdated(VoiceStateUpdateEventArgs e)
        {
            if (e.Guild?.Id != Channel.GuildId)
                return Task.CompletedTask;

            var beforeId = e.Before?.ChannelId;
            var afterId = e.After?.ChannelId ?? e.Channel?.Id;
            if (beforeId != Id && afterId != Id)
                return Task.CompletedTask;

            syncContext.Post(_ => RefreshVoiceMembers(), null);
            return Task.CompletedTask;
        }

        private void RefreshVoiceMembers()
        {
            if (!IsVoiceChannel)
                return;

            var guild = Channel.Guild;
            if (guild == null)
                return;

            var next = guild.VoiceStates.Values
                .Where(vs => vs.ChannelId == Id)
                .Select(CreateVoiceMember)
                .Where(u => u != null)
                .ToList();

            VoiceMembers.Clear();
            foreach (var member in next)
                VoiceMembers.Add(member);

            // the call clock starts the first time anyone is seen here and resets when the channel
            // empties, since no start timestamp exists on the wire
            if (next.Count > 0)
                _callStartedAt ??= DateTimeOffset.Now;
            else
                _callStartedAt = null;

            InvokePropertyChanged(nameof(HasVoiceMembers));
            InvokePropertyChanged(nameof(HasCallDuration));
            InvokePropertyChanged(nameof(CallDuration));

            _guildChannelList?.OnVoiceActivityChanged();
        }

        /// <summary>
        /// Re-reads the call clock. Driven by the channel list's shared timer rather than one
        /// timer per channel.
        /// </summary>
        internal void RefreshCallDuration()
            => InvokePropertyChanged(nameof(CallDuration));

        private void OnVoiceChannelStatusUpdated(VoiceChannelStatusUpdateEventArgs e)
        {
            if (e.ChannelId != Id)
                return;

            syncContext.Post(_ =>
            {
                InvokePropertyChanged(nameof(Status));
                InvokePropertyChanged(nameof(HasStatus));
            }, null);
        }

        private void OnVoiceChannelStartTimeUpdated(VoiceChannelStartTimeUpdateEventArgs e)
        {
            if (e.ChannelId != Id)
                return;

            syncContext.Post(_ =>
            {
                InvokePropertyChanged(nameof(HasCallDuration));
                InvokePropertyChanged(nameof(CallDuration));
                _guildChannelList?.OnVoiceActivityChanged();
            }, null);
        }

        private VoiceMemberViewModel CreateVoiceMember(DiscordVoiceState state)
        {
            var user = state.Member ?? state.User;
            if (user == null || user.Id == 0)
                return null;

            return new VoiceMemberViewModel(state, Channel.GuildId, this);
        }
    }
}
