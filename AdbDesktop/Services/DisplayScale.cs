using System.Windows;
using System.Windows.Media;

namespace AdbDesktop
{
    /// <summary>
    /// The factor between WPF's device-independent pixels and the monitor's real ones.
    ///
    /// This exists because a mirrored window has two different sizes and only one of them
    /// is the right one to ask Android for. Layout is in DIPs: a window 900 units wide is
    /// 900 units wide whatever the display scaling is. What the user actually looks at is
    /// physical pixels, and at 150% scaling that same window covers 1350 of them. Creating
    /// the virtual display at the DIP size therefore captures 900 pixels and lets WPF
    /// stretch them over 1350, which is soft no matter how high the bit rate goes.
    ///
    /// So every size heading for the device is multiplied by this, and everything else
    /// stays in DIPs. Touch coordinates need no adjustment: they are normalised against
    /// the video area before being sent, so they do not care what either size is.
    ///
    /// Global rather than per window because all the app windows are WPF content inside
    /// one real HWND, so they always share its scaling.
    /// </summary>
    internal static class DisplayScale
    {
        /// <summary>
        /// 1.0 at 100% scaling, 1.5 at 150%, and so on. Starts at 1.0 so that anything
        /// running before the shell window exists still gets a sane size.
        /// </summary>
        public static double Current { get; private set; } = 1.0;

        /// <summary>Raised after <see cref="Current"/> changes, on the UI thread.</summary>
        public static event Action? Changed;

        /// <summary>
        /// Follows a window's scaling for the rest of its life: reads it now, and again
        /// whenever the window moves to a monitor with different scaling.
        /// </summary>
        public static void Track(Window window)
        {
            Apply(VisualTreeHelper.GetDpi(window).DpiScaleX);

            window.DpiChanged += (_, e) => Apply(e.NewDpi.DpiScaleX);
        }

        private static void Apply(double scale)
        {
            // Sanity bound rather than trust: a bad value here would be multiplied into
            // every virtual display size, and the device refuses anything over 8192.
            if (double.IsNaN(scale) || scale <= 0)
                scale = 1.0;

            scale = Math.Clamp(scale, 1.0, 4.0);

            if (Math.Abs(scale - Current) < 0.001)
                return;

            Current = scale;
            Changed?.Invoke();
        }

        /// <summary>
        /// Converts a DIP length into the physical pixels behind it, which is what the
        /// Android virtual display should be sized in.
        /// </summary>
        public static int ToPixels(double dips) =>
            (int) Math.Round(Math.Max(1, dips) * Current);
    }
}
