using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AdbDesktop
{
    /// <summary>
    /// One row in the connection panel. This is every device adb can see, which is a
    /// wider set than <see cref="DeviceRegistry.Devices"/>: offline and unauthorised
    /// entries appear here too, because "seen but unauthorised" is the single most useful
    /// troubleshooting signal.
    /// </summary>
    public sealed class DeviceListItem : ViewModelBase
    {
        private bool _isAdded;

        /// <summary>
        /// Set by ConnectionViewModel. The row reads and writes its own auto-add flag
        /// through this rather than routing every toggle back up through a command.
        /// </summary>
        public DeviceRegistry? Registry { get; init; }

        public string Serial { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public bool IsOnline { get; init; }

        /// <summary>Drives the dot colour: green for USB, blue for wireless.</summary>
        public bool IsUsb { get; init; }

        /// <summary>Added to AdbDesktop, so it has a desktop of its own.</summary>
        public bool IsAdded
        {
            get => _isAdded;
            set
            {
                if (Set(ref _isAdded, value))
                {
                    RaisePropertyChanged(nameof(CanAdd));
                    RaisePropertyChanged(nameof(CanRemove));
                }
            }
        }

        /// <summary>
        /// An offline device cannot be added: there is nothing to enumerate apps from,
        /// so its desktop would be empty and unfillable.
        /// </summary>
        public bool CanAdd => IsOnline && !_isAdded;

        public bool CanRemove => _isAdded;

        /// <summary>
        /// Re-add this device automatically whenever it comes online. Per device, so
        /// marking one phone a fixture does not silently adopt every other phone that
        /// gets plugged into this PC.
        /// </summary>
        public bool AutoAdd
        {
            get => Registry?.GetAutoAdd(Serial) ?? false;
            set
            {
                if (Registry == null || Registry.GetAutoAdd(Serial) == value)
                    return;

                Registry.SetAutoAdd(Serial, value);
                RaisePropertyChanged();

                // Switching it on adds the device there and then.
                IsAdded = Registry.IsAdded(Serial);
            }
        }
    }

    /// <summary>
    /// Backs the connection panel that slides up out of the taskbar. This was a separate
    /// Window; it is inline now because a modal dialog breaks the illusion that AdbDesktop
    /// is a desktop rather than an app. Pairing is inline too, for the same reason --
    /// <see cref="Pair"/> is swapped in as a sub-view rather than opening a dialog.
    /// </summary>
    public sealed class ConnectionViewModel : ViewModelBase
    {
        private readonly AdbDesktopConfig _config;
        private readonly DeviceRegistry _registry;
        private readonly DispatcherTimer _refreshTimer;

        private string _listSignature = string.Empty;
        private bool _isPairing;
        private WifiPairViewModel? _pair;
        private int _modeIndex;
        private string _modeDetail = string.Empty;
        private string _statusMessage = string.Empty;
        private bool _canTcpIp;
        private bool _isWorking;

        public ObservableCollection<DeviceListItem> Devices { get; } = new();

        public RelayCommand StartPairCommand { get; }
        public RelayCommand CancelPairCommand { get; }
        public RelayCommand TcpIpCommand { get; }

        /// <summary>Gives a device a desktop of its own.</summary>
        public RelayCommand<DeviceListItem> AddDeviceCommand { get; }

        /// <summary>
        /// Asks to drop a device. Handled by MainViewModel rather than here: it destroys
        /// that device's icons, so it needs a confirmation the panel cannot show.
        /// </summary>
        public RelayCommand<DeviceListItem> RemoveDeviceCommand { get; }

        public event Action<string>? RemoveRequested;

        public ConnectionViewModel(AdbDesktopConfig config, DeviceRegistry registry)
        {
            _config = config;
            _registry = registry;

            _modeIndex = config.Device.WifiMode switch
            {
                WirelessMode.WirelessDebugging => 0,
                WirelessMode.TcpIp => 1,
                _ => 2
            };

            StartPairCommand = new RelayCommand(StartPair);
            CancelPairCommand = new RelayCommand(CancelPair);
            AddDeviceCommand = new RelayCommand<DeviceListItem>(AddDevice);
            RemoveDeviceCommand = new RelayCommand<DeviceListItem>(item =>
            {
                if (item != null)
                    RemoveRequested?.Invoke(item.Serial);
            });
            TcpIpCommand = new RelayCommand(async () => await EnableTcpIpAsync(), () => _canTcpIp && !_isWorking);

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += async (_, _) => await RefreshAsync();

            UpdateModeDetail();
        }

        // ---------- lifecycle ----------

        public async void Activate()
        {
            await RefreshAsync();
            _refreshTimer.Start();
        }

        public void Deactivate()
        {
            _refreshTimer.Stop();
            CancelPair();
        }

        // ---------- bound state ----------

        public bool IsPairing
        {
            get => _isPairing;
            private set
            {
                if (Set(ref _isPairing, value))
                    RaisePropertyChanged(nameof(IsDeviceListVisible));
            }
        }

        public bool IsDeviceListVisible => !_isPairing;

        public WifiPairViewModel? Pair
        {
            get => _pair;
            private set => Set(ref _pair, value);
        }

        /// <summary>0 = Wireless debugging, 1 = TCP/IP, 2 = USB only.</summary>
        public int ModeIndex
        {
            get => _modeIndex;
            set
            {
                if (!Set(ref _modeIndex, value)) return;

                _config.Device.WifiMode = value switch
                {
                    0 => WirelessMode.WirelessDebugging,
                    1 => WirelessMode.TcpIp,
                    _ => WirelessMode.UsbOnly
                };

                App.SaveConfig();
                UpdateModeDetail();
            }
        }

        public string ModeDetail
        {
            get => _modeDetail;
            private set => Set(ref _modeDetail, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => Set(ref _statusMessage, value);
        }

        public bool HasNoDevices => Devices.Count == 0;

        // ---------- device listing ----------

        public async Task RefreshAsync()
        {
            var output = await AdbHelper.RunAdbCaptureAsync("devices -l");
            var lines = DeviceQuery.ParseDeviceLines(output);

            var fresh = new List<DeviceListItem>();

            // Online devices come from the registry, which has already bundled a phone's
            // USB / TCP-IP / mDNS transports into one entry. Re-reading the adb lines here
            // would put the same phone in the list two or three times over.
            foreach (var device in _registry.Devices)
            {
                fresh.Add(new DeviceListItem
                {
                    Registry = _registry,
                    Serial = device.Serial,
                    Title = device.Label,
                    Detail = $"{device.TransportText}  ·  {DescribeTransports(device)}",
                    IsOnline = true,
                    IsUsb = device.IsUsb,
                    IsAdded = device.IsAdded
                });
            }

            // Offline and unauthorised entries never reach the registry, but they still
            // get a row: "seen but unauthorised" is the most useful signal there is when
            // a phone refuses to show up.
            foreach (var line in lines)
            {
                if (!string.IsNullOrEmpty(DeviceQuery.GetOnlineSerial(line)))
                    continue;

                var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                var model = ExtractModel(line);

                fresh.Add(new DeviceListItem
                {
                    Registry = _registry,
                    Serial = parts[0],
                    Title = string.IsNullOrEmpty(model) ? parts[0] : model,
                    Detail = $"{parts[0]}  ·  {ExtractState(line)}",
                    IsOnline = false,
                    IsAdded = false
                });
            }

            // Rebuilding the list on every 2s tick would swap rows out from under a mouse
            // that is on its way to an Add button, so only rebuild when it really differs.
            var signature = string.Join("|",
                fresh.Select(d => $"{d.Serial}:{d.IsOnline}:{d.IsUsb}:{d.IsAdded}:{d.AutoAdd}"));
            if (signature != _listSignature)
            {
                _listSignature = signature;

                Devices.Clear();
                foreach (var item in fresh)
                    Devices.Add(item);

                RaisePropertyChanged(nameof(HasNoDevices));
            }

            _canTcpIp = !string.IsNullOrEmpty(DeviceQuery.FindConnectedUsbSerial(lines));
        }

        /// <summary>
        /// Adds a device. It has to be in the registry -- that is, actually connected --
        /// which <see cref="DeviceListItem.CanAdd"/> already guarantees for enabled rows.
        /// </summary>
        private void AddDevice(DeviceListItem? item)
        {
            if (item == null || !item.CanAdd)
                return;

            var device = _registry.BySerial(item.Serial);
            if (device == null)
            {
                StatusMessage = $"{item.Title} is no longer connected.";
                return;
            }

            _registry.Add(device);
            item.IsAdded = true;
            StatusMessage = $"Added {device.Label}. It now has a desktop of its own.";
        }

        /// <summary>Reflects a change made elsewhere (a removal, or an auto-add).</summary>
        public void SyncAddedState()
        {
            foreach (var item in Devices)
                item.IsAdded = _registry.IsAdded(item.Serial);

            _listSignature = string.Join("|",
                Devices.Select(d => $"{d.Serial}:{d.IsOnline}:{d.IsUsb}:{d.IsAdded}:{d.AutoAdd}"));
        }

        /// <summary>
        /// The transport in use, and how many spares are behind it. Names the spare count
        /// rather than listing them, so a phone reachable three ways stays one short line.
        /// </summary>
        private static string DescribeTransports(DeviceInfo device)
        {
            var spares = device.Transports.Count - 1;

            return spares <= 0
                ? device.Transport
                : $"{device.Transport}  (+{spares} other route{(spares == 1 ? "" : "s")})";
        }

        private static string ExtractState(string line)
        {
            var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1] : "unknown";
        }

        /// <summary>`adb devices -l` appends "model:SM_S937B" among other key:value pairs.</summary>
        private static string ExtractModel(string line)
        {
            foreach (var token in line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                    return token.Substring("model:".Length).Replace('_', ' ');
            }

            return string.Empty;
        }

        // ---------- pairing ----------

        private void StartPair()
        {
            var vm = new WifiPairViewModel();
            vm.RequestClose += OnPairFinished;

            Pair = vm;
            IsPairing = true;
            vm.Start();
        }

        private void CancelPair()
        {
            if (Pair == null) return;

            Pair.RequestClose -= OnPairFinished;
            Pair.Cancel();
            Pair = null;
            IsPairing = false;
        }

        private void OnPairFinished(bool success)
        {
            var vm = Pair;
            if (vm == null) return;

            var serviceName = vm.ServiceName;

            UiThread.RunAsync(() =>
            {
                vm.RequestClose -= OnPairFinished;
                vm.Cancel();
                Pair = null;
                IsPairing = false;

                if (!success || string.IsNullOrWhiteSpace(serviceName))
                    return;

                _config.Device.MdnsServiceName = serviceName;
                _config.Device.WifiMode = WirelessMode.WirelessDebugging;
                _config.Device.IsWifiEnabled = true;
                App.SaveConfig();

                ModeIndex = 0;
                UpdateModeDetail();
                StatusMessage = $"Paired as {serviceName}.";
            });
        }

        // ---------- tcp/ip ----------

        private async Task EnableTcpIpAsync()
        {
            _isWorking = true;
            StatusMessage = "Switching the device to TCP/IP...";

            try
            {
                var lines = DeviceQuery.ParseDeviceLines(await AdbHelper.RunAdbCaptureAsync("devices"));
                var usb = DeviceQuery.FindConnectedUsbSerial(lines);

                if (string.IsNullOrEmpty(usb))
                {
                    StatusMessage = "No USB device is connected.";
                    return;
                }

                var ipPort = await DeviceQuery.SetupWirelessFromUsbAsync(usb);

                if (string.IsNullOrEmpty(ipPort))
                {
                    StatusMessage = "Could not switch to TCP/IP. Check that the phone is on this network.";
                    return;
                }

                _config.Device.SelectedDeviceWiFi = ipPort;
                _config.Device.WifiMode = WirelessMode.TcpIp;
                _config.Device.IsWifiEnabled = true;
                App.SaveConfig();

                ModeIndex = 1;
                StatusMessage = $"Connected over TCP/IP at {ipPort}.";
                await RefreshAsync();
            }
            finally
            {
                _isWorking = false;
            }
        }

        private void UpdateModeDetail()
        {
            ModeDetail = _config.Device.WifiMode switch
            {
                WirelessMode.WirelessDebugging => string.IsNullOrWhiteSpace(_config.Device.MdnsServiceName)
                    ? "Not paired yet. Pairing stores an mDNS service name, which is the only identifier that survives the phone reassigning its port."
                    : $"Paired as {_config.Device.MdnsServiceName}.",

                WirelessMode.TcpIp => string.IsNullOrWhiteSpace(_config.Device.SelectedDeviceWiFi)
                    ? "Connect over USB first, then use \"Enable TCP/IP from USB\"."
                    : $"Last known endpoint: {_config.Device.SelectedDeviceWiFi}.",

                _ => "Wireless connections are disabled. Only USB will be used."
            };
        }
    }
}
