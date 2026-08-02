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

            // A maximised window has nowhere to be dragged to.
            if (window.IsMaximized)
                return;

            _dragWindow = window;
            _dragging = true;

            var p = e.GetPosition(this);
            _dragOffset = new Point(p.X - window.X, p.Y - window.Y);

            ((UIElement) sender).CaptureMouse();
            e.Handled = true;
        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _dragWindow == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            var p = e.GetPosition(this);
            _dragWindow.X = p.X - _dragOffset.X;
            _dragWindow.Y = p.Y - _dragOffset.Y;

            // Clamped live rather than only on release: releasing outside the app (or
            // losing capture) would otherwise strand a window off-screen.
            Model?.ClampPosition(_dragWindow);
        }

        private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging)
                return;

            ((UIElement) sender).ReleaseMouseCapture();

            if (_dragWindow != null)
                Model?.ClampPosition(_dragWindow);

            _dragWindow = null;
            _dragging = false;
        }

        // ---------- auto-hiding chrome on a maximised window ----------

        private void WindowChrome_MouseEnter(object sender, MouseEventArgs e)
        {
            var window = WindowOf(sender);
            if (window is { IsMaximized: true })
                window.IsChromeRevealed = true;
        }

        private void TitleBar_MouseLeave(object sender, MouseEventArgs e)
        {
            var window = WindowOf(sender);
            if (window == null || !window.IsMaximized)
                return;

            // Don't snatch the bar away mid-drag.
            if (_dragging || Mouse.LeftButton == MouseButtonState.Pressed)
                return;

            window.IsChromeRevealed = false;
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
