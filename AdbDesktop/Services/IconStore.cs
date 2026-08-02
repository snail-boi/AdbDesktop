using System.IO;
using System.Linq;
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
        /// <summary>Saves a decoded image as the icon for a package. Returns the filename.</summary>
        public static string? Save(string package, BitmapSource image)
        {
            if (string.IsNullOrWhiteSpace(package) || image == null)
                return null;

            try
            {
                Directory.CreateDirectory(AppPaths.IconsDir);

                var fileName = Sanitize(package) + ".png";
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

                Debugger.show($"[ICON] Saved icon for {package} -> {fileName}");
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

        private static string Sanitize(string package)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(package.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
