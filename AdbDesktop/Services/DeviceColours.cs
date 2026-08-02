using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace AdbDesktop
{
    /// <summary>
    /// One colour per device, taken from a fixed palette.
    ///
    /// Not hashed from the serial: a hash spreads devices anywhere on the wheel, so two
    /// phones can land close enough that the colours are useless side by side. Eight
    /// chosen, well-separated colours cannot do that.
    ///
    /// Which one a device gets is its position in the known-device list, which is
    /// append-only and persisted -- so a device keeps its colour across restarts without
    /// anything extra being stored. Past eight devices the palette repeats; the connection
    /// panel is the legend for which is which.
    /// </summary>
    internal static class DeviceColours
    {
        /// <summary>Evenly spread around the wheel and pitched for the dark surface.</summary>
        private static readonly string[] PaletteHex =
        {
            "#E5534B", // red
            "#E8833A", // orange
            "#E5C04B", // amber
            "#6DC24B", // green
            "#3FBFA0", // teal
            "#4A9EE8", // blue
            "#9B6DE8", // violet
            "#E86DB0", // pink
        };

        private static readonly SolidColorBrush[] Brushes = PaletteHex
            .Select(hex =>
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                return brush;
            })
            .ToArray();

        public static int Count => Brushes.Length;

        /// <summary>
        /// The device's brush, or null for an icon with no device (the built-ins). The
        /// brushes are shared and frozen, so binding one per icon costs nothing.
        /// </summary>
        public static SolidColorBrush? BrushFor(string? serial)
        {
            if (string.IsNullOrEmpty(serial))
                return null;

            return Brushes[IndexFor(serial)];
        }

        private static int IndexFor(string serial)
        {
            var known = App.Config.Desktop.Devices;

            for (var i = 0; i < known.Count; i++)
            {
                if (string.Equals(known[i].Serial, serial, StringComparison.Ordinal))
                    return i % Brushes.Length;
            }

            // A device adbDesktop no longer has a record of, but whose icons are still on
            // a desktop. Nothing to count from, so spread it deterministically instead.
            return Spread(serial);
        }

        private static int Spread(string serial)
        {
            var sum = 0;
            foreach (var c in serial)
                sum = (sum * 31 + c) & 0x7FFFFFF;

            return sum % Brushes.Length;
        }
    }
}
