using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AdbDesktop
{
    /// <summary>
    /// Base class for every ViewModel. It implements INotifyPropertyChanged, which is
    /// the contract WPF data binding listens to: when a bound property changes, the VM
    /// raises PropertyChanged, and any control bound to that property refreshes itself.
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Standard setter helper. Assigns the new value only if it actually differs,
        /// raises PropertyChanged when it does, and returns true on change so the caller
        /// can run extra logic (like refreshing a derived property).
        /// </summary>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }
    }
}
