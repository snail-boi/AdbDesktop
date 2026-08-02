using System.Runtime.InteropServices;

namespace AdbDesktop
{
    /// <summary>
    /// P/Invoke surface for scrcpy_audio.dll -- the audio-only scrcpy port from AMPL,
    /// bundled as-is.
    ///
    /// Deliberately a separate DLL from scrcpy_video: Android captures audio
    /// device-wide rather than per-app, so there is exactly one audio stream no matter
    /// how many windows are open. That singleton shape is also why the native side's
    /// single static session is right here, where it would have been wrong for video.
    ///
    /// Note this one does link SDL3 (it uses it for threading and logging), which the
    /// video port does not.
    /// </summary>
    internal static class ScrcpyAudioNative
    {
        private const string DllName = "scrcpy_audio";

        public const int EventConnected = 0;
        public const int EventConnectionFailed = 1;
        public const int EventStreamStarted = 2;
        public const int EventStreamStopped = 3;
        public const int EventDisconnected = 4;
        public const int EventAudioDisabled = 5;
        public const int EventError = 6;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EventCallback(int evt, IntPtr userdata);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogCallback(int level, IntPtr message, IntPtr userdata);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PcmCallback(IntPtr data, uint numBytes, IntPtr userdata);

        /// <summary>Mirrors struct sca_settings. Field order must match the C header.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Settings
        {
            public uint StructSize;

            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Serial;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? AdbPath;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? ServerPath;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? AudioCodec;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? AudioSource;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? AudioEncoder;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? AudioCodecOptions;

            public uint AudioBitRate;
            public uint AudioBufferMs;
            public uint OutputBufferMs;
            public ushort PortFirst;
            public ushort PortLast;
            public byte AudioDup;
            public byte LogLevel;

            public EventCallback? EventCb;
            public LogCallback? LogCb;
            public PcmCallback? PcmCb;
            public IntPtr Userdata;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sca_settings_init(ref Settings settings);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sca_start(ref Settings settings);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sca_stop();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sca_get_format(out uint sampleRate, out uint channels,
            out uint bitsPerSample);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sca_read(IntPtr buffer, int maxBytes);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sca_get_device_name();

        private static bool _registered;
        private static readonly object Sync = new();

        public static bool IsAvailable { get; private set; }

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_registered)
                    return;

                _registered = true;

                // Shares the assembly-wide resolver with libwebp and scrcpy_video.
                // SDL3 is not registered: scrcpy_audio.dll imports it by name, and
                // loading scrcpy_audio by absolute path resolves it from Assets\ too.
                NativeAssets.Register(DllName, "scrcpy_audio.dll");

                try
                {
                    var probe = new Settings();
                    sca_settings_init(ref probe);
                    IsAvailable = probe.StructSize == (uint) Marshal.SizeOf<Settings>();

                    if (!IsAvailable)
                    {
                        Debugger.show($"[AUDIO] ABI mismatch: native {probe.StructSize}, " +
                                      $"managed {Marshal.SizeOf<Settings>()}.");
                    }
                }
                catch (Exception ex)
                {
                    IsAvailable = false;
                    Debugger.show("[AUDIO] scrcpy_audio.dll unavailable: " + ex.Message);
                }

                Debugger.show(IsAvailable
                    ? "[AUDIO] scrcpy_audio.dll ready."
                    : "[AUDIO] scrcpy_audio.dll NOT available.");
            }
        }
    }
}
