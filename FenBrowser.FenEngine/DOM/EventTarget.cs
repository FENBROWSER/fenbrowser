using FenBrowser.Core; // Added for FenLogger
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security; // Added
using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Service for dispatching events according to the DOM Event Flow.
    /// Handles Capture, Target, and Bubble phases.
    /// </summary>
    public static class EventTarget
    {
        private static EventListenerRegistry _registry = new EventListenerRegistry();

        public static EventListenerRegistry Registry => _registry;
        public static Action<object, DomEvent, IExecutionContext, bool, bool> ExternalListenerInvoker { get; set; }
        public static Func<Element, object> ResolveDocumentTarget { get; set; }
        public static Func<Element, object> ResolveWindowTarget { get; set; }

        /// <summary>
        /// Dispatch an event to the target element.
        /// </summary>
        /// <param name="target">The target element</param>
        /// <param name="evt">The event object</param>
        /// <param name="context">The execution context for callbacks</param>
        /// <returns>True if the event was not cancelled (defaultPrevented is false), otherwise false.</returns>
        public static bool DispatchEvent(Element target, DomEvent evt, IExecutionContext context)
        {
            if (target == null || evt == null) return true;

            if (!evt.Initialized)
            {
                throw new Exception("InvalidStateError: Failed to execute 'dispatchEvent' on 'EventTarget': The event's initialized flag is not set.");
            }

            var env = context?.Environment;
            var previousGlobalEvent = FenValue.Undefined;
            IObject windowObj = null;
            var previousWindowEvent = FenValue.Undefined;
            var exposeLegacyEvent = !IsInShadowTree(target);
            if (env != null)
            {
                previousGlobalEvent = env.Get("event");
                var winVal = env.Get("window");
                if (winVal.IsObject)
                {
                    windowObj = winVal.AsObject();
                    previousWindowEvent = windowObj.Get("event", context);
                }

                var legacyEventValue = exposeLegacyEvent ? FenValue.FromObject(evt) : FenValue.Undefined;
                env.Set("event", legacyEventValue);
                if (windowObj != null)
                {
                    windowObj.Set("event", legacyEventValue, context);
                }
            }

            void SetLegacyEventForTopLevel()
            {
                if (env == null) return;
                var legacyEventValue = FenValue.FromObject(evt);
                env.Set("event", legacyEventValue);
                if (windowObj != null)
                {
                    windowObj.Set("event", legacyEventValue, context);
                }
            }

            void SetLegacyEventForElement(Element element)
            {
                if (env == null) return;
                var expose = element != null && !IsInShadowTree(element);
                var legacyEventValue = expose ? FenValue.FromObject(evt) : FenValue.Undefined;
                env.Set("event", legacyEventValue);
                if (windowObj != null)
                {
                    windowObj.Set("event", legacyEventValue, context);
                }
            }

            // 1. Initialize Event
            evt.ResetState(); // Reset path/phase if re-dispatching
            evt.Target = target;
            evt.IsTrusted = true; // Assumed trusted if dispatched by engine
            evt.UpdateJsProperties(context); // Sync JS object

            // 2. Build Propagation Path
            var path = new List<Element>();
            var current = target.ParentElement;
            while (current != null)
            {
                path.Add(current);
                current = current.ParentElement;
            }
            // Path is currently Target -> Root. Invert for capture, use as-is for bubble (but skip target in list)

            // Populate event.path (composedPath)
            evt.Path.Clear();
            evt.Path.Add(target);
            evt.Path.AddRange(path);

            var documentTarget = ResolveDocumentTarget != null ? ResolveDocumentTarget(target) : null;
            var windowTarget = ResolveWindowTarget != null ? ResolveWindowTarget(target) : null;

            // 3. CAPTURING PHASE (Root -> Parent)
            evt.EventPhase = DomEvent.CAPTURING_PHASE;
            if (!evt.PropagationStopped)
            {
                if (ExternalListenerInvoker != null)
                {
                    if (windowTarget != null) { SetLegacyEventForTopLevel(); ExternalListenerInvoker(windowTarget, evt, context, true, false); }
                    if (!evt.PropagationStopped && documentTarget != null) { SetLegacyEventForTopLevel(); ExternalListenerInvoker(documentTarget, evt, context, true, false); }
                }
                else
                {
                    // WPT/headless path uses FenRuntime directly without JavaScriptEngine bridge.
                    SetLegacyEventForTopLevel(); InvokeFenRuntimeTopLevelListeners(context, evt, capturePhase: true);
                }
            }

            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (evt.PropagationStopped) break;
                var ancestor = path[i];
                evt.CurrentTarget = ancestor;
                evt.UpdateJsProperties(context);
                SetLegacyEventForElement(ancestor); InvokeListeners(ancestor, evt, context, true);
                if (!evt.PropagationStopped && ExternalListenerInvoker != null)
                {
                    SetLegacyEventForElement(ancestor); ExternalListenerInvoker(ancestor, evt, context, true, false);
                }
            }

            // 4. AT TARGET PHASE
            if (!evt.PropagationStopped)
            {
                evt.EventPhase = DomEvent.AT_TARGET;
                evt.CurrentTarget = target;
                evt.UpdateJsProperties(context);
                // Spec: "Any event listeners registered on the eventTarget... are triggered."
                // In practice, browsers usually fire capture listeners then bubble listeners (or insertion order).
                // We will fire Capture first, then Bubble.
                SetLegacyEventForElement(target); InvokeListeners(target, evt, context, true);  // Capture listeners at target
                if (!evt.PropagationStopped && ExternalListenerInvoker != null)
                {
                    SetLegacyEventForElement(target); ExternalListenerInvoker(target, evt, context, true, true);
                }

                SetLegacyEventForElement(target); InvokeListeners(target, evt, context, false); // Bubble listeners at target
                if (!evt.PropagationStopped && ExternalListenerInvoker != null)
                {
                    SetLegacyEventForElement(target); ExternalListenerInvoker(target, evt, context, false, true);
                }
            }

            // 5. BUBBLING PHASE (Parent -> Root)
            if (evt.Bubbles && !evt.PropagationStopped)
            {
                evt.EventPhase = DomEvent.BUBBLING_PHASE;
                for (int i = 0; i < path.Count; i++)
                {
                    if (evt.PropagationStopped) break;
                    var ancestor = path[i];
                    evt.CurrentTarget = ancestor;
                    evt.UpdateJsProperties(context);
                    SetLegacyEventForElement(ancestor); InvokeListeners(ancestor, evt, context, false);
                    if (!evt.PropagationStopped && ExternalListenerInvoker != null)
                    {
                        SetLegacyEventForElement(ancestor); ExternalListenerInvoker(ancestor, evt, context, false, false);
                    }
                }

                if (!evt.PropagationStopped)
                {
                    if (ExternalListenerInvoker != null)
                    {
                        if (documentTarget != null) { SetLegacyEventForTopLevel(); ExternalListenerInvoker(documentTarget, evt, context, false, false); }
                        if (!evt.PropagationStopped && windowTarget != null) { SetLegacyEventForTopLevel(); ExternalListenerInvoker(windowTarget, evt, context, false, false); }
                    }
                    else
                    {
                        // WPT/headless path uses FenRuntime directly without JavaScriptEngine bridge.
                        SetLegacyEventForTopLevel(); InvokeFenRuntimeTopLevelListeners(context, evt, capturePhase: false);
                    }
                }
            }

            // 6. Reset/Finalize
            evt.ClearPropagationFlags();
            evt.EventPhase = DomEvent.NONE;
            evt.CurrentTarget = null;
            evt.UpdateJsProperties(context);

            if (windowObj != null)
            {
                windowObj.Set("event", previousWindowEvent, context);
            }
            if (env != null)
            {
                env.Set("event", previousGlobalEvent);
            }

            return !evt.DefaultPrevented;
        }

        private static bool IsInShadowTree(Element element)
        {
            for (Node current = element; current != null; current = current.ParentNode)
            {
                if (current is ShadowRoot)
                {
                    return true;
                }
            }

            return false;
        }

        private static void InvokeListeners(Element element, DomEvent evt, IExecutionContext context, bool isCapturePhase)
        {
            if (evt.ImmediatePropagationStopped) return;

            // Get listeners specifically for this phase
            // Note: Registry.Get returns a COPY, safe for modification during iteration
            var listeners = _registry.Get(element, evt.Type, isCapturePhase);

            foreach (var listener in listeners)
            {
                if (evt.ImmediatePropagationStopped) return;

                // Handle 'once'
                if (listener.Once)
                {
                    _registry.RemoveOnce(element, evt.Type, listener);
                }

                try
                {
                    FenFunction callbackFn = null;
                    var callbackThis = FenValue.FromObject(new ElementWrapper(element, context));
                    var callback = listener.Callback;

                    if (callback.IsFunction)
                    {
                        callbackFn = callback.AsFunction() as FenFunction;
                    }
                    else if (callback.IsObject)
                    {
                        var handleEvent = callback.AsObject().Get("handleEvent", context);
                        if (handleEvent.IsFunction)
                        {
                            callbackFn = handleEvent.AsFunction() as FenFunction;
                            callbackThis = callback;
                        }
                    }

                    if (callbackFn == null) continue;

                    var evtVal = FenValue.FromObject(evt);
                    var args = new FenValue[] { evtVal };

                    if (context.ExecuteFunction != null && callback.IsFunction)
                    {
                        context.ThisBinding = callbackThis;
                        context.ExecuteFunction(callback, args);
                    }
                    else
                    {
                        context.CheckCallStackLimit();
                        context.CheckExecutionTimeLimit();
                        callbackFn.Invoke(args, context, callbackThis);
                    }

                    // Mirror top-level invoker semantics so assignment-based legacy flags
                    // (cancelBubble / returnValue) always influence propagation/default handling.
                    var cancelBubbleVal = evt.Get("cancelBubble");
                    if (cancelBubbleVal.IsBoolean && cancelBubbleVal.ToBoolean())
                        evt.StopPropagation();
                    var returnValueVal = evt.Get("returnValue");
                    if (returnValueVal.IsBoolean && !returnValueVal.ToBoolean())
                        evt.PreventDefault();
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[EventTarget] Error in listener for {evt.Type}: {ex.Message}", LogCategory.Events, ex);
                }
            }
        }


        private static void InvokeFenRuntimeTopLevelListeners(IExecutionContext context, DomEvent evt, bool capturePhase)
        {
            var env = context?.Environment;
            if (env == null || evt == null || string.IsNullOrWhiteSpace(evt.Type)) return;

            try
            {
                var winVal = env.Get("window");
                if (winVal.IsObject)
                {
                    InvokeFenListenerArray(winVal.AsObject(), evt, context, capturePhase);
                }
            }
            catch (Exception ex) { FenLogger.Warn($"[EventTarget] Window top-level listeners invocation failed: {ex.Message}", LogCategory.Events); }

            if (evt.PropagationStopped) return;

            try
            {
                var docVal = env.Get("document");
                if (docVal.IsObject)
                {
                    InvokeFenListenerArray(docVal.AsObject(), evt, context, capturePhase);
                }
            }
            catch (Exception ex) { FenLogger.Warn($"[EventTarget] Window top-level listeners invocation failed: {ex.Message}", LogCategory.Events); }
        }

        private static void InvokeFenListenerArray(IObject targetObj, DomEvent evt, IExecutionContext context, bool capturePhase)
        {
            if (targetObj == null || evt == null) return;

            var listenersVal = targetObj.Get("__fen_listeners__", context);
            if (!listenersVal.IsObject) return;

            var listenersObj = listenersVal.AsObject() as FenObject;
            if (listenersObj == null) return;

            var arrVal = listenersObj.Get(evt.Type, context);
            if (!arrVal.IsObject) return;

            var arr = arrVal.AsObject() as FenObject;
            if (arr == null) return;

            int len = (int)arr.Get("length", context).ToNumber();
            evt.Set("currentTarget", FenValue.FromObject(targetObj), context);

            for (int i = 0; i < len; i++)
            {
                if (evt.ImmediatePropagationStopped) break;

                var entryVal = arr.Get(i.ToString(), context);
                if (!entryVal.IsObject) continue;

                var entry = entryVal.AsObject();
                var callback = entry.Get("callback", context);
                if (!callback.IsFunction) continue;

                var capVal = entry.Get("capture", context);
                var cap = capVal.IsBoolean && capVal.ToBoolean();
                if (cap != capturePhase) continue;

                try
                {
                    callback.AsFunction().Invoke(new[] { FenValue.FromObject(evt) }, context, FenValue.FromObject(targetObj));
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[EventTarget] FenRuntime listener error for {evt.Type}: {ex.Message}", LogCategory.Events, ex);
                }

                var onceVal = entry.Get("once", context);
                if (onceVal.IsBoolean && onceVal.ToBoolean())
                {
                    for (int j = i + 1; j < len; j++)
                    {
                        arr.Set((j - 1).ToString(), arr.Get(j.ToString(), context), context);
                    }

                    len--;
                    arr.Delete(len.ToString(), context);
                    arr.Set("length", FenValue.FromNumber(len), context);
                    i--;
                }
            }
        }        // Shim to pass 'ThisBinding' correct for the listener
        private class ContextShim : IExecutionContext
        {
            private readonly IExecutionContext _inner;
            private readonly FenValue _thisBinding;

            public ContextShim(IExecutionContext inner, FenValue thisBinding)
            {
                _inner = inner;
                _thisBinding = thisBinding;
            }

            public FenValue ThisBinding { get => _thisBinding; set { } }
            public IPermissionManager Permissions => _inner.Permissions;
            public FenBrowser.FenEngine.Security.IResourceLimits Limits => _inner.Limits;
            public int CallStackDepth => _inner.CallStackDepth;
            public DateTime ExecutionStart => _inner.ExecutionStart;
            public bool ShouldContinue => _inner.ShouldContinue;
            public Action RequestRender => _inner.RequestRender;
            public void SetRequestRender(Action action) => _inner.SetRequestRender(action);
            public Action<Action, int> ScheduleCallback { get => _inner.ScheduleCallback; set => _inner.ScheduleCallback = value; }
            public Action<Action> ScheduleMicrotask { get => _inner.ScheduleMicrotask; set => _inner.ScheduleMicrotask = value; }
            public Func<FenValue, FenValue[], FenValue> ExecuteFunction { get => _inner.ExecuteFunction; set => _inner.ExecuteFunction = value; }
            public IModuleLoader ModuleLoader { get => _inner.ModuleLoader; set => _inner.ModuleLoader = value; }
            public Action<MutationRecord> OnMutation { get => _inner.OnMutation; set => _inner.OnMutation = value; }
            public string CurrentUrl { get => _inner.CurrentUrl; set => _inner.CurrentUrl = value; }
            public FenEnvironment Environment { get => _inner.Environment; set => _inner.Environment = value; }
            public FenValue NewTarget { get => _inner.NewTarget; set => _inner.NewTarget = value; }
            public string CurrentModulePath { get => _inner.CurrentModulePath; set => _inner.CurrentModulePath = value; }
            public bool StrictMode { get => _inner.StrictMode; set => _inner.StrictMode = value; }
            public void PushCallFrame(string name) => _inner.PushCallFrame(name);
            public void PopCallFrame() => _inner.PopCallFrame();
            public void CheckCallStackLimit() => _inner.CheckCallStackLimit();
            public void CheckExecutionTimeLimit() => _inner.CheckExecutionTimeLimit();
            public FenBrowser.FenEngine.Rendering.Core.ILayoutEngine GetLayoutEngine() => _inner.GetLayoutEngine();
        }
    }
}






