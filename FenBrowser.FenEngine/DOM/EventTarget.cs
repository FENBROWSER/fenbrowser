using FenBrowser.Core; // Added for FenLogger
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;
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
                throw new InvalidOperationException("InvalidStateError: Failed to execute 'dispatchEvent' on 'EventTarget': The event's initialized flag is not set.");
            }

            evt.BeginDispatch();

            // Event dispatch is a fresh JS entry point and must not inherit an expired
            // execution budget from a previous long-running script on the same page.
            if (context is FenBrowser.FenEngine.Core.ExecutionContext executionContext)
            {
                executionContext.Reset();
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

            try
            {
                // 1. Initialize Event
                evt.ResetState(); // Reset path/phase if re-dispatching
                evt.Target = target;
                evt.IsTrusted = true; // Assumed trusted if dispatched by engine
                evt.UpdateJsProperties(context); // Sync JS object

                // 2. Build Propagation Path
                var path = BuildPropagationPath(target, evt.Composed);

                // Populate event.path (composedPath)
                evt.Path.Clear();
                evt.Path.Add(target);
                evt.Path.AddRange(path);

                var useExternalInvoker = ExternalListenerInvoker != null && env != null;
                var documentTarget = useExternalInvoker && ResolveDocumentTarget != null ? ResolveDocumentTarget(target) : null;
                var windowTarget = useExternalInvoker && ResolveWindowTarget != null ? ResolveWindowTarget(target) : null;

                // 3. CAPTURING PHASE (Root -> Parent)
                evt.EventPhase = DomEvent.CAPTURING_PHASE;
                if (!evt.PropagationStopped)
                {
                    if (useExternalInvoker)
                    {
                        if (windowTarget != null)
                        {
                            SetLegacyEventForTopLevel();
                            ExternalListenerInvoker(windowTarget, evt, context, true, false);
                        }
                        if (!evt.PropagationStopped && documentTarget != null)
                        {
                            SetLegacyEventForTopLevel();
                            ExternalListenerInvoker(documentTarget, evt, context, true, false);
                        }
                    }
                    else
                    {
                        SetLegacyEventForTopLevel();
                        InvokeFenRuntimeTopLevelListeners(context, evt, capturePhase: true);
                    }
                }

                for (int i = path.Count - 1; i >= 0; i--)
                {
                    if (evt.PropagationStopped) break;
                    var ancestor = path[i];
                    evt.CurrentTarget = ancestor;
                    evt.UpdateJsProperties(context);
                    SetLegacyEventForElement(ancestor);
                    InvokeListeners(ancestor, evt, context, true);
                    if (!evt.PropagationStopped && useExternalInvoker)
                    {
                        SetLegacyEventForElement(ancestor);
                        ExternalListenerInvoker(ancestor, evt, context, true, false);
                    }
                }

                // 4. AT TARGET PHASE
                if (!evt.PropagationStopped)
                {
                    evt.EventPhase = DomEvent.AT_TARGET;
                    evt.CurrentTarget = target;
                    evt.UpdateJsProperties(context);
                    SetLegacyEventForElement(target);
                    InvokeListeners(target, evt, context, true);
                    if (!evt.PropagationStopped && useExternalInvoker)
                    {
                        SetLegacyEventForElement(target);
                        ExternalListenerInvoker(target, evt, context, true, true);
                    }

                    SetLegacyEventForElement(target);
                    InvokeListeners(target, evt, context, false);
                    if (!evt.PropagationStopped && useExternalInvoker)
                    {
                        SetLegacyEventForElement(target);
                        ExternalListenerInvoker(target, evt, context, false, true);
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
                        SetLegacyEventForElement(ancestor);
                        InvokeListeners(ancestor, evt, context, false);
                        if (!evt.PropagationStopped && useExternalInvoker)
                        {
                            SetLegacyEventForElement(ancestor);
                            ExternalListenerInvoker(ancestor, evt, context, false, false);
                        }
                    }

                    if (!evt.PropagationStopped)
                    {
                        if (useExternalInvoker)
                        {
                            if (documentTarget != null)
                            {
                                SetLegacyEventForTopLevel();
                                ExternalListenerInvoker(documentTarget, evt, context, false, false);
                            }
                            if (!evt.PropagationStopped && windowTarget != null)
                            {
                                SetLegacyEventForTopLevel();
                                ExternalListenerInvoker(windowTarget, evt, context, false, false);
                            }

                            if (!evt.PropagationStopped &&
                                string.Equals(evt.Type, "error", StringComparison.Ordinal) &&
                                IsInShadowTree(target) &&
                                windowTarget is IObject windowTopLevelTarget)
                            {
                                SetLegacyEventForTopLevel();
                                InvokeEventHandlerProperty(windowTopLevelTarget, FenValue.FromObject(windowTopLevelTarget), evt, context);
                            }
                        }
                        else
                        {
                            SetLegacyEventForTopLevel();
                            InvokeFenRuntimeTopLevelListeners(context, evt, capturePhase: false);
                        }
                    }
                }

                // 6. Reset/Finalize
                evt.EventPhase = DomEvent.NONE;
                evt.CurrentTarget = null;
                evt.Path.Clear();
                evt.ClearPropagationFlags();
                evt.UpdateJsProperties(context);
                return !evt.DefaultPrevented;
            }
            finally
            {
                evt.EndDispatch();

                if (windowObj != null)
                {
                    windowObj.Set("event", previousWindowEvent, context);
                }
                if (env != null)
                {
                    env.Set("event", previousGlobalEvent);
                }
            }
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

        private static List<Element> BuildPropagationPath(Element target, bool composed)
        {
            var path = new List<Element>();
            for (Node current = target; current != null;)
            {
                current = GetPropagationParent(current, composed);
                if (current is Element ancestor)
                {
                    path.Add(ancestor);
                }
            }

            return path;
        }

        private static Node GetPropagationParent(Node node, bool composed)
        {
            if (node == null)
            {
                return null;
            }

            if (node.ParentNode != null)
            {
                return node.ParentNode;
            }

            if (composed && node is ShadowRoot shadowRoot)
            {
                return shadowRoot.Host;
            }

            return null;
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
                    var callbackThis = DomWrapperFactory.Wrap(element, context);
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

                    // UI Events §4.3.2: passive listeners must not be able to call preventDefault().
                    if (listener.Passive) evt.IsPassiveContext = true;
                    try
                    {
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
                    }
                    finally
                    {
                        if (listener.Passive) evt.IsPassiveContext = false;
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
                    // Per spec, report the error to window.onerror
                    TryReportErrorToWindow(element, context, ex);
                }
            }

            if (!isCapturePhase && !evt.ImmediatePropagationStopped)
            {
                var wrappedTarget = DomWrapperFactory.Wrap(element, context);
                if (wrappedTarget.IsObject)
                {
                    InvokeEventHandlerProperty(wrappedTarget.AsObject(), wrappedTarget, evt, context);
                }
            }
        }


        /// <summary>
        /// Report an uncaught listener exception to window.onerror per the DOM spec.
        /// </summary>
        private static void TryReportErrorToWindow(Element sourceElement, IExecutionContext context, Exception ex)
        {
            try
            {
                var winVal = ResolveWindowValue(sourceElement, context);
                if (!winVal.IsObject)
                {
                    return;
                }

                var onerrorVal = winVal.AsObject().Get("onerror", context);
                if (!TryResolveCallable(onerrorVal, winVal, context, out var callback, out var callbackThis))
                {
                    return;
                }

                var errorValue = CreateReportedErrorValue(ex);
                var message = GetReportedErrorMessage(ex, errorValue);
                var args = new FenValue[]
                {
                    FenValue.FromString(message),
                    FenValue.FromString(string.Empty),
                    FenValue.FromNumber(0),
                    FenValue.FromNumber(0),
                    errorValue
                };
                InvokeCallback(callback, args, context, callbackThis);
            }
            catch (Exception reportingException)
            {
                FenLogger.Warn($"[EventTarget] window.onerror reporting failed: {reportingException.Message}", LogCategory.Events);
            }
        }

        private static FenValue ResolveWindowValue(Element sourceElement, IExecutionContext context)
        {
            if (sourceElement != null && ResolveWindowTarget != null)
            {
                var resolvedWindow = ResolveWindowTarget(sourceElement);
                if (resolvedWindow is IObject objectWindow)
                {
                    return FenValue.FromObject(objectWindow);
                }

                if (resolvedWindow is FenValue valueWindow && valueWindow.IsObject)
                {
                    return valueWindow;
                }
            }

            var env = context?.Environment;
            if (env == null)
            {
                return FenValue.Undefined;
            }

            var globalThis = env.Get("globalThis");
            if (globalThis.IsObject)
            {
                return globalThis;
            }

            var window = env.Get("window");
            if (window.IsObject)
            {
                return window;
            }

            var self = env.Get("self");
            return self.IsObject ? self : FenValue.Undefined;
        }

        private static bool TryResolveCallable(FenValue candidate, FenValue defaultThis, IExecutionContext context, out FenValue callback, out FenValue callbackThis)
        {
            callback = FenValue.Undefined;
            callbackThis = FenValue.Undefined;

            if (candidate.IsFunction)
            {
                callback = candidate;
                callbackThis = defaultThis;
                return true;
            }

            if (!candidate.IsObject)
            {
                return false;
            }

            var handleEvent = candidate.AsObject()?.Get("handleEvent", context) ?? FenValue.Undefined;
            if (!handleEvent.IsFunction)
            {
                return false;
            }

            callback = handleEvent;
            callbackThis = candidate;
            return true;
        }

        private static void InvokeCallback(FenValue callback, FenValue[] args, IExecutionContext context, FenValue callbackThis)
        {
            if (!callback.IsFunction)
            {
                return;
            }

            if (context?.ExecuteFunction != null)
            {
                context.ThisBinding = callbackThis;
                context.ExecuteFunction(callback, args);
                return;
            }

            context?.CheckCallStackLimit();
            context?.CheckExecutionTimeLimit();
            callback.AsFunction()?.Invoke(args, context, callbackThis);
        }

        private static FenValue CreateReportedErrorValue(Exception exception)
        {
            if (TryExtractThrownValue(exception, out var thrownValue))
            {
                return thrownValue;
            }

            if (exception is FenError fenError)
            {
                return fenError.ThrownValue;
            }

            return FenValue.FromError(exception?.Message ?? "Script error.");
        }

        private static string GetReportedErrorMessage(Exception exception, FenValue errorValue)
        {
            if ((errorValue.IsObject || errorValue.IsFunction) && errorValue.AsObject() != null)
            {
                var errorObject = errorValue.AsObject();
                var nameValue = errorObject.Get("name");
                var messageValue = errorObject.Get("message");

                if (!messageValue.IsUndefined && !messageValue.IsNull)
                {
                    return messageValue.AsString();
                }

                if (!nameValue.IsUndefined && !nameValue.IsNull)
                {
                    return nameValue.AsString();
                }
            }

            if (errorValue.IsError)
            {
                return errorValue.AsError() ?? "Script error.";
            }

            return exception?.Message ?? "Script error.";
        }

        private static bool TryExtractThrownValue(Exception exception, out FenValue thrownValue)
        {
            return JsThrownValueException.TryExtract(exception, out thrownValue);
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
            evt.Set("eventPhase", FenValue.FromNumber(evt.EventPhase), context);

            for (int i = 0; i < len; i++)
            {
                if (evt.ImmediatePropagationStopped) break;

                var entryVal = arr.Get(i.ToString(), context);
                if (!entryVal.IsObject) continue;

                var entry = entryVal.AsObject();
                var callback = entry.Get("callback", context);
                FenFunction callbackFn = null;
                var callbackThis = FenValue.FromObject(targetObj);
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

                var capVal = entry.Get("capture", context);
                var cap = capVal.IsBoolean && capVal.ToBoolean();
                if (cap != capturePhase) continue;

                // UI Events §4.3.2: honour passive flag so preventDefault() is suppressed.
                var passiveVal = entry.Get("passive", context);
                var isPassive = passiveVal.IsBoolean && passiveVal.ToBoolean();
                if (isPassive) evt.IsPassiveContext = true;
                try
                {
                    callbackFn.Invoke(new[] { FenValue.FromObject(evt) }, context, callbackThis);
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[EventTarget] FenRuntime listener error for {evt.Type}: {ex.Message}", LogCategory.Events, ex);
                }
                finally
                {
                    if (isPassive) evt.IsPassiveContext = false;
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

            if (!capturePhase && !evt.ImmediatePropagationStopped)
            {
                InvokeEventHandlerProperty(targetObj, FenValue.FromObject(targetObj), evt, context);
            }
        }        // Shim to pass 'ThisBinding' correct for the listener

        private static void InvokeEventHandlerProperty(IObject targetObj, FenValue thisArg, DomEvent evt, IExecutionContext context)
        {
            if (targetObj == null || evt == null)
            {
                return;
            }

            try
            {
                var onHandler = targetObj.Get("on" + evt.Type, context);
                if (!onHandler.IsFunction)
                {
                    return;
                }

                var handlerArgs = BuildEventHandlerArguments(targetObj, evt, context);
                var handlerResult = onHandler.AsFunction().Invoke(handlerArgs, context, thisArg);
                if (handlerResult.IsBoolean && !handlerResult.ToBoolean())
                {
                    evt.PreventDefault();
                }

                ApplyLegacyEventFlags(evt);
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[EventTarget] Error in handler property for {evt.Type}: {ex.Message}", LogCategory.Events, ex);
                TryReportErrorToWindow(evt.Target, context, ex);
            }
        }

        private static FenValue[] BuildEventHandlerArguments(IObject targetObj, DomEvent evt, IExecutionContext context)
        {
            if (targetObj == null || evt == null)
            {
                return Array.Empty<FenValue>();
            }

            if (string.Equals(evt.Type, "error", StringComparison.Ordinal) && IsWindowLikeTarget(targetObj, context))
            {
                var message = evt.Get("message");
                var filename = evt.Get("filename");
                var lineno = evt.Get("lineno");
                var colno = evt.Get("colno");
                var error = evt.Get("error");

                return new[]
                {
                    message.IsUndefined ? FenValue.FromString(string.Empty) : message,
                    filename.IsUndefined ? FenValue.FromString(string.Empty) : filename,
                    lineno.IsUndefined ? FenValue.FromNumber(0) : lineno,
                    colno.IsUndefined ? FenValue.FromNumber(0) : colno,
                    error.IsUndefined ? FenValue.FromObject(evt) : error
                };
            }

            return new[] { FenValue.FromObject(evt) };
        }

        private static bool IsWindowLikeTarget(IObject targetObj, IExecutionContext context)
        {
            var env = context?.Environment;
            if (env != null)
            {
                var windowVal = env.Get("window");
                if (windowVal.IsObject && ReferenceEquals(windowVal.AsObject(), targetObj))
                {
                    return true;
                }
            }

            try
            {
                var selfVal = targetObj.Get("self", context);
                if (selfVal.IsObject && ReferenceEquals(selfVal.AsObject(), targetObj))
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void ApplyLegacyEventFlags(DomEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            var cancelBubbleVal = evt.Get("cancelBubble");
            if (cancelBubbleVal.IsBoolean && cancelBubbleVal.ToBoolean())
                evt.StopPropagation();
            var returnValueVal = evt.Get("returnValue");
            if (returnValueVal.IsBoolean && !returnValueVal.ToBoolean())
                evt.PreventDefault();
        }

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
            public Action<FenValue, FenObject> OnUnhandledRejection { get => _inner.OnUnhandledRejection; set => _inner.OnUnhandledRejection = value; }
            public Action<FenValue, string> OnUncaughtException { get => _inner.OnUncaughtException; set => _inner.OnUncaughtException = value; }
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
