using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AdbDesktop
{
    /// <summary>
    /// Reads and writes one desktop's icons.
    ///
    /// Each desktop is its own file under <see cref="AppPaths.DesktopsDir"/> rather than a
    /// section of the main config. Desktops are independent -- an icon lives on exactly
    /// one -- so a file each keeps them that way, lets one be deleted or backed up alone,
    /// and stops a corrupt layout from taking the device list with it.
    ///
    /// Like the config manager, loading never throws: a missing or unreadable file yields
    /// an empty desktop.
    /// </summary>
    internal static class DesktopStore
    {
        private const string UnifiedId = "unified";

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        /// <summary>
        /// File name for a desktop. The unified desktop has no serial, hence the fixed
        /// name; a device desktop is keyed by its hardware serial, which is alphanumeric
        /// in practice but sanitised anyway so nothing can escape the folder.
        /// </summary>
        public static string PathFor(string? desktopSerial)
        {
            var id = string.IsNullOrEmpty(desktopSerial) ? UnifiedId : Sanitise(desktopSerial);
            return Path.Combine(AppPaths.DesktopsDir, id + ".json");
        }

        private static string Sanitise(string serial)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(serial.Length);

            foreach (var c in serial)
                sb.Append(invalid.Contains(c) ? '_' : c);

            var name = sb.ToString().Trim('.', ' ');
            return string.IsNullOrEmpty(name) ? "device" : name;
        }

        public static DesktopLayout Load(string? desktopSerial)
        {
            var path = PathFor(desktopSerial);

            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var layout = JsonSerializer.Deserialize<DesktopLayout>(json);
                        if (layout != null)
                            return Normalize(layout);
                    }
                }
            }
            catch (Exception ex)
            {
                Debugger.show($"[DESKTOP] Load failed for '{path}': {ex.Message}");
            }

            return new DesktopLayout();
        }

        public static void Save(string? desktopSerial, DesktopLayout layout)
        {
            var path = PathFor(desktopSerial);

            try
            {
                Directory.CreateDirectory(AppPaths.DesktopsDir);
                File.WriteAllText(path, JsonSerializer.Serialize(Normalize(layout), WriteOptions));
            }
            catch (Exception ex)
            {
                Debugger.show($"[DESKTOP] Save failed for '{path}': {ex.Message}");
            }
        }

        /// <summary>
        /// Puts one wallpaper on every desktop: the named ones (unified plus each known
        /// device, which may not have a file yet) and any other desktop file already on
        /// disk, so a device that is not currently known is not skipped.
        ///
        /// Only the wallpaper fields are touched -- each desktop keeps its own icons.
        /// </summary>
        public static void ApplyWallpaperToAll(string wallpaper, string fit, IEnumerable<string> deviceSerials)
        {
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Unified first, then every device AdbDesktop knows about.
            foreach (var serial in new[] { string.Empty }.Concat(deviceSerials))
            {
                var path = PathFor(serial);
                if (!handled.Add(path))
                    continue;

                WriteWallpaper(path, wallpaper, fit);
            }

            try
            {
                if (!Directory.Exists(AppPaths.DesktopsDir))
                    return;

                foreach (var path in Directory.EnumerateFiles(AppPaths.DesktopsDir, "*.json"))
                {
                    if (handled.Add(path))
                        WriteWallpaper(path, wallpaper, fit);
                }
            }
            catch (Exception ex)
            {
                Debugger.show("[DESKTOP] Sweeping desktop files failed: " + ex.Message);
            }
        }

        private static void WriteWallpaper(string path, string wallpaper, string fit)
        {
            try
            {
                var layout = LoadFrom(path);
                layout.Wallpaper = wallpaper;
                layout.WallpaperFit = fit;

                Directory.CreateDirectory(AppPaths.DesktopsDir);
                File.WriteAllText(path, JsonSerializer.Serialize(Normalize(layout), WriteOptions));
            }
            catch (Exception ex)
            {
                Debugger.show($"[DESKTOP] Wallpaper write failed for '{path}': {ex.Message}");
            }
        }

        private static DesktopLayout LoadFrom(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(json))
                        return JsonSerializer.Deserialize<DesktopLayout>(json) ?? new DesktopLayout();
                }
            }
            catch (Exception ex)
            {
                Debugger.show($"[DESKTOP] Load failed for '{path}': {ex.Message}");
            }

            return new DesktopLayout();
        }

        /// <summary>Drops a desktop's file. Used when a device is forgotten for good.</summary>
        public static void Delete(string? desktopSerial)
        {
            try
            {
                var path = PathFor(desktopSerial);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Debugger.show($"[DESKTOP] Delete failed: {ex.Message}");
            }
        }

        private static DesktopLayout Normalize(DesktopLayout layout)
        {
            layout.Icons ??= new List<DesktopIcon>();
            layout.Icons.RemoveAll(i => i == null || string.IsNullOrWhiteSpace(i.Package));

            if (string.IsNullOrWhiteSpace(layout.WallpaperFit))
                layout.WallpaperFit = "Fill";

            // A wallpaper the user has since deleted or moved: forget it rather than
            // failing to decode it on every desktop switch.
            if (!string.IsNullOrWhiteSpace(layout.Wallpaper) && !File.Exists(layout.Wallpaper))
                layout.Wallpaper = string.Empty;

            foreach (var icon in layout.Icons)
            {
                if (icon.Col < 0) icon.Col = 0;
                if (icon.Row < 0) icon.Row = 0;

                if (string.IsNullOrWhiteSpace(icon.Caption))
                    icon.Caption = icon.Package;
            }

            return layout;
        }
    }
}
