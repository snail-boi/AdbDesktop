using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AdbDesktop
{
    /// <summary>
    /// Backs the built-in AdbDesktop Settings app.
    ///
    /// It runs inside an ordinary app window rather than a dialog, because it IS an app
    /// as far as the shell is concerned -- that is what it was added for. One instance
    /// per open window; nothing here is global state.
    /// </summary>
    public sealed class SettingsViewModel : ViewModelBase
    {
        private readonly DesktopViewModel _desktop;
        private int _tabIndex;

        /// <summary>
        /// The live taskbar view model, bound directly. The settings window edits the
        /// running shell rather than a copy, so there is nothing to apply or revert.
        /// </summary>
        public TaskbarViewModel Taskbar { get; }

        public IReadOnlyList<string> TimeFormats { get; } =
            new[] { "t", "HH:mm", "HH:mm:ss", "h:mm tt", "h:mm:ss tt" };

        public IReadOnlyList<string> DateFormats { get; } =
            new[] { "ddd dd/MM", "ddd dd MMM", "dd/MM/yyyy", "d MMMM", "dddd d MMMM", "yyyy-MM-dd" };

        public SettingsViewModel(DesktopViewModel desktop, TaskbarViewModel taskbar)
        {
            _desktop = desktop;
            Taskbar = taskbar;

            SelectTabCommand = new RelayCommand<string>(SelectTab);
            ChooseWallpaperCommand = new RelayCommand(ChooseWallpaper);
            ClearWallpaperCommand = new RelayCommand(() => { _desktop.ClearWallpaper(); RaiseWallpaperChanged(); });
            OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
            ApplyWallpaperToAllCommand = new RelayCommand(ApplyWallpaperToAll);
            CheckForUpdatesCommand = new RelayCommand(() => _ = CheckForUpdatesAsync(), () => !_isCheckingUpdate);
            UpdateCommand = new RelayCommand(() => _ = RunUpdateAsync(), () => !_isCheckingUpdate);

            // The startup check has usually answered by now, so this only covers the case
            // where it had not finished or could not reach GitHub.
            if (Updater.LatestVersion == null)
                _ = CheckForUpdatesAsync();
            else
                _hasChecked = true;
            ShowWelcomeCommand = new RelayCommand(() => WelcomeRequested?.Invoke());
        }

        // ---------- first-run guide ----------

        /// <summary>
        /// Asks for the welcome guide again. The shell owns it -- it is drawn over the
        /// whole desktop, not inside this window -- so this only raises the request.
        /// </summary>
        public event Action? WelcomeRequested;

        public RelayCommand ShowWelcomeCommand { get; }

        // ---------- update ----------

        private bool _isCheckingUpdate;
        private bool _hasChecked;

        public RelayCommand CheckForUpdatesCommand { get; }

        /// <summary>
        /// Installs the update. Goes through Updater.CheckForUpdateAsync with the prompt
        /// on, which is what shows the release notes and then downloads and runs the
        /// setup -- or opens the releases page for a portable copy, which cannot replace
        /// itself.
        /// </summary>
        public RelayCommand UpdateCommand { get; }

        /// <summary>
        /// Where the check got to, in words. Reads Updater.Status rather than keeping its
        /// own copy, so the startup check's answer is already here when Settings opens.
        /// </summary>
        public string UpdateStatus
        {
            get
            {
                if (_isCheckingUpdate)
                    return "Checking for updates...";

                if (!_hasChecked && Updater.LatestVersion == null)
                    return "Not checked yet.";

                return Updater.Status switch
                {
                    AdbDesktop.UpdateStatus.UpdateAvailable => $"{Updater.LatestVersion} is available.",
                    AdbDesktop.UpdateStatus.DebugBuild => "Newer than the latest release - this is a local build.",
                    _ => "Up to date.",
                };
            }
        }

        public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatus);

        /// <summary>Only then is there anything for the update button to do.</summary>
        public bool IsUpdateAvailable =>
            !_isCheckingUpdate && Updater.Status == AdbDesktop.UpdateStatus.UpdateAvailable;

        /// <summary>
        /// A portable copy is a folder the user put somewhere, so the installer cannot
        /// find it to replace. Its button says where it actually goes.
        /// </summary>
        public bool IsPortable => AppPaths.IsPortable;

        public string UpdateButtonText => IsPortable ? "Get it from GitHub" : "Update now";

        private void RaiseUpdateState()
        {
            RaisePropertyChanged(nameof(UpdateStatus));
            RaisePropertyChanged(nameof(HasUpdateStatus));
            RaisePropertyChanged(nameof(IsUpdateAvailable));
        }

        /// <summary>
        /// Asks GitHub what the newest release is, without a dialog. What comes back is
        /// shown on the page; installing is a separate, deliberate press.
        /// </summary>
        private async Task CheckForUpdatesAsync()
        {
            _isCheckingUpdate = true;
            RaiseUpdateState();

            try
            {
                await Updater.CheckForUpdateAsync(Version, showPrompt: false);
            }
            catch (Exception ex)
            {
                Debugger.show("[SETTINGS] Update check failed: " + ex.Message);
            }
            finally
            {
                _isCheckingUpdate = false;
                _hasChecked = true;
                RaiseUpdateState();
            }
        }

        /// <summary>
        /// The prompt carries the release notes and does the install, so this is the
        /// existing update flow, reached deliberately instead of on a check.
        /// </summary>
        private async Task RunUpdateAsync()
        {
            // Timed, because the two ways this can fail look identical from the outside.
            // The prompt is modal: if it opened at all, the await cannot return until it
            // is closed. Returning in milliseconds therefore means the dialog was never
            // shown -- the call took a silent early exit -- while a long wait means it was
            // shown somewhere off screen or behind the shell.
            var started = DateTime.UtcNow;

            Debugger.show($"[SETTINGS] Update pressed. status={Updater.Status} " +
                          $"latest={Updater.LatestVersion ?? "(none)"} portable={AppPaths.IsPortable}");

            try
            {
                await Updater.CheckForUpdateAsync(Version, showPrompt: true, allowRemindLater: false);

                Debugger.show($"[SETTINGS] Update returned after " +
                              $"{(DateTime.UtcNow - started).TotalMilliseconds:F0} ms, " +
                              $"status={Updater.Status}");
            }
            catch (Exception ex)
            {
                Debugger.show("[SETTINGS] Update failed: " + ex);
            }
            finally
            {
                RaiseUpdateState();
            }
        }

        // ---------- session restore ----------

        /// <summary>
        /// Bound by name rather than by the enum, so the list reads as prose in the UI
        /// and the stored value stays a proper enum.
        /// </summary>
        public IReadOnlyList<string> SessionRestoreNames { get; } =
            new[] { "Off", "Window position", "Window position and apps" };

        private static readonly SessionRestore[] SessionRestoreValues =
            { SessionRestore.Off, SessionRestore.Position, SessionRestore.Apps };

        public string SessionRestoreMode
        {
            get
            {
                var index = Array.IndexOf(SessionRestoreValues, App.Config.Session.Restore);
                return SessionRestoreNames[index < 0 ? 1 : index];
            }
            set
            {
                var index = SessionRestoreNames.ToList().IndexOf(value);
                if (index < 0)
                    return;

                var mode = SessionRestoreValues[index];
                if (App.Config.Session.Restore == mode)
                    return;

                App.Config.Session.Restore = mode;

                // Turning it off drops what was already remembered, so switching back on
                // starts clean rather than restoring a session from days ago.
                if (mode == SessionRestore.Off)
                    App.Config.Session.Windows.Clear();

                App.SaveConfig();

                RaisePropertyChanged(nameof(SessionRestoreMode));
                RaisePropertyChanged(nameof(IsSessionRestoreApps));
            }
        }

        public bool IsSessionRestoreApps =>
            App.Config.Session.Restore == SessionRestore.Apps;

        // ---------- advanced: resize delay ----------

        /// <summary>
        /// Typed in rather than chosen from a list: what works depends on the phone, and
        /// only the person using it knows how theirs behaves.
        /// </summary>
        public int ResizeDelayMs
        {
            get => App.Config.Advanced.ResizeDelayMs;
            set
            {
                var clamped = Math.Clamp(value, AdvancedConfig.MinResizeDelayMs,
                                                AdvancedConfig.MaxResizeDelayMs);

                if (App.Config.Advanced.ResizeDelayMs != clamped)
                {
                    App.Config.Advanced.ResizeDelayMs = clamped;
                    App.SaveConfig();
                }

                // Raised even when the value did not change, so a typed number that was
                // clamped snaps back in the box instead of sitting there looking accepted.
                RaisePropertyChanged(nameof(ResizeDelayMs));
                RaisePropertyChanged(nameof(IsResizeDelayLow));
                RaisePropertyChanged(nameof(IsResizeDelayRisky));
            }
        }

        public int MinResizeDelayMs => AdvancedConfig.MinResizeDelayMs;
        public int MaxResizeDelayMs => AdvancedConfig.MaxResizeDelayMs;

        // ---------- advanced: diagnostic logging ----------

        /// <summary>
        /// Applies immediately to adb tracing, and to scrcpy's log level on the next
        /// session opened -- an already-running session was created at the old level.
        /// </summary>
        public bool DebugLogging
        {
            get => App.Config.Advanced.DebugLogging;
            set
            {
                if (App.Config.Advanced.DebugLogging == value)
                    return;

                App.Config.Advanced.DebugLogging = value;
                App.SaveConfig();

                Debugger.AdvancedEnabled = value;
                RaisePropertyChanged(nameof(DebugLogging));
            }
        }

        /// <summary>Below the default, where the warning is worth showing.</summary>
        public bool IsResizeDelayLow =>
            App.Config.Advanced.ResizeDelayMs < AdvancedConfig.DefaultResizeDelayMs;

        /// <summary>Low enough that falling over is the expected outcome, not a risk.</summary>
        public bool IsResizeDelayRisky =>
            App.Config.Advanced.ResizeDelayMs < AdvancedConfig.RiskyResizeDelayMs;

        public RelayCommand<string> SelectTabCommand { get; }
        public RelayCommand ChooseWallpaperCommand { get; }
        public RelayCommand ClearWallpaperCommand { get; }
        public RelayCommand OpenDataFolderCommand { get; }
        public RelayCommand ApplyWallpaperToAllCommand { get; }

        // ---------- tabs ----------

        public IReadOnlyList<string> Tabs { get; } =
            new[] { "General", "Taskbar", "Icons", "Other", "Advanced" };

        public int TabIndex
        {
            get => _tabIndex;
            set
            {
                if (!Set(ref _tabIndex, value))
                    return;

                RaisePropertyChanged(nameof(IsGeneral));
                RaisePropertyChanged(nameof(IsTaskbar));
                RaisePropertyChanged(nameof(IsIcons));
                RaisePropertyChanged(nameof(IsOther));
                RaisePropertyChanged(nameof(IsAdvanced));
            }
        }

        public bool IsGeneral => _tabIndex == 0;
        public bool IsTaskbar => _tabIndex == 1;
        public bool IsIcons => _tabIndex == 2;
        public bool IsOther => _tabIndex == 3;
        public bool IsAdvanced => _tabIndex == 4;

        private void SelectTab(string? index)
        {
            if (int.TryParse(index, out var i))
                TabIndex = i;
        }

        // ---------- general: wallpaper ----------

        /// <summary>
        /// Names the desktop being changed. Wallpaper is per desktop, so the settings
        /// window has to say which one it is acting on rather than looking global.
        /// </summary>
        public string WallpaperScope => _desktop.IsUnified
            ? "Unified desktop"
            : "This device's desktop";

        public bool HasWallpaper => _desktop.HasWallpaper;

        public string WallpaperPath => _desktop.HasWallpaper
            ? Path.GetFileName(_desktop.WallpaperPath)
            : "No wallpaper set";

        public IReadOnlyList<string> FitModes { get; } =
            new[] { "Fill", "Fit", "Stretch", "Center" };

        public string WallpaperFit
        {
            get => _desktop.WallpaperFit;
            set
            {
                if (string.IsNullOrEmpty(value) || value == _desktop.WallpaperFit)
                    return;

                _desktop.WallpaperFit = value;
                RaisePropertyChanged();
            }
        }

        private void ChooseWallpaper()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose a wallpaper",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|All files|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog() != true)
                return;

            _desktop.SetWallpaper(dialog.FileName);
            RaiseWallpaperChanged();
        }

        /// <summary>
        /// Copies this desktop's wallpaper onto every other one. With no wallpaper set it
        /// clears them all, which is the useful inverse rather than a no-op.
        /// </summary>
        private void ApplyWallpaperToAll()
        {
            _desktop.ApplyWallpaperToAllDesktops();

            AppliedMessage = _desktop.HasWallpaper
                ? "Applied to every desktop."
                : "Cleared on every desktop.";

            RaisePropertyChanged(nameof(AppliedMessage));
            RaisePropertyChanged(nameof(HasAppliedMessage));
        }

        /// <summary>Transient confirmation under the buttons. Empty until something is applied.</summary>
        public string AppliedMessage { get; private set; } = string.Empty;

        public bool HasAppliedMessage => !string.IsNullOrEmpty(AppliedMessage);

        private void RaiseWallpaperChanged()
        {
            // Any further change makes the last "applied" note stale.
            AppliedMessage = string.Empty;
            RaisePropertyChanged(nameof(AppliedMessage));
            RaisePropertyChanged(nameof(HasAppliedMessage));

            RaisePropertyChanged(nameof(HasWallpaper));
            RaisePropertyChanged(nameof(WallpaperPath));
            RaisePropertyChanged(nameof(WallpaperFit));
        }

        // ---------- icons ----------

        /*
         * Backed straight by the config, like the taskbar settings: there is one desktop
         * on screen and it is repainted as each option changes, so there is nothing to
         * apply or revert.
         */

        private static IconsConfig IconCfg => App.Config.Icons;

        public IReadOnlyList<string> IconShapeNames => IconShapes.All;

        public bool IconBackground
        {
            get => IconCfg.ShowBackground;
            set => SetIconOption(() => IconCfg.ShowBackground = value, IconCfg.ShowBackground == value,
                nameof(IconBackground));
        }

        public bool ScaleIconsToFit
        {
            get => IconCfg.ScaleToFit;
            set => SetIconOption(() => IconCfg.ScaleToFit = value, IconCfg.ScaleToFit == value,
                nameof(ScaleIconsToFit));
        }

        public string IconShape
        {
            get => IconCfg.Shape;
            set
            {
                if (!IconShapes.IsKnown(value))
                    return;

                SetIconOption(() => IconCfg.Shape = value,
                    string.Equals(IconCfg.Shape, value, StringComparison.Ordinal), nameof(IconShape));
            }
        }

        /// <summary>
        /// How an icon says which device it runs on. Unified desktop only -- a device's
        /// own desktop already answers the question, so the setting says so rather than
        /// quietly doing nothing there.
        /// </summary>
        public IReadOnlyList<string> DeviceMarkerNames => DeviceMarkers.All;

        public string DeviceMarker
        {
            get => IconCfg.DeviceMarker;
            set
            {
                if (!DeviceMarkers.IsKnown(value))
                    return;

                SetIconOption(() => IconCfg.DeviceMarker = value,
                    string.Equals(IconCfg.DeviceMarker, value, StringComparison.Ordinal),
                    nameof(DeviceMarker));

                RaisePropertyChanged(nameof(IsColourMarker));
            }
        }

        /// <summary>Drives the "this is what the colours mean" note under the dropdown.</summary>
        public bool IsColourMarker =>
            string.Equals(IconCfg.DeviceMarker, DeviceMarkers.Colour, StringComparison.Ordinal);

        /// <summary>
        /// Raised after any icon setting changes. The shell owns everything that reads
        /// them -- the desktop, the taskbar and the connection panel -- so it does the
        /// repainting rather than this window reaching into three places itself.
        /// </summary>
        public event Action? IconSettingsChanged;

        private void SetIconOption(Action apply, bool unchanged, string name)
        {
            if (unchanged)
                return;

            apply();
            App.SaveConfig();
            RaisePropertyChanged(name);

            IconSettingsChanged?.Invoke();
        }

        // ---------- advanced ----------

        public string DataFolder => AppPaths.DataRoot;

        public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString()
                                 ?? "unknown";

        private void OpenDataFolder()
        {
            try
            {
                AppPaths.EnsureDataDirectories();
                Process.Start(new ProcessStartInfo(AppPaths.DataRoot) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debugger.show("[SETTINGS] Could not open the data folder: " + ex.Message);
            }
        }
    }
}
