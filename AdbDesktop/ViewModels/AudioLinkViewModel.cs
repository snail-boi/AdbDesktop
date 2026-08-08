using System.Collections.Generic;
using System.Linq;

namespace AdbDesktop
{
    /// <summary>
    /// The audio links, one per device, running concurrently.
    ///
    /// Each device gets its own <see cref="ScrcpyAudioSession"/>, which holds its own
    /// private copy of the native module and its own WASAPI stream. Nothing is shared, so
    /// starting a second device does not disturb the first and the volume sliders are
    /// genuinely independent.
    ///
    /// First click on a device starts its link; clicking that device again opens the
    /// volume panel for it, rather than tearing the link down immediately (which would
    /// make the button a foot-gun once audio is playing).
    /// </summary>
    public sealed class AudioLinkViewModel : ViewModelBase
    {
        private readonly Dictionary<string, ScrcpyAudioSession> _sessions = new(StringComparer.Ordinal);

        private IReadOnlyList<DeviceInfo> _known = Array.Empty<DeviceInfo>();
        private DeviceInfo? _panelDevice;
        private bool _isPanelOpen;
        private string _status = string.Empty;

        public RelayCommand DisconnectCommand { get; }
        public RelayCommand ClosePanelCommand { get; }

        public AudioLinkViewModel()
        {
            DisconnectCommand = new RelayCommand(DisconnectPanelDevice);
            ClosePanelCommand = new RelayCommand(() => IsPanelOpen = false);
        }

        /// <summary>The device whose volume panel is open, if any.</summary>
        public DeviceInfo? Device => _panelDevice;

        public bool IsPanelOpen
        {
            get => _isPanelOpen;
            set => Set(ref _isPanelOpen, value);
        }

        public string PanelTitle => _panelDevice == null ? "Device audio" : $"{_panelDevice.Label} audio";

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        /// <summary>
        /// 0-100 for the panel's device. Stored on the device itself, so each one keeps
        /// its own level and reopening the panel shows that device's slider, not the last
        /// one touched.
        /// </summary>
        public double Volume
        {
            get => _panelDevice?.Volume ?? 100;
            set
            {
                if (_panelDevice == null || Math.Abs(_panelDevice.Volume - value) < 0.01)
                    return;

                _panelDevice.Volume = value;

                if (_sessions.TryGetValue(_panelDevice.Serial, out var session))
                    session.Volume = (float)(value / 100.0);

                RaisePropertyChanged();
                PushToDevices();
            }
        }

        /// <summary>
        /// Starts the link on a device, or -- if it is already playing -- opens its volume
        /// panel. Other devices are left alone: the links are independent.
        /// </summary>
        public void Toggle(DeviceInfo? device)
        {
            if (device == null)
                return;

            if (_sessions.ContainsKey(device.Serial))
            {
                // Already running. Clicking the device whose panel is open closes it;
                // clicking a different one switches the panel over to that device.
                if (ReferenceEquals(device, _panelDevice) && IsPanelOpen)
                {
                    IsPanelOpen = false;
                }
                else
                {
                    ShowPanelFor(device);
                }

                return;
            }

            Status = string.Empty;

            var session = new ScrcpyAudioSession();
            session.SessionEvent += evt => OnSessionEvent(device.Serial, evt);
            session.Volume = (float)(device.Volume / 100.0);

            if (!session.Start(device.Transport))
            {
                session.Dispose();
                ShowPanelFor(device);
                Status = $"Could not start the audio link for {device.Label} - see the log.";
                return;
            }

            _sessions[device.Serial] = session;
            PushToDevices();
        }

        private void ShowPanelFor(DeviceInfo device)
        {
            _panelDevice = device;
            IsPanelOpen = true;

            RaisePropertyChanged(nameof(Device));
            RaisePropertyChanged(nameof(PanelTitle));
            RaisePropertyChanged(nameof(Volume));
        }

        /// <summary>
        /// Follows a device that is now reachable a different way. Only that device's
        /// session is restarted; the others are untouched.
        /// </summary>
        public void RestartFor(DeviceInfo device)
        {
            if (!_sessions.TryGetValue(device.Serial, out var session))
                return;

            session.Stop();

            if (session.Start(device.Transport))
            {
                session.Volume = (float)(device.Volume / 100.0);
            }
            else
            {
                session.Dispose();
                _sessions.Remove(device.Serial);
                Status = $"Audio stopped: {device.Label} moved to {device.TransportText}.";
            }

            PushToDevices();
        }

        /// <summary>
        /// Drops the links of devices that have gone away, then refreshes every device's
        /// speaker glyph.
        /// </summary>
        public void Sync(IReadOnlyList<DeviceInfo> connected)
        {
            _known = connected;

            foreach (var serial in _sessions.Keys
                         .Where(s => connected.All(d => !string.Equals(d.Serial, s, StringComparison.Ordinal)))
                         .ToList())
            {
                StopSession(serial);
            }

            if (_panelDevice != null && !connected.Contains(_panelDevice))
            {
                _panelDevice = null;
                IsPanelOpen = false;
                RaisePropertyChanged(nameof(Device));
                RaisePropertyChanged(nameof(PanelTitle));
            }

            PushToDevices();
        }

        /// <summary>
        /// Pushes link state onto the devices themselves, so each taskbar speaker binds to
        /// its own device rather than to a single shared "is audio on" flag.
        /// </summary>
        private void PushToDevices()
        {
            foreach (var device in _known)
            {
                var on = _sessions.ContainsKey(device.Serial);

                device.AudioActive = on;
                device.AudioInnerWave = on && device.Volume > 0;
                device.AudioOuterWave = on && device.Volume >= 50;
            }
        }

        private void DisconnectPanelDevice()
        {
            if (_panelDevice != null)
                StopSession(_panelDevice.Serial);

            IsPanelOpen = false;
            Status = string.Empty;
            PushToDevices();
        }

        private void StopSession(string serial)
        {
            if (!_sessions.Remove(serial, out var session))
                return;

            session.Dispose();
        }

        private void OnSessionEvent(string serial, int evt)
        {
            _ = UiThread.RunAsync(() =>
            {
                switch (evt)
                {
                    case ScrcpyAudioNative.EventConnectionFailed:
                        Status = "Could not connect.";
                        StopSession(serial);
                        break;
                    case ScrcpyAudioNative.EventAudioDisabled:
                        Status = "The device refused to capture audio.";
                        StopSession(serial);
                        break;
                    case ScrcpyAudioNative.EventDisconnected:
                        StopSession(serial);
                        break;
                    default:
                        return;
                }

                PushToDevices();
            });
        }

        public void Dispose()
        {
            // Every session is asked to stop first, so their teardowns overlap rather
            // than the wait below being paid once per device.
            foreach (var session in _sessions.Values)
                session.Dispose();

            foreach (var session in _sessions.Values)
                session.WaitForTeardown(TimeSpan.FromSeconds(3));

            _sessions.Clear();
        }
    }
}
