using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AdbDesktop
{
    /// <summary>
    /// Wallpaper fit name to WPF's Stretch. The names are the user-facing ones, which do
    /// not line up with Stretch's: what a user calls "Stretch" (distort to fill) is
    /// Stretch.Fill, and what they call "Fill" (cover, cropping the overflow) is
    /// Stretch.UniformToFill.
    /// </summary>
    /// <summary>
    /// True reverses a panel's child order. Used for the Samsung nav-button arrangement;
    /// the children set FlowDirection back to LeftToRight so only the ORDER flips, not
    /// the glyphs.
    /// </summary>
    public sealed class BoolToFlowDirectionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    public sealed class WallpaperFitConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            (value as string) switch
            {
                "Fit" => System.Windows.Media.Stretch.Uniform,
                "Stretch" => System.Windows.Media.Stretch.Fill,
                "Center" => System.Windows.Media.Stretch.None,
                _ => System.Windows.Media.Stretch.UniformToFill,
            };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Visibility.Collapsed;
    }

    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value == null ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A colour string to a brush, for the window-border swatches. Anything unreadable
    /// comes back transparent rather than throwing: the swatch list is data like any
    /// other, and a bad entry should show as a gap, not take the page down.
    /// </summary>
    public sealed class ColourToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            Theme.TryParseColour(value as string, out var colour)
                ? new System.Windows.Media.SolidColorBrush(colour)
                : System.Windows.Media.Brushes.Transparent;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Visible when the string is empty. Pass ConverterParameter="invert" for the
    /// opposite, which is how the status line hides itself when there is nothing to say.
    /// </summary>
    public sealed class EmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var isEmpty = string.IsNullOrEmpty(value as string);

            if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
                isEmpty = !isEmpty;

            return isEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
