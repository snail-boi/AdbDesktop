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
        private SnapZone _snap;
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

        /// <summary>The frame a floating window is drawn with, in WPF units.</summary>
        public const double FloatingFrameBorder = 1;

        /// <summary>
        /// This window's border, in WPF units. Subtracted below because it is part of
        /// Width and Height but not part of the area the video is drawn in.
        ///
        /// Zero while maximised: a maximised window covers the whole desktop, so an
        /// outline round it has nothing to separate it from and only draws a line across
        /// the screen.
        ///
        /// The window Border binds <see cref="FrameBorderThickness"/> rather than setting
        /// a thickness of its own, so the two cannot drift apart. Getting them out of step
        /// is not a cosmetic couple of pixels: the frame is drawn with Stretch=Fill, so
        /// any mismatch resamples the whole image, and with a small one the sampling phase
        /// drifts across the picture. That reads as a soft, slightly-out-of-focus window
        /// rather than as a wrong size, which is why it survived for so long.
        /// </summary>
        public double FrameBorder => _isMaximized ? 0 : FloatingFrameBorder;

        /// <summary>What the window Border binds. See <see cref="FrameBorder"/>.</summary>
        public Thickness FrameBorderThickness => new(FrameBorder);

        /// <summary>
        /// Size of the area the mirrored display is actually drawn in. This is what the
        /// Android virtual display is created at, and what a flex display resizes to.
        /// </summary>
        public double ContentWidth => Math.Max(1, _width - 2 * FrameBorder);

        // Maximised windows give the whole height to the app: the title bar floats over
        // it rather than occupying a row.
        public double ContentHeight =>
            Math.Max(1, (_isMaximized ? _height : _height - TitleBarHeight)
                        - 2 * FrameBorder);

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

        /// <summary>
        /// Which tile the shell is holding this window in, if any. Set through
        /// WindowManagerViewModel.ApplySnap, which owns the bounds that go with it.
        /// </summary>
        public SnapZone Snap
        {
            get => _snap;
            set
            {
                if (!Set(ref _snap, value)) return;

                RaisePropertyChanged(nameof(IsSnapped));
                RaisePropertyChanged(nameof(IsTiled));
                IsMaximized = value == SnapZone.Full;
            }
        }

        /// <summary>True when the shell, not the user, is deciding this window's bounds.</summary>
        public bool IsSnapped => _snap != SnapZone.None;

        /// <summary>Snapped to a half or a quarter -- maximised does not count.</summary>
        public bool IsTiled => _snap is not (SnapZone.None or SnapZone.Full);

        /// <summary>
        /// Maximised, which is <see cref="SnapZone.Full"/> seen from the view's side.
        /// Kept as its own property because the window template triggers on it, and
        /// because it is what "immersive" means to the rest of the shell.
        /// </summary>
        public bool IsMaximized
        {
            get => _isMaximized;
            private set
            {
                if (!Set(ref _isMaximized, value)) return;

                // Un-maximising must not leave the title bar hidden.
                if (!value)
                    IsChromeRevealed = false;

                RaisePropertyChanged(nameof(TitleBarRow));
                RaisePropertyChanged(nameof(FrameBorderThickness));
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

        /// <summary>
        /// The settings this window's session was opened with. Null until it starts, and
        /// deliberately not refreshed afterwards. See StartMirroring.
        /// </summary>
        private ResolvedDisplayOptions? _options;

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
        /// Raised once a start attempt has settled, whether it worked or not. The window
        /// manager waits for this before bringing up the next session; see
        /// WindowManagerViewModel.QueueMirroringStart for why they cannot overlap.
        /// </summary>
        public event Action<AppWindowViewModel>? MirroringSettled;

        private void RaiseSettled() => MirroringSettled?.Invoke(this);

        /// <summary>
        /// Starts mirroring: a virtual display sized to this window's content area, with
        /// the app launched onto it.
        /// </summary>
        public void StartMirroring(string serial)
        {
            if (_session != null)
                return;

            // Resolved once, at open. Every one of these settings is baked into the
            // virtual display or the encoder, so none of them can change under a live
            // session. Re-reading them later would only produce a view model that
            // disagrees with the stream it is showing.
            _options = DisplayOptions.Resolve(
                App.Config.Display.Defaults,
                App.Config.Display.FindOverride(serial, Package));

            RaisePropertyChanged(nameof(IsViewOnly));

            var session = new ScrcpyVideoSession();
            session.FrameSurfaceChanged += bmp => Frame = bmp;
            session.SessionEvent += OnSessionEvent;

            // Physical pixels, not DIPs: the display is created at the size the user
            // actually looks at, so the frames map onto the window one for one instead of
            // being stretched up. See DisplayScale.
            if (!session.Open(serial, Package,
                              DisplayScale.ToPixels(ContentWidth),
                              DisplayScale.ToPixels(ContentHeight),
                              _options))
            {
                Status = "Could not start mirroring - see the log.";
                RaiseSettled();
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
                    // Video is flowing, so the device has finished loading the server
                    // off disk. This is the earliest point another session may push.
                    RaiseSettled();
                    break;
                case ScrcpyVideoNative.Event.ConnectionFailed:
                    Status = "Could not connect to the device.";
                    RaiseSettled();
                    break;
                case ScrcpyVideoNative.Event.Disconnected:
                    Status = "Disconnected.";
                    RaiseSettled();
                    break;
                case ScrcpyVideoNative.Event.Error:
                    Status = "Mirroring error - see the log.";
                    RaiseSettled();
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

            _resizeDebounce ??= new DispatcherTimer();

            // Re-read every time rather than at construction, so changing the setting
            // takes effect on the next drag instead of the next launch.
            _resizeDebounce.Interval =
                TimeSpan.FromMilliseconds(App.Config.Advanced.ResizeDelayMs);

            _resizeDebounce.Tick -= OnResizeDebounceTick;
            _resizeDebounce.Tick += OnResizeDebounceTick;

            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }

        private void OnResizeDebounceTick(object? sender, EventArgs e)
        {
            _resizeDebounce?.Stop();
            _session?.Resize(DisplayScale.ToPixels(ContentWidth),
                             DisplayScale.ToPixels(ContentHeight));
        }

        /// <summary>
        /// Re-sends the display size after the window moves to a monitor with different
        /// scaling. The window has not changed size in DIPs, so nothing else notices, but
        /// the number of real pixels behind it has changed and the display has to follow
        /// or the stretch comes straight back.
        /// </summary>
        public void OnDisplayScaleChanged() => ScheduleDisplayResize();

        // ---------- input forwarding ----------

        /// <summary>
        /// True once there is a live session that will act on input.
        ///
        /// A view-only window reports false, so callers that ask before doing work (such
        /// as setting a cursor or taking focus for typing) treat it the same as a window
        /// with no session yet, which is what it behaves like from the user's side.
        /// </summary>
        public bool CanReceiveInput => _session is { IsOpen: true } && !IsViewOnly;

        /// <summary>
        /// Watch without touching. This is what the settings call "View only"; scrcpy's
        /// own no-control option cannot be used for it here (see ScrcpyVideoSession.Open).
        ///
        /// Every Send* below checks it rather than relying on callers: input arrives from
        /// several places (the video surface, the taskbar nav buttons, keyboard focus),
        /// and one that forgot to ask would silently defeat the setting.
        /// </summary>
        public bool IsViewOnly => _options?.ViewOnly ?? false;

        public void SendTouch(int action, double nx, double ny, uint buttons)
        {
            if (IsViewOnly)
                return;

            _session?.Touch(action, nx, ny, buttons);
        }

        public void SendScroll(double nx, double ny, double hscroll, double vscroll)
        {
            if (IsViewOnly)
                return;

            _session?.Scroll(nx, ny, hscroll, vscroll);
        }

        public void SendKey(int action, int keycode, uint metastate)
        {
            if (IsViewOnly)
                return;

            _session?.Key(action, keycode, metastate);
        }

        public void SendText(string text)
        {
            if (IsViewOnly)
                return;

            _session?.Text(text);
        }

        public void SendBack(int action)
        {
            if (IsViewOnly)
                return;

            _session?.Back(action);
        }

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

        /// <summary>
        /// The floating bounds behind a snapped window. What gets remembered for session
        /// restore: a tile is a fraction of whatever the surface was, so the bounds worth
        /// keeping are the ones the window would return to.
        /// </summary>
        public Rect RestoreBounds
        {
            get
            {
                NormaliseRestoreSize();
                return new Rect(_restoreX, _restoreY, _restoreWidth, _restoreHeight);
            }
        }

        public void ApplyRestoreBounds()
        {
            NormaliseRestoreSize();

            X = _restoreX;
            Y = _restoreY;
            Width = _restoreWidth;
            Height = _restoreHeight;
        }

        /// <summary>Guards against restoring to nothing if a window was created snapped.</summary>
        private void NormaliseRestoreSize()
        {
            if (_restoreWidth < MinWidth || _restoreHeight < MinHeight)
            {
                _restoreWidth = Math.Max(_restoreWidth, 720);
                _restoreHeight = Math.Max(_restoreHeight, 480);
            }
        }
    }
}
