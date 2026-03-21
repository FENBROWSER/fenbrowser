using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Observers;

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
        private const int MaxThresholdEntries = 16;
        private const int MaxRootMarginLength = 128;

        /// <summary>
        /// Creates the IntersectionObserver constructor for registration in the global scope.
        /// </summary>
        public static FenObject CreateConstructor()
        {
            var observerCtor = new FenFunction("IntersectionObserver", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction)
                {
                    throw new InvalidOperationException("IntersectionObserver requires a callback function.");
                }

                var callback = args[0];
                var options = args.Length > 1 && args[1].IsObject ? args[1].AsObject() : null;

                if (!TryNormalizeOptions(options, out var thresholds, out var rootMargin, out var error))
                {
                    throw new InvalidOperationException(error ?? "Invalid IntersectionObserver options.");
                }

                if (!IntersectionObserverInstance.TryParseRootMargin(rootMargin, out var rootMarginOffsets, out error))
                {
                    throw new InvalidOperationException(error ?? "Invalid IntersectionObserver options.");
                }

                var instance = new IntersectionObserverInstance(callback, thresholds, rootMargin, rootMarginOffsets);
                ObserverCoordinator.Instance.RegisterIntersectionObserver(instance);

                var observerObj = new FenObject();
                observerObj.NativeObject = instance;
                instance.AttachObserverObject(observerObj);
                observerObj.Set("root", FenValue.Null);
                observerObj.Set("rootMargin", FenValue.FromString(rootMargin));
                observerObj.Set("thresholds", FenValue.FromObject(CreateThresholdArray(thresholds)));

                observerObj.Set("observe", FenValue.FromFunction(new FenFunction("observe", (obsArgs, obsThis) =>
                {
                    if (obsArgs.Length < 1 || !obsArgs[0].IsObject)
                    {
                        throw new InvalidOperationException("IntersectionObserver.observe requires a target object.");
                    }

                    instance.Observe(obsArgs[0].AsObject());
                    return FenValue.Undefined;
                })));

                observerObj.Set("unobserve", FenValue.FromFunction(new FenFunction("unobserve", (obsArgs, obsThis) =>
                {
                    if (obsArgs.Length < 1 || !obsArgs[0].IsObject)
                    {
                        throw new InvalidOperationException("IntersectionObserver.unobserve requires a target object.");
                    }

                    instance.Unobserve(obsArgs[0].AsObject());
                    return FenValue.Undefined;
                })));

                observerObj.Set("disconnect", FenValue.FromFunction(new FenFunction("disconnect", (obsArgs, obsThis) =>
                {
                    instance.Disconnect();
                    return FenValue.Undefined;
                })));

                observerObj.Set("takeRecords", FenValue.FromFunction(new FenFunction("takeRecords", (obsArgs, obsThis) =>
                {
                    return FenValue.FromObject(instance.TakeRecords());
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

        private static bool TryNormalizeOptions(IObject options, out List<double> thresholds, out string rootMargin, out string error)
        {
            thresholds = new List<double> { 0d };
            rootMargin = "0px";
            error = null;

            if (options == null)
            {
                return true;
            }

            if (!TryNormalizeThresholds(options.Get("threshold"), out thresholds, out error))
            {
                return false;
            }

            var rootMarginValue = options.Get("rootMargin");
            if (!rootMarginValue.IsUndefined && !rootMarginValue.IsNull)
            {
                var value = (rootMarginValue.ToString() ?? string.Empty).Trim();
                if (value.Length == 0)
                {
                    rootMargin = "0px";
                }
                else if (value.Length > MaxRootMarginLength || ContainsControlCharacters(value))
                {
                    error = "IntersectionObserver rootMargin is invalid.";
                    return false;
                }
                else
                {
                    rootMargin = value;
                }
            }

            return true;
        }

        private static bool TryNormalizeThresholds(FenValue thresholdValue, out List<double> thresholds, out string error)
        {
            thresholds = new List<double> { 0d };
            error = null;

            if (thresholdValue.IsUndefined || thresholdValue.IsNull)
            {
                return true;
            }

            if (thresholdValue.IsNumber)
            {
                if (!TryNormalizeThreshold(thresholdValue, out var singleThreshold, out error))
                {
                    return false;
                }

                thresholds = new List<double> { singleThreshold };
                return true;
            }

            if (!thresholdValue.IsObject)
            {
                error = "IntersectionObserver threshold must be a number or array-like object.";
                return false;
            }

            var thresholdObject = thresholdValue.AsObject();
            var count = ParseArrayLength(thresholdObject, MaxThresholdEntries + 1);
            if (count > MaxThresholdEntries)
            {
                error = $"IntersectionObserver supports at most {MaxThresholdEntries} threshold entries.";
                return false;
            }

            if (count == 0)
            {
                thresholds = new List<double> { 0d };
                return true;
            }

            var normalized = new List<double>(count);
            for (var i = 0; i < count; i++)
            {
                if (!TryNormalizeThreshold(thresholdObject.Get(i.ToString()), out var threshold, out error))
                {
                    return false;
                }

                normalized.Add(threshold);
            }

            thresholds = normalized.Distinct().OrderBy(x => x).ToList();
            return true;
        }

        private static bool TryNormalizeThreshold(FenValue thresholdValue, out double threshold, out string error)
        {
            threshold = 0d;
            error = null;

            if (!thresholdValue.IsNumber)
            {
                error = "IntersectionObserver threshold values must be numbers.";
                return false;
            }

            threshold = thresholdValue.ToNumber();
            if (double.IsNaN(threshold) || double.IsInfinity(threshold))
            {
                error = "IntersectionObserver threshold values must be finite numbers.";
                return false;
            }

            if (threshold < 0d || threshold > 1d)
            {
                error = "IntersectionObserver threshold values must be between 0 and 1.";
                return false;
            }

            return true;
        }

        private static int ParseArrayLength(IObject arrayLike, int defaultValue)
        {
            if (arrayLike == null)
            {
                return defaultValue;
            }

            var lengthValue = arrayLike.Get("length");
            if (!lengthValue.IsNumber)
            {
                return defaultValue;
            }

            var lengthNumber = lengthValue.ToNumber();
            if (double.IsNaN(lengthNumber) || lengthNumber < 0)
            {
                return 0;
            }

            if (lengthNumber > int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)Math.Floor(lengthNumber);
        }

        private static bool ContainsControlCharacters(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsControl(value[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static FenObject CreateThresholdArray(List<double> thresholds)
        {
            var array = new FenObject();
            for (var i = 0; i < thresholds.Count; i++)
            {
                array.Set(i.ToString(), FenValue.FromNumber(thresholds[i]));
            }

            array.Set("length", FenValue.FromNumber(thresholds.Count));
            return array;
        }
    }
}
