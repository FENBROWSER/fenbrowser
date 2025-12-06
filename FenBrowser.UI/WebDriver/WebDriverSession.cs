using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.WebDriver
{
    /// <summary>
    /// Manages WebDriver session state including timeouts, window handles, and element references.
    /// </summary>
    public class WebDriverSession
    {
        public string SessionId { get; }
        public DateTime CreatedAt { get; }
        public bool IsActive { get; private set; } = true;

        // Timeouts (in milliseconds)
        public int ScriptTimeout { get; set; } = 30000;
        public int PageLoadTimeout { get; set; } = 300000;
        public int ImplicitWaitTimeout { get; set; } = 0;

        // Window management
        public string CurrentWindowHandle { get; set; }
        public List<string> WindowHandles { get; } = new List<string>();

        // Element cache (element ID -> element reference)
        private readonly Dictionary<string, object> _elementCache = new Dictionary<string, object>();
        private int _elementCounter = 0;

        // Cookie storage (uses WebDriverCookie from FenBrowser.FenEngine.Rendering)
        public Dictionary<string, WebDriverCookie> Cookies { get; } = new Dictionary<string, WebDriverCookie>();

        // Alert state
        public string PendingAlertText { get; set; }
        public bool HasPendingAlert => !string.IsNullOrEmpty(PendingAlertText);

        public WebDriverSession()
        {
            SessionId = Guid.NewGuid().ToString();
            CreatedAt = DateTime.UtcNow;
            CurrentWindowHandle = Guid.NewGuid().ToString();
            WindowHandles.Add(CurrentWindowHandle);
        }

        public void Close()
        {
            IsActive = false;
            _elementCache.Clear();
        }

        /// <summary>
        /// Registers an element and returns its WebDriver element ID.
        /// </summary>
        public string RegisterElement(object element)
        {
            var elementId = $"element-{++_elementCounter}-{Guid.NewGuid():N}";
            _elementCache[elementId] = element;
            return elementId;
        }

        /// <summary>
        /// Gets a cached element by its WebDriver ID.
        /// </summary>
        public object GetElement(string elementId)
        {
            return _elementCache.TryGetValue(elementId, out var element) ? element : null;
        }

        /// <summary>
        /// Creates a new window and returns its handle.
        /// </summary>
        public string CreateWindow()
        {
            var handle = Guid.NewGuid().ToString();
            WindowHandles.Add(handle);
            return handle;
        }

        /// <summary>
        /// Closes a window by handle.
        /// </summary>
        public bool CloseWindow(string handle)
        {
            if (WindowHandles.Count <= 1) return false;
            return WindowHandles.Remove(handle);
        }

        /// <summary>
        /// Switches to a different window.
        /// </summary>
        public bool SwitchToWindow(string handle)
        {
            if (!WindowHandles.Contains(handle)) return false;
            CurrentWindowHandle = handle;
            return true;
        }
    }
}

