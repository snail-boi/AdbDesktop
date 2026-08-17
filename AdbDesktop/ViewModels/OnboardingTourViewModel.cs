using System.Collections.Generic;

namespace AdbDesktop
{
    /// <summary>Which real piece of the shell a tour step points at. None for the final card.</summary>
    public enum OnboardingTarget
    {
        None,
        AddDeviceButton,
        DeviceList,
        WirelessModeCombo,
        PairButtons,
        ConnectionCloseButton,
        DeviceArea,
        SearchButton,
        SettingsIcon
    }

    /// <summary>
    /// One stop on the tour. A view model rather than a plain record because
    /// <see cref="IsCurrent"/> has to be observable so the overlay can react to the
    /// step changing.
    /// </summary>
    public sealed class TourStep : ViewModelBase
    {
        public string Heading { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public OnboardingTarget Target { get; init; } = OnboardingTarget.None;

        /// <summary>The first step only: no Next button, waits for the real click on the target.</summary>
        public bool WaitsForRealAction { get; init; }

        /// <summary>The close-button step only: Next also fires the real close command.</summary>
        public bool ClosesConnectionOnAdvance { get; init; }

        /// <summary>The last step: a centered card with its own two buttons, no spotlight.</summary>
        public bool IsFinal { get; init; }

        private bool _isCurrent;

        public bool IsCurrent
        {
            get => _isCurrent;
            internal set => Set(ref _isCurrent, value);
        }
    }

    /// <summary>
    /// The guided tour shown the first time the app desktop opens: a spotlight walk
    /// through the add-device button, the connection panel, where devices and the
    /// Settings icon show up, and search, ending on a two-button wrap-up card.
    ///
    /// Content lives in <see cref="BuildSteps"/> rather than in the view: a list the
    /// view walks, not one hand-written block per step.
    /// </summary>
    public sealed class OnboardingTourViewModel : ViewModelBase
    {
        private readonly MainViewModel _main;
        private int _index;
        private bool _active;
        private bool _isHelpWipVisible;

        /// <summary>Raised when the tour is done with, however it was closed.</summary>
        public event Action? Finished;

        public IReadOnlyList<TourStep> Steps { get; }

        public TourStep Current => Steps[_index];

        /// <summary>Hidden on the first step (waits for the real click instead) and on the final card.</summary>
        public bool ShowNextButton => !Current.WaitsForRealAction && !Current.IsFinal;

        /// <summary>Hidden only on the final card, which has no further step to skip to.</summary>
        public bool ShowSkipButton => !Current.IsFinal;

        public bool IsHelpWipVisible
        {
            get => _isHelpWipVisible;
            private set => Set(ref _isHelpWipVisible, value);
        }

        public RelayCommand NextCommand { get; }
        public RelayCommand SkipCommand { get; }
        public RelayCommand HelpSetupCommand { get; }
        public RelayCommand CloseCommand { get; }

        public OnboardingTourViewModel(MainViewModel main)
        {
            _main = main;

            Steps = BuildSteps();

            NextCommand = new RelayCommand(Next);
            SkipCommand = new RelayCommand(() => GoTo(Steps.Count - 1));
            HelpSetupCommand = new RelayCommand(() => IsHelpWipVisible = true);
            CloseCommand = new RelayCommand(Close);

            Steps[0].IsCurrent = true;

            _main.ConnectionOpened += OnConnectionOpened;
        }

        /// <summary>
        /// Back to step one. Reopening from Settings should read as starting the tour,
        /// not as resuming wherever it was abandoned.
        /// </summary>
        public void Reset()
        {
            _active = true;
            IsHelpWipVisible = false;
            GoTo(0);
        }

        private void Next()
        {
            if (Current.ClosesConnectionOnAdvance)
                _main.CloseConnectionCommand.Execute(null);

            if (Current.IsFinal)
                Close();
            else
                GoTo(_index + 1);
        }

        private void Close()
        {
            _active = false;
            Finished?.Invoke();
        }

        /// <summary>
        /// The first step waits for the user to actually click the real add-device
        /// button rather than a Next button; this is how that click reaches the tour.
        /// Guarded so a connection panel opened for an unrelated reason, on any other
        /// step, does not skip the tour forward.
        /// </summary>
        private void OnConnectionOpened()
        {
            if (_active && _index == 0 && Current.WaitsForRealAction)
                GoTo(1);
        }

        private void GoTo(int index)
        {
            if (index < 0 || index >= Steps.Count || index == _index)
                return;

            Steps[_index].IsCurrent = false;
            _index = index;
            Steps[_index].IsCurrent = true;

            RaisePropertyChanged(nameof(Current));
            RaisePropertyChanged(nameof(ShowNextButton));
            RaisePropertyChanged(nameof(ShowSkipButton));
        }

        // ---------- content ----------

        private static IReadOnlyList<TourStep> BuildSteps() => new[]
        {
            new TourStep
            {
                Target = OnboardingTarget.AddDeviceButton,
                WaitsForRealAction = true,
                Heading = "Add or pair a device",
                Body = "this button shows you all devices which are currently paired and a way to pair new ones"
                     + ", or skip the tour if you already know your way around."
            },

            new TourStep
            {
                Target = OnboardingTarget.DeviceList,
                Heading = "Connection menu",
                Body = "Every paired device is listed here. To have it be useable, press add, or use auto add to make it add itself on startup "
                     + "each device has it's own desktop, battery indicator and audio link"
            },

            new TourStep
            {
                Target = OnboardingTarget.WirelessModeCombo,
                Heading = "Modes",
                Body = "Wireless debugging is the default way to connect over Wi-Fi it's the modern secure choice."
                     + "If you wish to use USB only then switch to USB mode"
                     + "Certain networks may block Wireless debugging, if you trust the network then pair using the TCP/IP method"
                     + ", but this is not as secure as the default method."
            },

            new TourStep
            {
                Target = OnboardingTarget.PairButtons,
                Heading = "Pairing a device",
                Body = "Pairing over Wi-Fi using Wireless debugging presents you with a qr code, scan it from the developer settings to pair"
                     + "If your device is not capable of pairing via qr code, then use the pairing code in developer settings"
                     + "if you must use TCP/IP (which is not as secure as the default method), then plug in USB cable, and press the enable TCP/IP button"
                     + "you can also use USB if you wish"
            },

            new TourStep
            {
                Target = OnboardingTarget.ConnectionCloseButton,
                ClosesConnectionOnAdvance = true,
                Heading = "Closing the panel",
                Body = "This closes the connection panel."
            },

            new TourStep
            {
                Target = OnboardingTarget.DeviceArea,
                Heading = "Added devices",
                Body = "Devices you add appear here. Add more than one and each gets its "
                     + "own numbered desktop in the taskbar, together with a single unified desktop which apps share,"
                     + "you can turn off this behavior in Settings to keep only the unified desktop."
                     + "here you can also see the battery level, notifications and audio link activity."
            },

            new TourStep
            {
                Target = OnboardingTarget.SearchButton,
                Heading = "Finding an app",
                Body = "Type an app's name and look for the correct package/name, then pick a result to add it as "
                     + "an icon. Nothing gets installed, we just need to pull the app to find its icon"
            },

            new TourStep
            {
                Target = OnboardingTarget.SettingsIcon,
                Heading = "Settings",
                Body = "This opens settings: mirroring options, taskbar "
                     + "behaviour, and everything else not covered here."
            },

            new TourStep
            {
                IsFinal = true,
                Heading = "You're set",
                Body = "Connect a phone, add it to the desktop, and search for an app "
                     + "whenever you're ready."
            },
        };
    }
}
