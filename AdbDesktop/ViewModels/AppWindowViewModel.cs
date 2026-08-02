using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AdbDesktop
{
    /// <summary>
    /// One app window on the desktop.
    ///
    /// These are plain WPF content, not hosted child HWNDs. That is a deliberate
    /// consequence of the rendering decision: an embedded HWND would composite on top
    /// of every WPF overlay in the shell (search, icon picker, dialogs), so frames come
    /// across from scrcpy_video.dll as pixels and get painted like anything else.
    /// </summary>
    public sealed class AppWindowViewModel : ViewModelBase
    {
        /// <summary>Height of the title bar, subtracted to get the content area.</summary>
        public const double TitleBarHeight = 32;

        public const double MinWidth = 280;
        public const double MinHeight = 200;

        private double _x;
        private double _y;
        private double _width = 720;
        private double _height = 480;
        private bool _isMinimized;
        private bool _isMaximized;
        private bool _isChromeRevealed;
        private bool _isActive;
        private int _zIndex;
        private string _title = string.Empty;
        private BitmapSource? _icon;

        /// <summary>Bounds to return to when un-maximising.</summary>
        private double _restoreX, _restoreY, _restoreWidth, _restoreHeight;

        public string Package { get; init; } = string.Empty;

        /// <summary>
        /// The built-in Settings app. Its window shows WPF content instead of a mirrored
        /// display, so it needs no device and never starts a scrcpy session.
        /// </summary>
        public bool IsSettings => BuiltInApps.IsSettings(Package);

        /// <summary>Non-null only for the Settings window. Set by the window manager.</summary>
        public SettingsViewModel? Settings { get; init; }

        /// <summary>
        /// The device this window mirrors. Part of its identity, not decoration: the same
        /// package installed on two phones is two independent windows.
        /// </summary>
        public string DeviceSerial { get; init; } = string.Empty;

        public string Title
        {
            get => _title;
            set => Set(ref _title, value);
        }

        public BitmapSource? Icon
        {
            get => _icon;
            set => Set(ref _icon, value);
        }

        public double X
        {
            get => _x;
            set => Set(ref _x, value);
        }

        public double Y
        {
            get => _y;
            set => Set(ref _y, value);
        }

        public double Width
        {
            get => _width;
            set
            {
                if (Set(ref _width, value))
                    RaiseContentSizeChanged();
            }
        }

        public double Height
        {
            get => _height;
            set
            {
                if (Set(ref _height, value))
                    RaiseContentSizeChanged();
            }
        }

        /// <summary>
        /// Size of the area below the title bar. This is what the Android virtual display
        /// will be created at, and what a flex display resizes to.
        /// </summary>
        public double ContentWidth => Math.Max(1, _width);

        // Maximised windows give the whole height to the app: the title bar floats over
        // it rather than occupying a row.
        public double ContentHeight =>
            Math.Max(1, _isMaximized ? _height : _height - TitleBarHeight);

        /// <summary>
        /// Pre-formatted for display. Exists because binding a Run to ContentWidth
        /// directly throws: Run.Text defaults to TwoWay, and these are read-only.
        /// </summary>
        public string ContentSizeText => $"{ContentWidth:F0} x {ContentHeight:F0}";

        private void RaiseContentSizeChanged()
        {
            RaisePropertyChanged(nameof(ContentWidth));
            RaisePropertyChanged(nameof(ContentHeight));
            RaisePropertyChanged(nameof(ContentSizeText));

            // Flex display: the window size is the display size.
            ScheduleDisplayResize();
        }

        public bool IsMinimized
        {
            get => _isMinimized;
            set
            {
                if (Set(ref _isMinimized, value))
                    RaisePropertyChanged(nameof(IsVisibleOnDesktop));
            }
        }

        public bool IsVisibleOnDesktop => !_isMinimized;

        public bool IsMaximized
        {
            get => _isMaximized;
            set
            {
                if (!Set(ref _isMaximized, value)) return;

                // Un-maximising must not leave the title bar hidden.
                if (!value)
                    IsChromeRevealed = false;

                RaisePropertyChanged(nameof(TitleBarRow));
                RaiseContentSizeChanged();
            }
        }

        /// <summary>
        /// Height reserved for the title bar in the window's layout grid.
        ///
        /// Zero while maximised: the title bar is then drawn as an overlay on top of the
        /// content instead of taking a row, so the app gets the whole window and the bar
        /// can slide in over it.
        /// </summary>
        public GridLength TitleBarRow =>
            _isMaximized ? new GridLength(0) : new GridLength(TitleBarHeight);

        /// <summary>Whether the auto-hidden title bar of a maximised window is showing.</summary>
        public bool IsChromeRevealed
        {
            get => _isChromeRevealed;
            set => Set(ref _isChromeRevealed, value);
        }

        public bool IsActive
        {
            get => _isActive;
            set => Set(ref _isActive, value);
        }

        public int ZIndex
        {
            get => _zIndex;
            set => Set(ref _zIndex, value);
        }

        // ---------- mirroring ----------

        private ScrcpyVideoSession? _session;
        private DispatcherTimer? _resizeDebounce;
        private WriteableBitmap? _frame;
        private string _status = "Connecting...";

        /// <summary>The live video surface, or null before the first frame.</summary>
        public WriteableBitmap? Frame
        {
            get => _frame;
            private set
            {
                if (Set(ref _frame, value))
                    RaisePropertyChanged(nameof(HasFrame));
            }
        }

        public bool HasFrame => _frame != null;

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        /// <summary>
        /// Starts mirroring: a virtual display sized to this window's content area, with
        /// the app launched onto it.
        /// </summary>
        public void StartMirroring(string serial)
        {
            if (_session != null)
                return;

            var session = new ScrcpyVideoSession();
            session.FrameSurfaceChanged += bmp => Frame = bmp;
            session.SessionEvent += OnSessionEvent;

            if (!session.Open(serial, Package, (int) ContentWidth, (int) ContentHeight))
            {
                Status = "Could not start mirroring - see the log.";
                return;
            }

            _session = session;
        }

        private void OnSessionEvent(ScrcpyVideoNative.Event e)
        {
            switch (e)
            {
                case ScrcpyVideoNative.Event.Connected:
                    Status = "Starting app...";
                    // The app can only be launched once the control channel is up, which
                    // is exactly what CONNECTED signals.
                    _session?.StartApp(Package);
                    break;
                case ScrcpyVideoNative.Event.StreamStarted:
                    Status = string.Empty;
                    break;
                case ScrcpyVideoNative.Event.ConnectionFailed:
                    Status = "Could not connect to the device.";
                    break;
                case ScrcpyVideoNative.Event.Disconnected:
                    Status = "Disconnected.";
                    break;
                case ScrcpyVideoNative.Event.Error:
                    Status = "Mirroring error - see the log.";
                    break;
            }
        }

        /// <summary>
        /// Pushes the new size to the virtual display, debounced.
        ///
        /// A drag-resize produces a size change per mouse move; each one makes Android
        /// tear down and re-lay-out the display, so they are coalesced and only the
        /// final size is sent.
        /// </summary>
        private void ScheduleDisplayResize()
        {
            if (_session == null || !_session.IsOpen)
                return;

            _resizeDebounce ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(220),
            };

            _resizeDebounce.Tick -= OnResizeDebounceTick;
            _resizeDebounce.Tick += OnResizeDebounceTick;

            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }

        private void OnResizeDebounceTick(object? sender, EventArgs e)
        {
            _resizeDebounce?.Stop();
            _session?.Resize((int) ContentWidth, (int) ContentHeight);
        }

        // ---------- input forwarding ----------

        /// <summary>True once there is a live session to send input to.</summary>
        public bool CanReceiveInput => _session is { IsOpen: true };

        public void SendTouch(int action, double nx, double ny, uint buttons) =>
            _session?.Touch(action, nx, ny, buttons);

        public void SendScroll(double nx, double ny, double hscroll, double vscroll) =>
            _session?.Scroll(nx, ny, hscroll, vscroll);

        public void SendKey(int action, int keycode, uint metastate) =>
            _session?.Key(action, keycode, metastate);

        public void SendText(string text) => _session?.Text(text);

        public void SendBack(int action) => _session?.Back(action);

        public void StopMirroring()
        {
            _resizeDebounce?.Stop();
            _resizeDebounce = null;

            if (_session == null)
                return;

            _session.SessionEvent -= OnSessionEvent;
            _session.Dispose();
            _session = null;
            Frame = null;
        }

        public void SaveRestoreBounds()
        {
            _restoreX = _x;
            _restoreY = _y;
            _restoreWidth = _width;
            _restoreHeight = _height;
        }

        public void ApplyRestoreBounds()
        {
            // Guard against restoring to nothing if a window was created maximised.
            if (_restoreWidth < MinWidth || _restoreHeight < MinHeight)
            {
                _restoreWidth = Math.Max(_restoreWidth, 720);
                _restoreHeight = Math.Max(_restoreHeight, 480);
            }

            X = _restoreX;
            Y = _restoreY;
            Width = _restoreWidth;
            Height = _restoreHeight;
        }
    }
}
