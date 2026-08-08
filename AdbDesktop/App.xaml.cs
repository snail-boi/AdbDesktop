using System.Windows;

namespace AdbDesktop
{
    public partial class App : Application
    {
        /// <summary>
        /// The one live config instance. Views and view models read and mutate it, then
        /// call <see cref="SaveConfig"/>.
        /// </summary>
        public static AdbDesktopConfig Config { get; private set; } = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppPaths.EnsureDataDirectories();
            Config = AdbDesktopConfigManager.Load();
            AdbHelper.AdbPath = Config.Paths.Adb;

            // Before the first show() below, so the advanced log starts from the
            // very first line rather than halfway through startup.
            Debugger.AdvancedEnabled = Config.Advanced.DebugLogging;

            Debugger.show($"[STARTUP] AdbDesktop starting. adb={AdbHelper.AdbPath}, data={AppPaths.DataRoot}");

            DispatcherUnhandledException += OnUnhandledException;
        }

        private bool _reportedFatal;
        private bool _showingFatal;

        /// <summary>
        /// Logs everything but reports at most once.
        ///
        /// An exception thrown during a layout or render pass repeats on every frame,
        /// and a MessageBox pumps messages -- which runs the next layout pass, which
        /// throws again. That turns a single bad binding into an unbounded cascade of
        /// dialogs and then a crash, so only the first one is ever shown.
        /// </summary>
        private void OnUnhandledException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
        {
            Debugger.show("[FATAL] " + args.Exception);
            args.Handled = true;

            if (_reportedFatal || _showingFatal)
                return;

            _reportedFatal = true;
            _showingFatal = true;
            try
            {
                MessageBox.Show(
                    args.Exception.Message +
                    "\n\nFurther errors will be written to the log only:\n" +
                    Debugger.LogDirectory,
                    "adbDesktop", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _showingFatal = false;
            }
        }

        public static void SaveConfig() => AdbDesktopConfigManager.Save(Config);

        protected override void OnExit(ExitEventArgs e)
        {
            SaveConfig();

            // Only the shell sessions we own are torn down. Killing the adb *server*
            // would disconnect AMPL/ASM if they are running against the same device.
            AdbHelper.AdbPath = string.Empty;

            base.OnExit(e);
        }
    }
}
