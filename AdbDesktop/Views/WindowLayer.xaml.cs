using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AdbDesktop
{
    /// <summary>
    /// Hosts the app windows. Drag and resize are input plumbing and live here; which
    /// window is on top, and what min/max/close mean, belong to WindowManagerViewModel.
    /// </summary>
    public partial class WindowLayer : UserControl
    {
        private AppWindowViewModel? _dragWindow;
        private Point _dragOffset;
        private bool _dragging;

        /// <summary>Pointer position when the drag began, for the pull-loose threshold.</summary>
        private Point _dragStart;

        /// <summary>
        /// Set while dragging a tiled window that has not yet been pulled out of its
        /// tile. Like Windows, a snapped window does not come loose the instant it is
        /// touched -- the pointer has to travel first, so a click or a double-click on
        /// the title bar leaves the tiling alone.
        /// </summary>
        private bool _dragPending;

        private const double PullLooseDistance = 12;

        private WindowManagerViewModel? Model => DataContext as WindowManagerViewModel;

        public WindowLayer()
        {
            InitializeComponent();
            SizeChanged += (_, _) => PushSurfaceSize();
            DataContextChanged += (_, _) => PushSurfaceSize();
        }

        private void PushSurfaceSize() => Model?.SetSurfaceSize(ActualWidth, ActualHeight);

        private static AppWindowViewModel? WindowOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as AppWindowViewModel;

        // ---------- activation ----------

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Preview so that clicking anywhere in the window raises it, including on
            // the caption buttons and the content area.
            var window = WindowOf(sender);
            if (window != null && !window.IsActive)
                Model?.Activate(window);

            // Touching a window is an answer to "what goes in the gap?", even if the
            // answer is "not now".
            Model?.CloseSnapAssist();
        }

        // ---------- title bar drag ----------

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var window = WindowOf(sender);
            if (window == null)
                return;

            if (e.ClickCount == 2)
            {
                Model?.ToggleMaximise(window);
                e.Handled = true;
                return;
            }

            _dragWindow = window;
            _dragging = true;

            // A tiled or maximised window is dragged out of its tile rather than around
            // the desktop; that only starts once the pointer has actually moved.
            _dragPending = window.IsSnapped;

            var p = e.GetPosition(this);
            _dragStart = p;
            _dragOffset = new Point(p.X - window.X, p.Y - window.Y);

            ((UIElement) sender).CaptureMouse();
            e.Handled = true;
        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _dragWindow == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            var p = e.GetPosition(this);

            if (_dragPending)
            {
                if (Math.Abs(p.X - _dragStart.X) < PullLooseDistance
                    && Math.Abs(p.Y - _dragStart.Y) < PullLooseDistance)
                    return;

                PullLoose(_dragWindow, p);
                _dragPending = false;
            }

            _dragWindow.X = p.X - _dragOffset.X;
            _dragWindow.Y = p.Y - _dragOffset.Y;

            // Clamped live rather than only on release: releasing outside the app (or
            // losing capture) would otherwise strand a window off-screen.
            Model?.ClampPosition(_dragWindow);

            // Arm the snap the release would perform, and show where it would land.
            Model?.ShowSnapPreview(Model.ZoneForPointer(p));
        }

        /// <summary>
        /// Takes a window out of its tile mid-drag and hands it back its floating size,
        /// keeping the same point of the title bar under the pointer. Without the ratio
        /// the window would jump sideways the moment it shrank.
        /// </summary>
        private void PullLoose(AppWindowViewModel window, Point pointer)
        {
            var ratio = window.Width > 0
                ? Math.Clamp((pointer.X - window.X) / window.Width, 0, 1)
                : 0.5;

            var grabY = Math.Clamp(pointer.Y - window.Y, 0, AppWindowViewModel.TitleBarHeight);

            Model?.ApplySnap(window, SnapZone.None);

            _dragOffset = new Point(ratio * window.Width, grabY);
        }

        private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging)
                return;

            // Cleared before the capture is released, because that release raises
            // LostMouseCapture synchronously and it would otherwise cancel the drop.
            var window = _dragWindow;

            _dragWindow = null;
            _dragging = false;
            _dragPending = false;

            var zone = Model?.PreviewZone ?? SnapZone.None;
            Model?.HideSnapPreview();

            if (window != null)
            {
                if (zone != SnapZone.None)
                    Model?.ApplySnap(window, zone);
                else
                    Model?.ClampPosition(window);
            }

            ((UIElement) sender).ReleaseMouseCapture();
        }

        /// <summary>
        /// Losing capture (alt-tab, a modal appearing) has to end the drag as cleanly as
        /// a button release would, or the next mouse move keeps dragging a window nobody
        /// is holding.
        /// </summary>
        private void TitleBar_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (!_dragging)
                return;

            Model?.HideSnapPreview();

            if (_dragWindow != null)
                Model?.ClampPosition(_dragWindow);

            _dragWindow = null;
            _dragging = false;
            _dragPending = false;
        }

        // ---------- snap assist ----------

        /// <summary>
        /// Clicking the empty space around the candidates declines the offer, the same as
        /// Escape. The tiles themselves are buttons and handle their own clicks.
        /// </summary>
        private void SnapAssistBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Model?.CloseSnapAssist();
            e.Handled = true;
        }

        // ---------- auto-hiding chrome on a maximised window ----------
        //
        // Driven by where the pointer is, sampled from MainWindow's PreviewMouseMove,
        // rather than by MouseEnter on a thin strip at the top of the window. The strip
        // was unreliable for two reasons that no amount of tuning fixes: it has to be a
        // few pixels tall to stay out of the app's way, and it is at the very top of the
        // shell, which is where every other overlay wants to be as well -- anything
        // painted over it takes the hit test and the reveal simply never happens.

        /// <summary>How close to the top the pointer must come for the bar to slide in.</summary>
        private const double RevealZone = 14;

        /// <summary>
        /// How far it must drop for the bar to go away again. Deliberately below the bar
        /// rather than equal to the reveal distance: with one threshold the bar flickers
        /// while the pointer rests on the boundary, and it would retract while the
        /// pointer is still on the buttons.
        /// </summary>
        private const double ConcealZone = AppWindowViewModel.TitleBarHeight + 22;

        /// <summary>
        /// Reveals or hides the title bar of the maximised window for a pointer at this
        /// position, in this control's coordinates.
        /// </summary>
        public void UpdateChromeReveal(Point pointer)
        {
            var window = Model?.Windows.FirstOrDefault(w => w.IsMaximized && !w.IsMinimized);
            if (window == null)
                return;

            if (pointer.Y <= RevealZone)
            {
                window.IsChromeRevealed = true;
                return;
            }

            // Don't snatch the bar away mid-drag: dragging it downwards is exactly how a
            // maximised window is pulled loose, and the pointer leaves the zone at once.
            if (_dragging || Mouse.LeftButton == MouseButtonState.Pressed)
                return;

            if (pointer.Y > ConcealZone)
                window.IsChromeRevealed = false;
        }

        /// <summary>
        /// Hides the bar when the pointer leaves the shell -- unless it left over the top
        /// edge, which is the one direction that must not count. Leaving upwards means the
        /// pointer is at the very top of the screen, which is precisely where the bar is
        /// being asked for; taking that as "gone" made it vanish exactly on arrival.
        /// </summary>
        public void PointerLeftShell(Point pointer)
        {
            if (pointer.Y <= RevealZone)
                return;

            if (_dragging)
                return;

            foreach (var w in Model?.Windows ?? Enumerable.Empty<AppWindowViewModel>())
                w.IsChromeRevealed = false;
        }

        // ---------- input forwarding ----------
        //
        // Upstream does this in input_manager.c against SDL events. There is no SDL
        // window here, so WPF's events are translated instead. Coordinates are sent
        // normalised (0..1) and scaled against the frame size on the way out, so the
        // window size never has to match the display size.

        private static (double X, double Y) Normalised(IInputElement surface, MouseEventArgs e)
        {
            var element = (FrameworkElement) surface;
            var p = e.GetPosition(surface);

            var w = Math.Max(1, element.ActualWidth);
            var h = Math.Max(1, element.ActualHeight);

            return (Math.Clamp(p.X / w, 0, 1), Math.Clamp(p.Y / h, 0, 1));
        }

        private static AppWindowViewModel? Target(object sender) =>
            (sender as FrameworkElement)?.DataContext as AppWindowViewModel;

        private void Video_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var vm = Target(sender);
            if (vm == null || !vm.CanReceiveInput) return;

            var (nx, ny) = Normalised((IInputElement) sender, e);
            vm.SendTouch(ScrcpyVideoNative.TouchDown, nx, ny, ScrcpyVideoNative.ButtonPrimary);

            // Capture so a drag that leaves the window still delivers the UP, otherwise
            // the device is left with a stuck pointer.
            ((UIElement) sender).CaptureMouse();
            ((UIElement) sender).Focus();
            e.Handled = true;
        }

        private void Video_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var vm = Target(sender);
            if (vm == null || !vm.CanReceiveInput) return;

            var (nx, ny) = Normalised((IInputElement) sender, e);
            vm.SendTouch(ScrcpyVideoNative.TouchUp, nx, ny, 0);

            ((UIElement) sender).ReleaseMouseCapture();
            e.Handled = true;
        }

        private void Video_MouseMove(object sender, MouseEventArgs e)
        {
            var vm = Target(sender);
            if (vm == null || !vm.CanReceiveInput) return;

            // Only drags are forwarded. Android has no notion of a hovering mouse for
            // touch input, and sending MOVE with no button down produces phantom
            // gestures in most apps.
            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            var (nx, ny) = Normalised((IInputElement) sender, e);
            vm.SendTouch(ScrcpyVideoNative.TouchMove, nx, ny, ScrcpyVideoNative.ButtonPrimary);
        }

        private void Video_RightDown(object sender, MouseButtonEventArgs e)
        {
            var vm = Target(sender);
            if (vm == null || !vm.CanReceiveInput) return;

            // Right-click is BACK, as in scrcpy.
            vm.SendBack(ScrcpyVideoNative.KeyDown);
            e.Handled = true;
        }

        private void Video_RightUp(object sender, MouseButtonEventArgs e)
        {
            var vm = Target(sender);
            if (vm == null || !vm.CanReceiveInput) return;

            vm.SendBack(ScrcpyVideoNative.KeyUp);
            e.Handled = true;
        }

        private void Video_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var vm = Target(sender);
            if (vm == null || !vm.CanReceiveInput) return;

            var (nx, ny) = Normalised((IInputElement) sender, e);

            // WPF reports 120 units per notch; Android wants roughly 1.0 per notch.
            vm.SendScroll(nx, ny, 0, e.Delta / 120.0);
            e.Handled = true;
        }

        // ---------- resize ----------

        private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb)
                return;

            var window = WindowOf(thumb);
            if (window == null || window.IsMaximized)
                return;

            // Resizing a tiled window is the user taking its bounds back. The neighbours
            // are not re-flowed to match -- it simply stops being tiled, at the size it
            // has now, which is the one behaviour that cannot surprise anyone.
            if (window.IsTiled)
                Model?.ReleaseTile(window);

            var edge = thumb.Tag as string ?? string.Empty;

            if (edge.Contains('E'))
            {
                window.Width = Math.Max(AppWindowViewModel.MinWidth,
                                        window.Width + e.HorizontalChange);
            }
            else if (edge.Contains('W'))
            {
                // Dragging the left edge moves the origin as well as the size, and the
                // clamp has to be applied to both or the window creeps.
                var newWidth = Math.Max(AppWindowViewModel.MinWidth,
                                        window.Width - e.HorizontalChange);
                window.X += window.Width - newWidth;
                window.Width = newWidth;
            }

            if (edge.Contains('S'))
            {
                window.Height = Math.Max(AppWindowViewModel.MinHeight,
                                         window.Height + e.VerticalChange);
            }
            else if (edge.Contains('N'))
            {
                var newHeight = Math.Max(AppWindowViewModel.MinHeight,
                                         window.Height - e.VerticalChange);
                window.Y += window.Height - newHeight;
                window.Height = newHeight;
            }
        }
    }
}
