using System.Globalization;
using System.Windows.Threading;

namespace AdbDesktop
{
    /// <summary>
    /// The taskbar's own status area: clock and date.
    ///
    /// Battery used to live here too. It is per-device now -- each device's taskbar bundle
    /// carries its own, mirrored straight off <see cref="DeviceInfo"/>, which DeviceRegistry
    /// already polls. Nothing here is device-specific any more.
    /// </summary>
    public sealed class TaskbarViewModel : ViewModelBase
    {
        private readonly DispatcherTimer _clockTimer;

        private string _time = string.Empty;
        private string _date = string.Empty;

        public TaskbarViewModel()
        {
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, _) => UpdateClock();

            UpdateClock();
        }

        public void Start() => _clockTimer.Start();

        public void Stop() => _clockTimer.Stop();

        // ---------- chrome settings ----------

        /*
         * Backed straight by the config rather than mirrored into fields: there is one
         * taskbar, the settings window and the taskbar itself both bind here, and a copy
         * would just be a second place for the two to disagree.
         */

        private static TaskbarConfig Cfg => App.Config.Taskbar;

        /// <summary>Raised when any chrome setting changes, so the shell can react.</summary>
        public event Action? ChromeChanged;

        private bool SetOption(Func<bool> get, Action<bool> set, bool value, string name)
        {
            if (get() == value)
                return false;

            set(value);
            App.SaveConfig();
            RaisePropertyChanged(name);
            ChromeChanged?.Invoke();
            return true;
        }

        public bool ShowNavButtons
        {
            get => Cfg.ShowNavButtons;
            set => SetOption(() => Cfg.ShowNavButtons, v => Cfg.ShowNavButtons = v, value, nameof(ShowNavButtons));
        }

        public bool ReverseNavButtons
        {
            get => Cfg.ReverseNavButtons;
            set => SetOption(() => Cfg.ReverseNavButtons, v => Cfg.ReverseNavButtons = v, value,
                nameof(ReverseNavButtons));
        }

        public bool ShowWindowTabs
        {
            get => Cfg.ShowWindowTabs;
            set => SetOption(() => Cfg.ShowWindowTabs, v => Cfg.ShowWindowTabs = v, value, nameof(ShowWindowTabs));
        }

        public bool ShowNotifications
        {
            get => Cfg.ShowNotifications;
            set => SetOption(() => Cfg.ShowNotifications, v => Cfg.ShowNotifications = v, value,
                nameof(ShowNotifications));
        }

        public bool ShowBattery
        {
            get => Cfg.ShowBattery;
            set => SetOption(() => Cfg.ShowBattery, v => Cfg.ShowBattery = v, value, nameof(ShowBattery));
        }

        public bool ShowClock
        {
            get => Cfg.ShowClock;
            set => SetOption(() => Cfg.ShowClock, v => Cfg.ShowClock = v, value, nameof(ShowClock));
        }

        public bool SearchIconOnly
        {
            get => Cfg.SearchIconOnly;
            set
            {
                if (SetOption(() => Cfg.SearchIconOnly, v => Cfg.SearchIconOnly = v, value, nameof(SearchIconOnly)))
                    RaisePropertyChanged(nameof(ShowSearchBar));
            }
        }

        public bool ShowSearchBar => !Cfg.SearchIconOnly;

        public bool DisableMultiDesktop
        {
            get => Cfg.DisableMultiDesktop;
            set => SetOption(() => Cfg.DisableMultiDesktop, v => Cfg.DisableMultiDesktop = v, value,
                nameof(DisableMultiDesktop));
        }

        public string TimeFormat
        {
            get => Cfg.TimeFormat;
            set => SetFormat(v => Cfg.TimeFormat = v, value, "t", nameof(TimeFormat));
        }

        public string DateFormat
        {
            get => Cfg.DateFormat;
            set => SetFormat(v => Cfg.DateFormat = v, value, "ddd dd/MM", nameof(DateFormat));
        }

        private void SetFormat(Action<string> set, string? value, string fallback, string name)
        {
            var format = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

            set(format);
            App.SaveConfig();
            RaisePropertyChanged(name);
            UpdateClock();
        }

        // ---------- clock ----------

        public string Time
        {
            get => _time;
            private set => Set(ref _time, value);
        }

        public string Date
        {
            get => _date;
            private set => Set(ref _date, value);
        }

        private void UpdateClock()
        {
            var now = DateTime.Now;

            // Both formats are user-settable .NET format strings, rendered in the machine's
            // locale -- so the default "ddd dd/MM" is "za 01/08" under nl-NL and
            // "Sat 01/08" under en-GB. A bad format string throws rather than silently
            // showing nonsense, so it falls back instead of taking the clock down.
            Time = Format(now, Cfg.TimeFormat, "t");
            Date = Format(now, Cfg.DateFormat, "ddd dd/MM");
        }

        private static string Format(DateTime value, string format, string fallback)
        {
            try
            {
                return value.ToString(format, CultureInfo.CurrentCulture);
            }
            catch (FormatException)
            {
                return value.ToString(fallback, CultureInfo.CurrentCulture);
            }
        }

    }
}
