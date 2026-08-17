using System.Windows;

namespace AdbDesktop
{
    /// <summary>
    /// The first-run "Windows desktop" / "App desktop" choice. Shown by App.xaml.cs
    /// before MainWindow exists; <see cref="ProceedToAppDesktop"/> tells the caller
    /// whether to go on and build the shell or shut down instead.
    /// </summary>
    public partial class DesktopChoiceWindow : Window
    {
        private readonly DesktopChoiceViewModel _vm;

        public bool ProceedToAppDesktop { get; private set; }

        public DesktopChoiceWindow()
        {
            InitializeComponent();

            _vm = new DesktopChoiceViewModel();
            DataContext = _vm;
            _vm.AppDesktopChosen += OnAppDesktopChosen;
        }

        private void OnAppDesktopChosen()
        {
            ProceedToAppDesktop = true;
            Close();
        }
    }
}
