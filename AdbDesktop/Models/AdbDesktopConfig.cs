using System.Collections.Generic;
using System.IO;
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

    public class AdbDesktopConfig
    {
        public PathsConfig Paths { get; set; } = new();
        public DeviceConfig Device { get; set; } = new();
        public DesktopConfig Desktop { get; set; } = new();
        public TaskbarConfig Taskbar { get; set; } = new();
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
