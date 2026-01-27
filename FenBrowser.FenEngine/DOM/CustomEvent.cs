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
        /// Custom data associated with the event
        /// </summary>
        public IValue Detail { get; }

        /// <summary>
        /// Create a new CustomEvent
        /// </summary>
        /// <param name="type">Event type</param>
        /// <param name="bubbles">Whether the event bubbles</param>
        /// <param name="cancelable">Whether the event is cancelable</param>
        /// <param name="detail">Custom data to pass with the event</param>
        public CustomEvent(string type, bool bubbles = false, bool cancelable = false, IValue detail = null)
            : base(type, bubbles, cancelable)
        {
            Detail = detail ;
            
            // Add detail property to JavaScript-accessible object
            Set("detail", (FenValue)Detail);
        }
    }
}
