using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbDesktop
{
    /// <summary>
    /// Turns an icon grey for display while its device is away. Done in pixels rather
    /// than with a WPF effect because the alpha channel has to survive: app icons are
    /// mostly transparent, and flattening them would leave a square block on the tile.
    /// </summary>
    internal static class IconGreyscale
    {
        /// <summary>
        /// Returns a frozen, desaturated copy. The original is handed back unchanged if
        /// anything about the conversion fails -- a colourful icon beats no icon.
        /// </summary>
        public static BitmapSource? Desaturate(BitmapSource? source)
        {
            if (source == null)
                return null;

            try
            {
                BitmapSource bgra = source.Format == PixelFormats.Bgra32
                    ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

                var width = bgra.PixelWidth;
                var height = bgra.PixelHeight;
                if (width <= 0 || height <= 0)
                    return source;

                var stride = width * 4;
                var pixels = new byte[stride * height];
                bgra.CopyPixels(pixels, stride, 0);

                for (var i = 0; i < pixels.Length; i += 4)
                {
                    // Rec. 601 luma, fixed point. Alpha (i + 3) is left alone.
                    var luma = (byte)((pixels[i + 2] * 77 + pixels[i + 1] * 150 + pixels[i] * 29) >> 8);
                    pixels[i] = luma;
                    pixels[i + 1] = luma;
                    pixels[i + 2] = luma;
                }

                var grey = BitmapSource.Create(
                    width, height, bgra.DpiX, bgra.DpiY,
                    PixelFormats.Bgra32, null, pixels, stride);

                grey.Freeze();
                return grey;
            }
            catch (Exception ex)
            {
                Debugger.show($"[ICON] Could not desaturate icon: {ex.Message}");
                return source;
            }
        }
    }
}
