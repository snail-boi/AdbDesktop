using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

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
            SnapAssistPickCommand = new RelayCommand<AppWindowViewModel>(FillFromSnapAssist);
        }

        public bool HasWindows => Windows.Count > 0;

        public void SetSurfaceSize(double width, double height)
        {
            if (width <= 0 || height <= 0)
                return;

            _surfaceWidth = width;
            _surfaceHeight = height;

            // A tile is a fraction of the surface, so every snapped window has to be
            // re-laid-out rather than merely nudged back inside.
            CloseSnapAssist();
            HideSnapPreview();

            foreach (var w in Windows)
            {
                if (w.IsSnapped)
                {
                    ApplyZoneBounds(w);
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
            // Whatever the assist was offering, a window arriving on the desktop answers
            // the question for the user.
            CloseSnapAssist();

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

            // Where the app's window was last time, if that is being remembered.
            if (!TryApplyRemembered(window, out var remembered))
            {
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
            }

            window.SaveRestoreBounds();

            Windows.Add(window);
            Track(window);
            RaisePropertyChanged(nameof(HasWindows));

            Activate(window);

            // After the window is in the list: snapping consults the others.
            if (remembered != SnapZone.None)
            {
                ApplySnap(window, remembered);

                // Restoring a tiled window is not the user tiling one, so the offer to
                // fill the other half would be noise.
                CloseSnapAssist();
            }

            var transport = TransportResolver?.Invoke(serial) ?? serial;

            if (!icon.IsBuiltIn && !string.IsNullOrEmpty(transport))
                QueueMirroringStart(window, transport);

            return window;
        }

        public void Close(AppWindowViewModel? window)
        {
            if (window == null)
                return;

            // Before it leaves the list, while its bounds still mean something. Closed by
            // hand, so it does not come back on its own -- only its position is kept.
            if (Windows.Contains(window))
            {
                RememberWindow(window, wasOpen: false);
                App.SaveConfig();
            }

            if (!Windows.Remove(window))
                return;

            // The window may be the one the assist was offering, or the one it was
            // offering a gap next to; either way the offer is stale.
            CloseSnapAssist();

            // Closing the window that the queue is waiting on must release it, or every
            // session behind it waits out the timeout.
            if (ReferenceEquals(window, _startingWindow))
                FinishStart();

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
                QueueMirroringStart(window, transport);
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

            CloseSnapAssist();

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

            ApplySnap(window, window.Snap == SnapZone.Full ? SnapZone.None : SnapZone.Full);
            Activate(window);
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
            CloseSnapAssist();
            HideSnapPreview();

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

        // ---------- session restore ----------
        //
        // Two separate things, behind one setting. Position remembers where each app's
        // window was so it opens there next time instead of cascading; Apps additionally
        // reopens what was open at shutdown, once the device is back.

        private static SessionConfig Session => App.Config.Session;

        private static bool IsSameEntry(WindowStateEntry entry, string serial, string package) =>
            string.Equals(entry.Package, package, StringComparison.Ordinal)
            && string.Equals(entry.DeviceSerial, serial, StringComparison.Ordinal);

        /// <summary>
        /// Puts a window back where its app was last time. False when there is nothing
        /// remembered, or remembering is off, in which case the caller cascades instead.
        /// </summary>
        private bool TryApplyRemembered(AppWindowViewModel window, out SnapZone snap)
        {
            snap = SnapZone.None;

            if (Session.Restore == SessionRestore.Off)
                return false;

            var entry = Session.Windows.FirstOrDefault(
                e => IsSameEntry(e, window.DeviceSerial, window.Package));

            if (entry == null)
                return false;

            window.Width = entry.Width;
            window.Height = entry.Height;
            window.X = entry.X;
            window.Y = entry.Y;

            // The surface may be a different size than it was last run.
            ClampPosition(window);

            snap = entry.Snap;
            return true;
        }

        /// <summary>
        /// Records a window's current bounds. <paramref name="wasOpen"/> is what decides
        /// whether Apps mode reopens it, so closing a window clears it and shutdown sets
        /// it on whatever is still on screen.
        /// </summary>
        private void RememberWindow(AppWindowViewModel window, bool wasOpen, int order = 0)
        {
            if (Session.Restore == SessionRestore.Off)
                return;

            var entry = Session.Windows.FirstOrDefault(
                e => IsSameEntry(e, window.DeviceSerial, window.Package));

            if (entry == null)
            {
                entry = new WindowStateEntry
                {
                    DeviceSerial = window.DeviceSerial,
                    Package = window.Package,
                };
                Session.Windows.Add(entry);
            }

            // A snapped window's own X/Y/Width/Height are the tile, not anything worth
            // restoring on a differently-sized surface. The floating bounds it would
            // return to are what gets remembered, and the zone is stored beside them.
            var bounds = window.IsSnapped ? window.RestoreBounds
                                          : new Rect(window.X, window.Y, window.Width, window.Height);

            entry.X = bounds.X;
            entry.Y = bounds.Y;
            entry.Width = bounds.Width;
            entry.Height = bounds.Height;
            entry.Snap = window.Snap;
            entry.WasOpen = wasOpen;
            entry.Order = order;
        }

        /// <summary>
        /// Writes down everything still open. Called at shutdown, before the windows are
        /// torn down and their bounds are gone.
        /// </summary>
        public void SaveSession()
        {
            if (Session.Restore == SessionRestore.Off)
            {
                // Nothing is remembered in this mode, and leaving last run's list behind
                // would resurrect it if the setting were turned back on.
                if (Session.Windows.Count > 0)
                {
                    Session.Windows.Clear();
                    App.SaveConfig();
                }
                return;
            }

            // Back to front, so the order recorded matches how they were stacked. A
            // minimised window still counts as open -- it was, and it comes back.
            var open = Windows.OrderBy(w => w.ZIndex).ToList();

            var order = 0;
            foreach (var window in open)
                RememberWindow(window, wasOpen: true, order: order++);

            App.SaveConfig();
        }

        /// <summary>
        /// The apps that were open on a device when AdbDesktop last closed, back to front.
        /// Empty unless the setting is Apps.
        /// </summary>
        public IReadOnlyList<WindowStateEntry> RestorableFor(string deviceSerial)
        {
            if (Session.Restore != SessionRestore.Apps)
                return Array.Empty<WindowStateEntry>();

            return Session.Windows
                .Where(e => e.WasOpen
                            && string.Equals(e.DeviceSerial, deviceSerial, StringComparison.Ordinal))
                .OrderBy(e => e.Order)
                .ToList();
        }

        // ---------- staged session starts ----------
        //
        // Sessions are brought up one at a time, because two starting together fight
        // over the device.
        //
        // Every session pushes the scrcpy server to the same fixed path on the device,
        // /data/local/tmp/scrcpy-server.jar, and then runs it from there by CLASSPATH.
        // Upstream is one session per process so nothing else was ever touching that
        // file. Here, the next window's adb push truncates and rewrites it while the
        // previous window's app_process is still loading classes out of it, and that
        // process dies -- the log shows each session terminating about 230ms after the
        // next one starts pushing, with only the last one surviving.
        //
        // Waiting for video before starting the next one removes the overlap. The window
        // still opens immediately and shows its status, so the only visible effect is
        // that several windows opened at once come up in sequence.

        private readonly Queue<(AppWindowViewModel Window, string Transport)> _pendingStarts = new();
        private AppWindowViewModel? _startingWindow;
        private DispatcherTimer? _startTimeout;

        /// <summary>
        /// Long enough that a slow device is not cut off, short enough that a session
        /// which never reports anything cannot wedge the queue for good.
        /// </summary>
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(12);

        public void QueueMirroringStart(AppWindowViewModel window, string transport)
        {
            _pendingStarts.Enqueue((window, transport));
            PumpStarts();
        }

        private void PumpStarts()
        {
            while (_startingWindow == null && _pendingStarts.Count > 0)
            {
                var (window, transport) = _pendingStarts.Dequeue();

                // It may have been closed while it waited its turn.
                if (!Windows.Contains(window))
                    continue;

                _startingWindow = window;
                window.MirroringSettled += OnMirroringSettled;

                _startTimeout ??= new DispatcherTimer { Interval = StartTimeout };
                _startTimeout.Tick -= OnStartTimeout;
                _startTimeout.Tick += OnStartTimeout;
                _startTimeout.Stop();
                _startTimeout.Start();

                window.StartMirroring(transport);
            }
        }

        private void OnMirroringSettled(AppWindowViewModel window)
        {
            if (ReferenceEquals(window, _startingWindow))
                FinishStart();
        }

        private void OnStartTimeout(object? sender, EventArgs e) => FinishStart();

        private void FinishStart()
        {
            _startTimeout?.Stop();

            if (_startingWindow != null)
            {
                _startingWindow.MirroringSettled -= OnMirroringSettled;
                _startingWindow = null;
            }

            PumpStarts();
        }

        // ---------- tiling ----------
        //
        // Windows' snap, on the shell's own surface: drag a window against an edge and it
        // takes that half (a corner takes a quarter, the top edge maximises), or use the
        // keyboard shortcuts. The desktop area is the "screen" here, so a tiled window
        // stops above the taskbar exactly as a maximised one does.

        /// <summary>How close the pointer must come to an edge for a drag to arm a snap.</summary>
        private const double EdgeMargin = 24;

        /// <summary>
        /// How far along an edge counts as its corner, and so picks a quarter rather than
        /// a half. Capped against the surface so it cannot swallow a whole small edge.
        /// </summary>
        private const double CornerMargin = 110;

        /// <summary>
        /// The zone a pointer at this position would snap to, in surface coordinates.
        /// <see cref="SnapZone.None"/> means "nowhere near an edge, leave it floating".
        /// </summary>
        public SnapZone ZoneForPointer(Point p)
        {
            var corner = Math.Min(CornerMargin, Math.Min(_surfaceWidth, _surfaceHeight) / 3);

            var nearTop = p.Y <= EdgeMargin;
            var nearBottom = p.Y >= _surfaceHeight - EdgeMargin;

            // Sides win over the top and bottom edges, so that sliding down the left edge
            // never flickers through "maximise" on the way past the corner.
            if (p.X <= EdgeMargin)
            {
                if (p.Y <= corner) return SnapZone.TopLeft;
                if (p.Y >= _surfaceHeight - corner) return SnapZone.BottomLeft;
                return SnapZone.Left;
            }

            if (p.X >= _surfaceWidth - EdgeMargin)
            {
                if (p.Y <= corner) return SnapZone.TopRight;
                if (p.Y >= _surfaceHeight - corner) return SnapZone.BottomRight;
                return SnapZone.Right;
            }

            if (nearTop)
            {
                if (p.X <= corner) return SnapZone.TopLeft;
                if (p.X >= _surfaceWidth - corner) return SnapZone.TopRight;
                return SnapZone.Full;
            }

            if (nearBottom)
            {
                if (p.X <= corner) return SnapZone.BottomLeft;
                if (p.X >= _surfaceWidth - corner) return SnapZone.BottomRight;
                // The middle of the bottom edge does nothing, as on Windows.
            }

            return SnapZone.None;
        }

        /// <summary>The rectangle a zone occupies. False for <see cref="SnapZone.None"/>.</summary>
        public bool TryZoneBounds(SnapZone zone, out Rect bounds)
        {
            // Rounded so two windows sharing an edge meet on a whole pixel and leave no
            // hairline of desktop showing between them.
            var halfWidth = Math.Round(_surfaceWidth / 2);
            var halfHeight = Math.Round(_surfaceHeight / 2);

            bounds = zone switch
            {
                SnapZone.Full => new Rect(0, 0, _surfaceWidth, _surfaceHeight),
                SnapZone.Left => new Rect(0, 0, halfWidth, _surfaceHeight),
                SnapZone.Right => new Rect(halfWidth, 0, _surfaceWidth - halfWidth, _surfaceHeight),
                SnapZone.TopLeft => new Rect(0, 0, halfWidth, halfHeight),
                SnapZone.TopRight => new Rect(halfWidth, 0, _surfaceWidth - halfWidth, halfHeight),
                SnapZone.BottomLeft => new Rect(0, halfHeight, halfWidth, _surfaceHeight - halfHeight),
                SnapZone.BottomRight => new Rect(halfWidth, halfHeight,
                                                 _surfaceWidth - halfWidth, _surfaceHeight - halfHeight),
                _ => Rect.Empty,
            };

            return zone != SnapZone.None;
        }

        /// <summary>
        /// Moves a window into a zone, or back out of one. The bounds a window returns to
        /// are captured the first time it leaves the floating state, not on every hop
        /// between zones, so snapping left then right then loose still gives back the size
        /// the window had before any of it.
        /// </summary>
        public void ApplySnap(AppWindowViewModel? window, SnapZone zone)
        {
            if (window == null) return;

            HideSnapPreview();

            if (zone == SnapZone.None)
            {
                if (!window.IsSnapped) return;

                window.Snap = SnapZone.None;
                window.ApplyRestoreBounds();
                ClampPosition(window);
                CloseSnapAssist();
                return;
            }

            if (!window.IsSnapped)
                window.SaveRestoreBounds();

            window.Snap = zone;
            ApplyZoneBounds(window);

            CloseSnapAssist();
            OfferSnapAssist(window);
        }

        /// <summary>
        /// Frees a tiled window where it stands, keeping its current bounds as the ones
        /// it floats at. Used when the user resizes a tile: the size they just dragged out
        /// is the size they want, not something to be restored away.
        /// </summary>
        public void ReleaseTile(AppWindowViewModel? window)
        {
            if (window is not { IsSnapped: true })
                return;

            window.Snap = SnapZone.None;
            window.SaveRestoreBounds();
            CloseSnapAssist();
        }

        private void ApplyZoneBounds(AppWindowViewModel window)
        {
            if (!TryZoneBounds(window.Snap, out var r))
                return;

            window.X = r.X;
            window.Y = r.Y;
            window.Width = r.Width;
            window.Height = r.Height;
        }

        /// <summary>
        /// Where an arrow shortcut takes a window from the zone it is in. Mirrors the
        /// Win+Arrow behaviour: the first press snaps, further presses walk around the
        /// halves and quarters, and coming back the way you came releases the window.
        /// </summary>
        public void TileWithKeyboard(AppWindowViewModel? window, TileDirection direction)
        {
            if (window == null) return;

            Activate(window);

            var zone = window.Snap;

            switch (direction)
            {
                case TileDirection.Left:
                    ApplySnap(window, zone switch
                    {
                        SnapZone.None or SnapZone.Full => SnapZone.Left,
                        SnapZone.Right => SnapZone.None,
                        SnapZone.TopRight => SnapZone.TopLeft,
                        SnapZone.BottomRight => SnapZone.BottomLeft,
                        _ => zone,
                    });
                    break;

                case TileDirection.Right:
                    ApplySnap(window, zone switch
                    {
                        SnapZone.None or SnapZone.Full => SnapZone.Right,
                        SnapZone.Left => SnapZone.None,
                        SnapZone.TopLeft => SnapZone.TopRight,
                        SnapZone.BottomLeft => SnapZone.BottomRight,
                        _ => zone,
                    });
                    break;

                case TileDirection.Up:
                    ApplySnap(window, zone switch
                    {
                        SnapZone.Left => SnapZone.TopLeft,
                        SnapZone.Right => SnapZone.TopRight,
                        SnapZone.BottomLeft => SnapZone.Left,
                        SnapZone.BottomRight => SnapZone.Right,
                        _ => SnapZone.Full,
                    });
                    break;

                case TileDirection.Down:
                    // Down out of a floating window is minimise, as on Windows.
                    if (zone == SnapZone.None)
                    {
                        Minimise(window);
                        return;
                    }

                    ApplySnap(window, zone switch
                    {
                        SnapZone.Left => SnapZone.BottomLeft,
                        SnapZone.Right => SnapZone.BottomRight,
                        SnapZone.TopLeft => SnapZone.Left,
                        SnapZone.TopRight => SnapZone.Right,
                        _ => SnapZone.None,
                    });
                    break;
            }
        }

        // ---------- snap preview ----------

        private SnapZone _previewZone;

        /// <summary>The zone the pointer is currently over during a drag, if any.</summary>
        public SnapZone PreviewZone => _previewZone;

        public bool IsSnapPreviewVisible => _previewZone != SnapZone.None;

        public double PreviewX { get; private set; }
        public double PreviewY { get; private set; }
        public double PreviewWidth { get; private set; }
        public double PreviewHeight { get; private set; }

        /// <summary>Paints the translucent outline of where a released drag would land.</summary>
        public void ShowSnapPreview(SnapZone zone)
        {
            if (zone == _previewZone)
                return;

            _previewZone = zone;

            if (TryZoneBounds(zone, out var r))
            {
                PreviewX = r.X;
                PreviewY = r.Y;
                PreviewWidth = r.Width;
                PreviewHeight = r.Height;

                RaisePropertyChanged(nameof(PreviewX));
                RaisePropertyChanged(nameof(PreviewY));
                RaisePropertyChanged(nameof(PreviewWidth));
                RaisePropertyChanged(nameof(PreviewHeight));
            }

            RaisePropertyChanged(nameof(PreviewZone));
            RaisePropertyChanged(nameof(IsSnapPreviewVisible));
        }

        public void HideSnapPreview() => ShowSnapPreview(SnapZone.None);

        // ---------- snap assist ----------
        //
        // Filling one half leaves the other one empty, and the window to put there is
        // almost always one that is already open. Same idea as Windows' snap assist:
        // offer the other windows in the gap, one click to tile the pair.

        private bool _isSnapAssistOpen;
        private SnapZone _assistZone;
        private bool _fillingFromAssist;

        public ObservableCollection<AppWindowViewModel> SnapAssistCandidates { get; } = new();

        public bool IsSnapAssistOpen
        {
            get => _isSnapAssistOpen;
            private set => Set(ref _isSnapAssistOpen, value);
        }

        public double AssistX { get; private set; }
        public double AssistY { get; private set; }
        public double AssistWidth { get; private set; }
        public double AssistHeight { get; private set; }

        public RelayCommand<AppWindowViewModel> SnapAssistPickCommand { get; }

        /// <summary>The zone opposite a half, or None for anything that leaves no obvious gap.</summary>
        private static SnapZone Opposite(SnapZone zone) => zone switch
        {
            SnapZone.Left => SnapZone.Right,
            SnapZone.Right => SnapZone.Left,
            _ => SnapZone.None,
        };

        private void OfferSnapAssist(AppWindowViewModel snapped)
        {
            // Only for the halves. Quarters leave an L-shaped gap that no single window
            // fills, and offering one of the three remaining corners would be a guess.
            var gap = Opposite(snapped.Snap);
            if (gap == SnapZone.None || _fillingFromAssist)
                return;

            var candidates = Windows
                .Where(w => !ReferenceEquals(w, snapped) && !w.IsMinimized && w.Snap != gap)
                .OrderByDescending(w => w.ZIndex)
                .ToList();

            if (candidates.Count == 0)
                return;

            SnapAssistCandidates.Clear();
            foreach (var w in candidates)
                SnapAssistCandidates.Add(w);

            if (!TryZoneBounds(gap, out var r))
                return;

            _assistZone = gap;

            AssistX = r.X;
            AssistY = r.Y;
            AssistWidth = r.Width;
            AssistHeight = r.Height;

            RaisePropertyChanged(nameof(AssistX));
            RaisePropertyChanged(nameof(AssistY));
            RaisePropertyChanged(nameof(AssistWidth));
            RaisePropertyChanged(nameof(AssistHeight));

            IsSnapAssistOpen = true;
        }

        public void CloseSnapAssist()
        {
            if (!_isSnapAssistOpen)
                return;

            IsSnapAssistOpen = false;
            _assistZone = SnapZone.None;
            SnapAssistCandidates.Clear();
        }

        private void FillFromSnapAssist(AppWindowViewModel? window)
        {
            var gap = _assistZone;
            CloseSnapAssist();

            if (window == null || gap == SnapZone.None)
                return;

            // Suppressed for this one call, or filling the gap would immediately offer to
            // fill the half we just came from.
            _fillingFromAssist = true;
            try
            {
                ApplySnap(window, gap);
            }
            finally
            {
                _fillingFromAssist = false;
            }

            Activate(window);
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
