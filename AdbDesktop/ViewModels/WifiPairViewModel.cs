using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace AdbDesktop
{
    /// <summary>
    /// Copied verbatim from AMPL. Owns both pairing paths:
    ///
    ///   QR mode: generates a QR code carrying random service-name/password credentials,
    ///   then listens on the mDNS multicast socket for the phone announcing its pairing
    ///   service and runs adb pair automatically.
    ///
    ///   Manual mode: the user types an ip:port and the 6-digit code and pairs directly.
    ///
    /// The mDNS loop runs on a background thread; WPF marshals simple property changes to
    /// the UI thread for us, and the view marshals the close request.
    /// </summary>
    public sealed class WifiPairViewModel : ViewModelBase
    {
        private static readonly Brush AmberBrush = CreateFrozen(255, 179, 71);

        // Raised when the dialog should close. The bool becomes DialogResult.
        public event Action<bool>? RequestClose;

        // Results read back by the caller after the dialog closes.
        public string ServiceName { get; private set; } = string.Empty;
        public string PairAddress { get; private set; } = string.Empty;

        private CancellationTokenSource? _cts;

        // Credentials embedded in the QR code, regenerated each time QR mode starts.
        private string _qrServiceName = string.Empty;
        private string _qrPassword = string.Empty;

        public RelayCommand ToggleModeCommand { get; }
        public RelayCommand PairCommand { get; }
        public RelayCommand CancelCommand { get; }

        public WifiPairViewModel()
        {
            ToggleModeCommand = new RelayCommand(ToggleMode);
            PairCommand = new RelayCommand(async () => await PairManualAsync());
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        }

        // -- Bound state ------------------------------------------------------

        private bool _isQrMode = true;
        public bool IsQrMode
        {
            get => _isQrMode;
            set
            {
                if (Set(ref _isQrMode, value))
                    RaisePropertyChanged(nameof(IsManualMode));
            }
        }

        public bool IsManualMode => !_isQrMode;

        private string _modeToggleText = "Use IP/port and code instead";
        public string ModeToggleText
        {
            get => _modeToggleText;
            set => Set(ref _modeToggleText, value);
        }

        private ImageSource? _qrImage;
        public ImageSource? QrImage
        {
            get => _qrImage;
            set => Set(ref _qrImage, value);
        }

        private string _qrStatus = "Waiting for phone to scan...";
        public string QrStatus
        {
            get => _qrStatus;
            set => Set(ref _qrStatus, value);
        }

        private Brush _qrStatusBrush = AmberBrush;
        public Brush QrStatusBrush
        {
            get => _qrStatusBrush;
            set => Set(ref _qrStatusBrush, value);
        }

        private string _pairAddressInput = string.Empty;
        public string PairAddressInput
        {
            get => _pairAddressInput;
            set => Set(ref _pairAddressInput, value);
        }

        private string _pairCodeInput = string.Empty;
        public string PairCodeInput
        {
            get => _pairCodeInput;
            set => Set(ref _pairCodeInput, value);
        }

        private string _manualStatus = string.Empty;
        public string ManualStatus
        {
            get => _manualStatus;
            set => Set(ref _manualStatus, value);
        }

        private Brush _manualStatusBrush = AmberBrush;
        public Brush ManualStatusBrush
        {
            get => _manualStatusBrush;
            set => Set(ref _manualStatusBrush, value);
        }

        private bool _isPairing;
        public bool IsPairing
        {
            get => _isPairing;
            set
            {
                if (Set(ref _isPairing, value))
                    RaisePropertyChanged(nameof(CanPair));
            }
        }

        public bool CanPair => !_isPairing;

        // -- Lifecycle (called by the view) -----------------------------------

        public void Start()
        {
            GenerateQrCode();
            StartQrPairingLoop();
        }

        public void Cancel()
        {
            _cts?.Cancel();
        }

        // -- Mode toggle -------------------------------------------------------

        private void ToggleMode()
        {
            IsQrMode = !IsQrMode;

            if (IsQrMode)
            {
                ModeToggleText = "Use IP/port and code instead";

                _cts?.Cancel();
                GenerateQrCode();
                StartQrPairingLoop();
            }
            else
            {
                _cts?.Cancel();
                ModeToggleText = "Use QR code instead";
            }
        }

        // -- QR mode -----------------------------------------------------------

        private void GenerateQrCode()
        {
            _qrServiceName = GenerateRandomString(8);
            _qrPassword = GenerateRandomString(10);

            string qrContent = $"WIFI:T:ADB;S:{_qrServiceName};P:{_qrPassword};;";

            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new BitmapByteQRCode(data);

            byte[] bitmapBytes = qrCode.GetGraphic(10, new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 });

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bitmapBytes);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            QrImage = image;
        }

        private void StartQrPairingLoop()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => QrPairingLoopAsync(token), token);
        }

        /// <summary>
        /// Listens on the mDNS multicast socket (UDP 5353) for the pairing service the phone
        /// broadcasts after scanning the QR code, then calls adb pair. Joins the multicast
        /// group on every active IPv4 interface so it receives regardless of adapter.
        /// </summary>
        private async Task QrPairingLoopAsync(CancellationToken token)
        {
            Socket? sock = null;

            try
            {
                IPAddress mdns = IPAddress.Parse("224.0.0.251");

                sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                sock.Bind(new IPEndPoint(IPAddress.Any, 5353));

                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus != OperationalStatus.Up) continue;
                    if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                        try
                        {
                            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                                new MulticastOption(mdns, addr.Address));

                            Debugger.show($"[QR] Joined mDNS multicast on {addr.Address} ({iface.Name})");
                        }
                        catch (Exception ex)
                        {
                            Debugger.show($"[QR] Could not join multicast on {addr.Address}: {ex.Message}");
                        }
                    }
                }

                sock.ReceiveTimeout = 1500;

                SendMdnsQuery(sock, mdns);

                var buffer = new byte[4096];
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

                while (!token.IsCancellationRequested)
                {
                    int received = 0;
                    remote = new IPEndPoint(IPAddress.Any, 0);

                    try
                    {
                        received = sock.ReceiveFrom(buffer, ref remote);
                    }
                    catch (SocketException)
                    {
                        if (!token.IsCancellationRequested)
                            SendMdnsQuery(sock, mdns);
                        continue;
                    }

                    if (received == 0 || token.IsCancellationRequested)
                        continue;

                    var senderIp = ((IPEndPoint)remote).Address;
                    var segment = new byte[received];
                    Buffer.BlockCopy(buffer, 0, segment, 0, received);

                    var services = ParseMdnsPacket(segment);

                    foreach (var svc in services)
                    {
                        if (token.IsCancellationRequested)
                            return;

                        if (!svc.Name.Contains(_qrServiceName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string ipPort = $"{senderIp}:{svc.Port}";

                        QrStatus = "Phone detected, pairing...";

                        Debugger.show($"[QR] Detected '{svc.Name}' at {ipPort}. Pairing...");

                        var result = await WirelessDebuggingHelper.PairWithPasswordAsync(ipPort, _qrPassword)
                            .ConfigureAwait(false);

                        Debugger.show($"[QR] Pair result: success={result.Success} output={result.Output}");

                        if (result.Success)
                        {
                            ServiceName = result.ServiceName;
                            PairAddress = ipPort;
                            RequestClose?.Invoke(true);
                            return;
                        }

                        QrStatusBrush = Brushes.OrangeRed;
                        QrStatus = "Pairing failed. Try scanning again or use IP/port instead."
                            + (string.IsNullOrWhiteSpace(result.Output) ? "" : " (" + result.Output.Trim() + ")");
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debugger.show("QR pairing loop fatal error: " + ex.Message);
            }
            finally
            {
                sock?.Close();
            }
        }

        // -- Manual IP/port mode ----------------------------------------------

        private async Task PairManualAsync()
        {
            if (_isPairing) return;

            var addr = PairAddressInput.Trim();
            var code = PairCodeInput.Trim();

            if (string.IsNullOrWhiteSpace(addr) || !addr.Contains(':'))
            {
                ManualStatus = "Pairing address must be in format ip:port.";
                return;
            }
            if (string.IsNullOrWhiteSpace(code))
            {
                ManualStatus = "Enter the 6-digit pairing code from your phone.";
                return;
            }

            IsPairing = true;
            ManualStatusBrush = Brushes.Gray;
            ManualStatus = "Pairing...";

            var result = await WirelessDebuggingHelper.PairAsync(addr, code).ConfigureAwait(true);

            if (!result.Success)
            {
                IsPairing = false;
                ManualStatusBrush = Brushes.OrangeRed;
                ManualStatus = "Pairing failed. Make sure the phone screen is still showing the code, "
                    + "the IP/port match exactly, and your PC is on the same Wi-Fi network. "
                    + (string.IsNullOrWhiteSpace(result.Output) ? "" : "Details: " + result.Output.Trim());
                return;
            }

            ServiceName = result.ServiceName;
            PairAddress = addr;
            RequestClose?.Invoke(true);
        }

        // -- mDNS helpers (raw UDP) -------------------------------------------

        private static void SendMdnsQuery(Socket sock, IPAddress mdns)
        {
            try
            {
                byte[] query = BuildMdnsQuery("_adb-tls-pairing._tcp.local");
                sock.SendTo(query, new IPEndPoint(mdns, 5353));
            }
            catch (Exception ex)
            {
                Debugger.show("SendMdnsQuery failed: " + ex.Message);
            }
        }

        private static byte[] BuildMdnsQuery(string serviceName)
        {
            byte[] header = new byte[12];
            header[5] = 1; // QDCOUNT = 1

            byte[] nameBytes = EncodeDnsName(serviceName);
            byte[] question = new byte[nameBytes.Length + 4];
            Buffer.BlockCopy(nameBytes, 0, question, 0, nameBytes.Length);
            question[nameBytes.Length] = 0;       // QTYPE high
            question[nameBytes.Length + 1] = 255; // QTYPE = ANY
            question[nameBytes.Length + 2] = 0;   // QCLASS high
            question[nameBytes.Length + 3] = 255; // QCLASS = ANY

            byte[] packet = new byte[header.Length + question.Length];
            Buffer.BlockCopy(header, 0, packet, 0, header.Length);
            Buffer.BlockCopy(question, 0, packet, header.Length, question.Length);
            return packet;
        }

        private static byte[] EncodeDnsName(string name)
        {
            var parts = name.Split('.');
            var result = new byte[name.Length + 2];
            int index = 0;

            foreach (var part in parts)
            {
                result[index++] = (byte)part.Length;
                byte[] partBytes = Encoding.UTF8.GetBytes(part);
                Buffer.BlockCopy(partBytes, 0, result, index, partBytes.Length);
                index += partBytes.Length;
            }
            result[index] = 0;
            return result;
        }

        private sealed class MdnsServiceRecord
        {
            public string Name { get; set; } = string.Empty;
            public ushort Port { get; set; }
        }

        private static List<MdnsServiceRecord> ParseMdnsPacket(byte[] data)
        {
            var result = new List<MdnsServiceRecord>();
            try
            {
                int i = 0;
                ReadUInt16(data, ref i); // transaction id
                ReadUInt16(data, ref i); // flags
                ushort qdCount = ReadUInt16(data, ref i);
                ushort anCount = ReadUInt16(data, ref i);
                ushort nsCount = ReadUInt16(data, ref i);
                ushort arCount = ReadUInt16(data, ref i);

                for (int q = 0; q < qdCount; q++)
                {
                    ReadDnsName(data, ref i);
                    ReadUInt16(data, ref i);
                    ReadUInt16(data, ref i);
                }

                int totalRR = anCount + nsCount + arCount;
                for (int r = 0; r < totalRR; r++)
                {
                    string name = ReadDnsName(data, ref i);
                    ushort type = ReadUInt16(data, ref i);
                    ReadUInt16(data, ref i); // class
                    ReadUInt32(data, ref i); // ttl
                    ushort rdLength = ReadUInt16(data, ref i);

                    if (type == 33) // SRV
                    {
                        ReadUInt16(data, ref i); // priority
                        ReadUInt16(data, ref i); // weight
                        ushort port = ReadUInt16(data, ref i);
                        ReadDnsName(data, ref i); // target
                        result.Add(new MdnsServiceRecord { Name = name, Port = port });
                    }
                    else
                    {
                        i += rdLength;
                    }
                }
            }
            catch (Exception ex)
            {
                Debugger.show("ParseMdnsPacket error: " + ex.Message);
            }

            return result;
        }

        private static string ReadDnsName(byte[] data, ref int i)
        {
            var sb = new StringBuilder();
            while (i < data.Length && data[i] != 0)
            {
                byte len = data[i++];
                if ((len & 0xC0) == 0xC0)
                {
                    int ptr = ((len & 0x3F) << 8) | data[i++];
                    int saved = i;
                    i = ptr;
                    sb.Append(ReadDnsName(data, ref i));
                    i = saved;
                    return sb.ToString();
                }
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.UTF8.GetString(data, i, len));
                i += len;
            }
            if (i < data.Length) i++;
            return sb.ToString();
        }

        private static ushort ReadUInt16(byte[] data, ref int i)
        {
            ushort v = (ushort)((data[i] << 8) | data[i + 1]);
            i += 2;
            return v;
        }

        private static uint ReadUInt32(byte[] data, ref int i)
        {
            uint v = (uint)((data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3]);
            i += 4;
            return v;
        }

        // -- Misc helpers ------------------------------------------------------

        private static string GenerateRandomString(int length)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                sb.Append(chars[Random.Shared.Next(chars.Length)]);
            return sb.ToString();
        }

        private static Brush CreateFrozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
