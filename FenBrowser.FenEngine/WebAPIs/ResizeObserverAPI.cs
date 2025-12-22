using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Observers;
using FenBrowser.Core.Engine; // Phase enum

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
        public static IValue CreateConstructor()
        {
            var observerCtor = new FenFunction("ResizeObserver", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction)
                {
                    return new ErrorValue("ResizeObserver requires a callback function");
                }

                var callback = args[0];

                // Create observer instance using the spec-compliant ObserverCoordinator system
                var instance = new ResizeObserverInstance(callback);
                
                // Register with ObserverCoordinator for proper post-layout evaluation
                ObserverCoordinator.Instance.RegisterResizeObserver(instance);
                
                // Create JavaScript-facing object
                var observerObj = new FenObject();
                observerObj.NativeObject = instance;
                
                // observe(target) - start observing a target element
                observerObj.Set("observe", FenValue.FromFunction(new FenFunction("observe", (obsArgs, obsThis) =>
                {
                    if (obsArgs.Length > 0 && obsArgs[0].IsObject)
                    {
                        var target = obsArgs[0].AsObject();
                        instance.Observe(target);
                    }
                    return FenValue.Undefined;
                })));

                // unobserve(target) - stop observing a specific target
                observerObj.Set("unobserve", FenValue.FromFunction(new FenFunction("unobserve", (obsArgs, obsThis) =>
                {
                    if (obsArgs.Length > 0 && obsArgs[0].IsObject)
                    {
                        var target = obsArgs[0].AsObject();
                        instance.Unobserve(target);
                    }
                    return FenValue.Undefined;
                })));

                // disconnect() - stop observing all targets
                observerObj.Set("disconnect", FenValue.FromFunction(new FenFunction("disconnect", (obsArgs, obsThis) =>
                {
                    instance.Disconnect();
                    return FenValue.Undefined;
                })));

                return FenValue.FromObject(observerObj);
            });

            return FenValue.FromFunction(observerCtor);
        }

        /// <summary>
        /// Clears all observers (called on page unload/navigation).
        /// </summary>
        public static void ClearAllObservers()
        {
            // ResizeObservers are cleared via ObserverCoordinator.Clear()
        }
    }
}
