using System.Windows;

namespace AdbDesktop
{
    /// <summary>
    /// Update prompt. The text and the three actions live in
    /// <see cref="UpdatePromptViewModel"/>. The one thing the view still owns is mapping the
    /// chosen action onto a DialogResult, because the caller in Updater inspects both the
    /// Choice and the ShowDialog() return value:
    ///   Install      -> DialogResult true
    ///   Ignore       -> DialogResult false
    ///   RemindLater  -> DialogResult null (close without a true/false result)
    ///
    /// The constructor and the Choice property are unchanged, so Updater needs no edits.
    /// </summary>
    public partial class UpdatePromptWindow : Window
    {
        private readonly UpdatePromptViewModel _vm;

        public UpdatePromptChoice Choice => _vm.Choice;

        public UpdatePromptWindow(string latestVersion, string patchNotes, bool allowRemindLater)
        {
            InitializeComponent();

            _vm = new UpdatePromptViewModel(latestVersion, patchNotes, allowRemindLater);
            DataContext = _vm;
            _vm.RequestClose += OnRequestClose;

            // Release notes follow the repo's release template (headings, bullets, links,
            // @mentions), so render them formatted instead of as one plain-text block.
            NotesContent.Content = PatchNotesRenderer.Render(_vm.Notes);
        }

        private void OnRequestClose()
        {
            DialogResult = _vm.Choice switch
            {
                UpdatePromptChoice.Install => true,
                UpdatePromptChoice.Ignore => false,
                _ => (bool?)null
            };
            Close();
        }
    }
}
