using System.Threading.Tasks;
using System.Windows;

namespace AdbDesktop
{
    /// <summary>
    /// Marshals work onto the WPF dispatcher. ConnectionMonitor ticks and the APK/icon
    /// pipeline both run on background threads, but they mutate bound state, so every
    /// hop back to the UI goes through here.
    /// </summary>
    internal static class UiThread
    {
        public static Task RunAsync(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(action).Task;
        }

        public static async Task<T> RunAsync<T>(Func<T> function)
        {
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
                return function();

            return await dispatcher.InvokeAsync(function).Task.ConfigureAwait(false);
        }
    }
}
