using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AdbDesktop
{
    /// <summary>
    /// The icon grid surface. Drag handling lives here rather than in the view model
    /// because it is pure input plumbing -- mouse capture, a drag threshold, and pixel
    /// offsets. The view model owns the part that matters: which cell an icon lands in.
    /// </summary>
    public partial class DesktopSurface : UserControl
    {
        private const double DragThreshold = 4;

        private DesktopIconViewModel? _dragIcon;
        private FrameworkElement? _dragElement;
        private Point _grabOffset;
        private Point _pressOrigin;
        private bool _dragStarted;

        private DesktopViewModel? Model => DataContext as DesktopViewModel;

        public DesktopSurface()
        {
            InitializeComponent();
            SizeChanged += OnSizeChanged;
            DataContextChanged += (_, _) => PushSurfaceSize();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) => PushSurfaceSize();

        private void PushSurfaceSize() => Model?.SetSurfaceSize(ActualWidth, ActualHeight);

        // ---------- inline rename ----------

        private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not TextBox box || !box.IsVisible)
                return;

            // Focus has to wait for the box to be laid out, and Explorer selects the
            // whole name so typing replaces it.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                box.Focus();
                Keyboard.Focus(box);
                box.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void RenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box || box.DataContext is not DesktopIconViewModel icon)
                return;

            if (e.Key == Key.Enter)
            {
                icon.CommitRename();
                Model?.Persist();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                icon.CancelRename();
                e.Handled = true;
            }
        }

        private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not TextBox box || box.DataContext is not DesktopIconViewModel icon)
                return;

            if (!icon.IsRenaming) return;

            // Clicking away commits, which is what Explorer does.
            icon.CommitRename();
            Model?.Persist();
        }

        /// <summary>
        /// Stops a click inside the rename box from being picked up as the start of an
        /// icon drag by the parent tile.
        /// </summary>
        private void RenameBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        // ---------- drag ----------

        private void Icon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not DesktopIconViewModel icon)
                return;

            // Never drag the tile that is currently being renamed.
            if (icon.IsRenaming)
                return;

            _dragIcon = icon;
            _dragElement = element;
            _dragStarted = false;
            _pressOrigin = e.GetPosition(this);

            // Where inside the tile the grab happened, so the icon doesn't jump to have
            // its top-left corner under the cursor.
            _grabOffset = new Point(_pressOrigin.X - icon.X, _pressOrigin.Y - icon.Y);

            element.CaptureMouse();
            e.Handled = true;
        }

        private void Icon_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragIcon == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            var position = e.GetPosition(this);

            // A small threshold keeps a plain click from being read as a 1px drag.
            if (!_dragStarted)
            {
                if (Math.Abs(position.X - _pressOrigin.X) < DragThreshold &&
                    Math.Abs(position.Y - _pressOrigin.Y) < DragThreshold)
                    return;

                _dragStarted = true;
                _dragIcon.IsDragging = true;
            }

            _dragIcon.X = position.X - _grabOffset.X;
            _dragIcon.Y = position.Y - _grabOffset.Y;
        }

        private void Icon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragIcon == null)
                return;

            _dragElement?.ReleaseMouseCapture();

            var icon = _dragIcon;
            var dragged = _dragStarted;

            _dragIcon = null;
            _dragElement = null;
            _dragStarted = false;

            icon.IsDragging = false;

            if (dragged)
            {
                Model?.MoveTo(icon, icon.X, icon.Y);
            }
            else
            {
                Model?.ApplyPixelPosition(icon);   // undo any sub-threshold nudge

                // A click that never became a drag opens the app. Single click rather
                // than double, matching the launcher feel; the drag threshold is what
                // keeps this from firing while arranging icons.
                Model?.ActivateIcon(icon);
            }
        }
    }
}
