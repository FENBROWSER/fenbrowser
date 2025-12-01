using System;
using System.Threading.Tasks;
using Avalonia.Threading;
// Windows UI usings are intentionally omitted here to keep this core library platform independent.

namespace FenBrowser.Core
{
    public static class UiThreadHelper
    {
        public static async Task RunAsync(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(action);
            }
        }

        public static async Task<T> RunAsync<T>(Func<T> func)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                return func();
            }
            else
            {
                return await Dispatcher.UIThread.InvokeAsync(func);
            }
        }

        public static Dispatcher TryGetDispatcher()
        {
            return Dispatcher.UIThread;
        }
        public static bool HasThreadAccess(object dispatcher)
        {
            if (dispatcher == null) return false;
            try
            {
                if (dispatcher is Dispatcher avaloniaDisp)
                    return avaloniaDisp.CheckAccess();
                // If the dispatcher is a Windows CoreDispatcher, try reflectively checking HasThreadAccess
                var t = dispatcher.GetType();
                if (t.FullName == "Windows.UI.Core.CoreDispatcher")
                {
                    var prop = t.GetProperty("HasThreadAccess");
                    if (prop != null)
                    {
                        var val = prop.GetValue(dispatcher);
                        if (val is bool b) return b;
                    }
                }
            }
            catch { }
            return false;
        }
        public static async Task RunAsyncAwaitable(Dispatcher dispatcher, object priority, Func<Task> action)
        {
            // Avalonia doesn't use priority parameter like UWP, so we ignore it
            if (dispatcher == null)
            {
                await action();
                return;
            }

            if (dispatcher.CheckAccess())
            {
                await action();
            }
            else
            {
                await dispatcher.InvokeAsync(action);
            }
        }

        // Generic overload that accepts either Avalonia.Dispatcher or Windows.CoreDispatcher
        public static async Task RunAsyncAwaitable(object dispatcher, object priority, Func<Task> action)
        {
            if (dispatcher == null)
            {
                await action();
                return;
            }
            // Detect Windows.Core.CoreDispatcher by name to avoid referencing Windows namespaces in core project.
            var dispatchType = dispatcher.GetType();
            if (dispatchType.FullName == "Windows.UI.Core.CoreDispatcher")
            {
                try
                {
                    // Check HasThreadAccess reflectively
                    var hasAccessProp = dispatchType.GetProperty("HasThreadAccess");
                    var hasAccess = hasAccessProp?.GetValue(dispatcher) as bool? ?? false;
                    if (hasAccess)
                    {
                        await action();
                        return;
                    }

                    // Try to find RunAsync method and invoke it with a callback that runs the task synchronously
                    var runAsyncMethod = dispatchType.GetMethod("RunAsync", new Type[] { typeof(object), typeof(object) })
                                        ?? dispatchType.GetMethod("RunAsync");
                    if (runAsyncMethod != null)
                    {
                        Action cb = () => { try { action().GetAwaiter().GetResult(); } catch { } };
                        var dispatchedHandlerType = dispatchType.Assembly.GetType("Windows.UI.Core.DispatchedHandler");
                        Delegate del = null;
                        if (dispatchedHandlerType != null)
                        {
                            del = Delegate.CreateDelegate(dispatchedHandlerType, cb.Target, cb.Method);
                        }
                        else
                        {
                            del = cb;
                        }
                        try
                        {
                            runAsyncMethod.Invoke(dispatcher, new object[] { priority ?? 0, del });
                        }
                        catch { }
                        return;
                    }
                }
                catch { }
            }
            if (dispatcher is Dispatcher avalonia)
            {
                await RunAsyncAwaitable(avalonia, priority, action);
                return;
            }
            // If the runtime type is unknown, just run the action
            await action();
        }

        // Generic RunAsync for any dispatcher types (Avalonia or Windows CoreDispatcher) without awaiting
        public static void RunAsync(object dispatcher, object priority, Action callback)
        {
            if (dispatcher == null || callback == null) return;
            try
            {
                if (dispatcher is Dispatcher avalonia)
                {
                    if (avalonia.CheckAccess()) callback();
                    else avalonia.InvokeAsync(callback);
                    return;
                }

                var dispatchType = dispatcher.GetType();
                if (dispatchType.FullName == "Windows.UI.Core.CoreDispatcher")
                {
                    try
                    {
                        var runAsyncMethod = dispatchType.GetMethod("RunAsync");
                        if (runAsyncMethod != null)
                        {
                            Action cb = () => { try { callback(); } catch { } };
                            var dispatchedHandlerType = dispatchType.Assembly.GetType("Windows.UI.Core.DispatchedHandler");
                            Delegate del = null;
                            if (dispatchedHandlerType != null)
                            {
                                del = Delegate.CreateDelegate(dispatchedHandlerType, cb.Target, cb.Method);
                            }
                            else
                            {
                                del = cb;
                            }
                            runAsyncMethod.Invoke(dispatcher, new object[] { priority ?? 0, del });
                        }
                    }
                    catch { }
                    return;
                }
            }
            catch { }
            // Unknown dispatcher: run synchronously as a fallback
            try { callback(); } catch { }
        }
    }
}

namespace FenBrowser.Core.Threading
{
    // Empty namespace for compatibility
}
