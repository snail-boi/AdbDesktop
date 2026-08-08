using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AdbDesktop
{
    public enum WirelessMode
    {
        TcpIp = 0,
        WirelessDebugging = 1,
        UsbOnly = 2
    }

    public class PathsConfig
    {
        public string Adb { get; set; } = string.Empty;
    }

    public class DeviceConfig
    {
        public string SelectedDeviceUSB { get; set; } = string.Empty;

        /// <summary>
        /// "ip:5555" in TcpIp mode, or the last-known ip:port in Wireless Debugging mode.
        /// In WD mode this is only a cache -- the port is reassigned every time wireless
        /// debugging toggles, so <see cref="MdnsServiceName"/> is the durable identity.
        /// </summary>
        public string SelectedDeviceWiFi { get; set; } = string.Empty;

        public string SelectedDeviceName { get; set; } = string.Empty;

        public WirelessMode WifiMode { get; set; } = WirelessMode.WirelessDebugging;

        /// <summary>"adb-XXXXXXXX-XXXXXX" -- stable across reboots and IP changes once paired.</summary>
        public string MdnsServiceName { get; set; } = string.Empty;

        public bool IsWifiEnabled { get; set; }
    }

    /// <summary>
    /// A device the user has added to AdbDesktop.
    ///
    /// Distinct from "currently connected": adb seeing a device does not put it on the
    /// desktop. Adding is explicit, or automatic for a device whose own
    /// <see cref="AutoAdd"/> flag is set, and once added a device keeps its icons forever
    /// -- only its desktop tab comes and goes with the connection.
    /// </summary>
    public class KnownDevice
    {
        /// <summary>adb serial: the identity. Models are not unique.</summary>
        public string Serial { get; set; } = string.Empty;

        /// <summary>Last seen model name, for labelling while disconnected.</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Re-add this device by itself whenever it comes online, including after a
        /// restart. This is the ONLY thing that persists about added-ness.
        ///
        /// Being added is otherwise a runtime state: it lasts until the device drops or
        /// AdbDesktop closes, and then has to be done again from the connection panel. That
        /// difference is the whole point of the two controls -- if adding persisted by
        /// itself, this flag would do nothing.
        ///
        /// Cleared when the device is removed, so a removal cannot be undone a second
        /// later by the very flag the user just acted against.
        /// </summary>
        public bool AutoAdd { get; set; }

        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    }

    public class DesktopIcon
    {
        public string Package { get; set; } = string.Empty;
        public string Caption { get; set; } = string.Empty;

        /// <summary>Filename only, relative to <see cref="AppPaths.IconsDir"/>.</summary>
        public string IconFile { get; set; } = string.Empty;

        /// <summary>
        /// Which device runs this app. Empty only for icons migrated from before
        /// multi-device support, which are adopted by the first device added.
        /// </summary>
        public string DeviceSerial { get; set; } = string.Empty;

        /// <summary>
        /// Grid coordinates, not pixels -- so the layout survives a window resize
        /// instead of icons drifting off the edge.
        ///
        /// One pair, because an icon lives on exactly one desktop: the one that was on
        /// screen when it was added. Which desktop that is comes from the file it is
        /// stored in, so it is not repeated here.
        /// </summary>
        public int Col { get; set; }
        public int Row { get; set; }
    }

    /// <summary>
    /// Everything about one desktop, stored as its own file. Icons and wallpaper travel
    /// together because they are both "what this desktop looks like".
    /// </summary>
    public class DesktopLayout
    {
        public List<DesktopIcon> Icons { get; set; } = new();

        /// <summary>
        /// Absolute path to the wallpaper image, or empty for the plain background.
        /// A path rather than a copy: the user picked a file they already have, and
        /// duplicating it into the data folder would just go stale.
        /// </summary>
        public string Wallpaper { get; set; } = string.Empty;

        /// <summary>Fill, Fit, Stretch, Center or Tile. See WallpaperFit.</summary>
        public string WallpaperFit { get; set; } = "Fill";
    }

    public class DesktopConfig
    {
        /// <summary>
        /// Devices AdbDesktop has seen. Icons are NOT here -- they live in one file per
        /// desktop under <see cref="AppPaths.DesktopsDir"/>.
        /// </summary>
        public List<KnownDevice> Devices { get; set; } = new();
    }

    /// <summary>
    /// Shell chrome settings. Global rather than per desktop: the taskbar is the shell,
    /// and having it change shape when you switch desktops would be disorienting.
    /// </summary>
    public class TaskbarConfig
    {
        public bool ShowNavButtons { get; set; } = true;

        /// <summary>Recents, home, back -- the Samsung order -- instead of back, home, recents.</summary>
        public bool ReverseNavButtons { get; set; }

        /// <summary>The open-window buttons.</summary>
        public bool ShowWindowTabs { get; set; } = true;

        public bool ShowNotifications { get; set; } = true;
        public bool ShowBattery { get; set; } = true;
        public bool ShowClock { get; set; } = true;

        /// <summary>Collapse the search bar to a single button.</summary>
        public bool SearchIconOnly { get; set; }

        /// <summary>
        /// Pin everything to the unified desktop and hide the U tab and the numbered
        /// boxes. The per-device desktops still exist on disk; they are just unreachable.
        /// </summary>
        public bool DisableMultiDesktop { get; set; }

        /// <summary>.NET format strings. "t" is the locale's short time.</summary>
        public string TimeFormat { get; set; } = "t";
        public string DateFormat { get; set; } = "ddd dd/MM";
    }

    /// <summary>
    /// The tile shapes an icon can take. Names are what is written to the config, so they
    /// are spelled out here rather than being an enum -- a hand-edited or older file with
    /// an unknown shape falls back instead of failing to parse.
    /// </summary>
    public static class IconShapes
    {
        public const string Square = "Square";
        public const string Circle = "Circle";
        public const string Squircle = "Squircle";

        public static readonly IReadOnlyList<string> All = new[] { Square, Circle, Squircle };

        public static bool IsKnown(string? shape) =>
            All.Any(s => string.Equals(s, shape, StringComparison.Ordinal));

        /// <summary>
        /// Corner radius for a tile of the given size. The squircle is a rounded rectangle
        /// at roughly the curve Android and iOS use, not a true superellipse -- close
        /// enough at 56px, and it stays a Border rather than becoming a clip geometry.
        /// </summary>
        public static double RadiusFor(string? shape, double size) => shape switch
        {
            Square => 0,
            Circle => size / 2,
            _ => size * 0.23,
        };
    }

    /// <summary>
    /// How an icon says which device it runs on. Only ever drawn on the unified desktop:
    /// a device's own desktop answers the question by existing, so a marker there would
    /// be noise on every icon at once.
    /// </summary>
    public static class DeviceMarkers
    {
        public const string None = "None";

        /// <summary>A bar under the icon in the device's colour, hashed from its serial.</summary>
        public const string Colour = "Colour";

        /// <summary>The device's number, as a small badge on the icon.</summary>
        public const string Badge = "Badge";

        public static readonly IReadOnlyList<string> All = new[] { None, Colour, Badge };

        public static bool IsKnown(string? marker) =>
            All.Any(m => string.Equals(m, marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// How desktop icons are drawn. Global rather than per desktop, for the same reason
    /// the taskbar is: this is the shell's look, and having icons change shape when you
    /// switch desktops would be disorienting.
    /// </summary>
    public class IconsConfig
    {
        /// <summary>One of <see cref="DeviceMarkers"/>.</summary>
        public string DeviceMarker { get; set; } = DeviceMarkers.None;

        /// <summary>The tile behind the artwork. Off leaves the icon on the wallpaper.</summary>
        public bool ShowBackground { get; set; } = true;

        /// <summary>
        /// Crop the transparent margin off the artwork and let what is left fill the tile.
        /// Android icons are often drawn small inside a large canvas, which makes them
        /// look undersized next to ones drawn edge to edge.
        /// </summary>
        public bool ScaleToFit { get; set; }

        /// <summary>One of <see cref="IconShapes"/>.</summary>
        public string Shape { get; set; } = IconShapes.Squircle;
    }

    /// <summary>
    /// Knobs that can make things worse. Nothing here needs touching for normal use.
    /// </summary>
    public class AdvancedConfig
    {
        /// <summary>
        /// How long a window must stop changing size before the new size is sent to the
        /// device, in milliseconds.
        ///
        /// Not a cosmetic delay. Each size that goes out makes Android reconfigure the
        /// virtual display and restart the encoder, and our decoder reopens against the
        /// new stream -- so this is a rate limit on a genuinely expensive round trip,
        /// not on a redraw. The window is not blank while it waits: the last frame is
        /// stretched to the new size, which is why the delay is barely visible.
        /// </summary>
        public int ResizeDelayMs { get; set; } = DefaultResizeDelayMs;

        /// <summary>
        /// Full diagnostic logging: every adb command and its output, plus scrcpy's own
        /// debug-level messages, into advanced_debug.log instead of the normal log.
        ///
        /// Off by default because it is verbose and rewritten from scratch each time it
        /// is turned on. It is what to enable when a session fails to connect: the reason
        /// the device-side server gave is a debug-level line, and is otherwise dropped.
        /// </summary>
        public bool DebugLogging { get; set; }

        public const int DefaultResizeDelayMs = 220;

        /// <summary>
        /// Bounds for <see cref="ResizeDelayMs"/>. Deliberately wide: what suits a given
        /// phone is the user's call. The floor is 1 rather than 0 only because zero or
        /// negative is not a delay at all -- it would leave the timer firing continuously.
        /// </summary>
        public const int MinResizeDelayMs = 1;
        public const int MaxResizeDelayMs = 2000;

        /// <summary>
        /// Below this, resizes are issued faster than the device can finish one, so the
        /// encoder-restart path never gets to settle. Worth a blunter warning than
        /// "this may stutter".
        /// </summary>
        public const int RiskyResizeDelayMs = 50;
    }

    public class AdbDesktopConfig
    {
        /// <summary>
        /// The first-run guide has been through once. Top level rather than inside one of
        /// the sections below, because it is about the app as a whole rather than about
        /// devices, desktops or chrome -- and a fresh config file being all-defaults is
        /// exactly what "first run" means.
        ///
        /// Set when the guide is closed however it is closed, so backing out of it does
        /// not bring it back on the next launch. Settings can reopen it on demand.
        /// </summary>
        public bool WelcomeSeen { get; set; }

        public PathsConfig Paths { get; set; } = new();
        public DeviceConfig Device { get; set; } = new();
        public DesktopConfig Desktop { get; set; } = new();
        public TaskbarConfig Taskbar { get; set; } = new();
        public IconsConfig Icons { get; set; } = new();
        public AdvancedConfig Advanced { get; set; } = new();
    }

    /// <summary>
    /// Load/Save for <see cref="AdbDesktopConfig"/>. Load never throws: a missing, empty or
    /// corrupt file yields defaults.
    /// </summary>
    public static class AdbDesktopConfigManager
    {
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public static string ConfigPath => AppPaths.ConfigPath;

        public static AdbDesktopConfig Load()
        {
            AdbDesktopConfig? config = null;

            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    if (!string.IsNullOrWhiteSpace(json))
                        config = JsonSerializer.Deserialize<AdbDesktopConfig>(json);
                }
            }
            catch (Exception ex)
            {
                Debugger.show("Config load failed, using defaults: " + ex.Message);
            }

            return Normalize(config ?? new AdbDesktopConfig());
        }

        public static void Save(AdbDesktopConfig config)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Normalize(config), WriteOptions));
            }
            catch (Exception ex)
            {
                Debugger.show("Config save failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Null-coalesces every sub-object so callers never have to null-check, and
        /// re-points the adb path at the bundled binary when it is unset or stale.
        /// </summary>
        private static AdbDesktopConfig Normalize(AdbDesktopConfig config)
        {
            config.Paths ??= new PathsConfig();
            config.Device ??= new DeviceConfig();
            config.Desktop ??= new DesktopConfig();
            config.Taskbar ??= new TaskbarConfig();
            config.Icons ??= new IconsConfig();
            config.Advanced ??= new AdvancedConfig();

            config.Advanced.ResizeDelayMs = Math.Clamp(config.Advanced.ResizeDelayMs,
                                                       AdvancedConfig.MinResizeDelayMs,
                                                       AdvancedConfig.MaxResizeDelayMs);

            if (!IconShapes.IsKnown(config.Icons.Shape))
                config.Icons.Shape = IconShapes.Squircle;

            if (!DeviceMarkers.IsKnown(config.Icons.DeviceMarker))
                config.Icons.DeviceMarker = DeviceMarkers.None;

            if (string.IsNullOrWhiteSpace(config.Taskbar.TimeFormat))
                config.Taskbar.TimeFormat = "t";
            if (string.IsNullOrWhiteSpace(config.Taskbar.DateFormat))
                config.Taskbar.DateFormat = "ddd dd/MM";

            config.Desktop.Devices ??= new List<KnownDevice>();
            config.Desktop.Devices.RemoveAll(d => d == null || string.IsNullOrWhiteSpace(d.Serial));

            if (string.IsNullOrWhiteSpace(config.Paths.Adb) || !File.Exists(config.Paths.Adb))
                config.Paths.Adb = AppPaths.AdbPath;

            return config;
        }
    }
}
