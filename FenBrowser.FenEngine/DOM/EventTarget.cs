using FenBrowser.Core; // Added for FenLogger
using FenBrowser.Core.Dom;
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

            // 1. Initialize Event
            evt.Target = target;
            evt.IsTrusted = true; // Assumed trusted if dispatched by engine
            evt.ResetState(); // Reset path/phase if re-dispatching
            evt.UpdateJsProperties(context); // Sync JS object

            // 2. Build Propagation Path
            var path = new List<Element>();
            var current = target.Parent;
            while (current is Element el)
            {
                path.Add(el);
                current = current.Parent;
            }
            // Path is currently Target -> Root. Invert for capture, use as-is for bubble (but skip target in list)
            
            // Populate event.path (composedPath)
            evt.Path.Clear();
            evt.Path.Add(target);
            evt.Path.AddRange(path);

            // 3. CAPTURING PHASE (Root -> Parent)
            evt.EventPhase = DomEvent.CAPTURING_PHASE;
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (evt.PropagationStopped) break;
                var ancestor = path[i];
                evt.CurrentTarget = ancestor;
                evt.UpdateJsProperties(context);
                InvokeListeners(ancestor, evt, context, true);
            }

            // 4. AT TARGET PHASE
            if (!evt.PropagationStopped)
            {
                evt.EventPhase = DomEvent.AT_TARGET;
                evt.CurrentTarget = target;
                evt.UpdateJsProperties(context);
                InvokeListeners(target, evt, context, false); // Spec says "at target" listeners run in order of registration, 
                                                              // but typically registered as "capture=false" or "catpure=true". 
                                                              // Listeners with capture=false run here? 
                                                              // Actually, distinct Listeners. 
                                                              // Both capture and bubble listeners run at target.
                                                              // We simply invoke all that match. 
                                                              // Our registry separates capture/bubble.
                
                // Invoke Capture listeners at target? No, usually Capture stops *before* target.
                // Re-reading spec: "Listeners registered for the capturing phase... but not on the event target itself."
                // Wait, "Any event listeners registered on the eventTarget... are triggered."
                // They run during the "At Target" phase.
                // For "At Target", we verify we run both? 
                InvokeListeners(target, evt, context, true);  // Capture listeners at target
                InvokeListeners(target, evt, context, false); // Bubble listeners at target
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
                    InvokeListeners(ancestor, evt, context, false);
                }
            }

            // 6. Reset/Finalize
            evt.EventPhase = DomEvent.NONE;
            evt.CurrentTarget = null;
            evt.UpdateJsProperties(context);

            return !evt.DefaultPrevented;
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

                // Execute callback
                // Execute callback
                try
                {
                    var func = listener.Callback.AsFunction();
                    if (func != null)
                    {
                        var evtVal = FenValue.FromObject(evt); 
                        var args = new IValue[] { evtVal };
                        var thisBinding = FenValue.FromObject(new ElementWrapper(element, context));

                        if (context.ExecuteFunction != null)
                        {
                            context.ExecuteFunction(listener.Callback, args);
                        }
                        else
                        {
                            context.CheckCallStackLimit();
                            context.CheckExecutionTimeLimit();
                            func.Invoke(args, new ContextShim(context, thisBinding));
                        }
                    }
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[EventTarget] Error in listener for {evt.Type}: {ex.Message}", LogCategory.Events, ex);
                }
            }
        }

        // Shim to pass 'ThisBinding' correct for the listener
        private class ContextShim : IExecutionContext
        {
            private readonly IExecutionContext _inner;
            private readonly IValue _thisBinding;

            public ContextShim(IExecutionContext inner, IValue thisBinding)
            {
                _inner = inner;
                _thisBinding = thisBinding;
            }

            public IValue ThisBinding { get => _thisBinding; set { } }
            public IPermissionManager Permissions => _inner.Permissions;
            public FenBrowser.FenEngine.Security.IResourceLimits Limits => _inner.Limits;
            public int CallStackDepth => _inner.CallStackDepth;
            public DateTime ExecutionStart => _inner.ExecutionStart;
            public bool ShouldContinue => _inner.ShouldContinue;
            public Action RequestRender => _inner.RequestRender;
            public void SetRequestRender(Action action) => _inner.SetRequestRender(action);
            public Action<Action, int> ScheduleCallback { get => _inner.ScheduleCallback; set => _inner.ScheduleCallback = value; }
            public Action<Action> ScheduleMicrotask { get => _inner.ScheduleMicrotask; set => _inner.ScheduleMicrotask = value; }
            public Func<IValue, IValue[], IValue> ExecuteFunction { get => _inner.ExecuteFunction; set => _inner.ExecuteFunction = value; }
            public IModuleLoader ModuleLoader { get => _inner.ModuleLoader; set => _inner.ModuleLoader = value; }
            public Action<MutationRecord> OnMutation { get => _inner.OnMutation; set => _inner.OnMutation = value; }
            public string CurrentUrl { get => _inner.CurrentUrl; set => _inner.CurrentUrl = value; }
            public FenEnvironment Environment { get => _inner.Environment; set => _inner.Environment = value; }
            public void PushCallFrame(string name) => _inner.PushCallFrame(name);
            public void PopCallFrame() => _inner.PopCallFrame();
            public void CheckCallStackLimit() => _inner.CheckCallStackLimit();
            public void CheckExecutionTimeLimit() => _inner.CheckExecutionTimeLimit();
        }
    }
}
