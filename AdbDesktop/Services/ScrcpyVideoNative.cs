using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AdbDesktop
{
    /// <summary>
    /// P/Invoke surface for scrcpy_video.dll (our video-only scrcpy port, built from
    /// upstream v4.1 source under "scrcpy video dll/scrcpy-video").
    ///
    /// The DLL and its FFmpeg dependencies live in Assets\ rather than on the probing
    /// path, so they are resolved by absolute path -- loading scrcpy_video.dll that way
    /// also makes Windows resolve its sibling avcodec/avutil/swscale from the same
    /// directory.
    /// </summary>
    internal static class ScrcpyVideoNative
    {
        private const string DllName = "scrcpy_video";

        private static bool _resolverInstalled;
        private static readonly object Sync = new();

        public static bool IsAvailable { get; private set; }

        public static string LibraryPath => AppPaths.GetResourcePath("scrcpy_video.dll");
        public static string ServerPath => AppPaths.GetResourcePath("scrcpy-server-v4.1");

        public const int LogVerbose = 0, LogDebug = 1, LogInfo = 2, LogWarn = 3, LogError = 4;

        public enum Event
        {
            Connected = 0,
            ConnectionFailed = 1,
            StreamStarted = 2,
            StreamStopped = 3,
            Disconnected = 4,
            SizeChanged = 5,
            Error = 6,
        }

        /// <summary>
        /// Mirrors struct scv_settings. Field order and types must match the C header
        /// exactly; StructSize is the ABI guard that catches it when they don't.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct Settings
        {
            public uint StructSize;

            [MarshalAs(UnmanagedType.LPStr)] public string? Serial;
            [MarshalAs(UnmanagedType.LPStr)] public string? AdbPath;
            [MarshalAs(UnmanagedType.LPStr)] public string? ServerPath;

            [MarshalAs(UnmanagedType.LPStr)] public string? VideoCodec;
            [MarshalAs(UnmanagedType.LPStr)] public string? VideoEncoder;
            [MarshalAs(UnmanagedType.LPStr)] public string? VideoCodecOptions;

            public uint VideoBitRate;
            [MarshalAs(UnmanagedType.LPStr)] public string? MaxFps;
            public ushort MaxSize;
            [MarshalAs(UnmanagedType.LPStr)] public string? Crop;
            [MarshalAs(UnmanagedType.LPStr)] public string? Angle;

            [MarshalAs(UnmanagedType.LPStr)] public string? NewDisplay;
            public uint DisplayId;
            public byte VdDestroyContent;
            public byte VdSystemDecorations;
            public byte Control;
            public byte FlexDisplay;
            public byte LockOrientation;

            public ushort PortFirst;
            public ushort PortLast;

            public byte LogLevel;

            public IntPtr EventCallback;
            public IntPtr UserData;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EventCallback(IntPtr session, int evt, IntPtr userdata);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogCallback(int level, IntPtr message, IntPtr userdata);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void scv_settings_init(ref Settings settings);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void scv_set_log_callback(LogCallback? cb, IntPtr userdata);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr scv_open(ref Settings settings, out int error);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void scv_close(IntPtr session);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int scv_get_size(IntPtr session, out uint width, out uint height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr scv_get_device_name(IntPtr session);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int scv_resize_display(IntPtr session, ushort width, ushort height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int scv_start_app(IntPtr session,
            [MarshalAs(UnmanagedType.LPStr)] string name);

        // Android AMOTION_EVENT_ACTION_*
        public const int TouchDown = 0, TouchUp = 1, TouchMove = 2;

        // Android AKEY_EVENT_ACTION_*
        public const int KeyDown = 0, KeyUp = 1;

        // Android AMOTION_EVENT_BUTTON_*
        public const uint ButtonPrimary = 1 << 0;
        public const uint ButtonSecondary = 1 << 1;
        public const uint ButtonTertiary = 1 << 2;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int scv_inject_touch(IntPtr session, int action, int x, int y,
            ushort width, ushort height, float pressure, uint buttons);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int scv_inject_scroll(IntPtr session, int x, int y,
            ushort width, ushort height, float hscroll, float vscroll);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int scv_inject_keycode(IntPtr session, int action, int keycode,
            uint repeat, uint metastate);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int scv_inject_text(IntPtr session,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int scv_back(IntPtr session, int action);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int scv_acquire_frame(IntPtr session, out IntPtr data,
            out uint stride, out uint width, out uint height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void scv_release_frame(IntPtr session);

        // Kept alive for the process lifetime: native stores the pointer.
        private static LogCallback? _logCallback;

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_resolverInstalled)
                    return;

                _resolverInstalled = true;

                // Via the shared resolver: .NET permits only one per assembly, and
                // libwebp registers there too.
                NativeAssets.Register(DllName, "scrcpy_video.dll");

                try
                {
                    // Round-trips the ABI guard as a load probe.
                    var probe = new Settings();
                    scv_settings_init(ref probe);
                    IsAvailable = probe.StructSize == (uint)Marshal.SizeOf<Settings>();

                    if (!IsAvailable)
                    {
                        Debugger.show($"[SCRCPY] ABI mismatch: native says {probe.StructSize} " +
                                      $"bytes, managed struct is {Marshal.SizeOf<Settings>()}.");
                    }
                    else
                    {
                        _logCallback = OnNativeLog;
                        scv_set_log_callback(_logCallback, IntPtr.Zero);
                    }
                }
                catch (Exception ex)
                {
                    IsAvailable = false;
                    Debugger.show("[SCRCPY] scrcpy_video.dll unavailable: " + ex.Message);
                }

                Debugger.show(IsAvailable
                    ? $"[SCRCPY] scrcpy_video.dll ready ({LibraryPath})."
                    : $"[SCRCPY] scrcpy_video.dll NOT available. Expected at {LibraryPath}");
            }
        }

        private static void OnNativeLog(int level, IntPtr message, IntPtr userdata)
        {
            var text = Marshal.PtrToStringAnsi(message);
            if (string.IsNullOrEmpty(text))
                return;

            // Verbose/debug would be very chatty; only surface info and above.
            if (level >= LogInfo)
                Debugger.show($"[scrcpy] {text}");
            else
                Debugger.advanced($"[scrcpy] {text}");
        }
    }
}
