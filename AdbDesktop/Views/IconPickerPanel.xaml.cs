using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AdbDesktop
{
    /// <summary>
    /// The icon grid, drawn inline over the desktop rather than in its own window.
    ///
    /// Tiles are built in code rather than templated because the last cell is a different
    /// thing entirely -- the "bring your own image" action, not a candidate.
    /// </summary>
    public partial class IconPickerPanel : UserControl
    {
        private IconPickerViewModel? _vm;
        private Border? _selectedTile;
        private Border? _byoTile;

        public IconPickerPanel()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _vm = DataContext as IconPickerViewModel;

            Tiles.Items.Clear();
            _selectedTile = null;
            _byoTile = null;

            if (_vm == null)
                return;

            foreach (var candidate in _vm.Candidates)
                Tiles.Items.Add(CreateCandidateTile(candidate));

            _byoTile = CreateBringYourOwnTile();
            Tiles.Items.Add(_byoTile);
        }

        private Border CreateCandidateTile(IconCandidate candidate)
        {
            var image = new Image
            {
                Source = candidate.Image,
                Width = 56,
                Height = 56,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var caption = new TextBlock
            {
                Text = $"{candidate.Width}x{candidate.Height}",
                FontSize = 10,
                Foreground = (Brush)FindResource("MutedTextBrush"),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(image);
            content.Children.Add(caption);

            var tile = NewTile(content);
            tile.ToolTip = candidate.Tooltip;

            tile.MouseLeftButtonUp += (_, e) =>
            {
                Select(tile, candidate.Image, candidate.Label);

                // Border is not a Control, so there is no MouseDoubleClick to hook;
                // ClickCount on the up-event is the equivalent.
                if (e.ClickCount == 2)
                    _vm?.ConfirmCommand.Execute(null);
            };

            return tile;
        }

        private Border CreateBringYourOwnTile()
        {
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            content.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 20,
                Foreground = (Brush)FindResource("MutedTextBrush"),
                TextAlignment = TextAlignment.Center
            });

            content.Children.Add(new TextBlock
            {
                Text = "Use my own image",
                FontSize = 10.5,
                Foreground = (Brush)FindResource("MutedTextBrush"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 6, 4, 0)
            });

            var tile = NewTile(content);
            tile.BorderBrush = (Brush)FindResource("LineBrush");
            tile.BorderThickness = new Thickness(1);
            tile.ToolTip = "Choose a PNG, JPEG, BMP or WebP file from this computer";
            tile.MouseLeftButtonUp += (_, _) => BrowseForImage(tile);

            return tile;
        }

        private Border NewTile(UIElement content) => new()
        {
            Width = 104,
            Height = 104,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = content
        };

        private void Select(Border tile, BitmapSource image, string label)
        {
            if (_selectedTile != null)
            {
                var wasByo = ReferenceEquals(_selectedTile, _byoTile);
                _selectedTile.BorderBrush = wasByo ? (Brush)FindResource("LineBrush") : null;
                _selectedTile.BorderThickness = wasByo ? new Thickness(1) : new Thickness(0);
            }

            _selectedTile = tile;
            tile.BorderBrush = (Brush)FindResource("AccentBrush");
            tile.BorderThickness = new Thickness(2);

            if (_vm == null) return;

            _vm.SelectedImage = image;
            _vm.SelectedLabel = label;
        }

        private void BrowseForImage(Border tile)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose an icon image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;

            var image = IconStore.LoadFromFile(dialog.FileName);
            if (image == null)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "That file could not be read as an image.",
                    "Unsupported image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Select(tile, image, System.IO.Path.GetFileName(dialog.FileName));
        }
    }
}
