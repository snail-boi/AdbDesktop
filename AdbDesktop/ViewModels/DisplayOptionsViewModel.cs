using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AdbDesktop
{
    /// <summary>
    /// Edits one <see cref="DisplayOptions"/>, either the global defaults or one app's
    /// override, for the shared options editor.
    ///
    /// Both layers use the same control because they are the same shape. What differs is
    /// only what "Default" resolves to, which arrives as <see cref="_inherited"/>: for the
    /// global set that is the built-in defaults, and for an override it is whatever the
    /// global currently says. So every list here carries a "Default (x)" entry meaning
    /// "leave unset", and the x is filled in from the layer underneath.
    ///
    /// Nothing is applied to a running window. Every one of these settings is baked into
    /// the encoder or the virtual display at open, so the editor's job ends at writing
    /// the config. See AppWindowViewModel.StartMirroring.
    /// </summary>
    public sealed class DisplayOptionsViewModel : ViewModelBase
    {
        private readonly DisplayOptions _model;
        private readonly ResolvedDisplayOptions _inherited;

        /// <summary>Raised whenever a value changes, so the owner can persist.</summary>
        public event Action? Changed;

        public DisplayOptionsViewModel(DisplayOptions model,
                                       ResolvedDisplayOptions inherited)
        {
            _model = model;
            _inherited = inherited;

            ToggleBitRateUnitCommand = new RelayCommand(ToggleBitRateUnit);

            // Whichever unit renders the stored value without a decimal point. A value
            // that is a whole number of Mbps is nearly always one the user typed as Mbps.
            _bitRateInMbps = _model.VideoBitRate is not { } bps
                             || bps % 1_000_000 == 0;

            _bitRateText = FormatBitRate(_model.VideoBitRate);
            _bufferText = _model.VideoBufferMs?.ToString(CultureInfo.InvariantCulture)
                          ?? string.Empty;
            _fpsText = _model.MaxFps?.ToString(CultureInfo.InvariantCulture)
                       ?? string.Empty;
        }

        private void RaiseChanged() => Changed?.Invoke();

        // ---------- inherit-aware list plumbing ----------

        /// <summary>
        /// The "leave this unset" entry. Carries the inherited value in its label so the
        /// user can see what they would get without having to look at the other layer.
        /// </summary>
        private static string DefaultLabel(string inheritedLabel) =>
            $"Default ({inheritedLabel})";

        // ---------- video quality (bit rate) ----------

        private bool _bitRateInMbps;
        private string _bitRateText;

        /// <summary>
        /// Mbps or Kbps, toggled by the button beside the field. Presentation only: the
        /// config always stores bits per second.
        /// </summary>
        public string BitRateUnit => _bitRateInMbps ? "Mbps" : "Kbps";

        public RelayCommand ToggleBitRateUnitCommand { get; }

        private int UnitDivisor => _bitRateInMbps ? 1_000_000 : 1_000;

        private string FormatBitRate(int? bps)
        {
            if (bps is not { } value)
                return string.Empty;

            var scaled = value / (double) UnitDivisor;

            // No trailing ".00" on the common whole-number case, but keep the fraction
            // when the stored value genuinely is not round in this unit.
            return scaled == Math.Floor(scaled)
                ? ((long) scaled).ToString(CultureInfo.InvariantCulture)
                : scaled.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public string BitRateText
        {
            get => _bitRateText;
            set
            {
                if (!Set(ref _bitRateText, value ?? string.Empty))
                    return;

                if (string.IsNullOrWhiteSpace(_bitRateText))
                {
                    _model.VideoBitRate = null;
                }
                else if (double.TryParse(_bitRateText, NumberStyles.Float,
                                         CultureInfo.InvariantCulture, out var scaled)
                         && scaled > 0)
                {
                    _model.VideoBitRate = (int) Math.Clamp(
                        scaled * UnitDivisor,
                        DisplayOptions.MinVideoBitRate,
                        DisplayOptions.MaxVideoBitRate);
                }
                else
                {
                    _model.VideoBitRate = null;
                }

                // Written back from what was actually stored, so a clamped or rejected
                // entry snaps to the truth instead of sitting there looking accepted.
                // Safe to do here rather than mid-typing because the binding only pushes
                // on lost focus.
                _bitRateText = FormatBitRate(_model.VideoBitRate);

                RaisePropertyChanged(nameof(BitRateText));
                RaisePropertyChanged(nameof(BitRateHint));
                RaiseChanged();
            }
        }

        /// <summary>
        /// Rewrites the number into the other unit rather than reinterpreting it: 8 Mbps
        /// becomes 8000 Kbps, not 8 Kbps.
        /// </summary>
        private void ToggleBitRateUnit()
        {
            var stored = _model.VideoBitRate;

            _bitRateInMbps = !_bitRateInMbps;

            _bitRateText = FormatBitRate(stored);

            RaisePropertyChanged(nameof(BitRateUnit));
            RaisePropertyChanged(nameof(BitRateText));
            RaisePropertyChanged(nameof(BitRateHint));
        }

        /// <summary>What an empty box will actually use.</summary>
        public string BitRateHint =>
            string.IsNullOrWhiteSpace(_bitRateText)
                ? $"Using the default: {_inherited.VideoBitRate / 1_000_000.0:0.###} Mbps"
                : string.Empty;

        // ---------- frame rate ----------

        private const string Unlimited = "Unlimited";

        private string _fpsText;

        private static string FpsLabel(int fps) =>
            fps > 0 ? $"{fps} fps" : Unlimited;

        /// <summary>
        /// Free text rather than a list of blessed values: any integer the device will
        /// accept is legitimate, and there is no reason for the app to have opinions about
        /// which ones. 0 means no cap.
        /// </summary>
        public string MaxFpsText
        {
            get => _fpsText;
            set
            {
                if (!Set(ref _fpsText, value ?? string.Empty))
                    return;

                if (string.IsNullOrWhiteSpace(_fpsText))
                    _model.MaxFps = null;
                else if (int.TryParse(_fpsText, NumberStyles.Integer,
                                      CultureInfo.InvariantCulture, out var fps))
                    _model.MaxFps = Math.Clamp(fps, 0, DisplayOptions.MaxMaxFps);
                else
                    _model.MaxFps = null;

                _fpsText = _model.MaxFps?.ToString(CultureInfo.InvariantCulture)
                           ?? string.Empty;

                RaisePropertyChanged(nameof(MaxFpsText));
                RaisePropertyChanged(nameof(MaxFpsHint));
                RaiseChanged();
            }
        }

        /// <summary>
        /// Says what the box currently means. 0 is the one value whose effect is not
        /// obvious from the number, so it gets said outright rather than left in the
        /// description underneath.
        /// </summary>
        public string MaxFpsHint
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_fpsText))
                    return $"Using the default: {FpsLabel(_inherited.MaxFps)}";

                return _model.MaxFps == 0
                    ? "Unlimited: the phone sends frames as fast as it can draw them."
                    : string.Empty;
            }
        }

        // ---------- codec ----------

        public IReadOnlyList<string> VideoCodecOptions =>
            new[] { DefaultLabel(VideoCodecs.DisplayName(_inherited.VideoCodec)) }
                .Concat(VideoCodecs.All.Select(VideoCodecs.DisplayName))
                .ToList();

        public string VideoCodec
        {
            get => _model.VideoCodec is { } codec
                ? VideoCodecs.DisplayName(codec)
                : DefaultLabel(VideoCodecs.DisplayName(_inherited.VideoCodec));
            set
            {
                if (string.IsNullOrEmpty(value) || value == VideoCodec)
                    return;

                _model.VideoCodec = value.StartsWith("Default", StringComparison.Ordinal)
                    ? null
                    : VideoCodecs.All.FirstOrDefault(
                        c => VideoCodecs.DisplayName(c) == value);

                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsCodecNonDefault));
                RaiseChanged();
            }
        }

        /// <summary>
        /// Drives the warning under the list. H.265 and AV1 need a device encoder that
        /// supports them, and when one is missing the session fails to start rather than
        /// falling back, which looks like the app being broken, not like a setting.
        /// </summary>
        public bool IsCodecNonDefault =>
            _model.VideoCodec != null && _model.VideoCodec != VideoCodecs.H264;

        // ---------- video buffer ----------

        private string _bufferText;

        public string VideoBufferText
        {
            get => _bufferText;
            set
            {
                if (!Set(ref _bufferText, value ?? string.Empty))
                    return;

                if (string.IsNullOrWhiteSpace(_bufferText))
                    _model.VideoBufferMs = null;
                else if (int.TryParse(_bufferText, NumberStyles.Integer,
                                      CultureInfo.InvariantCulture, out var ms))
                    _model.VideoBufferMs =
                        Math.Clamp(ms, 0, DisplayOptions.MaxVideoBufferMs);
                else
                    _model.VideoBufferMs = null;

                // Same write-back as the bit rate: a clamped or rejected entry snaps to
                // what was actually stored.
                _bufferText = _model.VideoBufferMs?.ToString(CultureInfo.InvariantCulture)
                              ?? string.Empty;

                RaisePropertyChanged(nameof(VideoBufferText));
                RaisePropertyChanged(nameof(VideoBufferHint));
                RaiseChanged();
            }
        }

        public string VideoBufferHint =>
            string.IsNullOrWhiteSpace(_bufferText)
                ? $"Using the default: {_inherited.VideoBufferMs} ms"
                : string.Empty;

        // ---------- view only ----------

        private const string On = "On";
        private const string Off = "Off";

        public IReadOnlyList<string> ViewOnlyOptions =>
            new[] { DefaultLabel(_inherited.ViewOnly ? On : Off), On, Off };

        public string ViewOnly
        {
            get => _model.ViewOnly is { } viewOnly
                ? (viewOnly ? On : Off)
                : DefaultLabel(_inherited.ViewOnly ? On : Off);
            set
            {
                if (string.IsNullOrEmpty(value) || value == ViewOnly)
                    return;

                _model.ViewOnly = value.StartsWith("Default", StringComparison.Ordinal)
                    ? null
                    : value == On;

                RaisePropertyChanged();
                RaiseChanged();
            }
        }

        // ---------- reset ----------

        /// <summary>
        /// Clears every field back to inherited. Used by the per-app dialog's "Use the
        /// defaults" button, which is what removes the override from the config entirely.
        /// </summary>
        public void Reset()
        {
            _model.VideoBitRate = null;
            _model.MaxFps = null;
            _model.VideoCodec = null;
            _model.VideoBufferMs = null;
            _model.ViewOnly = null;

            _bitRateText = string.Empty;
            _bufferText = string.Empty;
            _fpsText = string.Empty;

            RaisePropertyChanged(nameof(BitRateText));
            RaisePropertyChanged(nameof(BitRateHint));
            RaisePropertyChanged(nameof(VideoBufferText));
            RaisePropertyChanged(nameof(VideoBufferHint));
            RaisePropertyChanged(nameof(MaxFpsText));
            RaisePropertyChanged(nameof(MaxFpsHint));
            RaisePropertyChanged(nameof(VideoCodec));
            RaisePropertyChanged(nameof(IsCodecNonDefault));
            RaisePropertyChanged(nameof(ViewOnly));

            RaiseChanged();
        }
    }
}
