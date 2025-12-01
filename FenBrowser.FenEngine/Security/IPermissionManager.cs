using System;

namespace FenBrowser.FenEngine.Security
{
    /// <summary>
    /// JavaScript permissions enum.
    /// Permissions must be explicitly granted (deny by default).
    /// </summary>
    [Flags]
    public enum JsPermissions : long
    {
        None = 0,
        
        // Basic features
        Console = 1 << 0,            // console.log, console.error
        Math = 1 << 1,               // Math object
        Date = 1 << 2,               // Date object
        Json = 1 << 3,               // JSON.parse/stringify
        
        // DOM Access
        DomRead = 1 << 4,            // document.getElementById, querySelector
        DomWrite = 1 << 5,           // innerHTML, textContent
        DomEvents = 1 << 6,          // addEventListener
        
        // Network
        Fetch = 1 << 7,              // fetch() API
        XmlHttpRequest = 1 << 8,     // XMLHttpRequest
        
        // Storage
        LocalStorage = 1 << 9,       // localStorage
        SessionStorage = 1 << 10,    // sessionStorage
        Cookies = 1 << 11,           // document.cookie
        
        // Navigation
        Location = 1 << 12,          // window.location
        History = 1 << 13,           // history.pushState
        
        // Timers
        SetTimeout = 1 << 14,        // setTimeout
        SetInterval = 1 << 15,       // setInterval
        RequestAnimationFrame = 1 << 16,
        
        // Advanced features
        Workers = 1 << 17,           // Web Workers
        ServiceWorkers = 1 << 18,    // Service Workers
        Notifications = 1 << 19,     // Notification API
        Geolocation = 1 << 20,       // navigator.geolocation
        
        // DANGEROUS - Never grant these
        Eval = 1 << 30,              // eval(), Function constructor
        
        // Convenience combinations
        BasicWeb = Console | Math | Date | Json | DomRead,
        StandardWeb = BasicWeb | DomWrite | DomEvents | Fetch | LocalStorage | SetTimeout,
        AllSafe = StandardWeb | History | Location | RequestAnimationFrame
    }

    /// <summary>
    /// Manages JavaScript permissions and enforces security boundaries
    /// </summary>
    public interface IPermissionManager
    {
        /// <summary>
        /// Check if a permission is granted
        /// </summary>
        bool Check(JsPermissions permission);

        /// <summary>
        /// Check permission and log if denied
        /// </summary>
        bool CheckAndLog(JsPermissions permission, string operation);

        /// <summary>
        /// Grant a permission
        /// </summary>
        void Grant(JsPermissions permission);

        /// <summary>
        /// Revoke a permission
        /// </summary>
        void Revoke(JsPermissions permission);

        /// <summary>
        /// Log a security violation
        /// </summary>
        void LogViolation(JsPermissions permission, string operation, string details = null);

        /// <summary>
        /// Get all violations logged so far
        /// </summary>
        System.Collections.Generic.IReadOnlyList<SecurityViolation> GetViolations();
    }

    /// <summary>
    /// Represents a security violation
    /// </summary>
    public class SecurityViolation
    {
        public DateTime Timestamp { get; set; }
        public JsPermissions Permission { get; set; }
        public string Operation { get; set; }
        public string Details { get; set; }
    }
}
