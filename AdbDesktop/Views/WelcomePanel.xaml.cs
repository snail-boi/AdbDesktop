using System.Windows.Controls;

namespace AdbDesktop
{
    /// <summary>
    /// The first-run guide, drawn inline over the desktop like the icon picker rather than
    /// in its own window -- a separate top-level window over a shell that is itself
    /// borderless and maximised reads as a stray dialog.
    ///
    /// Everything it shows comes from <see cref="WelcomeViewModel"/>; there is nothing for
    /// the code-behind to do.
    /// </summary>
    public partial class WelcomePanel : UserControl
    {
        public WelcomePanel()
        {
            InitializeComponent();
        }
    }
}
