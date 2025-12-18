using System;
using System.Threading.Tasks;

namespace FenBrowser.Core
{
    public static class UiThreadHelper
    {
        // [MIGRATION] Avalonia Dispatcher removed. 
        // This class now executes actions immediately or on ThreadPool.
        // FenBrowser.Host must provide a synchronization context if needed.

        public static async Task RunAsync(Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UiThreadHelper] Error: {ex}");
            }
            await Task.CompletedTask;
        }

        public static async Task<T> RunAsync<T>(Func<T> func)
        {
            try
            {
                return func != null ? func() : default;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UiThreadHelper] Error: {ex}");
                return default;
            }
        }

        public static object TryGetDispatcher()
        {
            return null;
        }

        public static bool HasThreadAccess(object dispatcher)
        {
            return true; // Assume access for now in console/test mode
        }

        public static async Task RunAsyncAwaitable(object dispatcher, object priority, Func<Task> action)
        {
            if (action != null) await action();
        }

        public static void RunAsync(object dispatcher, object priority, Action callback)
        {
            callback?.Invoke();
        }
    }
}

namespace FenBrowser.Core.Threading
{
    // Empty namespace for compatibility
}
