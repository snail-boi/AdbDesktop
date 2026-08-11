using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace AdbDesktop
{
    public partial class MainWindow : Window
    {
        // WM_DEVICECHANGE and the two events worth reacting to. Broadcast to every
        // top-level window, so no RegisterDeviceNotification call is needed.
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_WINDOWPOSCHANGED = 0x0047;
        private const int WM_DPICHANGED = 0x02E0;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private readonly MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();

            WebpDecoder.Initialize();

            _vm = new MainViewModel();
            DataContext = _vm;

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Closed += OnClosed;
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;
            PreviewTextInput += OnTextInput;
            PreviewMouseMove += OnPreviewMouseMove;
            MouseLeave += (_, e) => Windows.PointerLeftShell(e.GetPosition(Windows));

            StateChanged += (_, _) => SyncResizeBorder();
            SyncResizeBorder();
        }

        /// <summary>
        /// Drops the resize border while maximised.
        ///
        /// The border makes its pixels non-client, and a non-client mouse movement raises
        /// no WPF mouse event at all: no MouseEnter, no MouseMove, nothing. Both
        /// auto-hiding bars live in exactly those pixels at the top of the screen, so
        /// pushing the pointer into the top edge -- the whole gesture -- went unheard, and
        /// the bars only appeared if the pointer came to rest just below the border. A
        /// maximised window cannot be resized by its edges anyway, so the border is doing
        /// nothing there but swallowing the input the feature depends on.
        /// </summary>
        private void SyncResizeBorder()
        {
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome == null)
                return;

            chrome.ResizeBorderThickness = WindowState == WindowState.Maximized
                ? new Thickness(0)
                : new Thickness(ResizeBorder);
        }

        /// <summary>Matches the ResizeBorderThickness declared on the window's chrome.</summary>
        private const double ResizeBorder = 6;

        /// <summary>
        /// The hook goes on here rather than in Loaded: the window starts maximised, so
        /// its first WM_GETMINMAXINFO arrives while the HWND is being created. By Loaded
        /// the bad maximised bounds have already been applied.
        /// </summary>
        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var source = (HwndSource?)PresentationSource.FromVisual(this);
            source?.AddHook(WndProc);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _vm.WindowManager.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(WindowManagerViewModel.IsImmersive))
                    OnImmersiveChanged();
            };

            _vm.Start();
        }

        private void OnClosed(object? sender, EventArgs e) => _vm.Dispose();

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_GETMINMAXINFO:
                    ClampMaximisedBounds(hwnd, lParam);
                    handled = true;
                    return IntPtr.Zero;

                case WM_DPICHANGED:
                    ReapplyMaximise();
                    return IntPtr.Zero;

                case WM_WINDOWPOSCHANGED:
                    CheckMonitorChanged(hwnd);
                    return IntPtr.Zero;

                case WM_DEVICECHANGE:
                    var evt = wParam.ToInt32();
                    if (evt is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
                    {
                        // Cheaper and far more responsive than waiting for the next 1s poll.
                        _ = _vm.OnUsbHotPlugAsync();
                    }
                    return IntPtr.Zero;

                default:
                    return IntPtr.Zero;
            }
        }

        private IntPtr _lastMonitor = IntPtr.Zero;
        private bool _reapplyingMaximise;

        /// <summary>
        /// Win+Shift+Arrow moves a maximised window to another monitor, but Windows does
        /// not always re-ask for the maximised bounds on the way -- so the window keeps
        /// the size it was given for the old monitor and ends up not filling the new one.
        /// Noticing the monitor handle change and re-maximising forces a fresh
        /// WM_GETMINMAXINFO against the correct display.
        /// </summary>
        private void CheckMonitorChanged(IntPtr hwnd)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == _lastMonitor)
                return;

            var previous = _lastMonitor;
            _lastMonitor = monitor;

            // The first message just establishes a baseline; nothing has moved yet.
            if (previous == IntPtr.Zero)
                return;

            ReapplyMaximise();
        }

        private void ReapplyMaximise()
        {
            if (_reapplyingMaximise || WindowState != WindowState.Maximized)
                return;

            _reapplyingMaximise = true;

            // Deferred, because we are inside the window procedure and about to change
            // the very state being reported. The Normal round-trip is what makes Windows
            // recompute the maximised rectangle for the new monitor.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (WindowState != WindowState.Maximized)
                        return;

                    WindowState = WindowState.Normal;
                    WindowState = WindowState.Maximized;
                }
                finally
                {
                    _reapplyingMaximise = false;
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Pins the maximised rectangle to the monitor's work area.
        ///
        /// Left alone, Windows maximises a WindowStyle=None window to the monitor rect
        /// inflated by the frame thickness. The extra few pixels hang off every edge, so
        /// anything anchored to the top -- the auto-hiding window controls -- sits above
        /// the visible area and can never be reached. Using rcWork rather than rcMonitor
        /// also keeps the Windows taskbar visible.
        ///
        /// Everything here is in physical pixels on both sides, so this needs no DPI
        /// conversion and works per-monitor on mixed-DPI setups.
        /// </summary>
        private static void ClampMaximisedBounds(IntPtr hwnd, IntPtr lParam)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info))
                return;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            // ptMaxPosition is relative to the monitor origin, not the desktop origin.
            mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
            mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
            mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
            mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;

            // Without matching track sizes the window can still be dragged larger than
            // the work area while maximised.
            mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
            mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_vm.DismissTopOverlay())
                {
                    e.Handled = true;
                    return;
                }
            }

            // Tiling shortcuts come before forwarding, or the arrows would go to the
            // device instead. Alt makes WPF report the real key as SystemKey.
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (TryTile(key))
            {
                e.Handled = true;
                return;
            }

            // With a focused app window and no overlay in the way, the keyboard belongs
            // to the device. Printable characters are deliberately left alone here and
            // handled by TextInput below, which copes with layouts, dead keys and IMEs.
            if (TryForwardKeyToDevice(e.Key, ScrcpyVideoNative.KeyDown))
            {
                e.Handled = true;
                return;
            }

            // Ctrl+K / Ctrl+F to summon search, matching the launcher-style shells this
            // is imitating.
            if ((e.Key == Key.K || e.Key == Key.F) &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                OpenSearch();
                e.Handled = true;
            }
        }

        // ---------- tiling shortcuts ----------

        /// <summary>
        /// Windows-style tiling on the focused window. Win+Arrow is claimed by the real
        /// Windows shell and never reaches an app, so the shortcut here is
        /// Ctrl+Alt+Arrow; the behaviour it drives is the same.
        /// </summary>
        private bool TryTile(Key key)
        {
            const ModifierKeys combo = ModifierKeys.Control | ModifierKeys.Alt;

            if ((Keyboard.Modifiers & combo) != combo)
                return false;

            var direction = key switch
            {
                Key.Left => TileDirection.Left,
                Key.Right => TileDirection.Right,
                Key.Up => TileDirection.Up,
                Key.Down => TileDirection.Down,
                _ => (TileDirection?) null,
            };

            if (direction == null)
                return false;

            var window = FocusedWindow();
            if (window == null)
                return false;

            _vm.WindowManager.TileWithKeyboard(window, direction.Value);
            return true;
        }

        // ---------- keyboard forwarding ----------

        /// <summary>
        /// True while a shell overlay owns the keyboard, so that typing in the search box
        /// is not also typed into the phone.
        /// </summary>
        private bool IsOverlayOpen =>
            _vm.IsSearchOpen || _vm.IsConnectionOpen || _vm.IsConfirmOpen
            || _vm.IsNoticeOpen || _vm.IsIconPickerOpen || _vm.IsBusy
            || _vm.IsWelcomeOpen || _vm.IsAppDisplayOptionsOpen;

        /// <summary>The focused app window, overlays permitting.</summary>
        private AppWindowViewModel? FocusedWindow() =>
            IsOverlayOpen
                ? null
                : _vm.WindowManager.Windows.FirstOrDefault(w => w.IsActive && !w.IsMinimized);

        /// <summary>
        /// The window keystrokes should go to: the focused one, but only if it has a live
        /// session behind it -- the built-in Settings window is WPF content and has none.
        /// </summary>
        private AppWindowViewModel? KeyboardTarget()
        {
            var window = FocusedWindow();
            return window is { CanReceiveInput: true } ? window : null;
        }

        private bool TryForwardKeyToDevice(Key key, int action)
        {
            var target = KeyboardTarget();
            if (target == null)
                return false;

            var keycode = AndroidKeys.Translate(key);
            if (keycode == null)
                return false;   // printable: leave it for TextInput

            target.SendKey(action, keycode.Value, AndroidKeys.MetaState(Keyboard.Modifiers));
            return true;
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (TryForwardKeyToDevice(e.Key, ScrcpyVideoNative.KeyUp))
                e.Handled = true;
        }

        private void OnTextInput(object sender, TextCompositionEventArgs e)
        {
            var target = KeyboardTarget();
            if (target == null || string.IsNullOrEmpty(e.Text))
                return;

            // Control characters arrive here too (Enter as "\r", Backspace as "\b");
            // those already went out as keycodes, so drop them.
            if (e.Text.Length == 1 && char.IsControl(e.Text[0]))
                return;

            target.SendText(e.Text);
            e.Handled = true;
        }

        // ---------- auto-hiding top bar ----------

        /// <summary>Pointer must drop past this before the bar retracts, so it can be used.</summary>
        private const double HideZone = 54;

        private bool _topBarShown;

        /// <summary>
        /// Primary reveal: the pointer touched the strip at the top of the screen.
        ///
        /// Suppressed while a window is maximised -- the top edge then belongs to that
        /// window's own title bar, and popping the shell's chrome over it would fight
        /// for the same few pixels.
        /// </summary>
        private void TopHotZone_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_vm.WindowManager.IsImmersive)
                return;

            SetTopBar(true);
        }

        /// <summary>
        /// Retract once the pointer leaves the bar itself. The mouse-move check below is
        /// the backstop for the case where the pointer clips the hot zone and darts away
        /// without ever entering the bar, so MouseLeave never fires.
        /// </summary>
        private void TopBar_MouseLeave(object sender, MouseEventArgs e)
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed)
                return;   // mid-drag; don't yank the handle away

            SetTopBar(false);
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            // A maximised app window auto-hides its title bar and needs the pointer's
            // position to know when to show it again. It is fed from here, on the window
            // and as a preview, because that is the only place the moves are guaranteed
            // to arrive: further down they are lost to whichever overlay currently
            // covers the top edge, and to whatever has captured the mouse.
            Windows.UpdateChromeReveal(e.GetPosition(Windows));

            if (!_topBarShown)
                return;

            if (e.GetPosition(this).Y > HideZone && !TopBar.IsMouseOver)
                SetTopBar(false);
        }

        private void SetTopBar(bool show)
        {
            if (_topBarShown == show) return;
            _topBarShown = show;

            var duration = new Duration(TimeSpan.FromMilliseconds(show ? 140 : 180));
            var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };

            TopBar.BeginAnimation(OpacityProperty,
                new DoubleAnimation(show ? 1 : 0, duration) { EasingFunction = ease });

            TopBarShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(show ? 0 : -40, duration) { EasingFunction = ease });
        }

        /// <summary>The strip is also a drag handle, like a title bar would be.</summary>
        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.Button)
                return;

            if (e.ClickCount == 2)
            {
                ToggleMaximise();
                return;
            }

            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        // ---------- auto-hiding taskbar (immersive mode) ----------

        private const double TaskbarHeight = 44;

        private bool _taskbarHidden;

        /// <summary>
        /// Slides the taskbar out of the way while a window is maximised, and back when
        /// the pointer reaches the bottom edge. Called whenever IsImmersive changes.
        /// </summary>
        private void OnImmersiveChanged()
        {
            if (_vm.WindowManager.IsImmersive)
            {
                SetTaskbar(false);
                SetTopBar(false);   // the shell's own chrome has no business showing now
            }
            else
            {
                SetTaskbar(true);
            }
        }

        private void SetTaskbar(bool show)
        {
            if (_taskbarHidden == !show)
                return;

            _taskbarHidden = !show;

            var duration = new Duration(TimeSpan.FromMilliseconds(show ? 140 : 200));
            var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };

            TaskbarShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(show ? 0 : TaskbarHeight, duration) { EasingFunction = ease });
        }

        private void BottomHotZone_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_vm.WindowManager.IsImmersive)
                SetTaskbar(true);
        }

        private void Taskbar_MouseLeave(object sender, MouseEventArgs e)
        {
            // Only auto-hides while immersive; otherwise it is a permanent fixture.
            if (!_vm.WindowManager.IsImmersive)
                return;

            if (Mouse.LeftButton == MouseButtonState.Pressed)
                return;   // mid-drag

            SetTaskbar(false);
        }

        // ---------- window chrome ----------

        private void Taskbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximise();
                return;
            }

            // The taskbar doubles as the drag handle, since the window has no title bar.
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximiseRestore_Click(object sender, RoutedEventArgs e) => ToggleMaximise();

        private void ToggleMaximise() =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ---------- search ----------

        private void SearchBar_Click(object sender, MouseButtonEventArgs e) => OpenSearch();

        private void OpenSearch()
        {
            _vm.IsSearchOpen = true;

            // Focus has to wait until the overlay is actually rendered.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void SearchDismiss_Click(object sender, MouseButtonEventArgs e) => _vm.IsSearchOpen = false;

        private void ConnectionDismiss_Click(object sender, MouseButtonEventArgs e) => _vm.IsConnectionOpen = false;

        private void AudioDismiss_Click(object sender, MouseButtonEventArgs e) => _vm.Audio.IsPanelOpen = false;

        /// <summary>The ping is a shortcut into the shade it came from.</summary>
        private void NotificationPing_Click(object sender, MouseButtonEventArgs e)
        {
            _vm.Notifications.OpenPingedDevice();
            e.Handled = true;
        }

        private void NotificationsDismiss_Click(object sender, MouseButtonEventArgs e) =>
            _vm.Notifications.IsPanelOpen = false;

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && AppList.Items.Count > 0)
            {
                AppList.SelectedIndex = 0;
                if (AppList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item)
                    item.Focus();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && AppList.Items.Count > 0)
            {
                Activate(AppList.Items[0] as AppEntry);
                e.Handled = true;
            }
        }

        private void AppList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            Activate(AppList.SelectedItem as AppEntry);
            e.Handled = true;
        }

        private void AppList_Activate(object sender, MouseButtonEventArgs e) =>
            Activate(AppList.SelectedItem as AppEntry);

        private void Activate(AppEntry? app)
        {
            if (app != null)
                _vm.AddAppCommand.Execute(app);
        }
    }
}
