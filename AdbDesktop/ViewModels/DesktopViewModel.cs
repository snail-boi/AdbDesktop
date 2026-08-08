using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AdbDesktop
{
    /// <summary>
    /// The icon grid. Positions are stored as Col/Row and converted to pixels here, so
    /// resizing the window re-flows the layout instead of leaving icons stranded off the
    /// right edge (which is what storing raw pixel coordinates would do).
    ///
    /// Desktops are INDEPENDENT. An icon lives on exactly one -- whichever was on screen
    /// when it was added -- so the unified desktop is a desktop in its own right, not an
    /// aggregate view of the others. Each one is its own file, loaded on switch.
    ///
    /// Which device runs an app (<see cref="DesktopIconViewModel.DeviceSerial"/>) is a
    /// separate question from which desktop its icon sits on: an app from device 2 can
    /// perfectly well have its icon on the unified desktop.
    /// </summary>
    public sealed class DesktopViewModel : ViewModelBase
    {
        public const double CellWidth = 96;
        public const double CellHeight = 110;
        public const double Margin = 24;

        private double _surfaceWidth = 1280;
        private double _surfaceHeight = 720;

        /// <summary>The icons of the desktop currently on screen. There is no other set.</summary>
        public ObservableCollection<DesktopIconViewModel> Icons { get; } = new();

        /// <summary>
        /// Raised when an icon is clicked without being dragged. MainViewModel turns this
        /// into an open window; the surface itself has no opinion about windows.
        /// </summary>
        public event Action<DesktopIconViewModel>? IconActivated;

        public void ActivateIcon(DesktopIconViewModel icon) => IconActivated?.Invoke(icon);

        public int Columns => Math.Max(1, (int)((_surfaceWidth - Margin * 2) / CellWidth));
        public int Rows => Math.Max(1, (int)((_surfaceHeight - Margin * 2) / CellHeight));

        /// <summary>Serial of the desktop being shown; empty means the unified desktop.</summary>
        public string ActiveDeviceSerial { get; private set; } = string.Empty;

        public bool IsUnified => string.IsNullOrEmpty(ActiveDeviceSerial);

        /// <summary>Loads the unified desktop. Called once at startup.</summary>
        public void Load() => ShowDesktop(string.Empty);

        // ---------- desktops ----------

        /// <summary>
        /// Switches desktops: saves the one on screen, then loads the other from its own
        /// file. Nothing is held for the inactive desktops, so they cannot drift.
        /// </summary>
        public void ShowDesktop(string? deviceSerial)
        {
            var target = deviceSerial ?? string.Empty;

            // Persist what is on screen before replacing it, or a switch would discard
            // any drag since the last save.
            if (Icons.Count > 0)
                Persist();

            ActiveDeviceSerial = target;

            var layout = DesktopStore.Load(target);

            _wallpaperPath = layout.Wallpaper;
            _wallpaperFit = layout.WallpaperFit;
            RaiseWallpaperChanged();

            Icons.Clear();

            foreach (var stored in layout.Icons)
            {
                var vm = new DesktopIconViewModel
                {
                    Package = stored.Package,
                    Caption = stored.Caption,
                    IconFile = stored.IconFile,
                    DeviceSerial = stored.DeviceSerial,
                    IsUnifiedDesktop = IsUnified,
                    Col = stored.Col,
                    Row = stored.Row,
                };

                vm.Image = ResolveImage(vm);
                Icons.Add(vm);
            }

            EnsureBuiltIns();
            RefreshDeviceMarkers();
            Reflow();

            RaisePropertyChanged(nameof(IsUnified));
            RaisePropertyChanged(nameof(HasUserIcons));
        }

        /// <summary>
        /// Refreshes the connected state (and label) of every icon on screen, so an app
        /// whose device is absent is greyed rather than looking launchable.
        /// </summary>
        public void ApplyDeviceStates(IReadOnlyList<DeviceInfo> connected)
        {
            foreach (var icon in Icons)
            {
                if (icon.IsBuiltIn)
                {
                    icon.IsDeviceConnected = true;
                    continue;
                }

                var device = connected.FirstOrDefault(d =>
                    string.Equals(d.Serial, icon.DeviceSerial, StringComparison.Ordinal));

                icon.IsDeviceConnected = device != null;

                // The badge shows the same number as the taskbar's device box, so it
                // follows the live numbering rather than keeping a stale one.
                icon.DeviceNumber = device?.Number ?? 0;

                if (device != null)
                {
                    icon.DeviceLabel = device.Label;
                    continue;
                }

                // Disconnected: fall back to the model recorded when the device was seen,
                // so the icon can still say which phone it belongs to.
                if (string.IsNullOrEmpty(icon.DeviceLabel))
                {
                    var known = App.Config.Desktop.Devices.FirstOrDefault(d =>
                        string.Equals(d.Serial, icon.DeviceSerial, StringComparison.Ordinal));

                    if (!string.IsNullOrWhiteSpace(known?.Model))
                        icon.DeviceLabel = known!.Model;
                }
            }

            RefreshDeviceMarkers();
        }

        /// <summary>
        /// Re-reads the global icon settings on every icon on screen. Icons on the other
        /// desktops pick the change up when they are next loaded, since they read the same
        /// settings when their view models are built.
        /// </summary>
        public void RefreshIconAppearance()
        {
            foreach (var icon in Icons)
                icon.RefreshAppearance();
        }

        /// <summary>
        /// Tells every icon whether more than one device is here, which is the only
        /// situation in which a device marker says anything.
        ///
        /// Counted from the devices, not from the icons: leftover icons from a phone that
        /// is not connected are not something the user is choosing between, so colouring
        /// the ones that are does not disambiguate anything.
        /// </summary>
        private void RefreshDeviceMarkers()
        {
            foreach (var icon in Icons)
                icon.HasSeveralDevices = DeviceColours.MarkersMeaningful;
        }

        // ---------- built-ins ----------

        /// <summary>
        /// A built-in with no custom icon falls back to its drawn one, so deleting the
        /// cached PNG (or never setting one) still leaves a usable tile.
        /// </summary>
        private static System.Windows.Media.Imaging.BitmapSource? ResolveImage(DesktopIconViewModel icon)
        {
            var stored = IconStore.Load(icon.IconFile);
            if (stored != null)
                return stored;

            return icon.IsBuiltIn ? BuiltInApps.SettingsIcon : null;
        }

        /// <summary>
        /// Adds any AdbDesktop-provided entry missing from this desktop. Built-ins belong to
        /// AdbDesktop rather than to a phone, so every desktop gets its own copy, positioned
        /// independently like everything else.
        /// </summary>
        private void EnsureBuiltIns()
        {
            if (Icons.Any(i => string.Equals(i.Package, BuiltInApps.SettingsPackage, StringComparison.Ordinal)))
                return;

            var settings = new DesktopIconViewModel
            {
                Package = BuiltInApps.SettingsPackage,
                Caption = BuiltInApps.SettingsCaption,
                Image = BuiltInApps.SettingsIcon,
                IsUnifiedDesktop = IsUnified
            };

            var (col, row) = FindFreeCell(settings);
            settings.Col = col;
            settings.Row = row;
            ApplyPixelPosition(settings);

            Icons.Add(settings);
            Persist();
        }

        /// <summary>
        /// Whether anything the user actually added is present. Drives the empty-state
        /// hint, which would otherwise never show now that Settings is always there.
        /// </summary>
        public bool HasUserIcons => Icons.Any(i => !i.IsBuiltIn);

        /// <summary>Restores a built-in to its drawn icon and drops the custom file.</summary>
        public void ResetIcon(DesktopIconViewModel icon)
        {
            if (!icon.IsBuiltIn)
                return;

            IconStore.Delete(icon.IconFile);
            icon.IconFile = string.Empty;
            icon.Image = BuiltInApps.SettingsIcon;
            Persist();
        }

        /// <summary>Writes the active desktop to its own file.</summary>
        public void Persist() =>
            DesktopStore.Save(ActiveDeviceSerial, new DesktopLayout
            {
                Icons = Icons.Select(i => i.ToModel()).ToList(),
                Wallpaper = _wallpaperPath,
                WallpaperFit = _wallpaperFit,
            });

        // ---------- wallpaper ----------

        private string _wallpaperPath = string.Empty;
        private string _wallpaperFit = "Fill";

        /// <summary>
        /// Per desktop, so the unified desktop and each device's can look different --
        /// which is also the quickest way to tell at a glance which one you are on.
        /// </summary>
        public string WallpaperPath => _wallpaperPath;

        public bool HasWallpaper => !string.IsNullOrEmpty(_wallpaperPath);

        /// <summary>
        /// Decoded on demand rather than cached across desktops: switching is rare, and
        /// holding several full-resolution images would cost far more than a re-decode.
        /// </summary>
        public System.Windows.Media.Imaging.BitmapImage? Wallpaper
        {
            get
            {
                if (string.IsNullOrEmpty(_wallpaperPath) || !System.IO.File.Exists(_wallpaperPath))
                    return null;

                try
                {
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    // Cached on load, so the file is not locked and can be replaced.
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(_wallpaperPath);
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                catch (Exception ex)
                {
                    Debugger.show($"[DESKTOP] Wallpaper decode failed: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>How the image fills the surface. Maps onto WPF's Stretch/TileMode.</summary>
        public string WallpaperFit
        {
            get => _wallpaperFit;
            set
            {
                var fit = string.IsNullOrWhiteSpace(value) ? "Fill" : value;
                if (fit == _wallpaperFit)
                    return;

                _wallpaperFit = fit;
                RaiseWallpaperChanged();
                Persist();
            }
        }

        public void SetWallpaper(string path)
        {
            _wallpaperPath = path ?? string.Empty;
            RaiseWallpaperChanged();
            Persist();
        }

        public void ClearWallpaper() => SetWallpaper(string.Empty);

        /// <summary>
        /// Gives every desktop this one's wallpaper. Only the wallpaper is copied -- each
        /// desktop keeps its own icons and their positions.
        /// </summary>
        public void ApplyWallpaperToAllDesktops()
        {
            // The other desktops are written straight to disk, so the active one has to be
            // on disk too before the sweep reads it back.
            Persist();

            DesktopStore.ApplyWallpaperToAll(
                _wallpaperPath,
                _wallpaperFit,
                App.Config.Desktop.Devices.Select(d => d.Serial));

            Debugger.show(string.IsNullOrEmpty(_wallpaperPath)
                ? "[DESKTOP] Cleared the wallpaper on every desktop."
                : "[DESKTOP] Applied the wallpaper to every desktop.");
        }

        private void RaiseWallpaperChanged()
        {
            RaisePropertyChanged(nameof(Wallpaper));
            RaisePropertyChanged(nameof(WallpaperPath));
            RaisePropertyChanged(nameof(HasWallpaper));
            RaisePropertyChanged(nameof(WallpaperFit));
        }

        // ---------- add / remove ----------

        /// <summary>
        /// The icon for a package on a specific device, on THIS desktop. Package alone is
        /// not a key: the same app installed on two phones is two icons.
        /// </summary>
        public DesktopIconViewModel? Find(string package, string deviceSerial) =>
            Icons.FirstOrDefault(i =>
                string.Equals(i.Package, package, StringComparison.Ordinal)
                && (i.IsBuiltIn || string.Equals(i.DeviceSerial, deviceSerial, StringComparison.Ordinal)));

        /// <summary>
        /// Adds an icon to the desktop currently on screen. That is the whole placement
        /// rule: an app added while looking at a device's desktop lands there, and one
        /// added from unified lands on unified. It never appears on both.
        /// </summary>
        public DesktopIconViewModel Add(string package, string caption, string iconFile,
            System.Windows.Media.Imaging.BitmapSource? image, string deviceSerial)
        {
            var existing = Find(package, deviceSerial);
            if (existing != null)
            {
                existing.Caption = caption;
                existing.IconFile = iconFile;
                existing.Image = image;
                Persist();
                return existing;
            }

            var icon = new DesktopIconViewModel
            {
                Package = package,
                Caption = caption,
                IconFile = iconFile,
                Image = image,
                DeviceSerial = deviceSerial,
                IsUnifiedDesktop = IsUnified
            };

            var (col, row) = FindFreeCell(icon);
            icon.Col = col;
            icon.Row = row;
            ApplyPixelPosition(icon);

            Icons.Add(icon);

            // The first app from a second phone is what makes the markers mean something.
            RefreshDeviceMarkers();

            Persist();
            RaisePropertyChanged(nameof(HasUserIcons));
            return icon;
        }

        public void Remove(DesktopIconViewModel icon)
        {
            // Built-ins are permanent. The context menu already hides the option, so this
            // is the backstop against any other caller.
            if (icon.IsBuiltIn)
                return;

            if (!Icons.Remove(icon))
                return;

            IconStore.Delete(icon.IconFile);

            // Removing the last app of a device leaves nothing to tell apart.
            RefreshDeviceMarkers();

            Persist();
            RaisePropertyChanged(nameof(HasUserIcons));
        }

        // ---------- layout ----------

        /// <summary>Recomputes pixel positions, and rescues any icon now outside the grid.</summary>
        public void SetSurfaceSize(double width, double height)
        {
            if (width <= 0 || height <= 0)
                return;

            _surfaceWidth = width;
            _surfaceHeight = height;
            Reflow();
        }

        private void Reflow()
        {
            var columns = Columns;
            var moved = false;

            // A narrower window can push icons past the right edge. Move only those,
            // so the rest of the user's arrangement is preserved.
            foreach (var icon in Icons.Where(i => i.Col >= columns).ToList())
            {
                var (col, row) = FindFreeCell(icon);
                icon.Col = col;
                icon.Row = row;
                moved = true;
            }

            foreach (var icon in Icons)
            {
                if (icon.IsDragging) continue;
                ApplyPixelPosition(icon);
            }

            if (moved)
                Persist();
        }

        public void ApplyPixelPosition(DesktopIconViewModel icon)
        {
            icon.X = Margin + icon.Col * CellWidth;
            icon.Y = Margin + icon.Row * CellHeight;
        }

        /// <summary>
        /// Snaps a dropped icon to the nearest cell. If that cell is taken, the icon
        /// settles into the closest free one rather than stacking.
        /// </summary>
        public void MoveTo(DesktopIconViewModel icon, double pixelX, double pixelY)
        {
            var col = (int)Math.Round((pixelX - Margin) / CellWidth);
            var row = (int)Math.Round((pixelY - Margin) / CellHeight);

            col = Math.Clamp(col, 0, Math.Max(0, Columns - 1));
            row = Math.Max(0, row);

            if (IsOccupied(col, row, icon))
                (col, row) = FindFreeCell(icon, col, row);

            icon.Col = col;
            icon.Row = row;
            ApplyPixelPosition(icon);
            Persist();
        }

        private bool IsOccupied(int col, int row, DesktopIconViewModel? ignore = null) =>
            Icons.Any(i => i != ignore && i.Col == col && i.Row == row);

        /// <summary>First free cell in reading order, used when placing a brand new icon.</summary>
        private (int Col, int Row) FindFreeCell(DesktopIconViewModel? ignore)
        {
            var columns = Columns;

            for (var row = 0; row < 500; row++)
            {
                for (var col = 0; col < columns; col++)
                {
                    if (!IsOccupied(col, row, ignore))
                        return (col, row);
                }
            }

            return (0, 0);
        }

        /// <summary>
        /// Nearest free cell to a target, searched as expanding rings. Used when a drop
        /// lands on an occupied cell.
        /// </summary>
        private (int Col, int Row) FindFreeCell(DesktopIconViewModel? ignore, int targetCol, int targetRow)
        {
            var columns = Columns;

            for (var radius = 1; radius < Math.Max(columns, 60); radius++)
            {
                var ring = new List<(int Col, int Row)>();

                for (var dc = -radius; dc <= radius; dc++)
                {
                    for (var dr = -radius; dr <= radius; dr++)
                    {
                        // Only the perimeter of this ring; the interior was covered by
                        // a smaller radius already.
                        if (Math.Abs(dc) != radius && Math.Abs(dr) != radius) continue;

                        var col = targetCol + dc;
                        var row = targetRow + dr;
                        if (col < 0 || row < 0 || col >= columns) continue;

                        ring.Add((col, row));
                    }
                }

                // Prefer true nearest within the ring (corners are further than edges).
                foreach (var (col, row) in ring.OrderBy(c =>
                             (c.Col - targetCol) * (c.Col - targetCol) +
                             (c.Row - targetRow) * (c.Row - targetRow)))
                {
                    if (!IsOccupied(col, row, ignore))
                        return (col, row);
                }
            }

            return FindFreeCell(ignore);
        }
    }
}
