using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AdbDesktop
{
    /// <summary>
    /// The spotlight overlay for the onboarding tour: everything dims except a cutout
    /// around whichever real control the current step points at, with a callout
    /// explaining it next to the cutout. Set <see cref="TargetResolver"/> (from
    /// MainWindow.xaml.cs, which is the one place that knows about every named control
    /// the tour can point at) before the tour is shown.
    /// </summary>
    public partial class OnboardingOverlay : UserControl
    {
        public Func<OnboardingTarget, FrameworkElement?>? TargetResolver { get; set; }

        private OnboardingTourViewModel? _vm;
        private int _pendingRetries;
        private int _generation;

        public OnboardingOverlay()
        {
            InitializeComponent();

            DataContextChanged += (_, _) => HookViewModel();
            SizeChanged += (_, _) => Recompute();

            // Replaying the tour from Settings re-shows this same instance rather than
            // recreating it: the outer overlay Grid goes Collapsed -> Visible again, but
            // WPF does not reliably raise SizeChanged for that on its own (the control
            // was already measured once before), which left the previous session's last
            // spotlight position on screen until something else happened to move it.
            // IsVisibleChanged is the direct signal for "this can be drawn now" and
            // fires every time, first show or replay alike.
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true)
                    Recompute();
            };
        }

        private void HookViewModel()
        {
            if (_vm != null)
                _vm.PropertyChanged -= OnVmPropertyChanged;

            _vm = DataContext as OnboardingTourViewModel;

            if (_vm != null)
                _vm.PropertyChanged += OnVmPropertyChanged;

            Recompute();
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OnboardingTourViewModel.Current))
                Recompute();
        }

        /// <summary>
        /// Entry point for anything that might have moved the target: a step change,
        /// this overlay becoming visible, or its own resize. Deliberately not driven by
        /// the target's own LayoutUpdated -- that event fires on every layout pass in
        /// the whole tree, not just when the tracked element actually moves, and
        /// reassigning DimLayer.Data (a new Geometry each time) itself forces another
        /// layout pass. Wiring the two together is a feedback loop: it free-runs,
        /// flickering the spotlight between a stale rect and the real one every frame.
        ///
        /// Every call bumps <see cref="_generation"/> and defers the actual work to
        /// ContextIdle, well below layout/render priority, so it only runs once
        /// whatever triggered it (a step change opening the connection panel, in
        /// particular) has fully finished settling -- and so that if several of these
        /// fire in a burst, only the last one requested is allowed to actually draw:
        /// an earlier one still finishes its own retry chain, but a stale generation
        /// check makes it a no-op instead of overwriting a newer, correct result with
        /// one read from layout that had not finished converging yet. That overwrite
        /// is what showed the callout landing in the right place for a frame and then
        /// snapping back to the top-left corner.
        /// </summary>
        private void Recompute()
        {
            var generation = ++_generation;
            _pendingRetries = 0;
            Dispatcher.BeginInvoke(new Action(() => RecomputeCore(generation)), DispatcherPriority.ContextIdle);
        }

        private void RecomputeCore(int generation)
        {
            if (generation != _generation)
                return;   // superseded by a newer request; let that one win instead

            UpdateLayout();

            // Collapsed (tour not open) leaves this at 0x0; nothing to draw yet, and
            // IsVisibleChanged/SizeChanged fire again once the overlay is shown.
            if (_vm == null || ActualWidth <= 0 || ActualHeight <= 0)
                return;

            var step = _vm.Current;

            if (step.IsFinal)
            {
                DimLayer.Data = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
                return;
            }

            var target = TargetResolver?.Invoke(step.Target);
            if (target == null || target.ActualWidth <= 0 || target.ActualHeight <= 0 || !target.IsVisible)
            {
                // Genuinely not ready yet (e.g. still waiting on something outside our
                // control). Retry a bounded number of times, one dispatcher pass apart,
                // rather than an open-ended subscription.
                if (_pendingRetries++ < 10)
                    Dispatcher.BeginInvoke(new Action(() => RecomputeCore(generation)), DispatcherPriority.ContextIdle);
                return;
            }

            UpdateGeometry(target);
        }

        private void UpdateGeometry(FrameworkElement target)
        {
            // TransformToVisual, not TransformToAncestor: the target lives in the main
            // shell's own visual tree, not inside this overlay, so there is no ancestor
            // relationship between them, only a common root.
            var topLeft = target.TransformToVisual(this).Transform(new Point(0, 0));
            var rect = new Rect(topLeft, new Size(target.ActualWidth, target.ActualHeight));
            rect.Inflate(6, 6);

            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
            group.Children.Add(new RectangleGeometry(rect, 8, 8));
            DimLayer.Data = group;

            PositionCallout(rect);
        }

        /// <summary>
        /// Centered under the hole, flipped above it if that would run off the bottom
        /// of the screen, and clamped to stay fully on screen either way.
        /// </summary>
        private void PositionCallout(Rect hole)
        {
            // Measured at its real constrained width (320, same as the XAML), not
            // PositiveInfinity: a Wrap TextBlock given infinite available width lays
            // out as one unwrapped line instead, which can hugely overstate
            // DesiredSize.Width and collapse the clamp below to its lower bound --
            // pinning the callout to the top-left corner regardless of where the hole
            // actually is.
            Callout.Measure(new Size(Callout.Width, double.PositiveInfinity));
            var size = Callout.DesiredSize;
            const double gap = 14, margin = 12;

            var left = hole.Left + hole.Width / 2 - size.Width / 2;
            var top = hole.Bottom + gap;
            if (top + size.Height > ActualHeight - margin)
                top = hole.Top - gap - size.Height;

            left = Math.Clamp(left, margin, Math.Max(margin, ActualWidth - size.Width - margin));
            top = Math.Clamp(top, margin, Math.Max(margin, ActualHeight - size.Height - margin));

            // Canvas.Left/Top rather than Margin + HorizontalAlignment/VerticalAlignment:
            // unambiguous absolute placement within the Canvas that now hosts Callout,
            // with nothing left to interact unexpectedly with its explicit Width.
            Canvas.SetLeft(Callout, left);
            Canvas.SetTop(Callout, top);
        }
    }
}
