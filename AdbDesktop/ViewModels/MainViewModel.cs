using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AdbDesktop
{
    public sealed class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly DeviceRegistry _registry;

        private bool _isSearchOpen;
        private bool _isConnectionOpen;
        private bool _isBusy;
        private string _busyMessage = string.Empty;
        private string _deviceLabel = "Disconnected";
        private bool _isConnected;
        private string _serial = string.Empty;
        private string _appSourceSignature = string.Empty;
        private int _appListGeneration;
        private CancellationTokenSource? _addCts;

        private bool _isConfirmOpen;
        private string _confirmTitle = string.Empty;
        private string _confirmMessage = string.Empty;
        private string _confirmDetail = string.Empty;
        private TaskCompletionSource<bool>? _confirmTcs;

        private bool _isNoticeOpen;
        private string _noticeTitle = string.Empty;
        private string _noticeMessage = string.Empty;

        private bool _isWelcomeOpen;

        private bool _isChooseDeviceOpen;
        private string _chooseDeviceMessage = string.Empty;
        private TaskCompletionSource<DeviceInfo?>? _chooseTcs;

        private IconPickerViewModel? _iconPicker;
        private TaskCompletionSource<BitmapSource?>? _pickTcs;

        public DesktopViewModel Desktop { get; } = new();
        public AppSearchViewModel Search { get; } = new();
        public ConnectionViewModel Connection { get; }
        public WindowManagerViewModel WindowManager { get; } = new();
        public TaskbarViewModel Taskbar { get; } = new();
        public AudioLinkViewModel Audio { get; } = new();
        public WelcomeViewModel Welcome { get; } = new();
        public NotificationsViewModel Notifications { get; } = new();

        /// <summary>Opens (or closes) one device's notification panel.</summary>
        public RelayCommand<DeviceInfo> DeviceNotificationsCommand { get; }

        /// <summary>Links (or reveals the volume panel for) one device's audio.</summary>
        public RelayCommand<DeviceInfo> DeviceAudioCommand { get; }

        /// <summary>Numbered box: switches to that device's desktop, or back to unified.</summary>
        public RelayCommand<DeviceInfo> DeviceDesktopCommand { get; }

        /// <summary>The Unified tab beside the search bar.</summary>
        public RelayCommand ShowUnifiedCommand { get; }

        public RelayCommand NavBackCommand { get; }
        public RelayCommand NavHomeCommand { get; }
        public RelayCommand NavRecentsCommand { get; }
        public RelayCommand ToggleSearchCommand { get; }
        public RelayCommand CloseSearchCommand { get; }
        public RelayCommand ToggleConnectionCommand { get; }
        public RelayCommand CloseConnectionCommand { get; }
        public RelayCommand ConfirmYesCommand { get; }
        public RelayCommand ConfirmNoCommand { get; }
        public RelayCommand<DeviceInfo> ChooseDeviceCommand { get; }
        public RelayCommand CancelChooseDeviceCommand { get; }
        public RelayCommand DismissNoticeCommand { get; }
        public RelayCommand<AppEntry> AddAppCommand { get; }
        public RelayCommand<DesktopIconViewModel> RemoveIconCommand { get; }
        public RelayCommand<DesktopIconViewModel> RenameIconCommand { get; }
        public RelayCommand<DesktopIconViewModel> ChangeIconCommand { get; }
        public RelayCommand<DesktopIconViewModel> ResetIconCommand { get; }

        public MainViewModel()
        {
            // The registry comes first: the connection panel adds and removes devices
            // through it, so it cannot be constructed without one.
            _registry = new DeviceRegistry(App.Config);
            _registry.DevicesChanged += OnDevicesChanged;
            _registry.TransportChanged += OnTransportChanged;

            // Windows are keyed by the device's identity serial; mirroring needs whatever
            // adb transport currently reaches it.
            WindowManager.TransportResolver = s => _registry.BySerial(s)?.Transport;

            // The registry owns every adb poll, so the panel's refresh goes through it
            // rather than reading the device itself and racing the scheduled read.
            Notifications.Reader = _registry.ReadNotificationsAsync;

            // Turning multi-desktop off has to take effect now, not on the next switch:
            // the desktop you are standing on may be the one that just became unreachable.
            Taskbar.ChromeChanged += OnChromeChanged;

            Connection = new ConnectionViewModel(App.Config, _registry);
            Connection.RemoveRequested += RemoveDevice;

            // After Connection exists: Settings edits the device markers, which the
            // taskbar and the connection panel are the legend for.
            WindowManager.SettingsFactory = () =>
            {
                var settings = new SettingsViewModel(Desktop, Taskbar);
                settings.IconSettingsChanged += OnIconSettingsChanged;
                settings.WelcomeRequested += ShowWelcome;
                return settings;
            };

            Welcome.Finished += CloseWelcome;

            NavBackCommand = new RelayCommand(() => NavPlaceholder("back"));
            NavHomeCommand = new RelayCommand(() => NavPlaceholder("home"));
            NavRecentsCommand = new RelayCommand(() => NavPlaceholder("recents"));

            ToggleSearchCommand = new RelayCommand(() => IsSearchOpen = !IsSearchOpen);
            CloseSearchCommand = new RelayCommand(() => IsSearchOpen = false);
            ToggleConnectionCommand = new RelayCommand(() => IsConnectionOpen = !IsConnectionOpen);
            CloseConnectionCommand = new RelayCommand(() => IsConnectionOpen = false);

            ConfirmYesCommand = new RelayCommand(() => ResolveConfirm(true));
            ConfirmNoCommand = new RelayCommand(() => ResolveConfirm(false));
            ChooseDeviceCommand = new RelayCommand<DeviceInfo>(ResolveChoice);
            CancelChooseDeviceCommand = new RelayCommand(() => ResolveChoice(null));
            DismissNoticeCommand = new RelayCommand(() => IsNoticeOpen = false);

            DeviceNotificationsCommand = new RelayCommand<DeviceInfo>(ShowNotifications);
            DeviceAudioCommand = new RelayCommand<DeviceInfo>(d =>
            {
                // Both panels anchor to the same corner of the taskbar, so only one of
                // them can be up at a time.
                Notifications.IsPanelOpen = false;
                Audio.Toggle(d);
            });
            DeviceDesktopCommand = new RelayCommand<DeviceInfo>(ShowDeviceDesktop);
            ShowUnifiedCommand = new RelayCommand(ShowUnifiedDesktop);

            AddAppCommand = new RelayCommand<AppEntry>(app => _ = AddAppAsync(app));
            RemoveIconCommand = new RelayCommand<DesktopIconViewModel>(RemoveIcon);
            RenameIconCommand = new RelayCommand<DesktopIconViewModel>(RenameIcon);
            ChangeIconCommand = new RelayCommand<DesktopIconViewModel>(icon => _ = ChangeIconAsync(icon));
            ResetIconCommand = new RelayCommand<DesktopIconViewModel>(ResetIcon);

            Desktop.Load();

            // Nothing has been enumerated yet, so this greys everything that is not
            // built in. The first DevicesChanged puts back whatever is actually here --
            // without it the restored icons would look launchable until then.
            Desktop.ApplyDeviceStates(_registry.Added.ToList());

            Desktop.IconActivated += OnIconActivated;
        }

        /// <summary>
        /// The devices on the taskbar: connected AND added. Merely being plugged in earns
        /// a row in the connection panel, nothing more -- otherwise adding would decide
        /// nothing and the button would be decoration.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<DeviceInfo> Devices { get; } = new();

        /// <summary>Rebuilds the taskbar list, preserving registry order.</summary>
        private void RefreshAddedDevices()
        {
            var added = _registry.Added.ToList();

            for (var i = Devices.Count - 1; i >= 0; i--)
                if (!added.Contains(Devices[i]))
                    Devices.RemoveAt(i);

            for (var i = 0; i < added.Count; i++)
            {
                var existing = Devices.IndexOf(added[i]);
                if (existing < 0)
                    Devices.Insert(i, added[i]);
                else if (existing != i)
                    Devices.Move(existing, i);
            }
        }

        public void Start()
        {
            Taskbar.Start();

            // Apply the empty state up front. The registry only raises DevicesChanged when
            // something actually changes, so with nothing plugged in it would never fire
            // and every icon would sit falsely un-greyed.
            OnDevicesChanged();

            _registry.Start();

            // Last, so the guide opens over a shell that has already settled rather than
            // over one still painting itself.
            if (!App.Config.WelcomeSeen)
                ShowWelcome();
        }

        /// <summary>Called by the WM_DEVICECHANGE hook so USB hot-plug registers instantly.</summary>
        public Task OnUsbHotPlugAsync() => _registry.ForceScanAsync();

        // ---------- state ----------

        public bool IsSearchOpen
        {
            get => _isSearchOpen;
            set
            {
                if (!Set(ref _isSearchOpen, value)) return;

                if (value)
                {
                    Search.Query = string.Empty;
                    IsConnectionOpen = false;   // only one panel up at a time
                }
            }
        }

        public bool IsConnectionOpen
        {
            get => _isConnectionOpen;
            set
            {
                if (!Set(ref _isConnectionOpen, value)) return;

                if (value)
                {
                    IsSearchOpen = false;
                    Connection.Activate();
                }
                else
                {
                    Connection.Deactivate();
                }
            }
        }

        // ---------- inline confirm / notice ----------

        public bool IsConfirmOpen
        {
            get => _isConfirmOpen;
            private set => Set(ref _isConfirmOpen, value);
        }

        public string ConfirmTitle
        {
            get => _confirmTitle;
            private set => Set(ref _confirmTitle, value);
        }

        public string ConfirmMessage
        {
            get => _confirmMessage;
            private set => Set(ref _confirmMessage, value);
        }

        public string ConfirmDetail
        {
            get => _confirmDetail;
            private set => Set(ref _confirmDetail, value);
        }

        public bool IsNoticeOpen
        {
            get => _isNoticeOpen;
            private set => Set(ref _isNoticeOpen, value);
        }

        public string NoticeTitle
        {
            get => _noticeTitle;
            private set => Set(ref _noticeTitle, value);
        }

        public string NoticeMessage
        {
            get => _noticeMessage;
            private set => Set(ref _noticeMessage, value);
        }

        /// <summary>
        /// Inline replacement for a MessageBox: shows the confirm card and completes when
        /// the user answers, so callers can still just await a bool.
        /// </summary>
        private Task<bool> ConfirmAsync(string title, string message, string detail)
        {
            ResolveConfirm(false);   // supersede anything already showing

            ConfirmTitle = title;
            ConfirmMessage = message;
            ConfirmDetail = detail;

            _confirmTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            IsConfirmOpen = true;

            return _confirmTcs.Task;
        }

        private void ResolveConfirm(bool answer)
        {
            var tcs = _confirmTcs;
            _confirmTcs = null;

            IsConfirmOpen = false;
            tcs?.TrySetResult(answer);
        }

        // ---------- inline device chooser ----------

        /// <summary>Candidates for a shared package, offered when adding it.</summary>
        public System.Collections.ObjectModel.ObservableCollection<DeviceInfo> ChoiceDevices { get; } = new();

        public bool IsChooseDeviceOpen
        {
            get => _isChooseDeviceOpen;
            private set => Set(ref _isChooseDeviceOpen, value);
        }

        public string ChooseDeviceMessage
        {
            get => _chooseDeviceMessage;
            private set => Set(ref _chooseDeviceMessage, value);
        }

        /// <summary>
        /// Asks which device a shared package should be added to. Same inline-card shape as
        /// the confirm prompt, so the caller still just awaits a single answer.
        /// </summary>
        private Task<DeviceInfo?> ChooseDeviceAsync(string appName, IReadOnlyList<DeviceInfo> candidates)
        {
            ResolveChoice(null);   // supersede anything already showing

            ChoiceDevices.Clear();
            foreach (var device in candidates.OrderBy(d => d.Number))
                ChoiceDevices.Add(device);

            ChooseDeviceMessage = $"“{appName}” is installed on {candidates.Count} devices. " +
                                  "Which one should the desktop icon use?";

            _chooseTcs = new TaskCompletionSource<DeviceInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            IsChooseDeviceOpen = true;

            return _chooseTcs.Task;
        }

        private void ResolveChoice(DeviceInfo? device)
        {
            var tcs = _chooseTcs;
            _chooseTcs = null;

            IsChooseDeviceOpen = false;
            tcs?.TrySetResult(device);
        }

        // ---------- first-run guide ----------

        public bool IsWelcomeOpen
        {
            get => _isWelcomeOpen;
            private set => Set(ref _isWelcomeOpen, value);
        }

        /// <summary>
        /// Opens the guide at page one. Called once on a fresh config, and on demand from
        /// Settings.
        /// </summary>
        public void ShowWelcome()
        {
            Welcome.Reset();

            // It covers the surface, so nothing else should be up behind it.
            IsSearchOpen = false;
            IsConnectionOpen = false;

            IsWelcomeOpen = true;
        }

        /// <summary>
        /// Closes it and records that it has been seen -- however it was closed. Backing
        /// out of the guide is an answer, so it must not reappear on the next launch;
        /// Settings is where it is reopened from.
        /// </summary>
        private void CloseWelcome()
        {
            IsWelcomeOpen = false;

            if (App.Config.WelcomeSeen)
                return;

            App.Config.WelcomeSeen = true;
            App.SaveConfig();
        }

        public void ShowNotice(string title, string message)
        {
            NoticeTitle = title;
            NoticeMessage = message;
            IsNoticeOpen = true;
        }

        // ---------- inline icon picker ----------

        public IconPickerViewModel? IconPicker
        {
            get => _iconPicker;
            private set
            {
                if (Set(ref _iconPicker, value))
                    RaisePropertyChanged(nameof(IsIconPickerOpen));
            }
        }

        public bool IsIconPickerOpen => _iconPicker != null;

        /// <summary>
        /// Shows the picker inline and completes when the user chooses or backs out, so
        /// the add-app pipeline can still await a single image.
        /// </summary>
        private Task<BitmapSource?> PickIconAsync(
            string package, string displayName, IReadOnlyList<IconCandidate> candidates,
            string? emptyMessage = null)
        {
            ResolvePick(null);   // supersede anything already showing

            var vm = new IconPickerViewModel(package, displayName, candidates, emptyMessage);
            vm.Finished += ResolvePick;

            IconPicker = vm;

            _pickTcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pickTcs.Task;
        }

        private void ResolvePick(BitmapSource? image)
        {
            var tcs = _pickTcs;
            _pickTcs = null;

            if (IconPicker != null)
            {
                IconPicker.Finished -= ResolvePick;
                IconPicker = null;
            }

            tcs?.TrySetResult(image);
        }

        /// <summary>Closes the topmost inline overlay. Bound to Escape.</summary>
        public bool DismissTopOverlay()
        {
            if (IsWelcomeOpen) { CloseWelcome(); return true; }
            if (IsNoticeOpen) { IsNoticeOpen = false; return true; }
            if (IsIconPickerOpen) { ResolvePick(null); return true; }
            if (IsConfirmOpen) { ResolveConfirm(false); return true; }
            if (IsChooseDeviceOpen) { ResolveChoice(null); return true; }
            if (Notifications.IsPanelOpen) { Notifications.IsPanelOpen = false; return true; }
            if (IsConnectionOpen) { IsConnectionOpen = false; return true; }
            if (IsSearchOpen) { IsSearchOpen = false; return true; }
            if (WindowManager.IsSnapAssistOpen) { WindowManager.CloseSnapAssist(); return true; }
            return false;
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set => Set(ref _isBusy, value);
        }

        public string BusyMessage
        {
            get => _busyMessage;
            private set => Set(ref _busyMessage, value);
        }

        public string DeviceLabel
        {
            get => _deviceLabel;
            private set => Set(ref _deviceLabel, value);
        }

        public bool IsConnected
        {
            get => _isConnected;
            private set => Set(ref _isConnected, value);
        }

        // ---------- device ----------

        /// <summary>
        /// The registry raises this on the UI thread whenever the set of devices changes.
        /// Everything that is still single-device -- the app list, the audio link, the
        /// window manager -- follows the primary device, which is what the old single
        /// device monitor reported.
        /// </summary>
        /// <summary>Devices whose windows have already been brought back this run.</summary>
        private readonly HashSet<string> _sessionRestored = new(StringComparer.Ordinal);

        /// <summary>
        /// Reopens the apps that were open when AdbDesktop last closed, per device, as
        /// each one becomes available -- a device that is plugged in ten minutes from now
        /// still gets its windows back.
        ///
        /// Once per device per run: reconnecting a phone is not a reason to reopen
        /// everything the user has since closed.
        /// </summary>
        private void RestoreSessionWindows(IReadOnlyList<DeviceInfo> added)
        {
            if (App.Config.Session.Restore != SessionRestore.Apps)
                return;

            foreach (var device in added)
            {
                if (!_sessionRestored.Add(device.Serial))
                    continue;

                foreach (var entry in WindowManager.RestorableFor(device.Serial))
                {
                    // The icon carries the caption and artwork the window needs, and its
                    // absence means the app was removed from the desktop since -- in
                    // which case there is nothing to reopen.
                    var icon = Desktop.Icons.FirstOrDefault(i =>
                        string.Equals(i.Package, entry.Package, StringComparison.Ordinal)
                        && string.Equals(i.DeviceSerial, device.Serial, StringComparison.Ordinal));

                    if (icon == null)
                        continue;

                    WindowManager.Open(icon);
                    Debugger.show($"[WIN] Restored window for {icon.Package}.");
                }
            }
        }

        private void OnDevicesChanged()
        {
            RefreshAddedDevices();

            // Only added devices count as "the device is here": an icon whose device is
            // connected but not added still has nothing to run on.
            var added = _registry.Added.ToList();

            // Before anything reads it: the icons, the taskbar entries and the connection
            // panel rows all decide whether to draw a device marker from this.
            var markersChanged = DeviceColours.MarkersMeaningful != added.Count > 1;
            DeviceColours.DeviceCount = added.Count;

            Desktop.ApplyDeviceStates(added);
            Audio.Sync(added);
            Notifications.Sync(added);
            RestoreSessionWindows(added);

            // Crossing one device makes the markers appear or disappear everywhere. The
            // icons are handled by ApplyDeviceStates; these two are not bound to it.
            if (markersChanged)
            {
                Connection.RefreshDeviceMarkers();

                foreach (var device in _registry.Devices)
                    device.RaiseMarkerChanged();
            }
            RaisePropertyChanged(nameof(HasDevices));
            RaisePropertyChanged(nameof(HasMultipleDevices));
            RaisePropertyChanged(nameof(ShowDeviceNumbers));

            // A device desktop cannot outlive its device being added and connected -- the
            // icons stay, but the desktop itself falls back to unified.
            if (!Desktop.IsUnified && !_registry.IsAdded(Desktop.ActiveDeviceSerial))
                ShowUnifiedDesktop();
            else
                MarkActiveDesktop();

            var primary = _registry.Primary;
            var serial = primary?.Serial ?? string.Empty;

            if (!string.Equals(serial, _serial, StringComparison.Ordinal))
            {
                _serial = serial;

                WindowManager.DeviceSerial = serial;
                IsConnected = primary != null;
                DeviceLabel = primary?.Label ?? "Disconnected";
            }

            if (primary == null)
            {
                _appSourceSignature = string.Empty;
                Search.Clear();
                return;
            }

            // The app list is merged across devices, so it has to be rebuilt whenever the
            // set it is merged from changes -- not just when the primary device does.
            var signature = string.Join("|", AppSourceDevices().Select(d => d.Serial));
            if (signature != _appSourceSignature)
            {
                _appSourceSignature = signature;
                _ = LoadAppsAsync();
            }
        }

        /// <summary>
        /// The device is still here, just reached a different way -- typically the cable
        /// came out and Wireless Debugging took over. Everything holding a live adb
        /// session has to be re-pointed, or it would sit on a transport that is gone.
        /// </summary>
        private void OnTransportChanged(DeviceInfo device, string previous)
        {
            Debugger.show($"[MAIN] {device.Label}: {previous} -> {device.Transport}, restarting sessions.");

            WindowManager.RestartMirroring(device.Serial, device.Transport);
            Audio.RestartFor(device);

            // The app list was fetched over the old transport; nothing about the device
            // changed, so it stays valid. Only live sessions need the restart.
        }

        /// <summary>
        /// Devices the search list is built from: the added ones only. Listing apps from a
        /// device with no desktop would offer to install onto something that cannot show
        /// the result.
        /// </summary>
        private List<DeviceInfo> AppSourceDevices() => _registry.Added.ToList();

        /// <summary>
        /// The bell in a device's taskbar bundle. Read-only: the panel shows what is
        /// posted, because nothing reachable over adb can dismiss a notification.
        /// </summary>
        private void ShowNotifications(DeviceInfo? device)
        {
            if (device == null)
                return;

            Audio.IsPanelOpen = false;
            Notifications.Toggle(device);
        }

        // ---------- desktops ----------

        /// <summary>
        /// Clicking a device's numbered box shows its desktop; clicking the one already
        /// shown goes back to unified, so the box is a toggle. (The Unified tab beside the
        /// search bar arrives with the desktop-switching milestone.)
        /// </summary>
        private void ShowDeviceDesktop(DeviceInfo? device)
        {
            if (device == null)
                return;

            if (string.Equals(Desktop.ActiveDeviceSerial, device.Serial, StringComparison.Ordinal))
                ShowUnifiedDesktop();
            else
                ShowDesktop(device.Serial);
        }

        private void ShowUnifiedDesktop() => ShowDesktop(string.Empty);

        /// <summary>
        /// Repaints everything that reads the icon settings. The device markers show on
        /// the desktop, but the taskbar and the connection panel are what say which colour
        /// belongs to which phone, so all three move together.
        /// </summary>
        private void OnIconSettingsChanged()
        {
            Desktop.RefreshIconAppearance();
            Connection.RefreshDeviceMarkers();

            foreach (var device in _registry.Devices)
                device.RaiseMarkerChanged();
        }

        private void OnChromeChanged()
        {
            if (Taskbar.DisableMultiDesktop && !Desktop.IsUnified)
                ShowUnifiedDesktop();

            RaisePropertyChanged(nameof(ShowDeviceNumbers));
            RaisePropertyChanged(nameof(HasDeviceDesktops));
        }

        /// <summary>
        /// Takes a device off the taskbar and clears its auto-add flag. Its icons are NOT
        /// touched: they are keyed by serial and persist, so adding the device again
        /// brings its desktop back exactly as it was. Nothing is destroyed here, so
        /// nothing needs confirming.
        /// </summary>
        private void RemoveDevice(string serial)
        {
            if (string.Equals(Desktop.ActiveDeviceSerial, serial, StringComparison.Ordinal))
                ShowUnifiedDesktop();

            // Close anything still mirroring it -- the window would otherwise outlive the
            // desktop it was launched from.
            WindowManager.CloseForDevice(serial);

            _registry.Forget(serial);
            Connection.SyncAddedState();
        }

        private void ShowDesktop(string serial)
        {
            Desktop.ShowDesktop(serial);
            MarkActiveDesktop();

            // The search list is scoped to the desktop: on a device's desktop you can only
            // add that device's apps, because that is the only thing it could run.
            Search.DeviceFilter = Desktop.ActiveDeviceSerial;

            Desktop.ApplyDeviceStates(_registry.Added.ToList());
        }

        private void MarkActiveDesktop()
        {
            foreach (var device in _registry.Devices)
                device.IsActiveDesktop =
                    string.Equals(device.Serial, Desktop.ActiveDeviceSerial, StringComparison.Ordinal);

            RaisePropertyChanged(nameof(IsUnifiedActive));
            RaisePropertyChanged(nameof(HasDeviceDesktops));
        }

        /// <summary>Highlights the Unified tab while it is the desktop on screen.</summary>
        public bool IsUnifiedActive => Desktop.IsUnified;

        /// <summary>
        /// Whether there is more than one desktop to be on. With a single added device the
        /// number boxes are hidden too, so there is nowhere to switch to and the tab would
        /// be a button that does nothing.
        /// </summary>
        public bool HasDeviceDesktops => Devices.Count > 1 && !Taskbar.DisableMultiDesktop;

        /// <summary>True when at least one device has been added.</summary>
        public bool HasDevices => Devices.Count > 0;

        /// <summary>
        /// Device numbers are a disambiguator, so they stay hidden until there is
        /// something to disambiguate -- and entirely when multi-desktop is off, since
        /// then they lead nowhere.
        /// </summary>
        public bool ShowDeviceNumbers => Devices.Count > 1 && !Taskbar.DisableMultiDesktop;

        /// <summary>
        /// Boxes each device's taskbar bundle so it is obvious where one phone's controls
        /// end and the next one's begin. With a single device there is nothing to run
        /// into, and the box would just be chrome around the whole cluster.
        /// </summary>
        public bool HasMultipleDevices => Devices.Count > 1;

        /// <summary>
        /// Builds the search list from every device at once. A package present on several
        /// devices gets ONE entry rather than one per device -- the list is a catalogue of
        /// apps, not of installations, and duplicate rows with nothing to tell them apart
        /// would be worse than useless. Which device it lands on is asked at add time.
        /// </summary>
        private async Task LoadAppsAsync()
        {
            var sources = AppSourceDevices();

            if (sources.Count == 0)
            {
                await UiThread.RunAsync(() => Search.Clear());
                return;
            }

            var generation = ++_appListGeneration;
            await UiThread.RunAsync(() => Search.IsLoading = true);

            try
            {
                var byPackage = new Dictionary<string, (string Display, List<string> Serials)>(
                    StringComparer.Ordinal);

                foreach (var device in sources)
                {
                    List<AppEntry> apps;
                    try
                    {
                        apps = await PackageService
                            .GetLaunchableAppsAsync(device.Transport).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // One unreachable device must not empty the whole list.
                        Debugger.show($"[MAIN] Listing apps on {device.Serial} failed: {ex.Message}");
                        continue;
                    }

                    foreach (var app in apps)
                    {
                        if (byPackage.TryGetValue(app.PackageName, out var existing))
                            existing.Serials.Add(device.Serial);
                        else
                            byPackage[app.PackageName] =
                                (app.DisplayName, new List<string> { device.Serial });
                    }
                }

                // Another device connected or dropped while we were enumerating, so this
                // list is already stale and a newer pass is on its way.
                if (generation != _appListGeneration)
                    return;

                var single = sources.Count == 1;

                var merged = byPackage
                    .Select(kv => new AppEntry
                    {
                        PackageName = kv.Key,
                        DisplayName = kv.Value.Display,
                        DeviceSerials = kv.Value.Serials,
                        DeviceSummary = single ? string.Empty : DescribeDevices(kv.Value.Serials)
                    })
                    .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                await UiThread.RunAsync(() => Search.SetApps(merged));
            }
            catch (Exception ex)
            {
                Debugger.show("[MAIN] Loading apps failed: " + ex.Message);
            }
            finally
            {
                if (generation == _appListGeneration)
                    await UiThread.RunAsync(() => Search.IsLoading = false);
            }
        }

        private string DescribeDevices(IReadOnlyList<string> serials)
        {
            if (serials.Count > 1)
                return $"On {serials.Count} devices";

            return serials.Count == 1
                ? $"On {_registry.BySerial(serials[0])?.Label ?? serials[0]}"
                : string.Empty;
        }

        // ---------- adding an app ----------

        private async Task AddAppAsync(AppEntry? app)
        {
            if (app == null || IsBusy)
                return;

            if (string.IsNullOrEmpty(_serial))
            {
                ShowNotice("No device",
                    "Add a device from the connection panel before adding apps.");
                return;
            }

            IsSearchOpen = false;

            // The list is merged across devices, so a shared package has to be pinned to
            // one before anything can be pulled -- the icon belongs to a device, not to
            // the package.
            var candidates = app.DeviceSerials
                .Select(s => _registry.BySerial(s))
                .Where(d => d != null)
                .Select(d => d!)
                .ToList();

            DeviceInfo? chosenDevice;

            // On a device's desktop there is nothing to choose: the icon belongs there, so
            // it belongs to that device. The prompt is only for the unified desktop, where
            // a shared package genuinely could go to either.
            if (!Desktop.IsUnified)
            {
                chosenDevice = candidates.FirstOrDefault(d =>
                    string.Equals(d.Serial, Desktop.ActiveDeviceSerial, StringComparison.Ordinal));

                if (chosenDevice == null)
                {
                    ShowNotice("Not installed here",
                        $"“{app.DisplayName}” is not installed on this desktop's device.");
                    return;
                }

                await AddFromAsync(app, chosenDevice);
                return;
            }

            if (candidates.Count == 0)
            {
                // Every device carrying it dropped off between listing and clicking.
                ShowNotice("Not connected",
                    $"No connected device has “{app.DisplayName}” any more.");
                return;
            }

            if (candidates.Count == 1)
            {
                chosenDevice = candidates[0];
            }
            else
            {
                chosenDevice = await ChooseDeviceAsync(app.DisplayName, candidates);
                if (chosenDevice == null)
                    return;
            }

            await AddFromAsync(app, chosenDevice);
        }

        /// <summary>Confirms, then pulls the APK from the device that was settled on.</summary>
        private async Task AddFromAsync(AppEntry app, DeviceInfo device)
        {
            var confirmed = await ConfirmAsync(
                "Add app",
                $"Add “{app.DisplayName}” to the desktop?",
                $"{app.PackageName}\nIts APK will be pulled from {device.Label} " +
                "so an icon can be extracted.");

            if (!confirmed)
                return;

            await PullAndPickAsync(app.PackageName, app.DisplayName, device, target: null);
        }

        private async Task ChangeIconAsync(DesktopIconViewModel? icon)
        {
            if (icon == null || IsBusy)
                return;

            // A built-in has no APK behind it, so there is nothing to pull or scan --
            // the picker opens straight onto the "use my own image" tile.
            if (icon.IsBuiltIn)
            {
                await ChangeBuiltInIconAsync(icon);
                return;
            }

            // Read the icons off the device that actually runs the app, not whichever
            // device happens to be primary.
            var serial = string.IsNullOrEmpty(icon.DeviceSerial) ? _serial : icon.DeviceSerial;
            var device = _registry.BySerial(serial);

            if (device is not { IsAdded: true })
            {
                ShowNotice("Not connected",
                    string.IsNullOrEmpty(icon.DeviceLabel)
                        ? "Connect a device to re-read this app's icons."
                        : $"Connect {icon.DeviceLabel} to re-read this app's icons.");
                return;
            }

            await PullAndPickAsync(icon.Package, icon.Caption, device, icon);
        }

        private async Task ChangeBuiltInIconAsync(DesktopIconViewModel icon)
        {
            var chosen = await PickIconAsync(
                icon.Package,
                icon.Caption,
                Array.Empty<IconCandidate>(),
                "This app is part of adbDesktop, so there is no APK to read icons from. " +
                "Upload your own image below, or cancel to keep the drawn icon.");

            if (chosen == null)
                return;

            // Built-ins belong to adbDesktop rather than to a phone, so they hash against
            // an empty serial -- one file per desktop's copy is not wanted here.
            var iconFile = IconStore.Save(icon.Package, icon.DeviceSerial, chosen);
            if (iconFile == null)
            {
                ShowNotice("Could not save icon", "Writing the icon file failed. See the log.");
                return;
            }

            icon.IconFile = iconFile;
            icon.Image = chosen;
            Desktop.Persist();
        }

        private void ResetIcon(DesktopIconViewModel? icon)
        {
            if (icon != null)
                Desktop.ResetIcon(icon);
        }

        private async Task PullAndPickAsync(string package, string displayName, DeviceInfo device,
            DesktopIconViewModel? target)
        {
            // Two different serials, deliberately: adb is addressed by transport, while
            // the icon is stored against the device's stable identity.
            var transport = device.Transport;
            var serial = device.Serial;

            _addCts?.Cancel();
            _addCts = new CancellationTokenSource();
            var ct = _addCts.Token;

            IsBusy = true;
            var progress = new Progress<string>(m => BusyMessage = m);
            var workDir = string.Empty;

            try
            {
                BusyMessage = "Locating APK...";

                var pull = await Task.Run(
                    () => ApkPuller.PullAsync(transport, package, progress, ct), ct).ConfigureAwait(false);

                workDir = pull.WorkingDirectory;

                if (!pull.Success)
                {
                    await UiThread.RunAsync(() =>
                        ShowNotice("Could not pull APK", pull.Error ?? "Unknown error."));
                    return;
                }

                var candidates = await IconExtractor
                    .ScanAsync(pull.LocalApks, progress, ct).ConfigureAwait(false);

                // The picker takes over the surface, so the busy overlay comes down first.
                await UiThread.RunAsync(() => IsBusy = false);

                var chosen = await await UiThread.RunAsync(
                    () => PickIconAsync(package, displayName, candidates)).ConfigureAwait(false);

                if (chosen == null)
                    return;

                // The identity serial, not the transport: the icon has to stay findable
                // after the same phone comes back on a different address.
                var iconFile = IconStore.Save(package, serial, chosen);
                if (iconFile == null)
                {
                    await UiThread.RunAsync(() =>
                        ShowNotice("Could not save icon", "Writing the icon file failed. See the log."));
                    return;
                }

                await UiThread.RunAsync(() =>
                {
                    if (target != null)
                    {
                        target.IconFile = iconFile;
                        target.Image = chosen;
                        Desktop.Persist();
                    }
                    else
                    {
                        // No implicit adding here: the app list only ever contains apps
                        // from devices that are already added.
                        // Lands on the desktop currently on screen, whichever that is.
                        Desktop.Add(package, displayName, iconFile, chosen, serial);

                        // Gives the fresh icon its device label and number straight away,
                        // rather than leaving it unmarked until the next device change.
                        Desktop.ApplyDeviceStates(_registry.Added.ToList());
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by another add; nothing to report.
            }
            catch (Exception ex)
            {
                Debugger.show("[MAIN] Add app failed: " + ex);
                await UiThread.RunAsync(() => ShowNotice("Something went wrong", ex.Message));
            }
            finally
            {
                // Always reclaim the scratch space, even on failure -- ASM's equivalent
                // never cleaned up and grew unbounded.
                if (!string.IsNullOrEmpty(workDir))
                    ApkPuller.Cleanup(workDir);

                await UiThread.RunAsync(() =>
                {
                    IsBusy = false;
                    BusyMessage = string.Empty;
                });
            }
        }

        // ---------- icon context menu ----------

        private void RemoveIcon(DesktopIconViewModel? icon)
        {
            if (icon != null)
                Desktop.Remove(icon);
        }

        /// <summary>
        /// Flips the icon into in-place edit mode. The surface handles focus, selection
        /// and commit -- there is no dialog.
        /// </summary>
        private void RenameIcon(DesktopIconViewModel? icon)
        {
            if (icon == null) return;

            foreach (var other in Desktop.Icons)
                if (other != icon && other.IsRenaming)
                    other.CommitRename();

            icon.IsRenaming = true;
        }

        /// <summary>
        /// Opens (or refocuses) a window for a desktop icon. The window has no display
        /// yet -- scrcpy_video.dll gets wired in next.
        /// </summary>
        private void OnIconActivated(DesktopIconViewModel icon)
        {
            // An overlay is up: treat the click as dismissing it rather than launching.
            if (IsBusy || IsConfirmOpen || IsNoticeOpen || IsIconPickerOpen || IsChooseDeviceOpen)
                return;

            // The icon outlives its device, so it can be clicked while there is nothing to
            // run it on. Opening a window would just fail against a stale serial.
            if (!icon.IsBuiltIn && !icon.IsDeviceConnected)
            {
                ShowNotice("Not connected",
                    string.IsNullOrEmpty(icon.DeviceLabel)
                        ? $"“{icon.Caption}” lives on a device that is not connected."
                        : $"“{icon.Caption}” lives on {icon.DeviceLabel}, which is not connected.");
                return;
            }

            WindowManager.Open(icon);
            Debugger.show($"[WIN] Opened window for {icon.Package}.");
        }

        private static void NavPlaceholder(string which)
        {
            // Wired up but intentionally inert: launching apps (and therefore anything
            // these buttons would act on) is the next milestone.
            Debugger.show($"[NAV] '{which}' pressed -- not implemented yet.");
        }

        public void Dispose()
        {
            _addCts?.Cancel();
            _addCts?.Dispose();
            ResolvePick(null);
            ResolveConfirm(false);
            ResolveChoice(null);

            // Before CloseAll: once the windows are gone so are their bounds.
            WindowManager.SaveSession();

            WindowManager.CloseAll();
            Taskbar.Stop();
            Audio.Dispose();
            Notifications.Dispose();
            Desktop.IconActivated -= OnIconActivated;
            Connection.Deactivate();
            _registry.DevicesChanged -= OnDevicesChanged;
            _registry.Dispose();
        }
    }
}
