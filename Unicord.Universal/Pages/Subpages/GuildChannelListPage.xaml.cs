using DSharpPlus;
using DSharpPlus.Entities;
using System.Linq;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models.Channels;
using Unicord.Universal.Models.Guild;
using Unicord.Universal.Services;
using Windows.Foundation.Metadata;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Navigation;

namespace Unicord.Universal.Pages.Subpages
{
    public sealed partial class GuildChannelListPage : Page
    {
        private bool _suspend = false;
        public DiscordGuild Guild { get; private set; }

        public GuildChannelListPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            Guild = e.Parameter as DiscordGuild;
            DataContext = new GuildChannelListViewModel(Guild);
        }

        private async void channelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var viewModel = e.AddedItems.FirstOrDefault() as ChannelListViewModel;
            if (viewModel != null && !_suspend)
            {
                if (viewModel.ChannelType is not (ChannelType.Text or ChannelType.Announcement or ChannelType.AnnouncementThread or ChannelType.GuildForum or ChannelType.PublicThread or ChannelType.PrivateThread))
                {
                    // Restoring the previous selection raises SelectionChanged again. Unguarded,
                    // that second pass saw a text channel in AddedItems and navigated to it, and
                    // the list scrolled to wherever that channel sits. Clicking a voice channel
                    // must leave both the open channel and the scroll position alone.
                    RestoreSelectionWithoutScrolling(e.RemovedItems.FirstOrDefault());

                    if (viewModel.ChannelType != ChannelType.Voice)
                        return;
                }

                var service = DiscordNavigationService.GetForCurrentView();
                await service.NavigateAsync(viewModel.Channel);
            }
        }

        /// <summary>
        /// Puts the selection back where it was without letting the list move. The selection change
        /// is suppressed so it cannot re-enter navigation, and the scroll offset is captured and
        /// reapplied because selecting an item asks the list to bring it into view.
        /// </summary>
        private void RestoreSelectionWithoutScrolling(object item)
        {
            var scrollViewer = channelsList.FindChild<ScrollViewer>();
            var offset = scrollViewer?.VerticalOffset;

            _suspend = true;
            channelsList.SelectedItem = item;
            _suspend = false;

            if (scrollViewer != null && offset != null && scrollViewer.VerticalOffset != offset.Value)
                scrollViewer.ChangeView(null, offset.Value, null, true);
        }

        private void OnButtonClicked(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var flyout = FlyoutBase.GetAttachedFlyout(Header);
            if (ApiInformation.IsTypePresent("Windows.UI.Xaml.Controls.Primitives.FlyoutShowOptions"))
            {
                var options = new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
                flyout.ShowAt(btn, options);
            }
            else
            {
                flyout.ShowAt(Header);
            }
        }

        internal void SetSelectedChannel(DiscordChannel channel)
        {
            _suspend = true;
            var viewModel = (GuildChannelListViewModel)DataContext;
            var channelVM = viewModel.Channels.FirstOrDefault(c => c.Channel == channel);

            channelsList.SelectedItem = channelVM;
            _suspend = false;
        }
    }
}
