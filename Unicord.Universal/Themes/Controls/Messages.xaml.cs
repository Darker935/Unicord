using System;
using Unicord.Universal.Models.Messages;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;

namespace Unicord.Universal.Resources.Controls
{
    public partial class Messages : ResourceDictionary
    {
        public Messages()
        {
            InitializeComponent();
        }

        public Uri ToUri(object obj) => (Uri)obj;

        // The hover tint is a separate layer rather than the message's own Background, because the
        // visual states already drive that Background for the mention highlight and a local value
        // set here would fight them.
        private void MessageRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
            => SetHoverOpacity(sender, 1);

        private void MessageRoot_PointerExited(object sender, PointerRoutedEventArgs e)
            => SetHoverOpacity(sender, 0);

        // The list virtualizes. A row hovered as it scrolls out of view gets reused for a different
        // message with the tint still on it, because the opacity is a local value on the instance
        // and PointerExited never ran. Every reuse changes the data context, so that is where it is
        // put back.
        private void MessageRoot_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
            => SetHoverOpacity(sender, 0);

        private static void SetHoverOpacity(object sender, double opacity)
        {
            if (sender is Panel root && root.Children.Count > 0 && root.Children[0] is Border hover)
                hover.Opacity = opacity;
        }

        private void ImageContainer_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            ImageBrush imageBrush = null;
            if (imageBrush == null)
            {
                var container = (Ellipse)sender;
                if (container == null || container.Fill == null)
                    return;

                imageBrush = (ImageBrush)container.Fill;
            }

            imageBrush.ImageSource = null;

            if (args.NewValue is not MessageViewModel message || message.Author == null || message.Author.AvatarUrl == null)
                return;

            imageBrush.ImageSource = new BitmapImage
            {
                UriSource = new Uri(message.Author.AvatarUrl),
                DecodePixelHeight = 36,
                DecodePixelWidth = 36,
                DecodePixelType = DecodePixelType.Logical
            };
        }
    }
}
