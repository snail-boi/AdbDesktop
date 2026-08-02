using System;

namespace AdbDesktop
{
    public enum UpdatePromptChoice
    {
        Install,
        RemindLater,
        Ignore
    }

    /// <summary>
    /// ViewModel for UpdatePromptWindow. Holds the display text and the three actions the
    /// user can take. Each command records the Choice and asks the view to close; the view
    /// then maps that choice onto the right DialogResult (true / null / false), which the
    /// caller in Updater relies on.
    ///
    /// The text properties are set once in the constructor and never change, so they are
    /// plain get-only properties (no change notification needed).
    /// </summary>
    public sealed class UpdatePromptViewModel : ViewModelBase
    {
        // Raised when the dialog should close. The view reads Choice to set DialogResult.
        public event Action? RequestClose;

        public UpdatePromptChoice Choice { get; private set; } = UpdatePromptChoice.RemindLater;

        public string Summary { get; }
        public string Notes { get; }
        public string Prompt { get; }
        public bool AllowRemindLater { get; }

        public RelayCommand InstallCommand { get; }
        public RelayCommand RemindLaterCommand { get; }
        public RelayCommand IgnoreCommand { get; }

        public UpdatePromptViewModel(string latestVersion, string patchNotes, bool allowRemindLater)
        {
            Summary = $"A new version {latestVersion} is available.";
            Notes = string.IsNullOrWhiteSpace(patchNotes) ? "No patch notes available." : patchNotes.Trim();
            Prompt = "Do you want to download and install it now?";
            AllowRemindLater = allowRemindLater;

            InstallCommand = new RelayCommand(() => Complete(UpdatePromptChoice.Install));
            RemindLaterCommand = new RelayCommand(() => Complete(UpdatePromptChoice.RemindLater));
            IgnoreCommand = new RelayCommand(() => Complete(UpdatePromptChoice.Ignore));
        }

        private void Complete(UpdatePromptChoice choice)
        {
            Choice = choice;
            RequestClose?.Invoke();
        }
    }
}
