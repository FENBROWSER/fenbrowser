using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Observers;
using FenBrowser.Core.Engine; // Phase enum

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Implements the IntersectionObserver API (JavaScript interface).
    /// 
    /// IMPORTANT: Per Phase D spec Section 3.3:
    /// "IntersectionObserver MUST NOT hook into SkiaDomRenderer. This is forbidden."
    /// 
    /// All observer evaluation happens via ObserverCoordinator with LayoutResult,
    /// NOT via renderer hooks.
    /// </summary>
    public static class IntersectionObserverAPI
    {
        /// <summary>
        /// Creates the IntersectionObserver constructor for registration in the global scope.
        /// </summary>
        public static IValue CreateConstructor()
        {
            var observerCtor = new FenFunction("IntersectionObserver", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction)
                {
                    return FenValue.FromError("IntersectionObserver requires a callback function");
                }

                var callback = args[0];
                var options = args.Length > 1 && args[1].IsObject ? args[1].AsObject() : null;

                // Parse options
                double threshold = 0;
                string rootMargin = "0px";

                if (options != null)
                {
                    var thresholdVal = options.Get("threshold");
                    if (thresholdVal != null && thresholdVal.IsNumber)
                    {
                        threshold = thresholdVal.ToNumber();
                    }

                    var rootMarginVal = options.Get("rootMargin");
                    if (rootMarginVal != null && !rootMarginVal.IsUndefined)
                    {
                        rootMargin = rootMarginVal.ToString();
                    }
                }

                // Create observer instance using the spec-compliant ObserverCoordinator system
                var instance = new IntersectionObserverInstance(callback, threshold);
                
                // Register with ObserverCoordinator for proper post-layout evaluation
                ObserverCoordinator.Instance.RegisterIntersectionObserver(instance);
                
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

                // takeRecords() - returns pending entries (always empty array since we use ObserverCoordinator)
                observerObj.Set("takeRecords", FenValue.FromFunction(new FenFunction("takeRecords", (obsArgs, obsThis) =>
                {
                    // In spec-compliant architecture, records are processed via callbacks
                    // takeRecords returns an empty array as pending entries are executed synchronously
                    var entriesArray = new FenObject();
                    entriesArray.Set("length", FenValue.FromNumber(0));
                    return FenValue.FromObject(entriesArray);
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
            ObserverCoordinator.Instance.Clear();
        }
    }
}
