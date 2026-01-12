using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Bridge interface to allow FenRuntime (JS) to communicate with the Engine's DOM.
    /// </summary>
    public interface IDomBridge
    {
        /// <summary>
        /// Finds an element by ID.
        /// </summary>
        IValue GetElementById(string id);

        /// <summary>
        /// Finds the first element matching a CSS selector.
        /// </summary>
        IValue QuerySelector(string selector);

        /// <summary>
        /// Adds an event listener to an element.
        /// </summary>
        void AddEventListener(string elementId, string eventName, IValue callback);
    }
}
