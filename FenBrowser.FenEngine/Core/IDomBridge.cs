using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Bridge interface to allow FenRuntime (JS) to communicate with the Engine's DOM.
    /// </summary>
    public interface IDomBridge
    {
        FenValue GetElementById(string id);
        FenValue QuerySelector(string selector);
        void AddEventListener(string elementId, string eventName, FenValue callback);
        FenValue CreateElement(string tagName);
        FenValue CreateTextNode(string text);
        void AppendChild(FenValue parent, FenValue child);
        void SetAttribute(FenValue element, string name, string value);
    }
}
