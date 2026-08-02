using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace AdbDesktop
{
    /// <summary>
    /// One DllImport resolver for the whole assembly.
    ///
    /// .NET allows <see cref="NativeLibrary.SetDllImportResolver"/> to be called only
    /// once per assembly -- a second call throws "A resolver is already set". Since
    /// AdbDesktop loads more than one native library from Assets\ (libwebp for icon
    /// decoding, scrcpy_video for mirroring), they share this registry instead of each
    /// installing their own.
    ///
    /// Everything is resolved by absolute path, which also makes Windows resolve each
    /// library's own dependencies from Assets\ rather than the PATH.
    /// </summary>
    internal static class NativeAssets
    {
        private sealed record Entry(string FileName, string[] Companions);

        private static readonly Dictionary<string, Entry> Registry =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly object Sync = new();
        private static bool _installed;

        /// <summary>
        /// Maps a DllImport name to a file in Assets\.
        /// </summary>
        /// <param name="companions">
        /// Libraries the main one imports, pre-loaded by absolute path first so the
        /// import resolves from memory rather than depending on the OS search order.
        /// </param>
        public static void Register(string importName, string fileName, params string[] companions)
        {
            lock (Sync)
            {
                Registry[importName] = new Entry(fileName, companions);
                Install();
            }
        }

        private static void Install()
        {
            if (_installed)
                return;

            _installed = true;

            NativeLibrary.SetDllImportResolver(
                typeof(NativeAssets).Assembly,
                (name, assembly, searchPath) =>
                {
                    Entry? entry;
                    lock (Sync)
                    {
                        if (!Registry.TryGetValue(name, out entry))
                            return IntPtr.Zero;
                    }

                    foreach (var companion in entry.Companions)
                    {
                        var companionPath = AppPaths.GetResourcePath(companion);
                        if (File.Exists(companionPath))
                            NativeLibrary.TryLoad(companionPath, out _);
                    }

                    var path = AppPaths.GetResourcePath(entry.FileName);
                    return File.Exists(path) && NativeLibrary.TryLoad(path, out var handle)
                        ? handle
                        : IntPtr.Zero;
                });
        }
    }
}
