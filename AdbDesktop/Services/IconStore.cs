using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace AdbDesktop
{
    /// <summary>
    /// Owns the on-disk icon cache. Everything the user picks -- whether extracted from
    /// an APK or supplied from their own machine -- is re-encoded to PNG, so the desktop
    /// renderer never has to branch on format (and never has to ask libwebp for anything
    /// at paint time).
    /// </summary>
    internal static class IconStore
    {
        /// <summary>
        /// Saves a decoded image as the icon for one app on one device. Returns the
        /// filename. The same package on two phones is two icons, so the device serial is
        /// part of the name -- keying on the package alone made them share a single file,
        /// where re-iconning one silently changed the other.
        /// </summary>
        public static string? Save(string package, string deviceSerial, BitmapSource image)
        {
            if (string.IsNullOrWhiteSpace(package) || image == null)
                return null;

            try
            {
                Directory.CreateDirectory(AppPaths.IconsDir);

                var fileName = FileNameFor(package, deviceSerial);
                var fullPath = Path.Combine(AppPaths.IconsDir, fileName);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));

                // Write through a buffer so a failure part-way cannot leave a truncated
                // PNG where a previously working icon used to be.
                using (var buffer = new MemoryStream())
                {
                    encoder.Save(buffer);
                    File.WriteAllBytes(fullPath, buffer.ToArray());
                }

                Debugger.show($"[ICON] Saved icon for {package} on '{deviceSerial}' -> {fileName}");
                return fileName;
            }
            catch (Exception ex)
            {
                Debugger.show($"[ICON] Failed to save icon for {package}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Loads a user-supplied image file. Goes through libwebp for .webp and WIC for
        /// everything else, so the "bring your own image" tile accepts the same formats
        /// the extractor produces.
        /// </summary>
        public static BitmapSource? LoadFromFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                var bytes = File.ReadAllBytes(path);

                return path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                    ? WebpDecoder.Decode(bytes)
                    : IconExtractor.DecodeWithWic(bytes);
            }
            catch (Exception ex)
            {
                Debugger.show($"[ICON] Could not load {path}: {ex.Message}");
                return null;
            }
        }

        public static string GetFullPath(string iconFile) =>
            string.IsNullOrWhiteSpace(iconFile)
                ? string.Empty
                : Path.Combine(AppPaths.IconsDir, iconFile);

        /// <summary>Loads a stored icon for display. Returns null when it is missing.</summary>
        public static BitmapSource? Load(string iconFile)
        {
            var path = GetFullPath(iconFile);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                // OnLoad + a stream copy so the file is not left locked -- otherwise
                // changing an icon later would fail to overwrite it.
                var bytes = File.ReadAllBytes(path);
                return IconExtractor.DecodeWithWic(bytes);
            }
            catch (Exception ex)
            {
                Debugger.show($"[ICON] Could not load {path}: {ex.Message}");
                return null;
            }
        }

        public static void Delete(string iconFile)
        {
            var path = GetFullPath(iconFile);
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Debugger.show($"[ICON] Could not delete {path}: {ex.Message}");
            }
        }

        /// <summary>
        /// "com.example.app-1a2b3c4d5e6f.png". The package stays in front so the cache is
        /// still readable by eye; the hash of package + serial is what actually keeps two
        /// phones' copies of the same app apart. Deterministic, so re-iconning overwrites
        /// that one file rather than leaving orphans behind -- and so a caller holding
        /// only a package and a serial (the notification panel) can find the icon of an
        /// app that is already on a desktop without being handed the filename.
        /// </summary>
        public static string FileNameFor(string package, string deviceSerial)
        {
            // Length-prefixed so a package ending in the separator cannot collide with a
            // serial starting with it.
            var key = $"{package.Length}:{package}|{deviceSerial ?? string.Empty}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));

            return $"{Sanitize(package)}-{Convert.ToHexString(hash, 0, 6).ToLowerInvariant()}.png";
        }

        private static string Sanitize(string package)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(package.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
