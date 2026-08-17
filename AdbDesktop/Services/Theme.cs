using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace AdbDesktop
{
    /// <summary>
    /// The parts of the palette the user can change.
    ///
    /// Separate from the fixed palette in App.xaml because these are replaced while the
    /// app is running: everything that draws with one binds it as a DynamicResource, so
    /// swapping the brush here repaints what is already on screen instead of only
    /// reaching windows opened afterwards.
    /// </summary>
    public static class Theme
    {
        /// <summary>
        /// Resource key for the focused app window's frame. Also declared in App.xaml, so
        /// the designer and a run that never calls <see cref="Apply"/> still have a brush.
        /// </summary>
        private const string WindowBorderKey = "WindowBorderBrush";

        /// <summary>The accent blue, which is what the window frame has always been.</summary>
        public const string DefaultWindowBorder = "#4C8DFF";

        /// <summary>
        /// The swatches offered in Settings. A shortlist rather than a full colour picker:
        /// this is one line 1px wide, and the useful question is which colour it is, not
        /// which of sixteen million.
        /// </summary>
        public static readonly IReadOnlyList<string> WindowBorderPresets = new[]
        {
            DefaultWindowBorder,
            "#7C5CFF",
            "#3FB8E0",
            "#46C46A",
            "#E5B04A",
            "#F2762E",
            "#E5484D",
            "#E066B0",
            "#ECECEF",
            "#8B8B96",
        };

        /// <summary>
        /// The stored window border colour, or the default when nothing usable is stored.
        /// A hand-edited config cannot leave windows with an unreadable frame.
        /// </summary>
        public static string WindowBorderColour =>
            Normalise(App.Config.Windows.BorderColour) ?? DefaultWindowBorder;

        /// <summary>
        /// Parses "#RRGGBB", "#AARRGGBB" or a colour name. Never throws: an unusable
        /// string is simply not a colour.
        /// </summary>
        public static bool TryParseColour(string? text, out Color colour)
        {
            colour = default;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            try
            {
                if (ColorConverter.ConvertFromString(text.Trim()) is not Color parsed)
                    return false;

                colour = parsed;
                return true;
            }
            catch
            {
                // ConvertFromString throws on anything it does not recognise.
                return false;
            }
        }

        /// <summary>
        /// The canonical "#RRGGBB" for a colour the user typed or picked, or null if it is
        /// not a colour. Alpha is dropped rather than honoured: a partly transparent frame
        /// reads as the wrong colour rather than as a translucent one, because what shows
        /// through it is the app's own pixels.
        /// </summary>
        public static string? Normalise(string? text) =>
            TryParseColour(text, out var colour)
                ? $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}"
                : null;

        /// <summary>
        /// Pushes the configured colours into the application's resources. Called once at
        /// startup and again whenever Settings changes one.
        /// </summary>
        public static void Apply()
        {
            if (Application.Current == null)
                return;

            TryParseColour(WindowBorderColour, out var colour);

            var brush = new SolidColorBrush(colour);
            brush.Freeze();

            Application.Current.Resources[WindowBorderKey] = brush;
        }
    }
}
