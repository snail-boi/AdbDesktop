using System.Collections.Generic;

namespace AdbDesktop
{
    /// <summary>One labelled paragraph on a welcome page.</summary>
    public sealed class WelcomePoint
    {
        public string Heading { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
    }

    /// <summary>
    /// One page of the guide. A view model rather than a plain record only because of
    /// <see cref="IsCurrent"/>: the dots in the progress strip are bound to the pages
    /// themselves, so each one has to be able to say when it is the page on screen.
    /// </summary>
    public sealed class WelcomeStep : ViewModelBase
    {
        private bool _isCurrent;

        public string Title { get; init; } = string.Empty;
        public string Lead { get; init; } = string.Empty;
        public IReadOnlyList<WelcomePoint> Points { get; init; } = Array.Empty<WelcomePoint>();

        /// <summary>The aside under the points. Empty leaves it out.</summary>
        public string Footnote { get; init; } = string.Empty;

        public bool HasFootnote => !string.IsNullOrEmpty(Footnote);

        public bool IsCurrent
        {
            get => _isCurrent;
            internal set => Set(ref _isCurrent, value);
        }
    }

    /// <summary>
    /// The first-run guide: what adbDesktop is, how to get a phone talking to it, how a
    /// device is added, and what "adding an app" does -- which is the one that most needs
    /// saying, because it installs nothing anywhere.
    ///
    /// Content lives here rather than in the XAML so the pages are a list the view walks,
    /// not four hand-written blocks with visibility triggers.
    /// </summary>
    public sealed class WelcomeViewModel : ViewModelBase
    {
        private int _index;

        /// <summary>Raised when the guide is done with, however it was closed.</summary>
        public event Action? Finished;

        public IReadOnlyList<WelcomeStep> Steps { get; }

        public RelayCommand NextCommand { get; }
        public RelayCommand BackCommand { get; }
        public RelayCommand SkipCommand { get; }

        public WelcomeViewModel()
        {
            Steps = BuildSteps();

            NextCommand = new RelayCommand(Next);
            BackCommand = new RelayCommand(() => GoTo(_index - 1), () => !IsFirst);
            SkipCommand = new RelayCommand(() => Finished?.Invoke());

            Steps[0].IsCurrent = true;
        }

        // ---------- paging ----------

        public WelcomeStep Current => Steps[_index];

        public bool IsFirst => _index == 0;
        public bool IsLast => _index == Steps.Count - 1;

        /// <summary>The last page finishes rather than advancing, so the button says so.</summary>
        public string NextText => IsLast ? "Get started" : "Next";

        public string ProgressText => $"{_index + 1} of {Steps.Count}";

        /// <summary>
        /// Back to page one. Reopening from Settings should read as opening the guide, not
        /// as resuming wherever it was abandoned three months ago.
        /// </summary>
        public void Reset() => GoTo(0);

        private void Next()
        {
            if (IsLast)
                Finished?.Invoke();
            else
                GoTo(_index + 1);
        }

        private void GoTo(int index)
        {
            if (index < 0 || index >= Steps.Count || index == _index)
                return;

            Steps[_index].IsCurrent = false;
            _index = index;
            Steps[_index].IsCurrent = true;

            RaisePropertyChanged(nameof(Current));
            RaisePropertyChanged(nameof(IsFirst));
            RaisePropertyChanged(nameof(IsLast));
            RaisePropertyChanged(nameof(NextText));
            RaisePropertyChanged(nameof(ProgressText));
        }

        // ---------- content ----------

        private static IReadOnlyList<WelcomeStep> BuildSteps() => new[]
        {
            new WelcomeStep
            {
                Title = "Welcome to adbDesktop",
                Lead = "Your phone's apps, opened as real windows on this PC. The phone runs "
                     + "them; this PC only shows them. Nothing here is an emulator, and nothing "
                     + "is copied onto your phone.",
                Points = new[]
                {
                    new WelcomePoint
                    {
                        Heading = "Three things to do",
                        Text = "Connect the phone, add it to the desktop, then put an app on it. "
                             + "The next three pages are those three things."
                    },
                    new WelcomePoint
                    {
                        Heading = "Nothing to install first",
                        Text = "adb ships inside adbDesktop, so there is nothing to download or "
                             + "set up on this PC. It reaches the phone over the USB cable or "
                             + "over your Wi-Fi network."
                    },
                },
                Footnote = "Windows need Android 11 or newer that is where Android's virtual "
                         + "displays arrived. An older phone still connects; its apps just cannot "
                         + "be given a window of their own."
            },

            new WelcomeStep
            {
                Title = "Connect your phone",
                Lead = "On the phone first: Settings → About phone, tap Build number seven "
                     + "times, then turn on USB debugging in Developer options. Everything below "
                     + "depends on that being on.",
                Points = new[]
                {
                    new WelcomePoint
                    {
                        Heading = "Over USB",
                        Text = "Plug the cable in and tap Allow on the phone's “Allow USB "
                             + "debugging?” prompt. Tick “Always allow from this "
                             + "computer” so it stops asking. That is the whole of it."
                    },
                    new WelcomePoint
                    {
                        Heading = "Over Wi-Fi (Wireless debugging, Android 11+)",
                        Text = "Turn on Developer options → Wireless debugging on the phone. "
                             + "In adbDesktop, press the phone-with-a-plus button at the right-hand "
                             + "end of the taskbar and choose “Pair over Wi-Fi”: either "
                             + "scan the QR code with “Pair device with QR code”, or type "
                             + "in the code from “Pair device with pairing code”. Phone "
                             + "and PC have to be on the same network."
                    },
                    new WelcomePoint
                    {
                        Heading = "Older phones: TCP/IP",
                        Text = "Below Android 11 there is no wireless debugging. Plug in over USB "
                             + "once and press “Enable TCP/IP from USB” in the same "
                             + "panel; the phone stays reachable over the network after the cable "
                             + "comes out."
                    },
                },
                Footnote = "Pairing has its own port, and it is not the connection port, the "
                         + "phone shows both, so use the one under the pairing dialog. Pair once "
                         + "and adbDesktop remembers the phone, even though the port changes every "
                         + "time wireless debugging is toggled."
            },

            new WelcomeStep
            {
                Title = "Add the device",
                Lead = "Connected is not the same as added. adb seeing your phone does not give it "
                     + "a desktop, that is a decision you make, in the same panel behind the "
                     + "phone-with-a-plus button.",
                Points = new[]
                {
                    new WelcomePoint
                    {
                        Heading = "Add to desktop",
                        Text = "Every device adb can see is listed there. Press “Add to "
                             + "desktop” on the one you want and it joins the taskbar, with "
                             + "its battery, its audio link and a desktop of its own."
                    },
                    new WelcomePoint
                    {
                        Heading = "Auto-add",
                        Text = "Tick this and the device puts itself back whenever it comes online, "
                             + "including after restarting adbDesktop. Without it, adding lasts "
                             + "until the device drops or the app closes, and has to be done again."
                    },
                    new WelcomePoint
                    {
                        Heading = "Remove",
                        Text = "Takes the device off the taskbar and clears its auto-add. Its icons "
                             + "are kept, so adding it again brings its desktop back exactly as it "
                             + "was."
                    },
                },
                Footnote = "Several phones can be added at once. Each gets its own numbered desktop "
                         + "in the taskbar, and the U tab is the unified desktop, where icons from "
                         + "all of them sit together."
            },

            new WelcomeStep
            {
                Title = "Adding an app installs nothing",
                Lead = "Putting an app on the desktop does not install it, not on the phone, not "
                     + "on this PC. The app is already on your phone. What adbDesktop makes is a "
                     + "shortcut to it.",
                Points = new[]
                {
                    new WelcomePoint
                    {
                        Heading = "How to add one",
                        Text = "Use the search bar in the middle of the taskbar, or press Ctrl+K. "
                             + "Type a name, press Enter, and confirm. The list is whatever is "
                             + "installed on your added devices."
                    },
                    new WelcomePoint
                    {
                        Heading = "What actually happens",
                        Text = "adbDesktop copies the app's APK off the phone into a scratch "
                             + "folder, digs the launcher icons out of it so you can pick one, then "
                             + "deletes the copy. The phone is only ever read from; nothing is "
                             + "written to it and nothing is left on this PC but the icon."
                    },
                    new WelcomePoint
                    {
                        Heading = "Opening an icon",
                        Text = "Launches the app on the phone, on a display of its own, and streams "
                             + "that display into a window here. The app is running on the phone the "
                             + "entire time, the window is a live view of it, not a copy running "
                             + "on your PC."
                    },
                },
                Footnote = "Right-click an icon to rename it, swap in your own image or remove it. "
                         + "Removing an icon is just that: the app stays on the phone, untouched."
            },
        };
    }
}
