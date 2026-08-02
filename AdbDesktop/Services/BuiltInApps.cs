using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbDesktop
{
    /// <summary>
    /// Desktop entries that AdbDesktop provides itself rather than pulling off a device.
    ///
    /// There is exactly one today: AdbDesktop Settings. It earns its place before it does
    /// anything useful because it gives the upcoming window system a target that has no
    /// scrcpy involvement at all -- when a window misbehaves, opening this one says
    /// immediately whether the fault is in our window code or in the mirroring.
    /// Later it also becomes the home for AdbDesktop's own settings.
    /// </summary>
    internal static class BuiltInApps
    {
        /// <summary>
        /// Reserved id. The colon is deliberate: it is illegal in an Android package
        /// name, so this can never collide with a real app pulled from a device.
        /// </summary>
        public const string SettingsPackage = "adbdesktop:settings";

        public const string SettingsCaption = "adbDesktop Settings";

        public static bool IsBuiltIn(string? package) =>
            string.Equals(package, SettingsPackage, StringComparison.Ordinal);

        /// <summary>Settings specifically. Today the only built-in, but not by definition.</summary>
        public static bool IsSettings(string? package) =>
            string.Equals(package, SettingsPackage, StringComparison.Ordinal);

        private static BitmapSource? _settingsIcon;

        /// <summary>
        /// The drawn fallback icon: a white cogwheel on a red rounded square. Generated
        /// rather than shipped as an asset, so it stays crisp and needs no file on disk.
        /// Cached because it never changes.
        /// </summary>
        public static BitmapSource SettingsIcon => _settingsIcon ??= DrawSettingsIcon();

        private static BitmapSource DrawSettingsIcon()
        {
            const int size = 128;
            const double centre = size / 2.0;

            const double bodyRadius = 34;   // solid disc the teeth grow out of
            const double toothReach = 47;   // outer tip of a tooth
            const double toothWidth = 15;
            const double holeRadius = 13;
            const int toothCount = 8;

            var gear = BuildGearGeometry(centre, bodyRadius, toothReach, toothWidth, holeRadius, toothCount);

            var background = new LinearGradientBrush(
                Color.FromRgb(0xD8, 0x2A, 0x2A),
                Color.FromRgb(0xA5, 0x14, 0x14),
                new Point(0, 0), new Point(1, 1));
            background.Freeze();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(background, null, new Rect(0, 0, size, size), 29, 29);
                dc.DrawGeometry(Brushes.White, null, gear);
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static Geometry BuildGearGeometry(
            double centre, double bodyRadius, double toothReach,
            double toothWidth, double holeRadius, int toothCount)
        {
            var origin = new Point(centre, centre);

            // Start from the disc, union a rotated tooth on at a time, then punch the
            // centre out. Unioning (rather than one EvenOdd group) is what stops the
            // overlap between each tooth and the disc from cancelling itself out.
            Geometry gear = new EllipseGeometry(origin, bodyRadius, bodyRadius);

            for (var i = 0; i < toothCount; i++)
            {
                // Deliberately overlaps the disc so the union leaves no seam.
                var tooth = new RectangleGeometry(
                    new Rect(centre - toothWidth / 2,
                             centre - toothReach,
                             toothWidth,
                             toothReach - bodyRadius + 8),
                    3, 3)
                {
                    Transform = new RotateTransform(i * (360.0 / toothCount), centre, centre)
                };

                gear = new CombinedGeometry(GeometryCombineMode.Union, gear, tooth);
            }

            gear = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                gear,
                new EllipseGeometry(origin, holeRadius, holeRadius));

            gear.Freeze();
            return gear;
        }
    }
}
