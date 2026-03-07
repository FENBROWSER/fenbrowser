using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Observers;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Implements the ResizeObserver API (JavaScript interface).
    ///
    /// IMPORTANT: Per Phase D spec Section 0:
    /// - No execution during Measure, Layout, or Paint
    /// - Uses immutable LayoutResult only
    /// - Dirty-flag driven (only fires on size change)
    /// - Executes only in JSExecution phase
    /// </summary>
    public static class ResizeObserverAPI
    {
        /// <summary>
        /// Creates the ResizeObserver constructor for registration in the global scope.
        /// </summary>
        public static FenObject CreateConstructor()
        {
            var observerCtor = new FenFunction("ResizeObserver", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction)
                {
                    throw new InvalidOperationException("ResizeObserver requires a callback function.");
                }

                var callback = args[0];
                var instance = new ResizeObserverInstance(callback);
                ObserverCoordinator.Instance.RegisterResizeObserver(instance);

                var observerObj = new FenObject();
                observerObj.NativeObject = instance;

                observerObj.Set("observe", FenValue.FromFunction(new FenFunction("observe", (obsArgs, obsThis) =>
                {
                    if (obsArgs.Length < 1 || !obsArgs[0].IsObject)
                    {
                        throw new InvalidOperationException("ResizeObserver.observe requires a target object.");
                    }

                    instance.Observe(obsArgs[0].AsObject());
                    return FenValue.Undefined;
                })));

                observerObj.Set("unobserve", FenValue.FromFunction(new FenFunction("unobserve", (obsArgs, obsThis) =>
                {
                    if (obsArgs.Length < 1 || !obsArgs[0].IsObject)
                    {
                        throw new InvalidOperationException("ResizeObserver.unobserve requires a target object.");
                    }

                    instance.Unobserve(obsArgs[0].AsObject());
                    return FenValue.Undefined;
                })));

                observerObj.Set("disconnect", FenValue.FromFunction(new FenFunction("disconnect", (obsArgs, obsThis) =>
                {
                    instance.Disconnect();
                    return FenValue.Undefined;
                })));

                return FenValue.FromObject(observerObj);
            })
            {
                IsConstructor = true,
                NativeLength = 1
            };

            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(observerCtor));
            observerCtor.Set("prototype", FenValue.FromObject(prototype));
            return observerCtor;
        }

        /// <summary>
        /// Clears all observers (called on page unload/navigation).
        /// </summary>
        public static void ClearAllObservers()
        {
            ObserverCoordinator.Instance.Clear();
        }
    }
}
