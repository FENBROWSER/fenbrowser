using System;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core
{
    public static class UiThreadHelper
    {
        private static readonly object Sync = new();
        private static Threading.IUiDispatcherAdapter _adapter;

        public static void Configure(Threading.IUiDispatcherAdapter adapter)
        {
            lock (Sync)
            {
                _adapter = adapter;
            }
        }

        public static void Reset()
        {
            Configure(null);
        }

        public static async Task RunAsync(Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[UiThreadHelper] Error executing action: {ex.Message}", LogCategory.General, ex);
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
                EngineLogCompat.Error($"[UiThreadHelper] Error executing func: {ex.Message}", LogCategory.General, ex);
                return default;
            }
        }

        public static object TryGetDispatcher()
        {
            lock (Sync)
            {
                return _adapter?.Dispatcher;
            }
        }

        public static bool HasThreadAccess(object dispatcher)
        {
            if (dispatcher == null)
                return true;

            lock (Sync)
            {
                return _adapter == null || _adapter.HasThreadAccess(dispatcher);
            }
        }

        public static async Task RunAsyncAwaitable(object dispatcher, object priority, Func<Task> action)
        {
            if (action == null)
                return;

            Threading.IUiDispatcherAdapter adapter;
            lock (Sync)
            {
                adapter = _adapter;
            }

            if (dispatcher == null || adapter == null || adapter.HasThreadAccess(dispatcher))
            {
                await action().ConfigureAwait(false);
                return;
            }

            await adapter.InvokeAsync(dispatcher, priority, action).ConfigureAwait(false);
        }

        public static void RunAsync(object dispatcher, object priority, Action callback)
        {
            if (callback == null)
                return;

            Threading.IUiDispatcherAdapter adapter;
            lock (Sync)
            {
                adapter = _adapter;
            }

            if (dispatcher == null || adapter == null || adapter.HasThreadAccess(dispatcher))
            {
                callback();
                return;
            }

            adapter.Post(dispatcher, priority, callback);
        }
    }
}

namespace FenBrowser.Core.Threading
{
    public interface IUiDispatcherAdapter
    {
        object Dispatcher { get; }
        bool HasThreadAccess(object dispatcher);
        Task InvokeAsync(object dispatcher, object priority, Func<Task> action);
        void Post(object dispatcher, object priority, Action callback);
    }
}
