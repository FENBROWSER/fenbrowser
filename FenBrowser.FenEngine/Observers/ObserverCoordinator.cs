using System;
using System.Collections.Generic;
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
            var previousPhase = EnginePhaseManager.CurrentPhase;
            var switchedToJsExecution = false;

            if (previousPhase == EnginePhase.Measure || previousPhase == EnginePhase.Layout || previousPhase == EnginePhase.Paint)
            {
                EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
                switchedToJsExecution = true;
            }

            try
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
            finally
            {
                if (switchedToJsExecution)
                {
                    EnginePhaseManager.EnterPhase(previousPhase);
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
        private readonly IValue _callback;
        private readonly double _threshold;
        private readonly List<IObject> _observedTargets = new List<IObject>();
        private readonly Dictionary<IObject, bool> _previouslyIntersecting = new Dictionary<IObject, bool>();

        public IntersectionObserverInstance(IValue callback, double threshold)
        {
            _callback = callback;
            _threshold = Math.Max(0, Math.Min(1, threshold));
        }

        public void Observe(IObject target)
        {
            lock (_observedTargets)
            {
                if (!_observedTargets.Contains(target))
                {
                    _observedTargets.Add(target);
                    _previouslyIntersecting[target] = false;
                }
            }
        }

        public void Unobserve(IObject target)
        {
            lock (_observedTargets)
            {
                _observedTargets.Remove(target);
                _previouslyIntersecting.Remove(target);
            }
        }

        public void Disconnect()
        {
            lock (_observedTargets)
            {
                _observedTargets.Clear();
                _previouslyIntersecting.Clear();
            }
            ObserverCoordinator.Instance.UnregisterIntersectionObserver(this);
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

            foreach (var jsTarget in targets)
            {
                var element = elementLookup(jsTarget);
                if (element == null) continue;

                if (!result.TryGetElementRect(element, out var targetRect)) continue;

                // Calculate intersection
                float intersectX = Math.Max(viewport.X, targetRect.X);
                float intersectY = Math.Max(viewport.Y, targetRect.Y);
                float intersectRight = Math.Min(viewport.Right, targetRect.Right);
                float intersectBottom = Math.Min(viewport.Bottom, targetRect.Bottom);

                float intersectWidth = Math.Max(0, intersectRight - intersectX);
                float intersectHeight = Math.Max(0, intersectBottom - intersectY);
                float intersectArea = intersectWidth * intersectHeight;

                float targetArea = targetRect.Width * targetRect.Height;
                double ratio = targetArea > 0 ? intersectArea / targetArea : 0;

                bool isIntersecting = intersectArea > 0 && ratio >= _threshold;

                // Check if state changed
                bool wasIntersecting = _previouslyIntersecting.TryGetValue(jsTarget, out var prev) && prev;

                if (isIntersecting != wasIntersecting)
                {
                    _previouslyIntersecting[jsTarget] = isIntersecting;

                    var entry = CreateEntry(jsTarget, isIntersecting, ratio, targetRect, viewport, intersectX, intersectY, intersectWidth, intersectHeight);
                    entries.Add(entry);
                }
            }

            if (entries.Count > 0)
            {
                // Enqueue callback for JS Execution Window
                ObserverCoordinator.Instance.EnqueueCallback(() =>
                {
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
                        fn?.Invoke(new FenValue[] { FenValue.FromObject(entriesArray) }, null);
                    }
                    catch { /* Callback errors don't break observers */ }
                });
            }
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

