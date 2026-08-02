using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbDesktop
{
    /// <summary>
    /// Minimal libwebp binding. WPF's imaging stack is WIC-based and has no built-in
    /// WebP codec, but modern Android build tooling emits launcher icons as .webp by
    /// default -- roughly half of a typical app's mipmap entries. Without this, those
    /// apps yield no icon candidates at all (which is one of the two reasons ASM's
    /// extraction fails so often).
    ///
    /// The DLL is resolved by absolute path out of Assets\ rather than by probing the
    /// PATH, using the same SetDllImportResolver pattern as AMPL's ScrcpyAudioNative.
    /// </summary>
    internal static class WebpDecoder
    {
        private const string LibraryName = "libwebp";

        private static bool _resolverInstalled;
        private static readonly object Sync = new();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WebPGetInfo(
            [In] byte[] data, UIntPtr dataSize, out int width, out int height);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WebPDecodeBGRA(
            [In] byte[] data, UIntPtr dataSize, out int width, out int height);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void WebPFree(IntPtr pointer);

        /// <summary>True once the bundled DLL has been located and P/Invoke works.</summary>
        public static bool IsAvailable { get; private set; }

        public static string LibraryPath => AppPaths.GetResourcePath("libwebp.dll");

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_resolverInstalled)
                    return;

                _resolverInstalled = true;

                // libwebp.dll imports libsharpyuv.dll, so the companion is pre-loaded by
                // absolute path. Registration goes through the shared resolver because
                // .NET permits only one per assembly.
                NativeAssets.Register(LibraryName, "libwebp.dll", "libsharpyuv.dll");

                // Probe with a 1x1 lossy webp so a missing or broken DLL is discovered
                // once, at startup, instead of throwing from inside the icon scan.
                try
                {
                    var probe = Convert.FromBase64String(
                        "UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==");
                    IsAvailable = WebPGetInfo(probe, (UIntPtr)probe.Length, out _, out _) != 0;
                }
                catch (Exception ex)
                {
                    IsAvailable = false;
                    Debugger.show("[WEBP] libwebp unavailable: " + ex.Message);
                }

                Debugger.show(IsAvailable
                    ? $"[WEBP] libwebp ready ({LibraryPath})."
                    : $"[WEBP] libwebp NOT available. .webp icons will be skipped. Expected at {LibraryPath}");
            }
        }

        /// <summary>
        /// Reads the dimensions from the header without decoding the pixels. Used by the
        /// size gate so the extractor only fully decodes the handful of entries that
        /// survive filtering.
        /// </summary>
        public static bool TryGetSize(byte[] data, out int width, out int height)
        {
            width = height = 0;
            if (!IsAvailable || data.Length < 16)
                return false;

            try
            {
                return WebPGetInfo(data, (UIntPtr)data.Length, out width, out height) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Decodes to a frozen BGRA32 BitmapSource, or null if the data is not a still
        /// webp (animated webp fails here, which is the desired outcome -- an animation
        /// is not a usable desktop icon).
        /// </summary>
        public static BitmapSource? Decode(byte[] data)
        {
            if (!IsAvailable || data.Length < 16)
                return null;

            var pixels = IntPtr.Zero;
            try
            {
                pixels = WebPDecodeBGRA(data, (UIntPtr)data.Length, out var width, out var height);
                if (pixels == IntPtr.Zero || width <= 0 || height <= 0)
                    return null;

                var stride = width * 4;
                var buffer = new byte[stride * height];
                Marshal.Copy(pixels, buffer, 0, buffer.Length);

                var bitmap = BitmapSource.Create(
                    width, height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Debugger.advanced("[WEBP] decode failed: " + ex.Message);
                return null;
            }
            finally
            {
                if (pixels != IntPtr.Zero)
                    WebPFree(pixels);
            }
        }
    }
}
