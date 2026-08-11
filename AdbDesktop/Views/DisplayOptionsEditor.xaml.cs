using System.Windows.Controls;

namespace AdbDesktop
{
    /// <summary>
    /// The shared mirroring-options editor. Pure markup over
    /// <see cref="DisplayOptionsViewModel"/>; both the Settings page and the per-app
    /// dialog drop it in and set a DataContext.
    /// </summary>
    public partial class DisplayOptionsEditor : UserControl
    {
        public DisplayOptionsEditor()
        {
            InitializeComponent();
        }
    }
}
