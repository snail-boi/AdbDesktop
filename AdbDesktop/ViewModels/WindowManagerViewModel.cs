using System.Collections.ObjectModel;
using System.Linq;

namespace AdbDesktop
{
    /// <summary>
    /// Owns the open app windows: placement, z-order, and the min/max/close state
    /// machine. Windows live inside the desktop area only, so a maximised window stops
    /// above the taskbar rather than covering it.
    /// </summary>
    public sealed class WindowManagerViewModel : ViewModelBase
    {
        private const double CascadeStep = 28;
        private const int CascadeWrap = 8;

        private double _surfaceWidth = 1280;
        private double _surfaceHeight = 720;
        private int _zCounter;
        private int _cascadeIndex;

        public ObservableCollection<AppWindowViewModel> Windows { get; } = new();

        /// <summary>
        /// Serial of the connected device. Set by MainViewModel as the connection state
        /// changes; a window opened while disconnected simply shows no video.
        /// </summary>
        public string DeviceSerial { get; set; } = string.Empty;

        /// <summary>
        /// Maps a device's identity serial to the adb transport currently reaching it.
        /// Windows are keyed by identity, but mirroring has to be started against the
        /// live transport, and that changes when a cable goes in or comes out.
        /// </summary>
        public Func<string, string?>? TransportResolver { get; set; }

        /// <summary>
        /// Builds the Settings app's view model when its window opens. Supplied by
        /// MainViewModel so the window manager needs no knowledge of the desktop.
        /// </summary>
        public Func<SettingsViewModel>? SettingsFactory { get; set; }

        public RelayCommand<AppWindowViewModel> CloseCommand { get; }
        public RelayCommand<AppWindowViewModel> MinimiseCommand { get; }
        public RelayCommand<AppWindowViewModel> MaximiseCommand { get; }
        public RelayCommand<AppWindowViewModel> ActivateCommand { get; }
        public RelayCommand<AppWindowViewModel> TaskbarClickCommand { get; }

        public WindowManagerViewModel()
        {
            CloseCommand = new RelayCommand<AppWindowViewModel>(Close);
            MinimiseCommand = new RelayCommand<AppWindowViewModel>(Minimise);
            MaximiseCommand = new RelayCommand<AppWindowViewModel>(ToggleMaximise);
            ActivateCommand = new RelayCommand<AppWindowViewModel>(Activate);
            TaskbarClickCommand = new RelayCommand<AppWindowViewModel>(TaskbarClick);
        }

        public bool HasWindows => Windows.Count > 0;

        public void SetSurfaceSize(double width, double height)
        {
            if (width <= 0 || height <= 0)
                return;

            _surfaceWidth = width;
            _surfaceHeight = height;

            foreach (var w in Windows)
            {
                if (w.IsMaximized)
                {
                    ApplyMaximisedBounds(w);
                }
                else
                {
                    // Keep at least a strip of the title bar reachable after a resize.
                    w.X = Math.Min(w.X, Math.Max(0, _surfaceWidth - 120));
                    w.Y = Math.Min(w.Y, Math.Max(0, _surfaceHeight - AppWindowViewModel.TitleBarHeight));
                }
            }
        }

        /// <summary>
        /// Opens a window for a desktop icon, or brings the app's existing window forward
        /// if it already has one on that device.
        ///
        /// A second window would get its own virtual display, but not its own copy of the
        /// app: the device-side server launches with FLAG_ACTIVITY_NEW_TASK alone, so
        /// Android MOVES the running task to whichever display asked for it last. The
        /// second window would therefore take the app off the first, leaving that one
        /// frozen on its final frame. Reusing the window is the honest behaviour until the
        /// launch itself can ask for a separate task.
        /// </summary>
        public AppWindowViewModel Open(DesktopIconViewModel icon)
        {
            // A window mirrors the device that owns the app, which is not necessarily the
            // primary one. Built-in entries (AdbDesktop Settings) have no device at all.
            var serial = string.IsNullOrEmpty(icon.DeviceSerial) ? DeviceSerial : icon.DeviceSerial;

            // Settings matches on being Settings at all -- it is AdbDesktop's own and belongs
            // to no device, so the serial it happened to open under says nothing. Everything
            // else matches on app AND device: the same app on two phones is two windows.
            var existing = BuiltInApps.IsSettings(icon.Package)
                ? Windows.FirstOrDefault(w => w.IsSettings)
                : Windows.FirstOrDefault(w =>
                    string.Equals(w.Package, icon.Package, StringComparison.Ordinal)
                    && string.Equals(w.DeviceSerial, serial, StringComparison.Ordinal));

            if (existing != null)
            {
                // Activate un-minimises on the way past.
                Activate(existing);
                return existing;
            }

            var window = new AppWindowViewModel
            {
                Package = icon.Package,
                DeviceSerial = serial,
                Title = icon.Caption,
                Settings = BuiltInApps.IsSettings(icon.Package) ? SettingsFactory?.Invoke() : null,
                Icon = icon.Image,
                Width = Math.Min(880, Math.Max(AppWindowViewModel.MinWidth, _surfaceWidth * 0.55)),
                Height = Math.Min(620, Math.Max(AppWindowViewModel.MinHeight, _surfaceHeight * 0.65)),
            };

            // Cascade so a second window does not land exactly on the first.
            var step = _cascadeIndex % CascadeWrap;
            _cascadeIndex++;

            window.X = Math.Max(0, 60 + step * CascadeStep);
            window.Y = Math.Max(0, 40 + step * CascadeStep);

            // If the cascade would push it off the bottom/right, pull it back on.
            if (window.X + window.Width > _surfaceWidth)
                window.X = Math.Max(0, _surfaceWidth - window.Width);
            if (window.Y + window.Height > _surfaceHeight)
                window.Y = Math.Max(0, _surfaceHeight - window.Height);

            window.SaveRestoreBounds();

            Windows.Add(window);
            Track(window);
            RaisePropertyChanged(nameof(HasWindows));

            Activate(window);

            var transport = TransportResolver?.Invoke(serial) ?? serial;

            if (!icon.IsBuiltIn && !string.IsNullOrEmpty(transport))
                window.StartMirroring(transport);

            return window;
        }

        public void Close(AppWindowViewModel? window)
        {
            if (window == null || !Windows.Remove(window))
                return;

            // Tears down the scrcpy session and, because vd_destroy_content is set,
            // the virtual display and the app running on it.
            window.StopMirroring();
            Untrack(window);

            RaisePropertyChanged(nameof(HasWindows));

            // Promote whatever was next in the stack, so focus never vanishes.
            var next = Windows.Where(w => !w.IsMinimized)
                              .OrderByDescending(w => w.ZIndex)
                              .FirstOrDefault();
            if (next != null)
                Activate(next);
        }

        /// <summary>
        /// Re-points every window of a device at a new transport. The old scrcpy session
        /// is dead or about to be (the cable came out), so the session is torn down and
        /// started again, which relaunches the app on a fresh virtual display. Visible,
        /// but better than a window frozen on a transport that no longer exists.
        /// </summary>
        public void RestartMirroring(string deviceSerial, string transport)
        {
            if (string.IsNullOrEmpty(transport))
                return;

            foreach (var window in Windows
                         .Where(w => string.Equals(w.DeviceSerial, deviceSerial, StringComparison.Ordinal))
                         .ToList())
            {
                window.StopMirroring();
                window.StartMirroring(transport);
            }
        }

        /// <summary>
        /// Closes every window mirroring a device. Used when the device is removed from
        /// AdbDesktop, where a window would otherwise outlive the desktop it came from.
        /// </summary>
        public void CloseForDevice(string serial)
        {
            foreach (var window in Windows
                         .Where(w => string.Equals(w.DeviceSerial, serial, StringComparison.Ordinal))
                         .ToList())
            {
                Close(window);
            }
        }

        public void Minimise(AppWindowViewModel? window)
        {
            if (window == null) return;

            window.IsMinimized = true;
            window.IsActive = false;

            var next = Windows.Where(w => !w.IsMinimized)
                              .OrderByDescending(w => w.ZIndex)
                              .FirstOrDefault();
            if (next != null)
                Activate(next);
        }

        public void ToggleMaximise(AppWindowViewModel? window)
        {
            if (window == null) return;

            if (window.IsMaximized)
            {
                window.IsMaximized = false;
                window.ApplyRestoreBounds();
            }
            else
            {
                window.SaveRestoreBounds();
                window.IsMaximized = true;
                ApplyMaximisedBounds(window);
            }

            Activate(window);
        }

        private void ApplyMaximisedBounds(AppWindowViewModel window)
        {
            window.X = 0;
            window.Y = 0;
            window.Width = _surfaceWidth;
            window.Height = _surfaceHeight;
        }

        public void Activate(AppWindowViewModel? window)
        {
            if (window == null) return;

            if (window.IsMinimized)
                window.IsMinimized = false;

            window.ZIndex = ++_zCounter;

            foreach (var w in Windows)
                w.IsActive = ReferenceEquals(w, window);
        }

        /// <summary>
        /// Taskbar button behaviour: clicking the focused window minimises it, anything
        /// else brings it forward.
        /// </summary>
        private void TaskbarClick(AppWindowViewModel? window)
        {
            if (window == null) return;

            if (window.IsActive && !window.IsMinimized)
                Minimise(window);
            else
                Activate(window);
        }

        /// <summary>Stops every session. Called when the shell shuts down.</summary>
        public void CloseAll()
        {
            foreach (var w in Windows.ToList())
                w.StopMirroring();

            Windows.Clear();
            RaisePropertyChanged(nameof(HasWindows));
        }

        /// <summary>
        /// How much of a window must stay on the desktop. Enough that the title bar is
        /// always grabbable, so a window can never be dragged somewhere unreachable.
        /// </summary>
        private const double KeepVisible = 140;

        /// <summary>
        /// Keeps a window on the surface. Applied continuously during a drag, not only
        /// on release, so it is never possible to lose one off an edge.
        /// </summary>
        public void ClampPosition(AppWindowViewModel window)
        {
            // Never let the title bar go above the top, or past either side / the
            // bottom by more than KeepVisible.
            var minX = Math.Min(0, -(window.Width - KeepVisible));
            var maxX = Math.Max(minX, _surfaceWidth - KeepVisible);

            var maxY = Math.Max(0, _surfaceHeight - AppWindowViewModel.TitleBarHeight);

            window.X = Math.Clamp(window.X, minX, maxX);
            window.Y = Math.Clamp(window.Y, 0, maxY);
        }

        // ---------- immersive (maximised) mode ----------

        /// <summary>
        /// True while a maximised window is on screen. The shell then hides its own
        /// chrome and the taskbar so the app really does own the whole surface.
        /// </summary>
        public bool IsImmersive =>
            Windows.Any(w => w.IsMaximized && !w.IsMinimized);

        private void OnWindowPropertyChanged(object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Only the two states that can change whether anything is maximised.
            if (e.PropertyName is nameof(AppWindowViewModel.IsMaximized)
                              or nameof(AppWindowViewModel.IsMinimized))
            {
                RaisePropertyChanged(nameof(IsImmersive));
            }
        }

        private void Track(AppWindowViewModel window)
        {
            window.PropertyChanged += OnWindowPropertyChanged;
            RaisePropertyChanged(nameof(IsImmersive));
        }

        private void Untrack(AppWindowViewModel window)
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
            RaisePropertyChanged(nameof(IsImmersive));
        }
    }
}
