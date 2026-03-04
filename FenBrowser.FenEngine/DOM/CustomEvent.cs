using System;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// DOM CustomEvent interface implementation.
    /// Extends Event with a detail property for passing custom data.
    /// </summary>
    public class CustomEvent : DomEvent
    {
        /// <summary>
        /// Custom data associated with the event.
        /// </summary>
        public FenValue Detail { get; private set; } = FenValue.Null;

        /// <summary>
        /// Create a new CustomEvent.
        /// </summary>
        public CustomEvent(string type, bool bubbles = false, bool cancelable = false, IValue detail = null)
            : base(type, bubbles, cancelable)
        {
            if (detail is FenValue fv)
            {
                Detail = fv;
            }

            Set("detail", Detail);
            Set("initCustomEvent", FenValue.FromFunction(new FenFunction("initCustomEvent", InitCustomEvent)));
        }

        private FenValue InitCustomEvent(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1)
            {
                throw new Exception("TypeError: Failed to execute 'initCustomEvent': 1 argument required, but only 0 present.");
            }

            // Per DOM: must be ignored while dispatching.
            if (EventPhase != NONE)
            {
                return FenValue.Undefined;
            }

            Type = args[0].ToString();
            Bubbles = args.Length >= 2 && args[1].ToBoolean();
            Cancelable = args.Length >= 3 && args[2].ToBoolean();
            Detail = args.Length >= 4 ? args[3] : FenValue.Null;
            Initialized = true;

            ResetState();

            Set("type", FenValue.FromString(Type));
            Set("bubbles", FenValue.FromBoolean(Bubbles));
            Set("cancelable", FenValue.FromBoolean(Cancelable));
            Set("detail", Detail);
            Set("defaultPrevented", FenValue.FromBoolean(false));
            Set("returnValue", FenValue.FromBoolean(true));
            Set("cancelBubble", FenValue.FromBoolean(false));

            return FenValue.Undefined;
        }
    }
}
