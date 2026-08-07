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
        private string _updateStatus = string.Empty;

        public RelayCommand CheckForUpdatesCommand { get; }

        public string UpdateStatus
        {
            get => _updateStatus;
            private set
            {
                if (Set(ref _updateStatus, value))
                    RaisePropertyChanged(nameof(HasUpdateStatus));
            }
        }

        public bool HasUpdateStatus => !string.IsNullOrEmpty(_updateStatus);

        /// <summary>
        /// Asks GitHub what the newest release is. The Updater raises its own prompt when
        /// there is one, so all that is reported back here is the "nothing to do" case --
        /// otherwise the button would look like it did nothing.
        /// </summary>
        private async Task CheckForUpdatesAsync()
        {
            _isCheckingUpdate = true;
            UpdateStatus = "Checking...";

            try
            {
                await Updater.CheckForUpdateAsync(Version, showPrompt: true, allowRemindLater: false);

                UpdateStatus = Updater.Status switch
                {
                    AdbDesktop.UpdateStatus.UpdateAvailable => $"Version {Updater.LatestVersion} is available.",
                    AdbDesktop.UpdateStatus.DebugBuild => "This build is newer than the latest release.",
                    _ => "Up to date.",
                };
            }
            catch (Exception ex)
            {
                Debugger.show("[SETTINGS] Update check failed: " + ex.Message);
                UpdateStatus = "Could not check for updates.";
            }
            finally
            {
                _isCheckingUpdate = false;
            }
        }

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
