// =============================================================================
// SandboxEnforcer.cs
// WebDriver Security - Session Isolation
// 
// PURPOSE: Ensures test isolation and prevents cross-session interference.
// SECURITY: Separate storage, cookies, cache per session.
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace FenBrowser.WebDriver.Security
{
    /// <summary>
    /// Enforces session isolation (sandboxing).
    /// </summary>
    public class SandboxEnforcer
    {
        private readonly ConcurrentDictionary<string, SessionSandbox> _sandboxes = new();
        
        /// <summary>
        /// Create a sandbox for a session.
        /// </summary>
        public SessionSandbox CreateSandbox(string sessionId)
        {
            var sandbox = new SessionSandbox(sessionId);
            _sandboxes[sessionId] = sandbox;
            return sandbox;
        }
        
        /// <summary>
        /// Get sandbox for a session.
        /// </summary>
        public SessionSandbox GetSandbox(string sessionId)
        {
            return _sandboxes.GetValueOrDefault(sessionId);
        }
        
        /// <summary>
        /// Destroy a session's sandbox.
        /// </summary>
        public void DestroySandbox(string sessionId)
        {
            if (_sandboxes.TryRemove(sessionId, out var sandbox))
            {
                sandbox.Clear();
            }
        }
        
        /// <summary>
        /// Clear sandbox for a session (keep it but reset state).
        /// </summary>
        public void ClearSandbox(string sessionId)
        {
            if (_sandboxes.TryGetValue(sessionId, out var sandbox))
            {
                sandbox.Clear();
            }
        }
    }
    
    /// <summary>
    /// Isolated storage for a WebDriver session.
    /// </summary>
    public class SessionSandbox
    {
        public string SessionId { get; }
        public DateTime CreatedAt { get; }
        
        // Isolated storage
        private readonly Dictionary<string, string> _localStorage = new();
        private readonly Dictionary<string, string> _sessionStorage = new();
        private readonly Dictionary<string, Cookie> _cookies = new();
        
        public SessionSandbox(string sessionId)
        {
            SessionId = sessionId;
            CreatedAt = DateTime.UtcNow;
        }
        
        // Local Storage
        public void SetLocalStorage(string key, string value) => _localStorage[key] = value;
        public string GetLocalStorage(string key) => _localStorage.GetValueOrDefault(key);
        public void RemoveLocalStorage(string key) => _localStorage.Remove(key);
        public void ClearLocalStorage() => _localStorage.Clear();
        public IReadOnlyDictionary<string, string> GetAllLocalStorage() => _localStorage;
        
        // Session Storage
        public void SetSessionStorage(string key, string value) => _sessionStorage[key] = value;
        public string GetSessionStorage(string key) => _sessionStorage.GetValueOrDefault(key);
        public void RemoveSessionStorage(string key) => _sessionStorage.Remove(key);
        public void ClearSessionStorage() => _sessionStorage.Clear();
        
        // Cookies
        public void SetCookie(Cookie cookie) => _cookies[cookie.Name] = cookie;
        public Cookie GetCookie(string name) => _cookies.GetValueOrDefault(name);
        public void RemoveCookie(string name) => _cookies.Remove(name);
        public void ClearCookies() => _cookies.Clear();
        public IEnumerable<Cookie> GetAllCookies() => _cookies.Values;
        
        /// <summary>
        /// Clear all sandbox data.
        /// </summary>
        public void Clear()
        {
            _localStorage.Clear();
            _sessionStorage.Clear();
            _cookies.Clear();
        }
    }
    
    /// <summary>
    /// Cookie representation for sandbox.
    /// </summary>
    public class Cookie
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Domain { get; set; }
        public string Path { get; set; } = "/";
        public DateTime? Expiry { get; set; }
        public bool Secure { get; set; }
        public bool HttpOnly { get; set; }
        public string SameSite { get; set; }
        
        public bool IsExpired => Expiry.HasValue && Expiry.Value < DateTime.UtcNow;
    }
}
