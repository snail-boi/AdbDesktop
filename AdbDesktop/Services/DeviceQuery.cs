using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AdbDesktop
{
    /// <summary>
    /// Device-level queries that sit on top of AdbHelper: parsing `adb devices`, reading
    /// device properties, and resolving which connected device to target. AdbHelper stays
    /// a thin transport; the parsing lives here.
    ///
    /// From AMPL, with the serial-classification helpers promoted from locals to public
    /// statics so ConnectionMonitor can share them.
    /// </summary>
    internal static class DeviceQuery
    {
        /// <summary>Split `adb devices` output into entry lines, dropping the header.</summary>
        public static string[] ParseDeviceLines(string output)
        {
            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !l.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        /// <summary>
        /// The serial from an `adb devices` entry, but only if its state is "device".
        /// Entries in state "offline", "unauthorized" or "no permissions" yield empty.
        ///
        /// The state is always the SECOND field, never the last one. With plain
        /// `adb devices` those are the same token:
        ///     R5CY51948WK    device
        /// but `adb devices -l` appends descriptors, and the last token is then the
        /// transport id:
        ///     192.168.0.132:39641   device product:x model:SM_S937B transport_id:5
        /// Testing the last token there reports every device as offline.
        /// </summary>
        public static string GetOnlineSerial(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry))
                return string.Empty;

            var parts = entry.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return string.Empty;

            return string.Equals(parts[1], "device", StringComparison.OrdinalIgnoreCase)
                ? parts[0]
                : string.Empty;
        }

        /// <summary>
        /// Wireless serials take three shapes: "ip:port" (TCP/IP), "adb-XXXX-YYYY"
        /// (Wireless Debugging via mDNS), and the fully-qualified "adb-..._adb-tls-...".
        /// </summary>
        public static bool IsWirelessSerial(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial))
                return false;

            return serial.Contains(':')
                || serial.StartsWith("adb-", StringComparison.OrdinalIgnoreCase)
                || serial.IndexOf("_adb-tls", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// How good a transport is, lowest first. USB is fastest and cannot change
        /// address; the mDNS name outlives a port change; "ip:port" is the most brittle,
        /// since the phone reassigns the port whenever wireless debugging is toggled.
        /// </summary>
        public static int TransportRank(string serial)
        {
            if (!IsWirelessSerial(serial))
                return 0;

            return serial.StartsWith("adb-", StringComparison.OrdinalIgnoreCase)
                   || serial.IndexOf("_adb-tls", StringComparison.OrdinalIgnoreCase) >= 0
                ? 1
                : 2;
        }

        /// <summary>
        /// The hardware serial embedded in a Wireless Debugging mDNS name, e.g.
        /// "adb-R5CY51948WK-RSXupg._adb-tls-connect._tcp" -> "R5CY51948WK". Used as a
        /// fallback when the device cannot be queried, and to avoid a shell round-trip
        /// for the common case.
        /// </summary>
        public static string HardwareSerialFromMdns(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)
                || !serial.StartsWith("adb-", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var rest = serial.Substring(4);
            var dash = rest.IndexOf('-');
            return dash > 0 ? rest.Substring(0, dash) : string.Empty;
        }

        /// <summary>
        /// The device's real hardware serial, which is the only identifier shared by all
        /// of its transports. One shell round-trip; callers cache it per transport.
        /// </summary>
        public static async Task<string> GetHardwareSerialAsync(string transport)
        {
            if (string.IsNullOrWhiteSpace(transport))
                return string.Empty;

            try
            {
                var output = await AdbHelper
                    .RunAdbCaptureAsync($"-s {transport} shell getprop ro.serialno")
                    .ConfigureAwait(false);

                var value = output?.Trim();
                if (!string.IsNullOrWhiteSpace(value) && !value.Contains(' '))
                    return value;
            }
            catch (Exception ex)
            {
                Debugger.show($"[DEV] ro.serialno failed for {transport}: {ex.Message}");
            }

            return string.Empty;
        }

        public static string FindConnectedUsbSerial(string[] deviceLines)
        {
            foreach (var entry in deviceLines)
            {
                var serial = GetOnlineSerial(entry);
                if (!string.IsNullOrEmpty(serial) && !IsWirelessSerial(serial))
                    return serial;
            }

            return string.Empty;
        }

        public static string FindConnectedWirelessSerial(string[] deviceLines)
        {
            foreach (var entry in deviceLines)
            {
                var serial = GetOnlineSerial(entry);
                if (!string.IsNullOrEmpty(serial) && IsWirelessSerial(serial))
                    return serial;
            }

            return string.Empty;
        }

        public static bool IsConnected(string[] deviceLines, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            // Via GetOnlineSerial rather than EndsWith("device"): that only holds for
            // plain `adb devices`, and breaks the moment the caller passes -l output.
            return deviceLines.Any(l =>
                string.Equals(GetOnlineSerial(l), id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Poll for a USB-attached device. Retries because a freshly plugged phone takes
        /// a moment to move from "offline"/"unauthorized" to "device".
        /// </summary>
        public static async Task<string> GetConnectedUsbDeviceAsync()
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
                var serial = FindConnectedUsbSerial(ParseDeviceLines(devices));
                if (!string.IsNullOrEmpty(serial))
                    return serial;

                if (attempt < 7)
                    await Task.Delay(500).ConfigureAwait(false);
            }

            return string.Empty;
        }

        public static async Task<int> GetWifiPortAsync(string usbSerial)
        {
            var output = await AdbHelper.RunAdbCaptureAsync($"-s {usbSerial} shell getprop service.adb.tcp.port").ConfigureAwait(false);
            if (int.TryParse(output.Trim(), out var port) && port > 0)
                return port;

            return 5555;
        }

        public static async Task<string> GetDeviceWifiIpAsync(string usbDevice)
        {
            var ipOutput = await AdbHelper.RunAdbCaptureAsync($"-s {usbDevice} shell ip -f inet addr show wlan0").ConfigureAwait(false);
            var match = Regex.Match(ipOutput, @"inet\s+(?<ip>\d+\.\d+\.\d+\.\d+)");
            if (match.Success)
                return match.Groups["ip"].Value;

            var routeOutput = await AdbHelper.RunAdbCaptureAsync($"-s {usbDevice} shell ip route").ConfigureAwait(false);
            match = Regex.Match(routeOutput, @"src\s+(?<ip>\d+\.\d+\.\d+\.\d+)");
            return match.Success ? match.Groups["ip"].Value : string.Empty;
        }

        public static async Task<string> GetDeviceSerialAsync(string device)
        {
            if (string.IsNullOrWhiteSpace(device))
                return string.Empty;

            var serial = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell getprop ro.serialno").ConfigureAwait(false);
            serial = serial.Trim();
            if (!string.IsNullOrWhiteSpace(serial))
                return serial;

            serial = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell getprop ro.boot.serialno").ConfigureAwait(false);
            return serial.Trim();
        }

        /// <summary>Human-readable model name, for the device pill.</summary>
        public static async Task<string> GetDeviceModelAsync(string device)
        {
            if (string.IsNullOrWhiteSpace(device))
                return string.Empty;

            var model = (await AdbHelper.RunAdbCaptureAsync($"-s {device} shell getprop ro.product.model").ConfigureAwait(false)).Trim();
            if (!string.IsNullOrWhiteSpace(model))
                return model;

            return (await AdbHelper.RunAdbCaptureAsync($"-s {device} shell getprop ro.product.name").ConfigureAwait(false)).Trim();
        }

        /// <summary>
        /// Classic tcpip flow: switch the USB-attached device to TCP mode and connect to
        /// it over the LAN. Returns the ip:port on success, empty on failure.
        /// </summary>
        public static async Task<string> SetupWirelessFromUsbAsync(string usbDevice)
        {
            if (string.IsNullOrWhiteSpace(usbDevice))
                return string.Empty;

            var port = await GetWifiPortAsync(usbDevice).ConfigureAwait(false);
            var ip = await GetDeviceWifiIpAsync(usbDevice).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(ip))
            {
                Debugger.show("[TCPIP] Could not read the device's wlan0 address.");
                return string.Empty;
            }

            await AdbHelper.RunAdbCaptureAsync($"-s {usbDevice} tcpip {port}").ConfigureAwait(false);
            await Task.Delay(750).ConfigureAwait(false);

            var ipPort = $"{ip}:{port}";
            var ok = await WirelessDebuggingHelper.ConnectAsync(ipPort).ConfigureAwait(false);
            Debugger.show(ok ? $"[TCPIP] Connected to {ipPort}." : $"[TCPIP] Failed to connect to {ipPort}.");
            return ok ? ipPort : string.Empty;
        }

        /// <summary>
        /// Resolves the serial we should currently talk to, given the saved config.
        /// USB always wins. In Wireless Debugging mode the live serial is discovered via
        /// mDNS reconnect; otherwise the saved Wi-Fi endpoint is used.
        /// </summary>
        public static async Task<string> ResolveActiveDeviceAsync(AdbDesktopConfig config)
        {
            var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
            var deviceLines = ParseDeviceLines(devices);

            if (!string.IsNullOrWhiteSpace(config.Device.SelectedDeviceUSB)
                && IsConnected(deviceLines, config.Device.SelectedDeviceUSB))
            {
                return config.Device.SelectedDeviceUSB;
            }

            // Any USB device beats a wireless one, even one we haven't seen before.
            var usb = FindConnectedUsbSerial(deviceLines);
            if (!string.IsNullOrEmpty(usb))
                return usb;

            // USB-only mode: never attempt a wireless connection.
            if (config.Device.WifiMode == WirelessMode.UsbOnly)
                return string.Empty;

            if (config.Device.WifiMode == WirelessMode.WirelessDebugging
                && !string.IsNullOrWhiteSpace(config.Device.MdnsServiceName))
            {
                // Already live? Don't pay for an mDNS sweep.
                var alreadyLive = FindConnectedWirelessSerial(deviceLines);
                if (!string.IsNullOrEmpty(alreadyLive))
                    return alreadyLive;

                var ipPort = await WirelessDebuggingHelper
                    .ReconnectViaMdnsAsync(config.Device.MdnsServiceName).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(ipPort))
                {
                    devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
                    deviceLines = ParseDeviceLines(devices);

                    var liveWireless = FindConnectedWirelessSerial(deviceLines);
                    if (!string.IsNullOrWhiteSpace(liveWireless))
                        return liveWireless;

                    if (IsConnected(deviceLines, ipPort))
                        return ipPort;
                }

                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(config.Device.SelectedDeviceWiFi)
                && config.Device.SelectedDeviceWiFi != "None")
            {
                if (!IsConnected(deviceLines, config.Device.SelectedDeviceWiFi))
                {
                    await AdbHelper.RunAdbCaptureAsync($"connect {config.Device.SelectedDeviceWiFi}").ConfigureAwait(false);
                    devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
                    deviceLines = ParseDeviceLines(devices);
                }

                if (IsConnected(deviceLines, config.Device.SelectedDeviceWiFi))
                    return config.Device.SelectedDeviceWiFi;
            }

            return string.Empty;
        }
    }
}
