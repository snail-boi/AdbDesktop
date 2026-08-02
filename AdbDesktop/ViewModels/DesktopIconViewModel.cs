using System.Windows.Media.Imaging;

namespace AdbDesktop
{
    /// <summary>
    /// One icon on the desktop. Col/Row is the stored truth; X/Y is the pixel position
    /// the Canvas binds to. They stay in sync except during a drag, when X/Y follows the
    /// mouse freely and Col/Row is only updated once the icon is dropped and snapped.
    /// </summary>
    public sealed class DesktopIconViewModel : ViewModelBase
    {
        private int _col;
        private int _row;
        private double _x;
        private double _y;
        private bool _isDragging;
        private bool _isRenaming;
        private bool _isDeviceConnected = true;
        private int _deviceNumber;
        private string _deviceLabel = string.Empty;
        private string _caption = string.Empty;
        private string _editCaption = string.Empty;
        private BitmapSource? _image;

        // The last DisplayImage handed out, with the two flags it was built for. Both the
        // trim and the desaturation are per-pixel work, so they run on change rather than
        // on every repaint.
        private BitmapSource? _shown;
        private bool _shownIsTrimmed;
        private bool _shownIsGrey;
        private bool _shownValid;

        public string Package { get; init; } = string.Empty;

        /// <summary>Which device runs this app. Empty for icons not yet adopted.</summary>
        public string DeviceSerial { get; set; } = string.Empty;

        /// <summary>
        /// Whether this icon is sitting on the unified desktop. The device markers exist
        /// only there: on a device's own desktop every icon belongs to that device, so a
        /// marker would say the same thing on all of them.
        /// </summary>
        public bool IsUnifiedDesktop { get; set; }

        /// <summary>
        /// The owning device's display number, or 0 while it is not connected -- numbers
        /// are positions in the current device list, so an absent device has none.
        /// </summary>
        public int DeviceNumber
        {
            get => _deviceNumber;
            set
            {
                if (!Set(ref _deviceNumber, value)) return;

                RaisePropertyChanged(nameof(DeviceNumberText));
                RaisePropertyChanged(nameof(ShowDeviceBadge));
            }
        }

        public string DeviceNumberText => _deviceNumber > 0 ? _deviceNumber.ToString() : string.Empty;

        /// <summary>Label of the owning device, for the context menu and tooltip.</summary>
        public string DeviceLabel
        {
            get => _deviceLabel;
            set
            {
                if (Set(ref _deviceLabel, value))
                    RaisePropertyChanged(nameof(DeviceText));
            }
        }

        /// <summary>
        /// Greys the icon on the unified desktop when its device is absent. The icon
        /// itself never goes away -- only the device's own desktop tab does.
        /// </summary>
        public bool IsDeviceConnected
        {
            get => _isDeviceConnected;
            set
            {
                if (!Set(ref _isDeviceConnected, value)) return;

                RaisePropertyChanged(nameof(DeviceText));
                RaisePropertyChanged(nameof(DisplayImage));
            }
        }

        public string DeviceText => string.IsNullOrEmpty(_deviceLabel)
            ? "Unknown device"
            : _isDeviceConnected ? $"Runs on {_deviceLabel}" : $"{_deviceLabel} (not connected)";

        private string _iconFile = string.Empty;

        public string IconFile
        {
            get => _iconFile;
            set
            {
                if (Set(ref _iconFile, value))
                    RaisePropertyChanged(nameof(HasCustomIcon));
            }
        }

        /// <summary>
        /// True for entries AdbDesktop supplies itself (currently just Settings). These can
        /// be renamed, moved and re-iconned, but never removed -- and they have no APK to
        /// read icons out of.
        /// </summary>
        public bool IsBuiltIn => BuiltInApps.IsBuiltIn(Package);

        public bool IsRemovable => !IsBuiltIn;

        /// <summary>A built-in falls back to its drawn icon when no custom one is set.</summary>
        public bool HasCustomIcon => !string.IsNullOrEmpty(IconFile);

        public string Caption
        {
            get => _caption;
            set => Set(ref _caption, value);
        }

        /// <summary>The icon as it really is. The tile paints <see cref="DisplayImage"/>.</summary>
        public BitmapSource? Image
        {
            get => _image;
            set
            {
                if (!Set(ref _image, value)) return;

                _shownValid = false;
                RaisePropertyChanged(nameof(DisplayImage));
            }
        }

        /// <summary>
        /// What the tile shows: the artwork trimmed to its content when icons are set to
        /// scale up, and desaturated while its device is away.
        /// </summary>
        public BitmapSource? DisplayImage
        {
            get
            {
                var trim = App.Config.Icons.ScaleToFit;
                var grey = !_isDeviceConnected;

                if (_shownValid && _shownIsTrimmed == trim && _shownIsGrey == grey)
                    return _shown;

                var image = _image;

                if (trim)
                    image = IconTrim.CropToContent(image);

                if (grey)
                    image = IconGreyscale.Desaturate(image);

                _shown = image;
                _shownIsTrimmed = trim;
                _shownIsGrey = grey;
                _shownValid = true;

                return image;
            }
        }

        // ---------- appearance ----------

        /*
         * The icon settings are global, but they are surfaced through each icon so the
         * item template can bind to them directly -- reaching out of the template to the
         * window's view model for what an icon looks like would be the wrong way round.
         */

        /// <summary>Both the tile and the artwork are drawn in a box this wide.</summary>
        public const double TileSize = 56;

        /// <summary>The rounded plate behind the artwork.</summary>
        public bool ShowTileBackground => App.Config.Icons.ShowBackground;

        public System.Windows.CornerRadius TileCorner =>
            new(IconShapes.RadiusFor(App.Config.Icons.Shape, TileSize));

        /// <summary>
        /// Breathing room between the artwork and the tile edge. Scaling to fit gives it
        /// up: the point is for the icon to reach the edges.
        /// </summary>
        public System.Windows.Thickness IconInset =>
            App.Config.Icons.ScaleToFit ? new System.Windows.Thickness(0) : new System.Windows.Thickness(2);

        // ---------- device marker ----------

        /*
         * Which device an app runs on is otherwise only in the right-click menu, which is
         * no help when you are looking at a screen full of icons from three phones. The
         * marker answers it at a glance, in one of two ways -- see DeviceMarkers.
         */

        /// <summary>
        /// True for an icon that could carry a marker: on the unified desktop, and owned
        /// by a device. Built-ins are adbDesktop's own and belong to no phone.
        /// </summary>
        private bool CanMark => IsUnifiedDesktop && !IsBuiltIn && !string.IsNullOrEmpty(DeviceSerial);

        /// <summary>The device's colour, hashed from its serial. Null when it has none.</summary>
        public System.Windows.Media.Brush? DeviceBrush => DeviceColours.BrushFor(DeviceSerial);

        /// <summary>The colour bar under this icon.</summary>
        public bool ShowDeviceColour =>
            CanMark && string.Equals(App.Config.Icons.DeviceMarker, DeviceMarkers.Colour, StringComparison.Ordinal);

        /// <summary>
        /// Whether the bar's height is held even for icons that do not draw one, so that
        /// captions stay on one line across the desktop instead of the built-ins riding
        /// up next to everything else.
        /// </summary>
        public bool ReserveColourBar =>
            IsUnifiedDesktop
            && string.Equals(App.Config.Icons.DeviceMarker, DeviceMarkers.Colour, StringComparison.Ordinal);

        /// <summary>
        /// The numbered badge. Needs a number, so it is absent while the device is -- the
        /// icon is greyed out then anyway, and the numbering only covers what is connected.
        /// </summary>
        public bool ShowDeviceBadge =>
            CanMark && _deviceNumber > 0
            && string.Equals(App.Config.Icons.DeviceMarker, DeviceMarkers.Badge, StringComparison.Ordinal);

        /// <summary>Re-reads the global icon settings. Called when they change.</summary>
        public void RefreshAppearance()
        {
            RaisePropertyChanged(nameof(ShowTileBackground));
            RaisePropertyChanged(nameof(TileCorner));
            RaisePropertyChanged(nameof(IconInset));
            RaisePropertyChanged(nameof(DisplayImage));

            RaisePropertyChanged(nameof(DeviceBrush));
            RaisePropertyChanged(nameof(ShowDeviceColour));
            RaisePropertyChanged(nameof(ReserveColourBar));
            RaisePropertyChanged(nameof(ShowDeviceBadge));
        }

        public int Col
        {
            get => _col;
            set => Set(ref _col, value);
        }

        public int Row
        {
            get => _row;
            set => Set(ref _row, value);
        }

        public double X
        {
            get => _x;
            set => Set(ref _x, value);
        }

        public double Y
        {
            get => _y;
            set => Set(ref _y, value);
        }

        /// <summary>Lifts the icon above its neighbours and dims it slightly while dragging.</summary>
        public bool IsDragging
        {
            get => _isDragging;
            set
            {
                if (Set(ref _isDragging, value))
                    RaisePropertyChanged(nameof(ZIndex));
            }
        }

        public int ZIndex => _isDragging || _isRenaming ? 100 : 0;

        /// <summary>True while the caption is being edited in place on the desktop.</summary>
        public bool IsRenaming
        {
            get => _isRenaming;
            set
            {
                if (!Set(ref _isRenaming, value)) return;

                if (value)
                    EditCaption = Caption;

                RaisePropertyChanged(nameof(ZIndex));
            }
        }

        /// <summary>Scratch copy the rename box binds to, so Escape can discard it.</summary>
        public string EditCaption
        {
            get => _editCaption;
            set => Set(ref _editCaption, value);
        }

        public void CommitRename()
        {
            var trimmed = EditCaption?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                Caption = trimmed;

            IsRenaming = false;
        }

        public void CancelRename()
        {
            EditCaption = Caption;
            IsRenaming = false;
        }

        public DesktopIcon ToModel() => new()
        {
            Package = Package,
            Caption = Caption,
            IconFile = IconFile,
            DeviceSerial = DeviceSerial,
            Col = Col,
            Row = Row
        };
    }
}
