using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

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

        // Dynamic properties (set during dispatch)
        public LiteElement Target { get; set; }
        public LiteElement CurrentTarget { get; set; }
        public int EventPhase { get; set; } = NONE;

        // State flags
        public bool DefaultPrevented { get; private set; }
        public bool PropagationStopped { get; private set; }
        public bool ImmediatePropagationStopped { get; private set; }
        public bool IsTrusted { get; set; }

        // Propagation path
        public List<LiteElement> Path { get; set; } = new List<LiteElement>();

        /// <summary>
        /// Create a new DOM Event
        /// </summary>
        /// <param name="type">Event type (e.g., "click", "submit")</param>
        /// <param name="bubbles">Whether the event bubbles up through the DOM</param>
        /// <param name="cancelable">Whether the event can be cancelled</param>
        /// <param name="composed">Whether the event crosses shadow DOM boundaries</param>
        public DomEvent(string type, bool bubbles = false, bool cancelable = false, bool composed = false)
        {
            Type = type ?? "";
            Bubbles = bubbles;
            Cancelable = cancelable;
            Composed = composed;
            TimeStamp = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            IsTrusted = false;

            // Set up JavaScript-accessible properties
            InitializeJsProperties();
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

            if (Target != null)
                Set("target", FenValue.FromObject(new ElementWrapper(Target, context)));
            else
                Set("target", FenValue.Null);

            if (CurrentTarget != null)
                Set("currentTarget", FenValue.FromObject(new ElementWrapper(CurrentTarget, context)));
            else
                Set("currentTarget", FenValue.Null);
        }

        /// <summary>
        /// Cancels the event if it is cancelable
        /// </summary>
        private IValue PreventDefault(IValue[] args, IValue thisVal)
        {
            if (Cancelable)
            {
                DefaultPrevented = true;
                Set("defaultPrevented", FenValue.FromBoolean(true));
            }
            return FenValue.Undefined;
        }

        /// <summary>
        /// Prevents further propagation of the current event in the capturing and bubbling phases
        /// </summary>
        private IValue StopPropagation(IValue[] args, IValue thisVal)
        {
            PropagationStopped = true;
            return FenValue.Undefined;
        }

        /// <summary>
        /// Prevents other listeners of the same event from being called
        /// </summary>
        private IValue StopImmediatePropagation(IValue[] args, IValue thisVal)
        {
            PropagationStopped = true;
            ImmediatePropagationStopped = true;
            return FenValue.Undefined;
        }

        /// <summary>
        /// Returns the event's path (ancestors from target to root)
        /// </summary>
        private IValue ComposedPath(IValue[] args, IValue thisVal)
        {
            var arr = new FenObject();
            for (int i = 0; i < Path.Count; i++)
            {
                // We'd need context to create ElementWrapper, so return element references
                arr.Set(i.ToString(), FenValue.FromString(Path[i].Tag ?? "#unknown"));
            }
            arr.Set("length", FenValue.FromNumber(Path.Count));
            return FenValue.FromObject(arr);
        }

        private IValue InitEvent(IValue[] args, IValue thisVal)
        {
            if (args.Length >= 1) Type = args[0].ToString();
            if (args.Length >= 2) Bubbles = args[1].ToBoolean();
            if (args.Length >= 3) Cancelable = args[2].ToBoolean();
            InitializeJsProperties();
            return FenValue.Undefined;
        }

        /// <summary>
        /// Reset event state for re-dispatch (not typically needed but spec-compliant)
        /// </summary>
        public void ResetState()
        {
            DefaultPrevented = false;
            PropagationStopped = false;
            ImmediatePropagationStopped = false;
            EventPhase = NONE;
            Target = null;
            CurrentTarget = null;
            Path.Clear();
        }
    }
}
