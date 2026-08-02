using System.IO;

namespace AdbDesktop
{
    /// <summary>
    /// Portable-vs-installed path resolution.
    ///
    /// Note the deliberate difference from AMPL: AMPL's ResourceRoot points at the
    /// *shared* %AppData%\Snail\Assets folder, which ASM also uses. AdbDesktop keeps its
    /// own Assets folder under its own product directory so it can never overwrite the
    /// adb.exe those two apps depend on.
    /// </summary>
    internal static class AppPaths
    {
        private const string CompanyFolder = "Snail";
        private const string ProductFolder = "AdbDesktop";

        private static readonly Lazy<bool> PortableCheck =
            new(() => File.Exists(Path.Combine(BaseDirectory, "portable.mode")));

        internal static bool IsPortable => PortableCheck.Value;

        internal static string BaseDirectory => Path.GetFullPath(AppContext.BaseDirectory);

        internal static string DataRoot => IsPortable
            ? BaseDirectory
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                CompanyFolder,
                ProductFolder);

        /// <summary>
        /// Where the bundled native binaries (adb.exe, libwebp.dll) live. In both modes
        /// this is the Assets folder next to the executable -- unlike the config/data
        /// root, these ship with the build and are never user-written.
        /// </summary>
        internal static string ResourceRoot => Path.Combine(BaseDirectory, "Assets");

        /// <summary>Icons the user picked, one PNG per package.</summary>
        internal static string IconsDir => GetDataPath("icons");

        /// <summary>
        /// One JSON file per desktop. Split out of the main config so a desktop's layout
        /// can be read, written, backed up or thrown away on its own, and so a corrupt
        /// desktop file cannot take the device list down with it.
        /// </summary>
        internal static string DesktopsDir => GetDataPath("desktops");

        /// <summary>Scratch space for pulled APKs. Cleaned per-package after each add.</summary>
        internal static string PullTempDir => Path.Combine(Path.GetTempPath(), "AdbDesktop", "pull");

        internal static string AdbPath => GetResourcePath("adb.exe");

        internal static string ConfigPath => GetDataPath("adbdesktop.json");

        internal static string GetDataPath(params string[] parts)
        {
            var all = new string[parts.Length + 1];
            all[0] = DataRoot;
            Array.Copy(parts, 0, all, 1, parts.Length);
            return Path.Combine(all);
        }

        internal static string GetResourcePath(string fileName) => Path.Combine(ResourceRoot, fileName);

        internal static void EnsureDataDirectories()
        {
            try
            {
                Directory.CreateDirectory(DataRoot);
                Directory.CreateDirectory(IconsDir);
                Directory.CreateDirectory(DesktopsDir);
            }
            catch
            {
                // Non-fatal: config/icon writes report their own failures.
            }
        }
    }
}
