using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models.Channels;
using Unicord.Universal.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unicord.Universal.Models.Guild
{
    public class GuildChannelListViewModel : GuildViewModel
    {
        private bool _canEdit;
        private readonly List<ChannelListViewModel> _allChannels = [];
        private DispatcherTimer _callTimer;
        private bool _requestedChannelInfo;

        public GuildChannelListViewModel(DiscordGuild guild)
            : base(guild.Id)
        {
            Channels = [];
            InitialiseLists();

            WeakReferenceMessenger.Default.Register<GuildChannelListViewModel, ReadyEventArgs>(this, (t, e) => t.OnReady(e.Event));
            WeakReferenceMessenger.Default.Register<GuildChannelListViewModel, GuildCreateEventArgs>(this, (t, e) => t.OnGuildAvailable(e.Event));
        }

        /// <summary>
        /// A guild arrives with no channels and no member of our own until it is synced, and the
        /// sidebar is navigated before that sync is awaited - on a cold start restoring the last
        /// channel, the list was therefore built from nothing and never rebuilt, so the guild looked
        /// selected with an empty channel list until another channel was clicked. The list is built
        /// again when the guild actually turns up.
        /// </summary>
        private Task OnGuildAvailable(GuildCreateEventArgs e)
        {
            if (e.Guild?.Id != Id)
                return Task.CompletedTask;

            syncContext.Post(o => InitialiseLists(), null);
            return Task.CompletedTask;
        }

        public string HeaderImage => Guild.GetBannerUrl();
        public ObservableCollection<ChannelListViewModel> Channels { get; set; }
        public ListViewReorderMode ReorderMode => CanEdit ? ListViewReorderMode.Enabled : ListViewReorderMode.Disabled;

        public bool CanEdit
        {
            get => _canEdit;
            set
            {
                OnPropertySet(ref _canEdit, value);
                InvokePropertyChanged(nameof(ReorderMode));
            }
        }

        private Task OnReady(ReadyEventArgs e)
        {
            // ephemeral channel data does not survive a new gateway session, so it is asked for
            // once per connection rather than once per app lifetime
            _requestedChannelInfo = false;
            syncContext.Post(o => InitialiseLists(), null);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Initially populates the server list
        /// </summary>
        private void InitialiseLists()
        {
            Channels.Clear();
            _allChannels.Clear();

            // No member of our own means the guild has not synced yet, and every filter below is a
            // permission check against it. Leave the list empty rather than throwing out of the page
            // constructor; OnGuildAvailable builds it again once the guild arrives.
            var currentMember = Guild.CurrentMember;
            if (currentMember == null)
                return;

            var permissions = currentMember.PermissionsIn(null);
            CanEdit = permissions.HasPermission(Permissions.ManageChannels);

            var channels = Guild.Channels;
            static bool FilterChannels(DiscordChannel channel, DiscordMember currentMember)
            {
                if (currentMember.IsOwner)
                    return true;

                return channel.IsCategory ?
                    channel.Children.Any(x => x.PermissionsFor(currentMember).HasPermission(Permissions.AccessChannels)) :
                    channel.PermissionsFor(currentMember).HasPermission(Permissions.AccessChannels);
            }

            static bool FilterThreads(DiscordThreadChannel channel)
            {
                var currentUserId = DiscordManager.Discord.CurrentUser.Id;
                return (channel.CurrentMember != null || 
                        channel.CreatorId == currentUserId ||
                        (channel.MemberIdsPreview != null && channel.MemberIdsPreview.Contains(currentUserId))) 
                    && !(channel.ThreadMetadata?.IsArchived ?? true);
            }

            // Use new discord channel category behaviour (new as of 2017 KEKW)
            var orderedChannels = channels.Select(t => t.Value)
                .Where(c => c.Type != ChannelType.Category)
                .Where(c => FilterChannels(c, currentMember))
                .OrderBy(c => c.Type == ChannelType.Voice)
                .ThenBy(c => c.Position)
                .GroupBy(g => g.Parent)
                .OrderBy(g => g.Key?.Position)
                .SelectMany(g => g.Key != null ? g.Prepend(g.Key) : g)
                .SelectMany<DiscordChannel, DiscordChannel>(c => [c, .. c.Threads.Where(FilterThreads).Cast<DiscordChannel>()])
                .Select(c => new ChannelListViewModel(c, this));

            foreach (var channel in orderedChannels)
                _allChannels.Add(channel);

            ApplyCollapseState();
            OnVoiceActivityChanged();
            _ = RequestChannelInfoAsync();
        }

        /// <summary>
        /// Asks for this guild's ephemeral channel data: voice channel status and the unix second
        /// each call started. Neither is on the channel object, so a call that began before the
        /// app launched is otherwise invisible to us.
        ///
        /// One gateway command per guild, sent when the guild's channels are actually shown, and
        /// only when the guild has voice channels at all. Later changes push themselves as
        /// VOICE_CHANNEL_STATUS_UPDATE and VOICE_CHANNEL_START_TIME_UPDATE, so this is never
        /// repeated or polled.
        /// </summary>
        private async Task RequestChannelInfoAsync()
        {
            if (_requestedChannelInfo)
                return;

            if (!_allChannels.Any(c => c.IsVoiceChannel))
                return;

            _requestedChannelInfo = true;

            try
            {
                await discord.RequestChannelInfoAsync(Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _requestedChannelInfo = false;
                Logger.LogError(ex);
            }
        }

        /// <summary>
        /// Starts or stops the call clock. One timer for the whole list, running only while a
        /// voice channel actually has somebody in it, so an idle guild ticks nothing.
        /// </summary>
        internal void OnVoiceActivityChanged()
        {
            var anyActive = _allChannels.Any(c => c.IsVoiceChannel && c.HasVoiceMembers);

            syncContext.Post(_ =>
            {
                if (!anyActive)
                {
                    _callTimer?.Stop();
                    return;
                }

                if (_callTimer == null)
                {
                    _callTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _callTimer.Tick += OnCallTimerTick;
                }

                if (!_callTimer.IsEnabled)
                    _callTimer.Start();
            }, null);
        }

        private void OnCallTimerTick(object sender, object e)
        {
            var ticked = false;
            foreach (var channel in _allChannels)
            {
                if (!channel.IsVoiceChannel || !channel.HasCallDuration)
                    continue;

                channel.RefreshCallDuration();
                ticked = true;
            }

            if (!ticked)
                _callTimer.Stop();
        }

        /// <summary>
        /// Rebuilds <see cref="Channels"/> from the full channel list, dropping the children of
        /// collapsed categories. Diffs in place so the list keeps its selection and scroll position.
        /// </summary>
        internal void ApplyCollapseState()
        {
            var visible = new List<ChannelListViewModel>(_allChannels.Count);
            ChannelListViewModel category = null;

            foreach (var channel in _allChannels)
            {
                if (channel.IsCategory)
                {
                    category = channel;
                    visible.Add(channel);
                    continue;
                }

                if (category?.IsCollapsed != true)
                    visible.Add(channel);
            }

            for (var i = Channels.Count - 1; i >= 0; i--)
            {
                if (!visible.Contains(Channels[i]))
                    Channels.RemoveAt(i);
            }

            // after the removals Channels is a subsequence of visible, so a single forward pass
            // inserting whatever does not line up is enough
            for (var i = 0; i < visible.Count; i++)
            {
                if (i >= Channels.Count || !ReferenceEquals(Channels[i], visible[i]))
                    Channels.Insert(i, visible[i]);
            }
        }
    }
}
