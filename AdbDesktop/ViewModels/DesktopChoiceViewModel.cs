namespace AdbDesktop
{
    /// <summary>
    /// The very first screen: pick "Windows desktop" (not built yet) or "App desktop"
    /// (the real app). Shown from a standalone Window rather than the in-shell overlay
    /// style used elsewhere, since no shell exists yet for it to overlay.
    /// </summary>
    public sealed class DesktopChoiceViewModel : ViewModelBase
    {
        /// <summary>Raised when "App desktop" is chosen. The window closes itself on this.</summary>
        public event Action? AppDesktopChosen;

        private bool _isWindowsWipVisible;

        public bool IsWindowsWipVisible
        {
            get => _isWindowsWipVisible;
            private set => Set(ref _isWindowsWipVisible, value);
        }

        public string WindowsWipMessage { get; } =
            "Windows desktop is not built yet. Pick App desktop to continue, or close this window to exit.";

        public RelayCommand ChooseAppDesktopCommand { get; }
        public RelayCommand ChooseWindowsDesktopCommand { get; }

        public DesktopChoiceViewModel()
        {
            ChooseAppDesktopCommand = new RelayCommand(() => AppDesktopChosen?.Invoke());
            ChooseWindowsDesktopCommand = new RelayCommand(() => IsWindowsWipVisible = true);
        }
    }
}
