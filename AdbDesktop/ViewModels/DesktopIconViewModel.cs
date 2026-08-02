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
        private string _deviceLabel = string.Empty;
        private string _caption = string.Empty;
        private string _editCaption = string.Empty;
        private BitmapSource? _image;

        public string Package { get; init; } = string.Empty;

        /// <summary>Which device runs this app. Empty for icons not yet adopted.</summary>
        public string DeviceSerial { get; set; } = string.Empty;

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
                if (Set(ref _isDeviceConnected, value))
                    RaisePropertyChanged(nameof(DeviceText));
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

        public BitmapSource? Image
        {
            get => _image;
            set => Set(ref _image, value);
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
