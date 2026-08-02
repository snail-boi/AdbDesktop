using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace AdbDesktop
{
    /// <summary>
    /// Tracks every connected device, not just one.
    ///
    /// Replaces the single-device ConnectionMonitor. Structurally the same 1s timer with
    /// a non-overlapping tick guard, but it maintains a set rather than resolving one
    /// serial, and owns the numbering rule.
    /// </summary>
    public sealed class DeviceRegistry : IDisposable
    {
        private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan BatteryInterval = TimeSpan.FromSeconds(60);

        private readonly System.Timers.Timer _timer;
        private readonly AdbDesktopConfig _config;
        private readonly SemaphoreSlim _tickLock = new(1, 1);
        private readonly Dictionary<string, DateTime> _lastBatteryPoll = new(StringComparer.Ordinal);

        /// <summary>adb transport -> hardware serial. A transport's identity never changes.</summary>
        private readonly Dictionary<string, string> _hardwareSerials = new(StringComparer.Ordinal);

        private DateTime _lastReconnectAttemptUtc = DateTime.MinValue;
        private bool _disposed;

        /// <summary>Ordered by <see cref="DeviceInfo.Number"/>, so the UI can bind directly.</summary>
        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        /// <summary>Raised (on the UI thread) whenever a device appears or disappears.</summary>
        public event Action? DevicesChanged;

        /// <summary>
        /// Raised when a device is still here but reachable a different way -- the cable
        /// was pulled and Wi-Fi took over, or vice versa. Anything holding a live adb
        /// session (mirroring, audio) has to be restarted against the new transport,
        /// because the old one is either gone or no longer the best.
        /// </summary>
        public event Action<DeviceInfo, string>? TransportChanged;

        /// <summary>
        /// Whether this specific device re-adds itself when it comes online. Per device:
        /// one phone being a permanent fixture says nothing about the next one plugged in.
        /// </summary>
        public bool GetAutoAdd(string serial) => Record(serial)?.AutoAdd ?? false;

        public void SetAutoAdd(string serial, bool value)
        {
            var record = Record(serial);

            if (record == null)
            {
                if (!value)
                    return;

                record = new KnownDevice
                {
                    Serial = serial,
                    Model = BySerial(serial)?.Model ?? string.Empty,
                };
                _config.Desktop.Devices.Add(record);
            }

            record.AutoAdd = value;
            AdbDesktopConfigManager.Save(_config);

            // Turning it on for a device that is here right now should take effect now,
            // not on the next reconnect.
            if (value && BySerial(serial) is { IsAdded: false } live)
                Add(live);
        }

        private KnownDevice? Record(string serial) => _config.Desktop.Devices
            .FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.Ordinal));

        public DeviceRegistry(AdbDesktopConfig config)
        {
            _config = config;
            _timer = new System.Timers.Timer(1000) { AutoReset = true };
            _timer.Elapsed += OnTick;
        }

        public void Start()
        {
            _timer.Start();
            Debugger.show("[DEV] Registry started.");
            _ = ScanAsync();
        }

        public void Stop() => _timer.Stop();

        public Task ForceScanAsync()
        {
            _lastReconnectAttemptUtc = DateTime.MinValue;
            return ScanAsync();
        }

        public DeviceInfo? BySerial(string? serial) =>
            string.IsNullOrEmpty(serial)
                ? null
                : Devices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.Ordinal));

        /// <summary>
        /// Lowest-numbered ADDED device, or null. Everything still single-device (the
        /// window manager's fallback serial, the status text) follows this, so a device
        /// the user has not added never becomes the one AdbDesktop talks to.
        /// </summary>
        public DeviceInfo? Primary => Devices.Where(d => d.IsAdded).OrderBy(d => d.Number).FirstOrDefault();

        /// <summary>
        /// Puts a device on the taskbar and gives it a desktop, for as long as it stays
        /// connected and AdbDesktop stays open. Deliberately NOT persisted -- the persistent
        /// version is <see cref="SetAutoAdd"/>, and if this survived a restart the two
        /// would be the same button.
        ///
        /// The device's icons persist regardless; they are keyed by serial and outlive
        /// both the connection and the process.
        /// </summary>
        public void Add(DeviceInfo device)
        {
            if (device.IsAdded)
                return;

            RegisterKnown(device);
            Renumber();
            DevicesChanged?.Invoke();
        }

        /// <summary>
        /// The persistence half of <see cref="Add"/>, without the notification. Reconcile
        /// uses this for auto-add: numbers and labels are not assigned until the whole
        /// batch has been reconciled, so raising DevicesChanged from inside it would show
        /// subscribers a half-built device.
        /// </summary>
        /// <summary>
        /// Marks the device added (runtime only) and keeps a record of its model, so it
        /// can still be named in the UI while it is disconnected.
        /// </summary>
        private void RegisterKnown(DeviceInfo device)
        {
            var known = Record(device.Serial);

            if (known == null)
            {
                known = new KnownDevice { Serial = device.Serial };
                _config.Desktop.Devices.Add(known);
            }

            known.Model = device.Model;
            known.AddedUtc = DateTime.UtcNow;

            device.IsAdded = true;
            AdbDesktopConfigManager.Save(_config);

            Debugger.show($"[DEV] Added {device.Label} ({device.Serial}) to AdbDesktop.");
        }

        /// <summary>
        /// Drops a device from AdbDesktop. Its icons are the caller's problem -- the registry
        /// only owns the device list.
        /// </summary>
        public void Forget(string serial)
        {
            var live = BySerial(serial);
            var record = Record(serial);

            if (live is not { IsAdded: true } && record is not { AutoAdd: true })
                return;

            if (live != null)
                live.IsAdded = false;

            if (record != null)
            {
                // Clearing auto-add matters: leaving it set would re-add the device on the
                // very next tick, quietly undoing what the user just did.
                record.AutoAdd = false;
                AdbDesktopConfigManager.Save(_config);
            }

            Debugger.show($"[DEV] Removed {serial} from AdbDesktop.");
            Renumber();
            DevicesChanged?.Invoke();
        }

        /// <summary>Whether the device is on the taskbar right now.</summary>
        public bool IsAdded(string serial) => BySerial(serial)?.IsAdded == true;

        /// <summary>Connected AND added, in display order. What the taskbar shows.</summary>
        public IEnumerable<DeviceInfo> Added => Devices.Where(d => d.IsAdded);

        private async void OnTick(object? sender, ElapsedEventArgs e)
        {
            try
            {
                await ScanAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show("[DEV] Tick error: " + ex.Message);
            }
        }

        private async Task ScanAsync()
        {
            // Skip rather than queue: an adb call can outrun the 1s tick.
            if (!await _tickLock.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                var output = await AdbHelper.RunAdbCaptureAsync("devices -l").ConfigureAwait(false);
                var lines = DeviceQuery.ParseDeviceLines(output);

                var live = new List<LiveTransport>();
                foreach (var line in lines)
                {
                    var transport = DeviceQuery.GetOnlineSerial(line);
                    if (string.IsNullOrEmpty(transport))
                        continue;

                    live.Add(new LiveTransport(
                        transport,
                        await ResolveHardwareSerialAsync(transport).ConfigureAwait(false),
                        ExtractModel(line)));
                }

                // Drop cache entries for transports that are gone. An "ip:port" can be
                // handed to a different phone later, and a stale entry there would merge
                // two devices into one.
                foreach (var stale in _hardwareSerials.Keys
                             .Where(k => live.All(l => !string.Equals(l.Transport, k, StringComparison.Ordinal)))
                             .ToList())
                {
                    _hardwareSerials.Remove(stale);
                }

                // Several transports can reach one phone. Group them so it is one device
                // with a spare route, not two or three devices that happen to look alike.
                var grouped = live
                    .GroupBy(t => t.HardwareSerial, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                await UiThread.RunAsync(() => Reconcile(grouped)).ConfigureAwait(false);

                await TryReconnectWirelessAsync(live.Count).ConfigureAwait(false);
                await PollBatteriesAsync().ConfigureAwait(false);
            }
            finally
            {
                _tickLock.Release();
            }
        }

        /// <summary>Adds newcomers, drops departures, then renumbers. UI thread.</summary>
        private void Reconcile(List<IGrouping<string, LiveTransport>> live)
        {
            var changed = false;

            foreach (var gone in Devices
                         .Where(d => live.All(g => !string.Equals(g.Key, d.Serial, StringComparison.OrdinalIgnoreCase)))
                         .ToList())
            {
                Devices.Remove(gone);
                _lastBatteryPoll.Remove(gone.Serial);
                Debugger.show($"[DEV] Disconnected: {gone.Label} ({gone.Serial})");
                changed = true;
            }

            foreach (var group in live)
            {
                // Best available route: USB, else the mDNS name, else ip:port.
                var ordered = group
                    .OrderBy(t => DeviceQuery.TransportRank(t.Transport))
                    .ToList();

                var best = ordered[0];
                var transports = ordered.Select(t => t.Transport).ToList();
                var model = ordered.Select(t => t.Model).FirstOrDefault(m => !string.IsNullOrEmpty(m))
                            ?? string.Empty;

                var existing = BySerial(group.Key);

                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(model) && existing.Model != model)
                    {
                        existing.Model = model;
                        changed = true;
                    }

                    if (!existing.Transports.SequenceEqual(transports, StringComparer.Ordinal))
                    {
                        existing.Transports = transports;
                        existing.RaiseConnectionChanged();
                    }

                    // The route changed underneath us: either the cable went in, or it
                    // came out and Wi-Fi took over. Anything mid-session has to follow.
                    if (!string.Equals(existing.Transport, best.Transport, StringComparison.Ordinal))
                    {
                        var previous = existing.Transport;

                        existing.Transport = best.Transport;
                        existing.IsUsb = !DeviceQuery.IsWirelessSerial(best.Transport);
                        RememberTransport(existing);

                        Debugger.show(
                            $"[DEV] {existing.Label} switched transport: {previous} -> {best.Transport}");

                        TransportChanged?.Invoke(existing, previous);
                        changed = true;
                    }

                    continue;
                }

                var device = new DeviceInfo
                {
                    Serial = group.Key,
                    Transport = best.Transport,
                    Transports = transports,
                    IsUsb = !DeviceQuery.IsWirelessSerial(best.Transport),
                    Model = string.IsNullOrEmpty(model) ? group.Key : model,
                };

                Devices.Add(device);
                RememberTransport(device);

                Debugger.show($"[DEV] Connected: {model} ({group.Key}) via {best.Transport}"
                              + (transports.Count > 1 ? $" (+{transports.Count - 1} spare)" : string.Empty));
                changed = true;

                // A device arrives NOT added. Only its own auto-add flag puts it on the
                // taskbar without the user asking; everything else waits for Add.
                if (GetAutoAdd(device.Serial))
                    RegisterKnown(device);
            }

            if (!changed)
                return;

            Renumber();
            DevicesChanged?.Invoke();
        }

        /// <summary>
        /// Hardware serial for a transport, cached: it is one shell round-trip and never
        /// changes for a given transport. The mDNS name already contains it, so that case
        /// costs nothing; ip:port has to be asked.
        /// </summary>
        private async Task<string> ResolveHardwareSerialAsync(string transport)
        {
            if (_hardwareSerials.TryGetValue(transport, out var cached))
                return cached;

            // A USB serial IS the hardware serial, so there is nothing to ask.
            var resolved = !DeviceQuery.IsWirelessSerial(transport)
                ? transport
                : DeviceQuery.HardwareSerialFromMdns(transport);

            if (string.IsNullOrEmpty(resolved))
                resolved = await DeviceQuery.GetHardwareSerialAsync(transport).ConfigureAwait(false);

            // Unreachable: fall back to the transport as its own identity. That gives up
            // bundling for this one rather than merging two phones by accident.
            if (string.IsNullOrEmpty(resolved))
                return transport;

            _hardwareSerials[transport] = resolved;
            return resolved;
        }

        private sealed record LiveTransport(string Transport, string HardwareSerial, string Model);

        /// <summary>
        /// USB devices always take lower numbers than wireless ones; within each group
        /// it is connection order. So plugging in a cable bumps every wireless device up
        /// by one, which is the documented behaviour.
        ///
        /// The number is a display ordinal only -- everything device-scoped is keyed by
        /// serial, precisely because these shift.
        /// </summary>
        private void Renumber()
        {
            var ordered = Devices
                .OrderByDescending(d => d.IsUsb)
                .ThenBy(d => d.FirstSeenUtc)
                .ToList();

            // Only added devices are numbered: the number is the label on the taskbar box,
            // and a device that is merely connected has no box. Numbering everything would
            // leave the visible ones as 2 and 4.
            var n = 0;
            foreach (var device in ordered)
                device.Number = device.IsAdded ? ++n : 0;

            // Keep the collection itself in display order so the taskbar needs no sorting.
            for (var i = 0; i < ordered.Count; i++)
            {
                var current = Devices.IndexOf(ordered[i]);
                if (current != i)
                    Devices.Move(current, i);
            }

            ApplyLabels();
        }

        /// <summary>
        /// Model name normally, with a suffix only where two connected devices report
        /// the same one -- so the common case stays clean.
        /// </summary>
        private void ApplyLabels()
        {
            foreach (var group in Devices.GroupBy(d => d.Model, StringComparer.OrdinalIgnoreCase))
            {
                var members = group.OrderBy(d => d.Number).ToList();
                if (members.Count == 1)
                {
                    members[0].Label = members[0].Model;
                    continue;
                }

                for (var i = 0; i < members.Count; i++)
                    members[i].Label = $"{members[i].Model} ({i + 1})";
            }
        }

        /// <summary>
        /// Caches how we last reached this device, which is what the wireless reconnect
        /// path reads on the next launch. Not identity -- a wireless port is reassigned
        /// every time Wireless Debugging is toggled, so MdnsServiceName remains the
        /// durable handle.
        /// </summary>
        private void RememberTransport(DeviceInfo device)
        {
            if (device.IsUsb)
                _config.Device.SelectedDeviceUSB = device.Transport;
            else
                _config.Device.SelectedDeviceWiFi = device.Transport;

            if (!string.IsNullOrWhiteSpace(device.Model))
                _config.Device.SelectedDeviceName = device.Model;

            AdbDesktopConfigManager.Save(_config);
        }

        private static string ExtractModel(string line)
        {
            foreach (var token in line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                    return token.Substring("model:".Length).Replace('_', '-');
            }

            return string.Empty;
        }

        /// <summary>
        /// Best-effort wireless reconnect, throttled. Only attempted when nothing is
        /// connected wirelessly yet -- an mDNS sweep is expensive.
        /// </summary>
        private async Task TryReconnectWirelessAsync(int liveCount)
        {
            if (_config.Device.WifiMode == WirelessMode.UsbOnly)
                return;

            // A device that already has a wireless route needs no sweep, even if it is
            // currently being reached over USB -- the route is there for the taking.
            if (Devices.Any(d => d.Transports.Any(DeviceQuery.IsWirelessSerial)))
                return;

            if (DateTime.UtcNow - _lastReconnectAttemptUtc < ReconnectInterval)
                return;

            _lastReconnectAttemptUtc = DateTime.UtcNow;

            if (_config.Device.WifiMode == WirelessMode.WirelessDebugging
                && !string.IsNullOrWhiteSpace(_config.Device.MdnsServiceName))
            {
                await WirelessDebuggingHelper
                    .ReconnectViaMdnsAsync(_config.Device.MdnsServiceName).ConfigureAwait(false);
            }
            else if (_config.Device.WifiMode == WirelessMode.TcpIp
                     && !string.IsNullOrWhiteSpace(_config.Device.SelectedDeviceWiFi))
            {
                await WirelessDebuggingHelper
                    .TryConnectLastKnownAsync(_config.Device.SelectedDeviceWiFi).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// One `dumpsys battery` per device, reduced device-side with awk so only five
        /// short lines cross the wire. Staggered by a per-device timestamp so several
        /// phones do not all get polled on the same tick.
        /// </summary>
        private async Task PollBatteriesAsync()
        {
            foreach (var device in Devices.ToList())
            {
                if (_lastBatteryPoll.TryGetValue(device.Serial, out var last)
                    && DateTime.UtcNow - last < BatteryInterval)
                    continue;

                _lastBatteryPoll[device.Serial] = DateTime.UtcNow;

                try
                {
                    var output = await AdbHelper.RunAdbCaptureAsync(
                        $"-s {device.Transport} shell sh -c \"dumpsys battery | awk -F: '/^  level:/{{l=\\$2}} " +
                        "/^  status:/{{s=\\$2}} /^  AC powered:/{{a=\\$2}} /^  USB powered:/{{u=\\$2}} " +
                        "/^  Wireless powered:/{{w=\\$2}} END{{print l; print s; print a; print u; print w}}'\"")
                        .ConfigureAwait(false);

                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length < 5)
                        continue;

                    if (!int.TryParse(lines[0].Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var level) || level < 0)
                        continue;

                    int.TryParse(lines[1].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var status);

                    var ac = string.Equals(lines[2].Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    var usb = string.Equals(lines[3].Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    var wireless = string.Equals(lines[4].Trim(), "true", StringComparison.OrdinalIgnoreCase);

                    // 2 = charging, 3 = discharging, 4 = not charging, 5 = full.
                    var charging = (status == 2 || ac || usb || wireless) && status != 5;

                    await UiThread.RunAsync(() =>
                    {
                        device.BatteryLevel = Math.Min(level, 100);
                        device.BatteryCharging = charging;
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debugger.show($"[DEV] Battery poll failed for {device.Serial}: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer.Stop();
            _timer.Elapsed -= OnTick;
            _timer.Dispose();
            _tickLock.Dispose();
        }
    }
}
