namespace AdbDesktop
{
    /// <summary>
    /// The per-app mirroring options dialog, opened from an icon's context menu.
    ///
    /// Edits a working copy rather than the stored override, so backing out leaves the
    /// config untouched. Saving writes through <see cref="DisplayConfig.SetOverride"/>,
    /// which drops an override that ends up with nothing overridden, so "use the
    /// defaults" leaves no entry behind rather than an empty one.
    /// </summary>
    public sealed class AppDisplayOptionsViewModel : ViewModelBase
    {
        private readonly DisplayOptions _draft;

        /// <summary>
        /// Restarts this app's session in place, or null when it has no window on the
        /// desktop. Supplied by the shell: none of these settings can be applied to a live
        /// session, so the only way to see a change is a fresh one. The window itself
        /// survives; only the session behind it is replaced.
        /// </summary>
        private readonly Action? _reopen;

        /// <summary>Raised with true when the user saved, false when they backed out.</summary>
        public event Action<bool>? Finished;

        public string Package { get; }
        public string DeviceSerial { get; }

        /// <summary>The icon's caption, so the dialog names the app the user right-clicked.</summary>
        public string AppName { get; }

        public DisplayOptionsViewModel Editor { get; }

        public RelayCommand SaveCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand UseDefaultsCommand { get; }
        public RelayCommand SaveAndRefreshCommand { get; }

        public AppDisplayOptionsViewModel(string appName, string package, string serial,
                                          Action? reopen)
        {
            AppName = appName;
            Package = package;
            DeviceSerial = serial;
            _reopen = reopen;

            // A copy: the dialog must be cancellable, and the stored override is live
            // config that the next window opened would read.
            _draft = App.Config.Display.FindOverride(serial, package)?.Clone()
                     ?? new DisplayOptions();

            // "Default (x)" in this dialog means the global setting, not the built-in
            // one, so the inherited set is the globals resolved on their own.
            Editor = new DisplayOptionsViewModel(
                _draft, DisplayOptions.Resolve(App.Config.Display.Defaults, null));

            Editor.Changed += OnEditorChanged;

            SaveCommand = new RelayCommand(() => Save(reopen: false));
            SaveAndRefreshCommand = new RelayCommand(() => Save(reopen: true));
            CancelCommand = new RelayCommand(() => Finished?.Invoke(false));
            UseDefaultsCommand = new RelayCommand(Editor.Reset);
        }

        private void OnEditorChanged()
        {
            RaisePropertyChanged(nameof(IsCustomised));
            RaisePropertyChanged(nameof(ScopeText));
        }

        /// <summary>
        /// Whether this app differs from the defaults at all. Drives the header line, so
        /// the dialog says which of the two states it is in without the user having to
        /// compare every field against the Settings page.
        /// </summary>
        public bool IsCustomised => !_draft.IsEmpty;

        public string ScopeText => IsCustomised
            ? "This app uses its own settings. Anything left on Default follows the Mirroring page in Settings."
            : "This app follows the defaults on the Mirroring page in Settings. Change anything below to give it settings of its own.";

        /// <summary>
        /// True when this app has a window on the desktop right now, which is the only
        /// case where the reopen button has anything to do.
        /// </summary>
        public bool IsWindowOpen => _reopen != null;

        private void Save(bool reopen)
        {
            _draft.Normalize();

            App.Config.Display.SetOverride(DeviceSerial, Package, _draft);
            App.SaveConfig();

            if (reopen)
                _reopen?.Invoke();

            Finished?.Invoke(true);
        }

        /// <summary>Drops the editor subscription once the dialog is gone.</summary>
        public void Detach() => Editor.Changed -= OnEditorChanged;
    }
}
