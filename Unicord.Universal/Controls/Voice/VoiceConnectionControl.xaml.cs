using Unicord.Universal.Models.Voice;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unicord.Universal.Controls.Voice
{
    public sealed partial class VoiceConnectionControl : UserControl
    {
        public VoiceConnectionModel ConnectionModel
        {
            get => (VoiceConnectionModel)GetValue(ConnectionModelProperty);
            set => SetValue(ConnectionModelProperty, value);
        }

        public static readonly DependencyProperty ConnectionModelProperty =
            DependencyProperty.Register("ConnectionModel", typeof(VoiceConnectionModel), typeof(VoiceConnectionControl), new PropertyMetadata(null, OnConnectionModelChanged));

        public VoiceConnectionControl()
        {
            this.InitializeComponent();
        }

        private static void OnConnectionModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            await ConnectionModel?.DisconnectAsync();
        }
    }
}
