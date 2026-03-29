using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.DOM;
using FenBrowser.Core.Engine; // Phase enum

namespace FenBrowser.FenEngine.Observers
{
    /// <summary>
    /// Coordinates observer callbacks after layout completes.
    /// This is the ONLY place where IntersectionObserver and ResizeObserver evaluate geometry.
    /// 
    /// Data Flow (per spec):
    /// Host (scroll/resize) → Engine invalidation → Layout → LayoutResult → 
    /// ObserverCoordinator evaluation → Callbacks enqueued → JS Execution Window
    /// 
    /// Execution Order (per spec Section 6):
    /// 1. IntersectionObserver (position-based)
    /// 2. ResizeObserver (size-based)
    /// </summary>
    public class ObserverCoordinator
    {
        private readonly List<IntersectionObserverInstance> _intersectionObservers = new List<IntersectionObserverInstance>();
        private readonly List<ResizeObserverInstance> _resizeObservers = new List<ResizeObserverInstance>();
        private readonly List<Action> _pendingCallbacks = new List<Action>();
        private long _lastLayoutId = -1;
        private float _lastScrollY = -1;

        private static ObserverCoordinator _instance;
        public static ObserverCoordinator Instance => _instance ??= new ObserverCoordinator();

        #region IntersectionObserver Registration

        /// <summary>
        /// Register an IntersectionObserver instance.
        /// </summary>
        public void RegisterIntersectionObserver(IntersectionObserverInstance observer)
        {
            lock (_intersectionObservers)
            {
                if (!_intersectionObservers.Contains(observer))
                {
                    _intersectionObservers.Add(observer);
                }
            }
        }

        /// <summary>
        /// Unregister an IntersectionObserver instance.
        /// </summary>
        public void UnregisterIntersectionObserver(IntersectionObserverInstance observer)
        {
            lock (_intersectionObservers)
            {
                _intersectionObservers.Remove(observer);
            }
        }

        #endregion

        #region ResizeObserver Registration

        /// <summary>
        /// Register a ResizeObserver instance.
        /// </summary>
        public void RegisterResizeObserver(ResizeObserverInstance observer)
        {
            lock (_resizeObservers)
            {
                if (!_resizeObservers.Contains(observer))
                {
                    _resizeObservers.Add(observer);
                }
            }
        }

        /// <summary>
        /// Unregister a ResizeObserver instance.
        /// </summary>
        public void UnregisterResizeObserver(ResizeObserverInstance observer)
        {
            lock (_resizeObservers)
            {
                _resizeObservers.Remove(observer);
            }
        }

        #endregion

        /// <summary>
        /// Called AFTER layout completes, BEFORE paint.
        /// This evaluates all observers against the immutable LayoutResult.
        /// </summary>
        /// <param name="result">Immutable geometry snapshot from layout.</param>
        /// <param name="elementLookup">Function to resolve Element from JS wrapper.</param>
        public void OnLayoutComplete(
            LayoutResult result,
            Func<IObject, Element> elementLookup)
        {
            if (result == null) return;

            // Dirty-flag check: only evaluate if geometry actually changed
            bool layoutChanged = result.LayoutId != _lastLayoutId;
            bool scrollChanged = Math.Abs(result.ScrollOffsetY - _lastScrollY) > 0.5f;

            if (!layoutChanged && !scrollChanged)
            {
                return; // No changes, skip evaluation
            }

            _lastLayoutId = result.LayoutId;
            _lastScrollY = result.ScrollOffsetY;

            // Per spec Section 6: Execution order matters
            // 1. IntersectionObserver (position-based)
            EvaluateIntersectionObservers(result, elementLookup);
            
            // 2. ResizeObserver (size-based)
            EvaluateResizeObservers(result, elementLookup);
        }

        private void EvaluateIntersectionObservers(
            LayoutResult result,
            Func<IObject, Element> elementLookup)
        {
            List<IntersectionObserverInstance> observers;
            lock (_intersectionObservers)
            {
                observers = new List<IntersectionObserverInstance>(_intersectionObservers);
            }

            var viewport = result.GetVisibleViewport();

            foreach (var observer in observers)
            {
                observer.EvaluateWithLayoutResult(result, viewport, elementLookup);
            }
        }

        private void EvaluateResizeObservers(
            LayoutResult result,
            Func<IObject, Element> elementLookup)
        {
            List<ResizeObserverInstance> observers;
            lock (_resizeObservers)
            {
                observers = new List<ResizeObserverInstance>(_resizeObservers);
            }

            foreach (var observer in observers)
            {
                observer.EvaluateWithLayoutResult(result, elementLookup);
            }
        }

        /// <summary>
        /// Execute all pending observer callbacks in the JS Execution Window.
        /// Called by the engine after entering JSExecution phase.
        /// </summary>
        public void ExecutePendingCallbacks(IExecutionContext context)
        {
            var currentPhase = EngineContext.Current.CurrentPhase;
            if (currentPhase == EnginePhase.Measure || currentPhase == EnginePhase.Layout || currentPhase == EnginePhase.Paint)
            {
                using var phaseScope = EngineContext.Current.PushPhase(EnginePhase.JSExecution);
                ExecutePendingCallbacksCore();
                return;
            }

            ExecutePendingCallbacksCore();
        }

        private void ExecutePendingCallbacksCore()
        {
            List<Action> callbacks;
            lock (_pendingCallbacks)
            {
                callbacks = new List<Action>(_pendingCallbacks);
                _pendingCallbacks.Clear();
            }

            foreach (var callback in callbacks)
            {
                try
                {
                    callback();
                }
                catch
                {
                    // Callback errors should not break the observer system
                }
            }
        }

        /// <summary>
        /// Enqueue a callback for execution in the JS Execution Window.
        /// </summary>
        internal void EnqueueCallback(Action callback)
        {
            lock (_pendingCallbacks)
            {
                _pendingCallbacks.Add(callback);
            }
        }

        /// <summary>
        /// Clear all observers and callbacks (called on navigation).
        /// </summary>
        public void Clear()
        {
            lock (_intersectionObservers)
            {
                _intersectionObservers.Clear();
            }
            lock (_resizeObservers)
            {
                _resizeObservers.Clear();
            }
            lock (_pendingCallbacks)
            {
                _pendingCallbacks.Clear();
            }
            _lastLayoutId = -1;
            _lastScrollY = -1;
        }
    }

    /// <summary>
    /// Instance of an IntersectionObserver with spec-compliant evaluation.
    /// </summary>
    public class IntersectionObserverInstance
    {
        public readonly struct RootMarginOffsets
        {
            public RootMarginOffsets(RootMarginValue top, RootMarginValue right, RootMarginValue bottom, RootMarginValue left)
            {
                Top = top;
                Right = right;
                Bottom = bottom;
                Left = left;
            }

            public RootMarginValue Top { get; }
            public RootMarginValue Right { get; }
            public RootMarginValue Bottom { get; }
            public RootMarginValue Left { get; }
        }

        public readonly struct RootMarginValue
        {
            public RootMarginValue(float value, bool isPercent)
            {
                Value = value;
                IsPercent = isPercent;
            }

            public float Value { get; }
            public bool IsPercent { get; }

            public float Resolve(float referenceLength)
            {
                return IsPercent ? referenceLength * (Value / 100f) : Value;
            }
        }

        private sealed class ObservedTargetState
        {
            public bool HasSnapshot { get; set; }
            public bool IsIntersecting { get; set; }
            public double Ratio { get; set; }
            public int ThresholdIndex { get; set; } = -1;
        }

        private readonly IValue _callback;
        private readonly List<double> _thresholds;
        private readonly string _rootMargin;
        private readonly RootMarginOffsets _rootMarginOffsets;
        private readonly List<IObject> _observedTargets = new List<IObject>();
        private readonly Dictionary<IObject, ObservedTargetState> _targetStates = new Dictionary<IObject, ObservedTargetState>();
        private readonly List<FenObject> _queuedEntries = new List<FenObject>();
        private readonly object _queueLock = new object();
        private FenObject _observerObject;
        private bool _callbackQueued;

        public IntersectionObserverInstance(IValue callback, double threshold)
            : this(callback, new[] { threshold }, "0px", new RootMarginOffsets(
                new RootMarginValue(0, false),
                new RootMarginValue(0, false),
                new RootMarginValue(0, false),
                new RootMarginValue(0, false)))
        {
        }

        public IntersectionObserverInstance(IValue callback, IEnumerable<double> thresholds, string rootMargin, RootMarginOffsets rootMarginOffsets)
        {
            _callback = callback;
            _thresholds = NormalizeThresholds(thresholds);
            _rootMargin = string.IsNullOrWhiteSpace(rootMargin) ? "0px" : rootMargin.Trim();
            _rootMarginOffsets = rootMarginOffsets;
        }

        public void AttachObserverObject(FenObject observerObject)
        {
            _observerObject = observerObject;
        }

        public void Observe(IObject target)
        {
            lock (_observedTargets)
            {
                if (!_observedTargets.Contains(target))
                {
                    _observedTargets.Add(target);
                    _targetStates[target] = new ObservedTargetState();
                }
            }
        }

        public void Unobserve(IObject target)
        {
            lock (_observedTargets)
            {
                _observedTargets.Remove(target);
                _targetStates.Remove(target);
            }
        }

        public void Disconnect()
        {
            lock (_observedTargets)
            {
                _observedTargets.Clear();
                _targetStates.Clear();
            }
            lock (_queueLock)
            {
                _queuedEntries.Clear();
                _callbackQueued = false;
            }
            ObserverCoordinator.Instance.UnregisterIntersectionObserver(this);
        }

        public FenObject TakeRecords()
        {
            var entriesArray = FenObject.CreateArray();
            lock (_queueLock)
            {
                for (var i = 0; i < _queuedEntries.Count; i++)
                {
                    entriesArray.Set(i.ToString(), FenValue.FromObject(_queuedEntries[i]));
                }

                entriesArray.Set("length", FenValue.FromNumber(_queuedEntries.Count));
                _queuedEntries.Clear();
                _callbackQueued = false;
            }

            return entriesArray;
        }

        /// <summary>
        /// Evaluate intersections using immutable LayoutResult (never live renderer state).
        /// </summary>
        public void EvaluateWithLayoutResult(
            LayoutResult result,
            ElementGeometry viewport,
            Func<IObject, Element> elementLookup)
        {
            if (result == null || elementLookup == null) return;

            var entries = new List<FenObject>();

            IObject[] targets;
            lock (_observedTargets)
            {
                targets = _observedTargets.ToArray();
            }

            var effectiveRootBounds = ApplyRootMargin(viewport);

            foreach (var jsTarget in targets)
            {
                var element = elementLookup(jsTarget);
                if (element == null) continue;

                if (!result.TryGetElementRect(element, out var targetRect)) continue;

                var targetState = _targetStates.TryGetValue(jsTarget, out var existingState)
                    ? existingState
                    : (_targetStates[jsTarget] = new ObservedTargetState());

                float intersectX = Math.Max(effectiveRootBounds.X, targetRect.X);
                float intersectY = Math.Max(effectiveRootBounds.Y, targetRect.Y);
                float intersectRight = Math.Min(effectiveRootBounds.Right, targetRect.Right);
                float intersectBottom = Math.Min(effectiveRootBounds.Bottom, targetRect.Bottom);

                float intersectWidth = Math.Max(0, intersectRight - intersectX);
                float intersectHeight = Math.Max(0, intersectBottom - intersectY);
                float intersectArea = intersectWidth * intersectHeight;

                float targetArea = targetRect.Width * targetRect.Height;
                double ratio = targetArea > 0 ? intersectArea / targetArea : 0;
                bool isIntersecting = intersectArea > 0;
                var thresholdIndex = GetThresholdIndex(ratio, isIntersecting);

                if (ShouldQueueEntry(targetState, ratio, isIntersecting, thresholdIndex))
                {
                    targetState.HasSnapshot = true;
                    targetState.IsIntersecting = isIntersecting;
                    targetState.Ratio = ratio;
                    targetState.ThresholdIndex = thresholdIndex;

                    var entry = CreateEntry(
                        jsTarget,
                        isIntersecting,
                        ratio,
                        targetRect,
                        effectiveRootBounds,
                        intersectX,
                        intersectY,
                        intersectWidth,
                        intersectHeight);
                    entries.Add(entry);
                }
            }

            if (entries.Count > 0)
            {
                EnqueueEntries(entries);
            }
        }

        public static bool TryParseRootMargin(string rawValue, out RootMarginOffsets offsets, out string error)
        {
            offsets = default;
            error = null;

            var value = string.IsNullOrWhiteSpace(rawValue) ? "0px" : rawValue.Trim();
            var parts = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || parts.Length > 4)
            {
                error = "IntersectionObserver rootMargin is invalid.";
                return false;
            }

            var parsedValues = new RootMarginValue[4];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!TryParseRootMarginValue(parts[i], out parsedValues[i]))
                {
                    error = "IntersectionObserver rootMargin is invalid.";
                    return false;
                }
            }

            var top = parsedValues[0];
            var right = parts.Length switch
            {
                1 => parsedValues[0],
                2 => parsedValues[1],
                3 => parsedValues[1],
                _ => parsedValues[1]
            };
            var bottom = parts.Length switch
            {
                1 => parsedValues[0],
                2 => parsedValues[0],
                3 => parsedValues[2],
                _ => parsedValues[2]
            };
            var left = parts.Length switch
            {
                1 => parsedValues[0],
                2 => parsedValues[1],
                3 => parsedValues[1],
                _ => parsedValues[3]
            };

            offsets = new RootMarginOffsets(top, right, bottom, left);
            return true;
        }

        private static bool TryParseRootMarginValue(string token, out RootMarginValue value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            token = token.Trim();
            bool isPercent;
            string numericPart;
            if (token.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                isPercent = false;
                numericPart = token.Substring(0, token.Length - 2);
            }
            else if (token.EndsWith("%", StringComparison.Ordinal))
            {
                isPercent = true;
                numericPart = token.Substring(0, token.Length - 1);
            }
            else
            {
                return false;
            }

            if (!float.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue))
            {
                return false;
            }

            value = new RootMarginValue(numericValue, isPercent);
            return true;
        }

        private static List<double> NormalizeThresholds(IEnumerable<double> thresholds)
        {
            var normalized = new SortedSet<double>();
            if (thresholds != null)
            {
                foreach (var threshold in thresholds)
                {
                    var clamped = Math.Max(0d, Math.Min(1d, threshold));
                    normalized.Add(clamped);
                }
            }

            if (normalized.Count == 0)
            {
                normalized.Add(0d);
            }

            return new List<double>(normalized);
        }

        private ElementGeometry ApplyRootMargin(ElementGeometry viewport)
        {
            var topMargin = _rootMarginOffsets.Top.Resolve(viewport.Height);
            var rightMargin = _rootMarginOffsets.Right.Resolve(viewport.Width);
            var bottomMargin = _rootMarginOffsets.Bottom.Resolve(viewport.Height);
            var leftMargin = _rootMarginOffsets.Left.Resolve(viewport.Width);

            return new ElementGeometry(
                viewport.X - leftMargin,
                viewport.Y - topMargin,
                viewport.Width + leftMargin + rightMargin,
                viewport.Height + topMargin + bottomMargin);
        }

        private bool ShouldQueueEntry(ObservedTargetState state, double ratio, bool isIntersecting, int thresholdIndex)
        {
            if (state == null || !state.HasSnapshot)
            {
                return true;
            }

            if (state.IsIntersecting != isIntersecting)
            {
                return true;
            }

            return state.ThresholdIndex != thresholdIndex;
        }

        private int GetThresholdIndex(double ratio, bool isIntersecting)
        {
            if (!isIntersecting && ratio <= 0d)
            {
                return _thresholds.Count > 0 && _thresholds[0] == 0d ? 0 : -1;
            }

            var index = -1;
            for (var i = 0; i < _thresholds.Count; i++)
            {
                if (ratio >= _thresholds[i])
                {
                    index = i;
                }
            }

            return index;
        }

        private void EnqueueEntries(List<FenObject> entries)
        {
            var shouldSchedule = false;
            lock (_queueLock)
            {
                _queuedEntries.AddRange(entries);
                if (!_callbackQueued)
                {
                    _callbackQueued = true;
                    shouldSchedule = true;
                }
            }

            if (!shouldSchedule)
            {
                return;
            }

            ObserverCoordinator.Instance.EnqueueCallback(() =>
            {
                if (!_callback.IsFunction)
                {
                    TakeRecords();
                    return;
                }

                var entriesArray = TakeRecords();
                if (entriesArray.Get("length").ToNumber() <= 0)
                {
                    return;
                }

                try
                {
                    var fn = _callback.AsFunction();
                    var args = _observerObject != null
                        ? new[] { FenValue.FromObject(entriesArray), FenValue.FromObject(_observerObject) }
                        : new[] { FenValue.FromObject(entriesArray) };
                    fn?.Invoke(args, null);
                }
                catch
                {
                    // Callback errors don't break observers.
                }
            });
        }

        private FenObject CreateEntry(
            IObject target, bool isIntersecting, double ratio,
            ElementGeometry targetRect, ElementGeometry viewport,
            float intersectX, float intersectY, float intersectWidth, float intersectHeight)
        {
            var entry = new FenObject();
            entry.Set("target", FenValue.FromObject(target));
            entry.Set("isIntersecting", FenValue.FromBoolean(isIntersecting));
            entry.Set("intersectionRatio", FenValue.FromNumber(ratio));
            entry.Set("time", FenValue.FromNumber(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

            // boundingClientRect
            var boundingRect = CreateDOMRect(targetRect);
            entry.Set("boundingClientRect", FenValue.FromObject(boundingRect));

            // intersectionRect
            var intersectionRect = CreateDOMRect(new ElementGeometry(intersectX, intersectY, intersectWidth, intersectHeight));
            entry.Set("intersectionRect", FenValue.FromObject(intersectionRect));

            // rootBounds
            var rootBounds = CreateDOMRect(viewport);
            entry.Set("rootBounds", FenValue.FromObject(rootBounds));

            return entry;
        }

        private FenObject CreateDOMRect(ElementGeometry geom)
        {
            var rect = new FenObject();
            rect.Set("x", FenValue.FromNumber(geom.X));
            rect.Set("y", FenValue.FromNumber(geom.Y));
            rect.Set("width", FenValue.FromNumber(geom.Width));
            rect.Set("height", FenValue.FromNumber(geom.Height));
            rect.Set("top", FenValue.FromNumber(geom.Top));
            rect.Set("left", FenValue.FromNumber(geom.Left));
            rect.Set("right", FenValue.FromNumber(geom.Right));
            rect.Set("bottom", FenValue.FromNumber(geom.Bottom));
            return rect;
        }
    }

    /// <summary>
    /// Instance of a ResizeObserver with spec-compliant size tracking.
    /// 
    /// IMPORTANT: Per Phase D spec Section 5:
    /// - Only fires on SIZE change (not position change)
    /// - Uses dirty-flag (lastWidth, lastHeight, lastLayoutId)
    /// - Never polls - evaluation runs only when LayoutId changed
    /// </summary>
    public class ResizeObserverInstance
    {
        private readonly IValue _callback;
        private readonly List<IObject> _observedTargets = new List<IObject>();
        
        /// <summary>
        /// Tracks last seen size for each target (width, height, layoutId).
        /// Fires callback only when size actually changed.
        /// </summary>
        private readonly Dictionary<IObject, (float width, float height, long layoutId)> _lastSeen 
            = new Dictionary<IObject, (float, float, long)>();

        public ResizeObserverInstance(IValue callback)
        {
            _callback = callback;
        }

        public void Observe(IObject target)
        {
            lock (_observedTargets)
            {
                if (!_observedTargets.Contains(target))
                {
                    _observedTargets.Add(target);
                    // Initialize with invalid size to force first callback
                    _lastSeen[target] = (-1, -1, -1);
                }
            }
        }

        public void Unobserve(IObject target)
        {
            lock (_observedTargets)
            {
                _observedTargets.Remove(target);
                _lastSeen.Remove(target);
            }
        }

        public void Disconnect()
        {
            lock (_observedTargets)
            {
                _observedTargets.Clear();
                _lastSeen.Clear();
            }
            ObserverCoordinator.Instance.UnregisterResizeObserver(this);
        }

        /// <summary>
        /// Evaluate size changes using immutable LayoutResult (never live renderer state).
        /// Per spec Section 5: Only fires if size actually changed.
        /// </summary>
        public void EvaluateWithLayoutResult(
            LayoutResult result,
            Func<IObject, Element> elementLookup)
        {
            if (result == null || elementLookup == null) return;

            var entries = new List<FenObject>();

            IObject[] targets;
            lock (_observedTargets)
            {
                targets = _observedTargets.ToArray();
            }

            foreach (var jsTarget in targets)
            {
                var element = elementLookup(jsTarget);
                if (element == null) continue;

                if (!result.TryGetElementRect(element, out var targetRect)) continue;

                float newWidth = targetRect.Width;
                float newHeight = targetRect.Height;

                // Dirty-flag check: only fire if size changed
                var lastSeen = _lastSeen.TryGetValue(jsTarget, out var prev) ? prev : (-1, -1, -1L);
                
                if (Math.Abs(newWidth - lastSeen.width) > 0.5f || 
                    Math.Abs(newHeight - lastSeen.height) > 0.5f)
                {
                    _lastSeen[jsTarget] = (newWidth, newHeight, result.LayoutId);

                    var entry = CreateEntry(jsTarget, newWidth, newHeight);
                    entries.Add(entry);
                }
            }

            if (entries.Count > 0)
            {
                // Enqueue callback for JS Execution Window
                ObserverCoordinator.Instance.EnqueueCallback(() =>
                {
                    // Phase assertion at callback execution time
                    EnginePhaseManager.AssertNotInPhase(
                        EnginePhase.Measure,
                        EnginePhase.Layout,
                        EnginePhase.Paint);

                    if (!_callback.IsFunction) return;

                    var entriesArray = new FenObject();
                    for (int i = 0; i < entries.Count; i++)
                    {
                        entriesArray.Set(i.ToString(), FenValue.FromObject(entries[i]));
                    }
                    entriesArray.Set("length", FenValue.FromNumber(entries.Count));

                    try
                    {
                        var fn = _callback.AsFunction();
                        // Callback signature: (entries, observer) => void
                        fn?.Invoke(new FenValue[] { FenValue.FromObject(entriesArray) }, null);
                    }
                    catch { /* Callback errors don't break observers */ }
                });
            }
        }

        private FenObject CreateEntry(IObject target, float width, float height)
        {
            var entry = new FenObject();
            entry.Set("target", FenValue.FromObject(target));

            // contentRect (content box only per spec Section 2)
            var contentRect = new FenObject();
            contentRect.Set("x", FenValue.FromNumber(0));
            contentRect.Set("y", FenValue.FromNumber(0));
            contentRect.Set("width", FenValue.FromNumber(width));
            contentRect.Set("height", FenValue.FromNumber(height));
            contentRect.Set("top", FenValue.FromNumber(0));
            contentRect.Set("left", FenValue.FromNumber(0));
            contentRect.Set("right", FenValue.FromNumber(width));
            contentRect.Set("bottom", FenValue.FromNumber(height));
            entry.Set("contentRect", FenValue.FromObject(contentRect));

            // contentBoxSize (array with single entry)
            var boxSize = new FenObject();
            boxSize.Set("blockSize", FenValue.FromNumber(height));
            boxSize.Set("inlineSize", FenValue.FromNumber(width));
            
            var boxSizeArray = new FenObject();
            boxSizeArray.Set("0", FenValue.FromObject(boxSize));
            boxSizeArray.Set("length", FenValue.FromNumber(1));
            entry.Set("contentBoxSize", FenValue.FromObject(boxSizeArray));

            // borderBoxSize (same as content for now)
            entry.Set("borderBoxSize", FenValue.FromObject(boxSizeArray));

            return entry;
        }
    }
}

