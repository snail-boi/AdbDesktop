using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbDesktop
{
    /// <summary>
    /// One scrcpy video session, surfaced as a <see cref="WriteableBitmap"/> an
    /// <c>Image</c> can bind to.
    ///
    /// Frames are pulled on WPF's rendering tick rather than pushed from the decoder
    /// thread. That keeps the copy on the UI thread (which is where the bitmap must be
    /// written anyway), lets WPF run at its own rate, and means a decoder producing
    /// faster than we paint just overwrites frames nobody would have seen.
    /// </summary>
    internal sealed class ScrcpyVideoSession : IDisposable
    {
        private readonly ScrcpyVideoNative.EventCallback _eventCallback;

        /// <summary>
        /// Roots the callback for as long as native can still call it.
        ///
        /// A field alone is not enough. The field only lives as long as this object, and
        /// this object becomes unreachable the moment AppWindowViewModel drops its
        /// reference in StopMirroring -- while scv_close is still running on a background
        /// thread and still raising events (StreamStopped, Disconnected) through the
        /// function pointer. Collecting the delegate underneath that leaves native calling
        /// into a stub that has been freed.
        /// </summary>
        private GCHandle _callbackHandle;

        private IntPtr _session;
        private WriteableBitmap? _bitmap;
        private int _bitmapWidth;
        private int _bitmapHeight;
        private bool _renderHooked;
        private bool _disposed;

        /// <summary>Raised on the UI thread when the bitmap instance is replaced.</summary>
        public event Action<WriteableBitmap?>? FrameSurfaceChanged;

        public event Action<ScrcpyVideoNative.Event>? SessionEvent;

        public bool IsOpen => _session != IntPtr.Zero;

        public ScrcpyVideoSession()
        {
            // Held in a field: native keeps the function pointer for the session's life,
            // so letting the delegate be collected would crash on the next event.
            _eventCallback = OnNativeEvent;
        }

        /// <summary>
        /// Starts a session on a new Android virtual display of the given size, and
        /// launches an app onto it.
        /// </summary>
        public bool Open(string serial, string package, int width, int height, int dpi = 220)
        {
            ScrcpyVideoNative.Initialize();

            if (!ScrcpyVideoNative.IsAvailable)
            {
                Debugger.show("[SCRCPY] Cannot open session: native library unavailable.");
                return false;
            }

            if (IsOpen)
                return true;

            width = Math.Max(96, width);
            height = Math.Max(96, height);

            var settings = new ScrcpyVideoNative.Settings();
            ScrcpyVideoNative.scv_settings_init(ref settings);

            settings.Serial = string.IsNullOrWhiteSpace(serial) ? null : serial;
            settings.AdbPath = AppPaths.AdbPath;
            settings.ServerPath = ScrcpyVideoNative.ServerPath;
            settings.MaxFps = "60";

            // A virtual display per window is what allows several apps side by side,
            // independent of what the phone screen is showing.
            settings.NewDisplay = $"{width}x{height}/{dpi}";
            settings.VdDestroyContent = 1;

            // No status bar, nav bar or DeX chrome inside the window: the window's own
            // title bar is the chrome, and the app should fill the rest.
            settings.VdSystemDecorations = 0;

            // Control is required both to launch the app and to resize the display.
            settings.Control = 1;
            settings.FlexDisplay = 1;

            // A desktop window has a shape of its own; an app must not be able to
            // rotate the display out from under it. Resizing still works -- that is a
            // size change via flex, not a rotation.
            settings.LockOrientation = 1;

            // Rooted before the pointer is handed over, released only once scv_close has
            // returned and native can no longer call it.
            if (!_callbackHandle.IsAllocated)
                _callbackHandle = GCHandle.Alloc(_eventCallback);

            settings.EventCallback = Marshal.GetFunctionPointerForDelegate(_eventCallback);

            _session = ScrcpyVideoNative.scv_open(ref settings, out var error);
            if (_session == IntPtr.Zero)
            {
                Debugger.show($"[SCRCPY] scv_open failed for {package}: error={error}");
                ReleaseCallback();
                return false;
            }

            Debugger.show($"[SCRCPY] Session opened for {package} at {width}x{height}.");

            Package = package;
            HookRendering();
            return true;
        }

        public string Package { get; private set; } = string.Empty;

        /// <summary>Launches the app onto this session's virtual display.</summary>
        public void StartApp(string package)
        {
            if (!IsOpen || string.IsNullOrWhiteSpace(package))
                return;

            var r = ScrcpyVideoNative.scv_start_app(_session, package);
            if (r != 0)
                Debugger.show($"[SCRCPY] scv_start_app({package}) returned {r}");
        }

        /// <summary>
        /// Asks Android to re-lay-out at a new size. Safe to call on every resize tick:
        /// the native side never queues these, a newer request replaces a pending one.
        /// </summary>
        public void Resize(int width, int height)
        {
            if (!IsOpen)
                return;

            width = Math.Clamp(width, 96, ushort.MaxValue);
            height = Math.Clamp(height, 96, ushort.MaxValue);

            ScrcpyVideoNative.scv_resize_display(_session, (ushort)width, (ushort)height);
        }

        // ---------- input ----------

        /// <summary>
        /// Current frame size. Touch coordinates are expressed against this, and the
        /// device scales them, so the host never has to match the display size itself.
        /// </summary>
        private (ushort W, ushort H) FrameSize =>
            ((ushort) Math.Clamp(_bitmapWidth, 1, ushort.MaxValue),
             (ushort) Math.Clamp(_bitmapHeight, 1, ushort.MaxValue));

        /// <param name="nx">Normalised 0..1 position within the video area.</param>
        public void Touch(int action, double nx, double ny, uint buttons)
        {
            if (!IsOpen || _bitmap == null)
                return;

            var (w, h) = FrameSize;
            var x = (int) Math.Clamp(nx * w, 0, w - 1);
            var y = (int) Math.Clamp(ny * h, 0, h - 1);

            // Android treats pressure 0 as "not touching", which is what an UP is.
            var pressure = action == ScrcpyVideoNative.TouchUp ? 0f : 1f;

            ScrcpyVideoNative.scv_inject_touch(_session, action, x, y, w, h, pressure, buttons);
        }

        public void Scroll(double nx, double ny, double hscroll, double vscroll)
        {
            if (!IsOpen || _bitmap == null)
                return;

            var (w, h) = FrameSize;
            var x = (int) Math.Clamp(nx * w, 0, w - 1);
            var y = (int) Math.Clamp(ny * h, 0, h - 1);

            ScrcpyVideoNative.scv_inject_scroll(_session, x, y, w, h,
                (float) hscroll, (float) vscroll);
        }

        public void Key(int action, int androidKeycode, uint metastate)
        {
            if (!IsOpen)
                return;

            ScrcpyVideoNative.scv_inject_keycode(_session, action, androidKeycode, 0, metastate);
        }

        public void Text(string text)
        {
            if (!IsOpen || string.IsNullOrEmpty(text))
                return;

            ScrcpyVideoNative.scv_inject_text(_session, text);
        }

        public void Back(int action)
        {
            if (!IsOpen)
                return;

            ScrcpyVideoNative.scv_back(_session, action);
        }

        // ---------- frame pump ----------

        private void HookRendering()
        {
            if (_renderHooked)
                return;

            CompositionTarget.Rendering += OnRendering;
            _renderHooked = true;
        }

        private void UnhookRendering()
        {
            if (!_renderHooked)
                return;

            CompositionTarget.Rendering -= OnRendering;
            _renderHooked = false;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!IsOpen)
                return;

            var r = ScrcpyVideoNative.scv_acquire_frame(_session, out var data,
                out var stride, out var width, out var height);

            if (r != 1)
                return;   // nothing new this tick; the previous frame is still valid

            try
            {
                if (_bitmap == null || _bitmapWidth != (int)width || _bitmapHeight != (int)height)
                {
                    _bitmapWidth = (int)width;
                    _bitmapHeight = (int)height;
                    _bitmap = new WriteableBitmap(_bitmapWidth, _bitmapHeight, 96, 96,
                                                  PixelFormats.Bgra32, null);
                    FrameSurfaceChanged?.Invoke(_bitmap);
                }

                _bitmap.WritePixels(
                    new Int32Rect(0, 0, _bitmapWidth, _bitmapHeight),
                    data, (int)(stride * height), (int)stride);
            }
            catch (Exception ex)
            {
                Debugger.show("[SCRCPY] Frame blit failed: " + ex.Message);
            }
            finally
            {
                // Always release: the native side will not hand out another frame while
                // one is held.
                ScrcpyVideoNative.scv_release_frame(_session);
            }
        }

        private void OnNativeEvent(IntPtr session, int evt, IntPtr userdata)
        {
            var e = (ScrcpyVideoNative.Event)evt;
            Debugger.show($"[SCRCPY] {Package}: {e}");

            // Fires on a native background thread.
            _ = UiThread.RunAsync(() => SessionEvent?.Invoke(e));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            UnhookRendering();

            var handle = _session;
            _session = IntPtr.Zero;

            if (handle == IntPtr.Zero)
            {
                ReleaseCallback();
                return;
            }

            // scv_close blocks until every internal thread joins, which can take a
            // moment (it kills the device-side server), so keep it off the UI thread.
            var package = Package;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    ScrcpyVideoNative.scv_close(handle);
                    Debugger.show($"[SCRCPY] Session closed for {package}.");
                }
                catch (Exception ex)
                {
                    Debugger.show("[SCRCPY] scv_close failed: " + ex.Message);
                }
                finally
                {
                    // Only now is native finished with the callback. Freeing it any
                    // earlier -- including by simply letting this object go out of scope
                    // -- leaves the teardown events calling a collected delegate.
                    ReleaseCallback();
                }
            });
        }

        private void ReleaseCallback()
        {
            if (_callbackHandle.IsAllocated)
                _callbackHandle.Free();
        }
    }
}
