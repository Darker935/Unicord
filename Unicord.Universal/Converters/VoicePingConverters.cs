using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace Unicord.Universal.Converters
{
    /// <summary>
    /// Latency bands behind the voice signal indicator. A ping of zero means neither the UDP
    /// keepalive nor the gateway heartbeat has reported a round trip yet, which is not the same
    /// as a bad connection.
    /// </summary>
    internal static class VoicePingBands
    {
        internal const uint Good = 100;
        internal const uint Fair = 250;

        internal static uint FromValue(object value)
            => value switch
            {
                uint u => u,
                int i when i > 0 => (uint)i,
                _ => 0
            };

        /// <summary>
        /// 3 = good, 2 = fair, 1 = poor, 0 = nothing measured yet.
        /// </summary>
        internal static int Strength(uint ping)
            => ping == 0 ? 0
            : ping < Good ? 3
            : ping < Fair ? 2
            : 1;
    }

    /// <summary>
    /// Lights the arcs of the drawn wifi indicator. The converter parameter is the arc's own level,
    /// 1 being the innermost: arcs at or below the current strength are solid, the rest fade out.
    /// </summary>
    public class VoicePingArcOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var strength = VoicePingBands.Strength(VoicePingBands.FromValue(value));

            if (!int.TryParse(parameter as string, out var level))
                level = 1;

            return level <= strength ? 1.0 : 0.2;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Colours the indicator green / yellow / red on the same bands, grey while unmeasured.
    /// </summary>
    public class VoicePingBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var ping = VoicePingBands.FromValue(value);
            var key = ping == 0 ? "ApplicationSecondaryForegroundThemeBrush"
                : ping < VoicePingBands.Good ? "VoicePingGoodBrush"
                : ping < VoicePingBands.Fair ? "VoicePingFairBrush"
                : "VoicePingPoorBrush";

            return Application.Current.Resources[key] as Brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Tooltip text for the signal indicator.
    /// </summary>
    public class VoicePingConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var ping = VoicePingBands.FromValue(value);
            return ping == 0 ? "Measuring connection..." : $"{ping}ms";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
