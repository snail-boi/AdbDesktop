using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AdbDesktop
{
    /// <summary>
    /// One row in the panel. Wraps a <see cref="DeviceNotification"/> to carry the icon,
    /// which the model has no business loading: it is a plain record read off the device,
    /// while the artwork comes from adbDesktop's own icon cache.
    /// </summary>
    public sealed class NotificationRow
    {
        public NotificationRow(DeviceNotification item, BitmapSource? icon)
        {
            Item = item;
            Icon = icon;
        }

        public DeviceNotification Item { get; }

        /// <summary>The desktop icon for this app, when it has one. Null falls back to the initial.</summary>
        public BitmapSource? Icon { get; }

        public bool HasIcon => Icon != null;

        public string AppName => Item.AppName;
        public string TimeText => Item.TimeText;

        public string Title => Item.Title;
        public string Text => Item.Text;

        public bool HasTitle => !string.IsNullOrEmpty(Item.Title);
        public bool HasText => !string.IsNullOrEmpty(Item.Text);

        /// <summary>Neither line came through, so the row says so instead of looking broken.</summary>
        public bool HasNoContent => !HasTitle && !HasText;

        /// <summary>Stand-in tile for an app that is not on any desktop, so has no icon.</summary>
        public string Initial => string.IsNullOrEmpty(AppName)
            ? "?"
            : AppName.Substring(0, 1).ToUpperInvariant();

        public string Tooltip => Item.Package;
    }

    /// <summary>
    /// The notification panel, one device at a time.
    ///
    /// Read-only by design rather than by omission: nothing reachable over adb can dismiss
    /// a notification or fire its action, so the only thing offered beyond reading them is
    /// pulling the shade down on the phone itself.
    ///
    /// The counts on the taskbar bells do not come from here -- they live on each
    /// <see cref="DeviceInfo"/> and are refreshed by the registry whether this panel has
    /// ever been opened or not. This only mirrors one device's list into rows.
    /// </summary>
    public sealed class NotificationsViewModel : ViewModelBase
    {
        private readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.Ordinal);

        private DeviceInfo? _device;
        private bool _isPanelOpen;
        private bool _isRefreshing;

        /// <summary>
        /// Supplied by the shell: re-reads one device's shade now. The registry owns the
        /// adb side of this, so the panel asks rather than polling on its own.
        /// </summary>
        public Func<DeviceInfo, Task>? Reader { get; set; }

        public ObservableCollection<NotificationRow> Rows { get; } = new();

        public RelayCommand RefreshCommand { get; }
        public RelayCommand OpenOnPhoneCommand { get; }
        public RelayCommand CloseCommand { get; }

        public NotificationsViewModel()
        {
            RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !_isRefreshing);
            OpenOnPhoneCommand = new RelayCommand(OpenOnPhone);
            CloseCommand = new RelayCommand(() => IsPanelOpen = false);
        }

        public DeviceInfo? Device => _device;

        public bool IsPanelOpen
        {
            get => _isPanelOpen;
            set => Set(ref _isPanelOpen, value);
        }

        public string PanelTitle => _device == null ? "Notifications" : $"{_device.Label} notifications";

        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set => Set(ref _isRefreshing, value);
        }

        /// <summary>Nothing posted, and the read worked -- an honestly empty shade.</summary>
        public bool IsEmpty => _device is { NotificationsReadable: true, HasNotifications: false };

        /// <summary>The device would not give up its notification dump.</summary>
        public bool IsUnreadable => _device is { NotificationsReadable: false };

        public bool IsRedacted => _device is { NotificationsRedacted: true };

        public string CountText => _device == null || !_device.NotificationsReadable
            ? string.Empty
            : _device.NotificationCount switch
            {
                0 => string.Empty,
                1 => "1 notification",
                var n => $"{n} notifications",
            };

        /// <summary>
        /// Clicking a device's bell. The same button closes the panel again, and clicking
        /// a different device's bell switches the panel over rather than closing it --
        /// matching how the audio button behaves.
        /// </summary>
        public void Toggle(DeviceInfo? device)
        {
            if (device == null)
                return;

            if (ReferenceEquals(device, _device) && IsPanelOpen)
            {
                IsPanelOpen = false;
                return;
            }

            ShowFor(device);
        }

        private void ShowFor(DeviceInfo device)
        {
            if (!ReferenceEquals(device, _device))
            {
                Detach();
                _device = device;
                device.PropertyChanged += OnDevicePropertyChanged;
            }

            IsPanelOpen = true;
            Rebuild();

            RaisePropertyChanged(nameof(Device));
            RaisePropertyChanged(nameof(PanelTitle));

            // Opening the panel is a request to see what is there NOW, not what the last
            // scheduled read found up to fifteen seconds ago.
            _ = RefreshAsync();
        }

        /// <summary>Drops the panel if its device has gone away.</summary>
        public void Sync(IReadOnlyList<DeviceInfo> added)
        {
            if (_device == null || added.Contains(_device))
                return;

            Detach();
            _device = null;
            IsPanelOpen = false;
            Rebuild();

            RaisePropertyChanged(nameof(Device));
            RaisePropertyChanged(nameof(PanelTitle));
        }

        private async Task RefreshAsync()
        {
            var device = _device;
            var reader = Reader;

            if (device == null || reader == null || IsRefreshing)
                return;

            IsRefreshing = true;
            try
            {
                await reader(device);
            }
            catch (Exception ex)
            {
                Debugger.show($"[NOTIF] Refresh failed for {device.Label}: {ex.Message}");
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        private void OpenOnPhone()
        {
            if (_device == null)
                return;

            // Fire and forget: the shade opening is visible on the phone, and there is
            // nothing to report back if the vendor build has no statusbar command.
            _ = NotificationService.ExpandShadeAsync(_device.Transport);
        }

        /// <summary>
        /// The registry writes each new read onto the device itself, so the panel follows
        /// the device rather than being pushed to. That also means a shade that changes
        /// while the panel is open updates in place.
        /// </summary>
        private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DeviceInfo.Notifications))
                return;

            Rebuild();
        }

        private void Detach()
        {
            if (_device != null)
                _device.PropertyChanged -= OnDevicePropertyChanged;
        }

        private void Rebuild()
        {
            Rows.Clear();

            if (_device != null)
            {
                foreach (var item in _device.Notifications)
                    Rows.Add(new NotificationRow(item, IconFor(item.Package)));
            }

            RaisePropertyChanged(nameof(IsEmpty));
            RaisePropertyChanged(nameof(IsUnreadable));
            RaisePropertyChanged(nameof(IsRedacted));
            RaisePropertyChanged(nameof(CountText));
        }

        /// <summary>
        /// The app's desktop icon, if it has one. Only apps the user has added have been
        /// through the picker, so most notifications have no artwork and fall back to the
        /// lettered tile. Cached per package because the list is rebuilt on every read.
        /// </summary>
        private BitmapSource? IconFor(string package)
        {
            if (_device == null || string.IsNullOrEmpty(package))
                return null;

            var key = $"{_device.Serial}|{package}";
            if (_iconCache.TryGetValue(key, out var cached))
                return cached;

            var image = IconStore.Load(IconStore.FileNameFor(package, _device.Serial));
            _iconCache[key] = image;
            return image;
        }

        public void Dispose()
        {
            Detach();
            _device = null;
        }
    }
}
