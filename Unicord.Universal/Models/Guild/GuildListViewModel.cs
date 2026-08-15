using System.ComponentModel;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;

namespace Unicord.Universal.Models.Guild
{
    internal class GuildListViewModel : GuildViewModel, IGuildListViewModel
    {
        private GuildListFolderViewModel _parent;
        private bool _isSelected;

        public GuildListViewModel(DiscordGuild guild, GuildListFolderViewModel parent = null)
            : base(guild.Id)
        {
            _parent = parent;
            PropertyChanged += OnSelfPropertyChanged;
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                OnPropertySet(ref _isSelected, value);
                InvokePropertyChanged(nameof(ShowUnreadDot));
            }
        }

        /// <summary>
        /// The rail's left-edge unread dot. Hidden while the guild is open, where the list item's
        /// own selection indicator occupies the same spot.
        /// </summary>
        public bool ShowUnreadDot
            => Unread && !IsSelected;

        /// <summary>
        /// The corner badge is for mention counts only. Plain unread is carried by the left-edge
        /// dot, so an unread guild with no mentions gets no badge.
        /// </summary>
        public bool ShowMentionBadge
            => MentionCount > 0;

        public int MentionCount
        {
            get
            {
                if (Muted)
                    return -1;

                var count = 0;
                foreach (var channel in AccessibleChannels)
                {
                    if (channel.Muted)
                        continue;

                    if (discord.ReadStates.TryGetValue(channel.Id, out var rs))
                        count += rs.MentionCount;
                }

                return count == 0 ? -1 : count;
            }
        }

        public bool TryGetModelForGuild(DiscordGuild guild, out GuildListViewModel model)
        {
            if (Guild == guild)
            {
                model = this;
                return true;
            }

            model = null;
            return false;
        }

        protected override void OnReadStateUpdatedCore(ReadStateUpdateEventArgs e)
        {
            InvokePropertyChanged(nameof(MentionCount));
            InvokePropertyChanged(nameof(ShowMentionBadge));
        }

        // Unread lives on the base and is also raised outside the read-state path, so the dot
        // follows it from here rather than from every call site.
        private void OnSelfPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Unread))
                InvokePropertyChanged(nameof(ShowUnreadDot));
        }
    }
}
