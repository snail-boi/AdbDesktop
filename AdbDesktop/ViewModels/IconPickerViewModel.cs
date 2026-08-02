using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

namespace AdbDesktop
{
    /// <summary>
    /// Backs the icon-picker grid. Candidates come from <see cref="IconExtractor"/>; the
    /// dialog appends a "bring your own image" tile after them.
    /// </summary>
    public sealed class IconPickerViewModel : ViewModelBase
    {
        private BitmapSource? _selectedImage;
        private string _selectedLabel = string.Empty;

        /// <summary>Raised with the chosen image, or null when the user backs out.</summary>
        public event Action<BitmapSource?>? Finished;

        public RelayCommand ConfirmCommand { get; }
        public RelayCommand CancelCommand { get; }

        private readonly string? _emptyMessage;

        public IconPickerViewModel(
            string package,
            string displayName,
            IEnumerable<IconCandidate> candidates,
            string? emptyMessage = null)
        {
            Package = package;
            DisplayName = displayName;
            _emptyMessage = emptyMessage;

            foreach (var candidate in candidates)
                Candidates.Add(candidate);

            ConfirmCommand = new RelayCommand(
                () => Finished?.Invoke(_selectedImage),
                () => _selectedImage != null);

            CancelCommand = new RelayCommand(() => Finished?.Invoke(null));
        }

        public string Package { get; }
        public string DisplayName { get; }

        public ObservableCollection<IconCandidate> Candidates { get; } = new();

        public bool HasCandidates => Candidates.Count > 0;

        public string Heading => $"Choose an icon for {DisplayName}";

        public string SubHeading => HasCandidates
            ? $"{Candidates.Count} image{(Candidates.Count == 1 ? "" : "s")} found in the APK."
            : _emptyMessage ?? "No usable image was found in this APK. Supply your own below.";

        public BitmapSource? SelectedImage
        {
            get => _selectedImage;
            set
            {
                if (Set(ref _selectedImage, value))
                    RaisePropertyChanged(nameof(HasSelection));
            }
        }

        public string SelectedLabel
        {
            get => _selectedLabel;
            set => Set(ref _selectedLabel, value);
        }

        public bool HasSelection => _selectedImage != null;
    }
}
