using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// DOM Level 3 Event interface implementation.
    /// Represents an event that takes place in the DOM.
    /// </summary>
    public class DomEvent : FenObject
    {
        // Event phase constants
        public const int NONE = 0;
        public const int CAPTURING_PHASE = 1;
        public const int AT_TARGET = 2;
        public const int BUBBLING_PHASE = 3;

        // Core event properties (read-only after construction)
        public string Type { get; protected set; }
        public bool Bubbles { get; protected set; }
        public bool Cancelable { get; protected set; }
        public bool Composed { get; protected set; }
        public double TimeStamp { get; }

        // Public methods for internal use
        public void StopPropagation() => PropagationStopped = true;
        public void StopImmediatePropagation() { PropagationStopped = true; ImmediatePropagationStopped = true; }

        /// <summary>
        /// Set by EventTarget when invoking a passive listener.
        /// While true, preventDefault() is a no-op (UI Events §4.3.2 passive flag).
        /// </summary>
        internal bool IsPassiveContext { get; set; }

        public void PreventDefault()
        {
            if (IsPassiveContext)
            {
                // UI Events §4.3.2: "If the passive flag is set, do nothing."
                // Log once per dispatch to help developers diagnose the violation.
                FenBrowser.Core.FenLogger.Warn(
                    $"[Event] preventDefault() ignored inside passive listener for '{Type}'.",
                    FenBrowser.Core.Logging.LogCategory.Events);
                return;
            }
            if (Cancelable) DefaultPrevented = true;
        }

        // Dynamic properties (set during dispatch)
        public Element Target { get; set; }
        public Element CurrentTarget { get; set; }
        public int EventPhase { get; set; } = NONE;

        // State flags
        public bool DefaultPrevented { get; private set; }
        public bool PropagationStopped { get; private set; }
        public bool ImmediatePropagationStopped { get; private set; }
        public bool IsTrusted { get; set; }
        public bool Initialized { get; set; } = true;
        internal bool IsDispatching { get; private set; }
        public bool ReturnValue => !DefaultPrevented;
        public bool CancelBubble => PropagationStopped;

        // Propagation PropagationPath
        internal List<Element> PropagationPath { get; } = new List<Element>();
        public List<Element> Path => PropagationPath;

        private IExecutionContext _context;

        private void SetOwnProperty(string key, FenValue value, IExecutionContext context = null)
        {
            base.SetWithReceiver(key, value, FenValue.FromObject(this), context);
        }

        /// <summary>
        /// Create a new DOM Event
        /// </summary>
        /// <param name="type">Event type (e.g., "click", "submit")</param>
        /// <param name="bubbles">Whether the event bubbles up through the DOM</param>
        /// <param name="cancelable">Whether the event can be cancelled</param>
        /// <param name="composed">Whether the event crosses shadow DOM boundaries</param>
        public DomEvent(string type, bool bubbles = false, bool cancelable = false, bool composed = false, IExecutionContext context = null, bool initialized = true)
        {
            Type = type ?? "";
            _context = context;
            Bubbles = bubbles;
            Cancelable = cancelable;
            Composed = composed;
            TimeStamp = ResolveEventTimeStamp(context);
            IsTrusted = false;
            Initialized = initialized;

            // Set up JavaScript-accessible properties
            InitializeJsProperties();
        }

        private static double ResolveEventTimeStamp(IExecutionContext context)
        {
            try
            {
                var perfVal = context?.Environment?.Get("performance") ?? FenValue.Undefined;
                if (perfVal.IsObject)
                {
                    var nowFn = perfVal.AsObject().Get("now", context);
                    if (nowFn.IsFunction)
                    {
                        var now = nowFn.AsFunction().Invoke(Array.Empty<FenValue>(), context, perfVal);
                        if (now.IsNumber)
                        {
                            var ts = now.ToNumber();
                            if (!double.IsNaN(ts) && !double.IsInfinity(ts))
                                return ts;
                        }
                    }
                }
            }
            catch
            {
            }

            return Environment.TickCount64;
        }
        private void InitializeJsProperties()
        {
            // Read-only properties
            Set("type", FenValue.FromString(Type));
            Set("bubbles", FenValue.FromBoolean(Bubbles));
            Set("cancelable", FenValue.FromBoolean(Cancelable));
            Set("composed", FenValue.FromBoolean(Composed));
            Set("timeStamp", FenValue.FromNumber(TimeStamp));
            Set("isTrusted", FenValue.FromBoolean(IsTrusted));

            // Phase constants
            Set("NONE", FenValue.FromNumber(NONE));
            Set("CAPTURING_PHASE", FenValue.FromNumber(CAPTURING_PHASE));
            Set("AT_TARGET", FenValue.FromNumber(AT_TARGET));
            Set("BUBBLING_PHASE", FenValue.FromNumber(BUBBLING_PHASE));

            // Dynamic properties (updated during dispatch)
            Set("target", FenValue.Null);
            Set("currentTarget", FenValue.Null);
            Set("eventPhase", FenValue.FromNumber(NONE));
            Set("defaultPrevented", FenValue.FromBoolean(false));
            Set("returnValue", FenValue.FromBoolean(true));
            Set("cancelBubble", FenValue.FromBoolean(false));
            Set("srcElement", FenValue.Null);

            // Methods
            Set("preventDefault", FenValue.FromFunction(new FenFunction("preventDefault", PreventDefault)));
            Set("stopPropagation", FenValue.FromFunction(new FenFunction("stopPropagation", StopPropagation)));
            Set("stopImmediatePropagation", FenValue.FromFunction(new FenFunction("stopImmediatePropagation", StopImmediatePropagation)));
            Set("composedPath", FenValue.FromFunction(new FenFunction("composedPath", ComposedPath)));
            Set("initEvent", FenValue.FromFunction(new FenFunction("initEvent", InitEvent)));
        }

        /// <summary>
        /// Update JavaScript-accessible properties to reflect current state
        /// </summary>
        public void UpdateJsProperties(IExecutionContext context)
        {
            Set("eventPhase", FenValue.FromNumber(EventPhase));
            Set("defaultPrevented", FenValue.FromBoolean(DefaultPrevented));
            Set("isTrusted", FenValue.FromBoolean(IsTrusted));
            Set("returnValue", FenValue.FromBoolean(!DefaultPrevented));
            Set("cancelBubble", FenValue.FromBoolean(PropagationStopped));

            if (Target != null)
            {
                var wrappedTarget = DomWrapperFactory.Wrap(Target, context);
                Set("target", wrappedTarget);
                Set("srcElement", wrappedTarget);
            }
            else
            {
                Set("target", FenValue.Null);
                Set("srcElement", FenValue.Null);
            }

            if (CurrentTarget != null)
                Set("currentTarget", DomWrapperFactory.Wrap(CurrentTarget, context));
            else
                Set("currentTarget", FenValue.Null);
        }

        /// <summary>
        /// Cancels the event if it is cancelable
        /// </summary>
        private FenValue PreventDefault(FenValue[] args, FenValue thisVal)
        {
            if (IsPassiveContext)
            {
                return FenValue.Undefined;
            }

            if (Cancelable)
            {
                DefaultPrevented = true;
                Set("defaultPrevented", FenValue.FromBoolean(true));
                Set("returnValue", FenValue.FromBoolean(false));
            }
            return FenValue.Undefined;
        }

        /// <summary>
        /// Prevents further propagation of the current event in the capturing and bubbling phases
        /// </summary>
        private FenValue StopPropagation(FenValue[] args, FenValue thisVal)
        {
            PropagationStopped = true;
            Set("cancelBubble", FenValue.FromBoolean(true));
            return FenValue.Undefined;
        }

        /// <summary>
        /// Prevents other listeners of the same event from being called
        /// </summary>
        private FenValue StopImmediatePropagation(FenValue[] args, FenValue thisVal)
        {
            PropagationStopped = true;
            ImmediatePropagationStopped = true;
            Set("cancelBubble", FenValue.FromBoolean(true));
            return FenValue.Undefined;
        }

        /// <summary>
        /// Returns the event's PropagationPath (ancestors from target to root)
        /// </summary>
        private FenValue ComposedPath(FenValue[] args, FenValue thisVal)
        {
            // Generic EventTarget (e.g. window) dispatch uses AT_TARGET-only path.
            // Per DOM, composedPath() should expose [target] during dispatch, then [].
            var phaseValue = Get("eventPhase");
            var inDispatchPhase = phaseValue.IsNumber && phaseValue.ToNumber() != NONE;
            if ((PropagationPath == null || PropagationPath.Count == 0) && inDispatchPhase)
            {
                var arrSingle = FenObject.CreateArray();
                var targetValue = Get("target");
                if (!targetValue.IsUndefined && !targetValue.IsNull)
                {
                    arrSingle.Set("0", targetValue);
                    arrSingle.Set("length", FenValue.FromNumber(1));
                    return FenValue.FromObject(arrSingle);
                }
            }

            // Requires context to wrap node path entries.
            if (_context == null) return FenValue.FromObject(FenObject.CreateArray());

            var nodes = new List<Node>();
            if (PropagationPath != null)
            {
                // PropagationPath currently contains Elements, convert to Nodes
                foreach (var el in PropagationPath) nodes.Add(el);
            }

            var arr = FenObject.CreateArray();
            for (int i = 0; i < nodes.Count; i++)
            {
                arr.Set(i.ToString(), DomWrapperFactory.Wrap(nodes[i], _context));
            }
            arr.Set("length", FenValue.FromNumber(nodes.Count));
            return FenValue.FromObject(arr);
        }

        private FenValue InitEvent(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1)
            {
                throw new FenTypeError("TypeError: Failed to execute 'initEvent': 1 argument required, but only 0 present.");
            }

            // Per DOM: initEvent must be ignored while dispatching.
            if (IsDispatching)
            {
                return FenValue.Undefined;
            }

            Type = args[0].ToString();
            Initialized = true;
            Bubbles = args.Length >= 2 && args[1].ToBoolean();
            Cancelable = args.Length >= 3 && args[2].ToBoolean();
            ResetInternalStateForInitialization();
            InitializeJsProperties();
            return FenValue.Undefined;
        }
        private bool TryHandleLegacyMutableProperty(string key, FenValue value, IExecutionContext context)
        {
            var normalized = (key ?? string.Empty).ToLowerInvariant();
            if (normalized == "returnvalue")
            {
                var allowDefault = value.ToBoolean();
                // Legacy behavior: false implies preventDefault() only when cancelable.
                if (!allowDefault)
                {
                    if (Cancelable)
                    {
                        DefaultPrevented = true;
                        SetOwnProperty("defaultPrevented", FenValue.FromBoolean(true), context);
                        SetOwnProperty("returnValue", FenValue.FromBoolean(false), context);
                    }
                    else
                    {
                        SetOwnProperty("returnValue", FenValue.FromBoolean(true), context);
                    }
                    // If not cancelable, assignment has no effect.
                    return true;
                }

                // Per legacy behavior, once canceled, setting returnValue=true must not uncancel.
                if (!DefaultPrevented)
                {
                    SetOwnProperty("defaultPrevented", FenValue.FromBoolean(false), context);
                    SetOwnProperty("returnValue", FenValue.FromBoolean(true), context);
                }
                else
                {
                    SetOwnProperty("returnValue", FenValue.FromBoolean(false), context);
                }
                return true;
            }

            if (normalized == "cancelbubble")
            {
                var stop = value.ToBoolean();
                // Per legacy semantics, once true it cannot be reset to false.
                if (stop)
                {
                    PropagationStopped = true;
                    SetOwnProperty("cancelBubble", FenValue.FromBoolean(true), context);
                }
                else if (!PropagationStopped)
                {
                    SetOwnProperty("cancelBubble", FenValue.FromBoolean(false), context);
                }
                return true;
            }

            return false;
        }

        public override void Set(string key, FenValue value, IExecutionContext context = null)
        {
            if (TryHandleLegacyMutableProperty(key, value, context))
            {
                return;
            }

            SetOwnProperty(key, value, context);
        }

        public override void Set(string key, FenValue value, bool strict)
        {
            if (TryHandleLegacyMutableProperty(key, value, null))
            {
                return;
            }

            base.Set(key, value, strict);
        }

        public override void SetWithReceiver(string key, FenValue value, FenValue receiver, IExecutionContext context = null)
        {
            if (TryHandleLegacyMutableProperty(key, value, context))
            {
                return;
            }

            base.SetWithReceiver(key, value, receiver, context);
        }

        public override FenValue Get(string key, IExecutionContext context = null)
        {
            var normalized = (key ?? string.Empty).ToLowerInvariant();
            return normalized switch
            {
                "cancelbubble" => FenValue.FromBoolean(PropagationStopped),
                "returnvalue" => FenValue.FromBoolean(!DefaultPrevented),
                "defaultprevented" => FenValue.FromBoolean(DefaultPrevented),
                "eventphase" => FenValue.FromNumber(EventPhase),
                "istrusted" => FenValue.FromBoolean(IsTrusted),
                _ => base.Get(key, context)
            };
        }

        public override FenValue GetWithReceiver(string key, FenValue receiver, IExecutionContext context = null)
        {
            var normalized = (key ?? string.Empty).ToLowerInvariant();
            return normalized switch
            {
                "cancelbubble" => FenValue.FromBoolean(PropagationStopped),
                "returnvalue" => FenValue.FromBoolean(!DefaultPrevented),
                "defaultprevented" => FenValue.FromBoolean(DefaultPrevented),
                "eventphase" => FenValue.FromNumber(EventPhase),
                "istrusted" => FenValue.FromBoolean(IsTrusted),
                _ => base.GetWithReceiver(key, receiver, context)
            };
        }
        /// <summary>
        /// Reset event state for re-dispatch (not typically needed but spec-compliant)
        /// </summary>
        public void ResetState()
        {
            // Preserve pre-dispatch propagation flags for the first dispatch.
            // They are cleared after dispatch finalization so the same event can
            // be re-dispatched per DOM/WPT expectations.
            EventPhase = NONE;
            Target = null;
            CurrentTarget = null;
            PropagationPath.Clear();
        }

        protected void ResetInternalStateForInitialization()
        {
            DefaultPrevented = false;
            PropagationStopped = false;
            ImmediatePropagationStopped = false;
            IsPassiveContext = false;
            EventPhase = NONE;
            Target = null;
            CurrentTarget = null;
            PropagationPath.Clear();
        }

        internal void BeginDispatch()
        {
            if (IsDispatching)
            {
                throw new InvalidOperationException("InvalidStateError: Failed to execute 'dispatchEvent' on 'EventTarget': The event is already being dispatched.");
            }

            IsDispatching = true;
            IsPassiveContext = false;
        }

        internal void EndDispatch()
        {
            IsDispatching = false;
            IsPassiveContext = false;
        }

        public void ClearPropagationFlags()
        {
            PropagationStopped = false;
            ImmediatePropagationStopped = false;
            SetOwnProperty("cancelBubble", FenValue.FromBoolean(false));
        }
        public void FinalizeDispatchState()
        {
            EventPhase = NONE;
            CurrentTarget = null;
            PropagationStopped = false;
            ImmediatePropagationStopped = false;
            Set("currentTarget", FenValue.Null);
            Set("eventPhase", FenValue.FromNumber(NONE));
            SetOwnProperty("cancelBubble", FenValue.FromBoolean(false));
            Set("defaultPrevented", FenValue.FromBoolean(DefaultPrevented));
            SetOwnProperty("returnValue", FenValue.FromBoolean(!DefaultPrevented));
        }
    }
}














