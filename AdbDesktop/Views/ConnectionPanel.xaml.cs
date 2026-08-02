using System.Windows.Controls;

namespace AdbDesktop
{
    /// <summary>
    /// Inline replacement for the old DeviceConnectWindow. Slides up out of the taskbar
    /// alongside the search panel; all behaviour lives in ConnectionViewModel.
    /// </summary>
    public partial class ConnectionPanel : UserControl
    {
        public ConnectionPanel()
        {
            InitializeComponent();
        }
    }
}
