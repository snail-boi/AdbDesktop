using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbDesktop
{
    /// <summary>
    /// Crops the transparent margin off an icon so the artwork itself can fill the tile.
    ///
    /// Android icons are routinely drawn small inside a much larger canvas, so displaying
    /// them at their nominal size leaves one app looking half the size of the next. What
    /// the eye reads as "the icon" is the opaque part, which is what this measures.
    /// </summary>
    internal static class IconTrim
    {
        /// <summary>Anything below this is margin, not artwork -- soft shadows included.</summary>
        private const byte AlphaFloor = 8;

        /// <summary>
        /// Returns the artwork with its transparent border removed, or the original when
        /// there is nothing to crop (and when the image is entirely transparent, which
        /// would otherwise leave nothing to show at all).
        /// </summary>
        public static BitmapSource? CropToContent(BitmapSource? source)
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

                var left = width;
                var top = height;
                var right = -1;
                var bottom = -1;

                for (var y = 0; y < height; y++)
                {
                    var row = y * stride;

                    for (var x = 0; x < width; x++)
                    {
                        if (pixels[row + x * 4 + 3] < AlphaFloor)
                            continue;

                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        bottom = y;
                    }
                }

                // Fully transparent, or already edge to edge: nothing worth cropping.
                if (right < left || bottom < top)
                    return source;

                if (left == 0 && top == 0 && right == width - 1 && bottom == height - 1)
                    return source;

                var cropped = new CroppedBitmap(bgra,
                    new Int32Rect(left, top, right - left + 1, bottom - top + 1));

                cropped.Freeze();
                return cropped;
            }
            catch (Exception ex)
            {
                Debugger.show($"[ICON] Could not trim icon: {ex.Message}");
                return source;
            }
        }
    }
}
