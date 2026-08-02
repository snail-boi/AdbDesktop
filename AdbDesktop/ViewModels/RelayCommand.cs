using System.Windows.Input;

namespace AdbDesktop
{
    /// <summary>
    /// Minimal ICommand. A button in XAML binds to a RelayCommand on the ViewModel
    /// (Command="{Binding ApplyCommand}") instead of using a Click event handler in
    /// code-behind.
    ///
    /// CanExecute is optional. When supplied, WPF disables the bound control while it
    /// returns false. CommandManager.RequerySuggested makes WPF re-check it whenever the
    /// UI does something that might have changed the answer.
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }

    /// <summary>
    /// Same as RelayCommand, but the command receives a parameter. This is what you use
    /// for a button inside a list row, where each row needs to act on its own item: the
    /// XAML passes CommandParameter="{Binding}" (the row item) and it arrives here as the
    /// typed argument.
    /// </summary>
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        private readonly Func<T?, bool>? _canExecute;

        public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;

        public void Execute(object? parameter) => _execute((T?)parameter);

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
