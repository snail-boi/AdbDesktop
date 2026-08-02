using System.Windows.Controls;

namespace AdbDesktop
{
    /// <summary>
    /// The built-in Settings app's content. Hosted inside an ordinary app window, not a
    /// dialog -- as far as the shell is concerned this IS an app.
    /// </summary>
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }
    }
}
