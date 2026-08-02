using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AdbDesktop
{
    /// <summary>
    /// The app list behind the taskbar search. Text only, no icons -- an icon can't exist
    /// until the APK has been pulled, and pulling all ~150 installed apps up front would
    /// cost minutes and gigabytes.
    /// </summary>
    public sealed class AppSearchViewModel : ViewModelBase
    {
        private readonly List<AppEntry> _allApps = new();

        private string _query = string.Empty;
        private bool _isLoading;
        private string _statusMessage = string.Empty;
        private string _deviceFilter = string.Empty;

        public ObservableCollection<AppEntry> Results { get; } = new();

        public string Query
        {
            get => _query;
            set
            {
                if (Set(ref _query, value))
                    ApplyFilter();
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => Set(ref _isLoading, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => Set(ref _statusMessage, value);
        }

        public bool HasApps => _allApps.Count > 0;

        /// <summary>
        /// Restricts the list to one device's apps. Set to the desktop currently on
        /// screen: an icon added there belongs to that device, so offering another
        /// device's apps would let you install something the desktop cannot run.
        /// Empty means the unified desktop, which shows everything.
        /// </summary>
        public string DeviceFilter
        {
            get => _deviceFilter;
            set
            {
                if (Set(ref _deviceFilter, value ?? string.Empty))
                    ApplyFilter();
            }
        }

        public void SetApps(IEnumerable<AppEntry> apps)
        {
            _allApps.Clear();
            _allApps.AddRange(apps);
            RaisePropertyChanged(nameof(HasApps));
            ApplyFilter();
        }

        public void Clear()
        {
            _allApps.Clear();
            Results.Clear();
            RaisePropertyChanged(nameof(HasApps));
        }

        private void ApplyFilter()
        {
            Results.Clear();

            // On a device's desktop, only that device's apps are installable there.
            IEnumerable<AppEntry> scoped = string.IsNullOrEmpty(_deviceFilter)
                ? _allApps
                : _allApps.Where(a => a.DeviceSerials.Contains(_deviceFilter, StringComparer.Ordinal));

            // Match on both the prettified name and the raw package, so "disc" finds
            // Discord and "com.discord" does too.
            var matches = string.IsNullOrWhiteSpace(_query)
                ? scoped
                : scoped.Where(a =>
                    a.DisplayName.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                    a.PackageName.Contains(_query, StringComparison.OrdinalIgnoreCase));

            foreach (var app in matches.Take(200))
                Results.Add(app);

            StatusMessage = _allApps.Count == 0
                ? "Add a device from the connection panel to list its apps."
                : Results.Count == 0
                    ? string.IsNullOrWhiteSpace(_query)
                        ? "This device has no listable apps."
                        : $"No app matches \"{_query}\"."
                    : string.Empty;
        }
    }
}
