using Unicord.Universal.Models.Emoji;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unicord.Universal.Controls.Emoji
{
    public sealed class EmojiControl : Control
    {
        /// <summary>
        /// Taken from the fonts themselves rather than measured at runtime. Segoe UI and Segoe UI
        /// Emoji share a vertical box exactly - both are 2048 units per em with a 2210 unit ascent
        /// and a 514 unit descent - and every emoji glyph carries the same 2812 unit advance, its
        /// artwork filling a 2162 unit square centred in that advance, running from 359 units below
        /// the baseline to 1803 above it. So:
        ///
        ///   artwork          2162 / 2048 em across
        ///   artwork bottom    359 / 2048 em below the baseline
        ///   line bottom       514 / 2048 em below the baseline
        ///
        /// Constants rather than a measured probe, because they cannot disagree with the font -
        /// and when a diagnostic build did report what a TextBlock measures, it matched these to
        /// four decimal places.
        /// </summary>
        public const double ArtworkScale = 2162d / 2048d;

        /// <inheritdoc cref="ArtworkScale"/>
        public const double LineDescent = 514d / 2048d;

        /// <summary>
        /// Where a glyph's artwork starts inside the line box the text engine gives it: 325 units in
        /// from the left of the advance, and 2210 - 1803 units down from the top. Shifting the glyph
        /// back by these puts its artwork on the box.
        /// </summary>
        private const double ArtworkLeftBearing = 325d / 2048d;

        /// <inheritdoc cref="ArtworkLeftBearing"/>
        private const double ArtworkTopBearing = (2210d - 1803d) / 2048d;

        private TextBlock _glyph;

        public EmojiViewModel Emoji
        {
            get => (EmojiViewModel)GetValue(EmojiProperty);
            set => SetValue(EmojiProperty, value);
        }

        public static readonly DependencyProperty EmojiProperty =
            DependencyProperty.Register("Emoji", typeof(EmojiViewModel), typeof(EmojiControl), new PropertyMetadata(default(EmojiViewModel)));

        /// <summary>
        /// The side of the square the emoji is drawn into. A custom emoji's image fills it, and a
        /// unicode emoji's artwork is scaled to fill it too, so the two are interchangeable.
        /// </summary>
        public double Size
        {
            get => (double)GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.Register("Size", typeof(double), typeof(EmojiControl), new PropertyMetadata(32d, OnSizeChanged));

        public bool Animate
        {
            get { return (bool)GetValue(AnimateProperty); }
            set { SetValue(AnimateProperty, value); }
        }

        public static readonly DependencyProperty AnimateProperty =
            DependencyProperty.Register("Animate", typeof(bool), typeof(EmojiControl), new PropertyMetadata(false));

        public EmojiControl()
        {
            this.DefaultStyleKey = typeof(EmojiControl);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _glyph = GetTemplateChild("PART_Glyph") as TextBlock;
            UpdateGlyph();
        }

        private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((EmojiControl)d).UpdateGlyph();

        /// <summary>
        /// Sizes and places the glyph so its artwork fills the box exactly, which is what the image
        /// a custom emoji uses does on its own.
        ///
        /// The glyph is positioned on a Canvas rather than aligned or transformed inside the box.
        /// Its line box is always larger than the box - wider by its side bearings, taller by its
        /// leading - and a TextBlock draws its text inside its arranged rectangle and clips the
        /// rest, so any fixed-size parent clamps that rectangle and cuts the artwork at draw time.
        /// Offsetting it afterwards only moves whatever survived the clip. A Canvas measures its
        /// children unconstrained, so the glyph keeps its whole line box.
        ///
        /// The offsets are the bearings themselves: the glyph is pulled back by the empty space its
        /// own line box puts to the left of and above its artwork, which lands the artwork on the
        /// box exactly.
        /// </summary>
        private void UpdateGlyph()
        {
            if (_glyph == null)
                return;

            var fontSize = Size / ArtworkScale;

            _glyph.FontSize = fontSize;
            Canvas.SetLeft(_glyph, -ArtworkLeftBearing * fontSize);
            Canvas.SetTop(_glyph, -ArtworkTopBearing * fontSize);
        }
    }
}
