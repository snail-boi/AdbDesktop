using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AdbDesktop
{
    /// <summary>
    /// A drawn battery indicator: outlined body, proportional fill, terminal cap, and a
    /// bolt while charging.
    ///
    /// Geometry follows AMPL's "classic" style, minus its config-driven machinery (three
    /// visual styles, inside/outside percentage and bolt placement). The taskbar needs
    /// exactly one look, and the percentage is a separate TextBlock beside it.
    /// </summary>
    public sealed class BatteryGlyph : Control
    {
        /*
         * The uncharged remainder is light grey rather than dark, because the
         * percentage is drawn inside the pill: a dark empty portion would swallow the
         * text as soon as the level dropped below it. Light empty + light fill means
         * dark text reads at every level. (Same reasoning as AMPL's pill style.)
         */
        private static readonly Brush EmptyBrush = Frozen(0xC8, 0xC8, 0xD0);
        private static readonly Brush GoodBrush = Frozen(0x46, 0xC4, 0x6A);
        private static readonly Brush LowBrush = Frozen(0xE5, 0x48, 0x4D);
        private static readonly Brush NormalBrush = Frozen(0xEC, 0xEC, 0xEF);
        private static readonly Brush TextBrushDark = Frozen(0x14, 0x14, 0x18);

        private static readonly Typeface LabelTypeface =
            new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public static readonly DependencyProperty LevelProperty =
            DependencyProperty.Register(nameof(Level), typeof(int), typeof(BatteryGlyph),
                new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ChargingProperty =
            DependencyProperty.Register(nameof(Charging), typeof(bool), typeof(BatteryGlyph),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>0-100, or negative when unknown.</summary>
        public int Level
        {
            get => (int) GetValue(LevelProperty);
            set => SetValue(LevelProperty, value);
        }

        public bool Charging
        {
            get => (bool) GetValue(ChargingProperty);
            set => SetValue(ChargingProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var w = ActualWidth;
            var h = ActualHeight;
            if (w <= 0 || h <= 0)
                return;

            // One UI style: a solid pill, no outline and no terminal cap.
            var pill = new Rect(0, 0, w, h);
            var radius = h / 2.0;

            // The uncharged remainder stays visible, so the fill reads as a proportion
            // rather than just a colour change.
            dc.DrawRoundedRectangle(EmptyBrush, null, pill, radius, radius);

            var level = Math.Clamp(Level, 0, 100);
            if (Level < 0)
                return;

            if (level > 0)
            {
                var fillBrush = level <= 15 ? LowBrush : level >= 100 ? GoodBrush : NormalBrush;

                // Draw the full rounded pill but clip it to the charged width, so the
                // left cap stays round and the right edge is a clean vertical cut.
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, w * (level / 100.0), h)));
                dc.DrawRoundedRectangle(fillBrush, null, pill, radius, radius);
                dc.Pop();
            }

            // Percentage inside the pill. The charging bolt is drawn outside by the
            // taskbar, so nothing competes with the number for space.
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var label = new FormattedText(
                level.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                h * 0.62,
                TextBrushDark,
                dpi);

            dc.DrawText(label, new Point((w - label.Width) / 2, (h - label.Height) / 2));
        }
    }
}
