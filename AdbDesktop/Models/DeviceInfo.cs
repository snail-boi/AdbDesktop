using System.Collections.Generic;
using System.Linq;

namespace AdbDesktop
{
    /// <summary>
    /// One physical device, however many ways adb can currently reach it.
    ///
    /// A phone routinely shows up two or three times in `adb devices`: over USB, over
    /// TCP/IP as "ip:port", and over Wireless Debugging as an mDNS name. Those are
    /// transports, not devices, so they are bundled here and the best one is used.
    ///
    /// Identity is therefore the HARDWARE serial (ro.serialno), not the adb serial: the
    /// adb serial differs per transport, and an ip:port one changes whenever the phone
    /// reassigns its port. Model is only a label - two identical handsets report the
    /// same one, which is why it can never be the key.
    /// </summary>
    public sealed class DeviceInfo : ViewModelBase
    {
        private int _number;
        private string _model = string.Empty;
        private string _label = string.Empty;
        private string _transport = string.Empty;
        private bool _isUsb;
        private int _batteryLevel = -1;
        private bool _batteryCharging;
        private bool _audioActive;
        private bool _audioInnerWave;
        private bool _audioOuterWave;
        private double _volume = 100;
        private bool _isAdded;
        private bool _isActiveDesktop;

        /// <summary>
        /// Hardware serial (ro.serialno). Stable across transports, reboots and port
        /// changes, and the key for everything device-scoped: icons, config, windows.
        /// NOT what gets passed to `adb -s` - that is <see cref="Transport"/>.
        /// </summary>
        public required string Serial { get; init; }

        /// <summary>
        /// The adb serial currently used to reach the device, i.e. the argument to
        /// `adb -s`. Changes underneath everything when a better transport appears or
        /// the current one drops.
        /// </summary>
        public string Transport
        {
            get => _transport;
            set
            {
                if (Set(ref _transport, value))
                    RaisePropertyChanged(nameof(ConnectionTooltip));
            }
        }

        /// <summary>Every adb serial that currently reaches this device.</summary>
        public IReadOnlyList<string> Transports { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Whether the CURRENT transport is USB. USB devices sort ahead of wireless ones,
        /// so this participates in numbering rather than being cosmetic, and it flips
        /// when the cable is pulled on a device that also has Wi-Fi.
        /// </summary>
        public bool IsUsb
        {
            get => _isUsb;
            set
            {
                if (Set(ref _isUsb, value))
                {
                    RaisePropertyChanged(nameof(TransportText));
                    RaisePropertyChanged(nameof(ConnectionTooltip));
                }
            }
        }

        /// <summary>Used to order devices within a transport group.</summary>
        public DateTime FirstSeenUtc { get; init; } = DateTime.UtcNow;

        public string Model
        {
            get => _model;
            set => Set(ref _model, value);
        }

        /// <summary>
        /// 1-based position. USB before wireless, connection order within each group.
        /// Recomputed whenever the set of devices changes, so it is a display ordinal,
        /// never an identity.
        /// </summary>
        public int Number
        {
            get => _number;
            set
            {
                if (Set(ref _number, value))
                    RaisePropertyChanged(nameof(NumberText));
            }
        }

        public string NumberText => _number > 0 ? _number.ToString() : string.Empty;

        /// <summary>
        /// This device's colour, and whether to draw it. The taskbar entry and the
        /// connection panel row are both legends for the marker on the unified desktop:
        /// they are how you know which colour is which phone.
        /// </summary>
        public System.Windows.Media.Brush? DeviceBrush => DeviceColours.BrushFor(Serial);

        public bool ShowDeviceColour =>
            string.Equals(App.Config.Icons.DeviceMarker, DeviceMarkers.Colour, StringComparison.Ordinal);

        /// <summary>Re-reads the marker setting after it is changed in Settings.</summary>
        public void RaiseMarkerChanged() => RaisePropertyChanged(nameof(ShowDeviceColour));

        /// <summary>
        /// What the taskbar shows. Normally the model; gains a suffix only when another
        /// connected device reports the same model.
        /// </summary>
        public string Label
        {
            get => string.IsNullOrEmpty(_label) ? (string.IsNullOrEmpty(_model) ? Serial : _model) : _label;
            set
            {
                if (!Set(ref _label, value))
                    return;

                // Every tooltip in the device's taskbar bundle names it.
                RaisePropertyChanged(nameof(BatteryTooltip));
                RaisePropertyChanged(nameof(AudioTooltip));
                RaisePropertyChanged(nameof(DesktopTooltip));
                RaisePropertyChanged(nameof(NotificationsTooltip));
                RaisePropertyChanged(nameof(ConnectionTooltip));
            }
        }

        /// <summary>
        /// Whether the user has added this device to AdbDesktop. adb seeing a device does
        /// not put it on the desktop -- adding is explicit, or automatic for a device
        /// whose own <see cref="KnownDevice.AutoAdd"/> flag is set. Only an added device
        /// gets its own desktop; a merely-connected one is just listed for pairing.
        /// </summary>
        public bool IsAdded
        {
            get => _isAdded;
            set
            {
                if (Set(ref _isAdded, value))
                    RaisePropertyChanged(nameof(DesktopTooltip));
            }
        }

        public int BatteryLevel
        {
            get => _batteryLevel;
            set
            {
                if (Set(ref _batteryLevel, value))
                {
                    RaisePropertyChanged(nameof(HasBattery));
                    RaisePropertyChanged(nameof(BatteryTooltip));
                }
            }
        }

        public bool BatteryCharging
        {
            get => _batteryCharging;
            set
            {
                if (Set(ref _batteryCharging, value))
                    RaisePropertyChanged(nameof(BatteryTooltip));
            }
        }

        public bool HasBattery => _batteryLevel >= 0;

        public string BatteryTooltip => _batteryLevel < 0
            ? $"{Label}: battery unknown"
            : _batteryCharging
                ? $"{Label}: {_batteryLevel}% (charging)"
                : $"{Label}: {_batteryLevel}%";

        /// <summary>
        /// Whether this device owns the audio link. At most one device can: the native
        /// audio port keeps a single static session, so links are mutually exclusive.
        /// </summary>
        public bool AudioActive
        {
            get => _audioActive;
            set
            {
                if (Set(ref _audioActive, value))
                {
                    RaisePropertyChanged(nameof(AudioInactive));
                    RaisePropertyChanged(nameof(AudioMuted));
                    RaisePropertyChanged(nameof(AudioTooltip));
                }
            }
        }

        public bool AudioInactive => !_audioActive;

        /// <summary>
        /// 0-100, this device's own playback level. Per device because the links run
        /// concurrently -- a single shared volume would move every stream at once.
        /// </summary>
        public double Volume
        {
            get => _volume;
            set => Set(ref _volume, Math.Clamp(value, 0, 100));
        }

        /*
         * The speaker glyph mirrors the actual level rather than being a static on/off
         * badge. These are pushed in by AudioLinkViewModel rather than bound to it,
         * because a device that does not own the link must read as silent no matter what
         * the (single, global) session volume happens to be.
         */

        /// <summary>Any volume above zero.</summary>
        public bool AudioInnerWave
        {
            get => _audioInnerWave;
            set
            {
                if (Set(ref _audioInnerWave, value))
                    RaisePropertyChanged(nameof(AudioMuted));
            }
        }

        /// <summary>Only once it is genuinely loud.</summary>
        public bool AudioOuterWave
        {
            get => _audioOuterWave;
            set => Set(ref _audioOuterWave, value);
        }

        /// <summary>Crossed-out speaker: not linked, or linked but silent.</summary>
        public bool AudioMuted => !_audioActive || !_audioInnerWave;

        public string AudioTooltip => _audioActive
            ? $"{Label}: audio playing - click for volume"
            : $"Play {Label} audio through this PC";

        /// <summary>Highlights this device's number box while its desktop is shown.</summary>
        public bool IsActiveDesktop
        {
            get => _isActiveDesktop;
            set => Set(ref _isActiveDesktop, value);
        }

        /// <summary>
        /// Only an added device has a desktop, so the number box doubles as a prompt when
        /// the device is merely connected.
        /// </summary>
        public string DesktopTooltip => _isAdded
            ? $"Show {Label}'s desktop"
            : $"{Label} has not been added yet - add it from the connection panel";

        public string NotificationsTooltip => $"{Label}: notifications (not implemented yet)";

        /// <summary>
        /// Names the transport in use and any spare, so a device that is reachable two
        /// ways says so rather than looking like a duplicate that went missing.
        /// </summary>
        public string ConnectionTooltip
        {
            get
            {
                var text = $"{Label} - connected over {TransportText} ({_transport})";

                var spares = Transports.Where(t =>
                    !string.Equals(t, _transport, StringComparison.Ordinal)).ToList();

                return spares.Count == 0
                    ? text
                    : $"{text}\nAlso reachable via: {string.Join(", ", spares)}";
            }
        }

        public string TransportText => IsUsb ? "USB" : "Wireless";

        /// <summary>Refreshes the transport-derived text after <see cref="Transports"/> changes.</summary>
        public void RaiseConnectionChanged() => RaisePropertyChanged(nameof(ConnectionTooltip));
    }
}
