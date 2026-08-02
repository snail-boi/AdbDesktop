using System.IO;
using System.Runtime.InteropServices;

namespace AdbDesktop
{
    /// <summary>
    /// One privately-loaded copy of scrcpy_audio.dll.
    ///
    /// The port keeps a single `static struct sca_session`, so one loaded module can only
    /// ever run one capture. Windows keys loaded modules by path, though, so a copy under
    /// a different file name is a genuinely separate module with its own statics -- which
    /// is how several devices stream at once without touching AMPL's port or rewriting it
    /// to be handle-based.
    ///
    /// Exports are bound by hand rather than through [DllImport], because DllImport binds
    /// to a name, and every copy would collapse onto whichever module loaded first.
    /// </summary>
    internal sealed class ScrcpyAudioModule : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SettingsInitFn(ref ScrcpyAudioNative.Settings settings);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StartFn(ref ScrcpyAudioNative.Settings settings);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void StopFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReadFn(IntPtr buffer, int maxBytes);

        private static readonly object Sync = new();
        private static int _nextSlot;
        private static bool _stagedDependencies;

        private readonly SettingsInitFn _settingsInit;
        private readonly StartFn _start;
        private readonly StopFn _stop;
        private readonly ReadFn _read;

        private IntPtr _handle;

        public string Path { get; }

        private ScrcpyAudioModule(IntPtr handle, string path)
        {
            _handle = handle;
            Path = path;

            _settingsInit = Bind<SettingsInitFn>("sca_settings_init");
            _start = Bind<StartFn>("sca_start");
            _stop = Bind<StopFn>("sca_stop");
            _read = Bind<ReadFn>("sca_read");
        }

        private T Bind<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_handle, name));

        /// <summary>
        /// Stages and loads a fresh copy. Returns null if the copy or the load fails, in
        /// which case the caller simply cannot start another stream.
        /// </summary>
        public static ScrcpyAudioModule? Create()
        {
            lock (Sync)
            {
                try
                {
                    var dir = StageDirectory();
                    var source = AppPaths.GetResourcePath("scrcpy_audio.dll");

                    if (!File.Exists(source))
                    {
                        Debugger.show("[AUDIO] scrcpy_audio.dll is missing from Assets.");
                        return null;
                    }

                    // A slot is never reused within a run. Overwriting a copy that is
                    // still mapped would fail anyway, and the files are small.
                    var slot = _nextSlot++;
                    var path = System.IO.Path.Combine(dir, $"scrcpy_audio_{slot}.dll");

                    File.Copy(source, path, overwrite: true);

                    // Absolute path, so the loader searches this folder for SDL3.dll --
                    // which is why the dependency is staged alongside.
                    var handle = NativeLibrary.Load(path);
                    return new ScrcpyAudioModule(handle, path);
                }
                catch (Exception ex)
                {
                    Debugger.show("[AUDIO] Could not load a private audio module: " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>
        /// A scratch folder holding the copies plus SDL3.dll. scrcpy_audio imports SDL3
        /// by name, and loading from here means here is where it gets looked for.
        /// </summary>
        private static string StageDirectory()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AdbDesktop", "audio");
            Directory.CreateDirectory(dir);

            if (_stagedDependencies)
                return dir;

            foreach (var dependency in new[] { "SDL3.dll" })
            {
                var source = AppPaths.GetResourcePath(dependency);
                if (!File.Exists(source))
                    continue;

                var target = System.IO.Path.Combine(dir, dependency);

                try
                {
                    // Already mapped by an earlier session: the existing copy is the same
                    // file, so a failed overwrite is fine.
                    if (!File.Exists(target))
                        File.Copy(source, target);
                }
                catch (IOException)
                {
                }
            }

            _stagedDependencies = true;
            return dir;
        }

        public void SettingsInit(ref ScrcpyAudioNative.Settings settings) => _settingsInit(ref settings);

        public int Start(ref ScrcpyAudioNative.Settings settings) => _start(ref settings);

        public void Stop() => _stop();

        public void Read(IntPtr buffer, int maxBytes) => _read(buffer, maxBytes);

        public void Dispose()
        {
            if (_handle == IntPtr.Zero)
                return;

            var handle = _handle;
            _handle = IntPtr.Zero;

            try
            {
                NativeLibrary.Free(handle);
            }
            catch (Exception ex)
            {
                Debugger.advanced("[AUDIO] Freeing audio module failed: " + ex.Message);
            }
        }
    }
}
