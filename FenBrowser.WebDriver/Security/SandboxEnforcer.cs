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
using System.Linq;

namespace FenBrowser.WebDriver.Security
{
    /// <summary>
    /// Enforces session isolation (sandboxing).
    /// </summary>
    public class SandboxEnforcer
    {
        private readonly ConcurrentDictionary<string, SessionSandbox> _sandboxes = new(StringComparer.Ordinal);

        /// <summary>
        /// Create a sandbox for a session. Session identifiers are unique; silently
        /// replacing an existing sandbox can leave references to the old state alive
        /// and split isolation ownership between two objects.
        /// </summary>
        public SessionSandbox CreateSandbox(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID is required.", nameof(sessionId));

            var sandbox = new SessionSandbox(sessionId);
            if (!_sandboxes.TryAdd(sessionId, sandbox))
            {
                sandbox.Clear();
                throw new InvalidOperationException($"A sandbox already exists for session '{sessionId}'.");
            }

            return sandbox;
        }

        /// <summary>
        /// Get sandbox for a session.
        /// </summary>
        public SessionSandbox GetSandbox(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return null;

            return _sandboxes.TryGetValue(sessionId, out var sandbox) ? sandbox : null;
        }

        /// <summary>
        /// Destroy a session's sandbox.
        /// </summary>
        public void DestroySandbox(string sessionId)
        {
            if (!string.IsNullOrWhiteSpace(sessionId) &&
                _sandboxes.TryRemove(sessionId, out var sandbox))
            {
                sandbox.Clear();
            }
        }

        /// <summary>
        /// Clear sandbox for a session (keep it but reset state).
        /// </summary>
        public void ClearSandbox(string sessionId)
        {
            if (!string.IsNullOrWhiteSpace(sessionId) &&
                _sandboxes.TryGetValue(sessionId, out var sandbox))
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
        private readonly record struct CookieKey(string Name, string Domain, string Path);

        public string SessionId { get; }
        public DateTime CreatedAt { get; }

        private readonly ConcurrentDictionary<string, string> _localStorage = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _sessionStorage = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<CookieKey, Cookie> _cookies = new();

        public SessionSandbox(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID is required.", nameof(sessionId));

            SessionId = sessionId;
            CreatedAt = DateTime.UtcNow;
        }

        // Local Storage
        public void SetLocalStorage(string key, string value)
        {
            ArgumentNullException.ThrowIfNull(key);
            _localStorage[key] = value ?? string.Empty;
        }

        public string GetLocalStorage(string key) =>
            key != null && _localStorage.TryGetValue(key, out var value) ? value : null;

        public void RemoveLocalStorage(string key)
        {
            if (key != null)
                _localStorage.TryRemove(key, out _);
        }

        public void ClearLocalStorage() => _localStorage.Clear();

        // Return a detached snapshot. Returning the backing Dictionary behind an
        // IReadOnlyDictionary type previously allowed callers to downcast and mutate
        // supposedly isolated state without going through the sandbox API.
        public IReadOnlyDictionary<string, string> GetAllLocalStorage() =>
            new Dictionary<string, string>(_localStorage, StringComparer.Ordinal);

        // Session Storage
        public void SetSessionStorage(string key, string value)
        {
            ArgumentNullException.ThrowIfNull(key);
            _sessionStorage[key] = value ?? string.Empty;
        }

        public string GetSessionStorage(string key) =>
            key != null && _sessionStorage.TryGetValue(key, out var value) ? value : null;

        public void RemoveSessionStorage(string key)
        {
            if (key != null)
                _sessionStorage.TryRemove(key, out _);
        }

        public void ClearSessionStorage() => _sessionStorage.Clear();

        // Cookies. Cookie identity includes name + domain + path; using only Name
        // caused legal same-name cookies on different scopes to overwrite each other.
        public void SetCookie(Cookie cookie)
        {
            ArgumentNullException.ThrowIfNull(cookie);
            if (string.IsNullOrWhiteSpace(cookie.Name))
                throw new ArgumentException("Cookie name is required.", nameof(cookie));

            var normalized = CloneCookie(cookie);
            var key = ToCookieKey(normalized);
            if (normalized.IsExpired)
            {
                _cookies.TryRemove(key, out _);
                return;
            }

            _cookies[key] = normalized;
        }

        /// <summary>
        /// Compatibility lookup by name. If multiple scoped cookies share the name,
        /// prefer the longest path and then the most recently inserted dictionary view.
        /// Callers that know scope should use GetCookie(name, domain, path).
        /// </summary>
        public Cookie GetCookie(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            PurgeExpiredCookies();
            Cookie best = null;
            foreach (var pair in _cookies)
            {
                if (!string.Equals(pair.Key.Name, name, StringComparison.Ordinal))
                    continue;

                if (best == null || (pair.Value.Path?.Length ?? 0) > (best.Path?.Length ?? 0))
                    best = pair.Value;
            }

            return best == null ? null : CloneCookie(best);
        }

        public Cookie GetCookie(string name, string domain, string path = "/")
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var key = new CookieKey(name, NormalizeDomain(domain), NormalizePath(path));
            if (!_cookies.TryGetValue(key, out var cookie))
                return null;

            if (cookie.IsExpired)
            {
                _cookies.TryRemove(key, out _);
                return null;
            }

            return CloneCookie(cookie);
        }

        public void RemoveCookie(string name)
        {
            if (string.IsNullOrEmpty(name))
                return;

            foreach (var pair in _cookies)
            {
                if (string.Equals(pair.Key.Name, name, StringComparison.Ordinal))
                    _cookies.TryRemove(pair.Key, out _);
            }
        }

        public void RemoveCookie(string name, string domain, string path = "/")
        {
            if (string.IsNullOrEmpty(name))
                return;

            _cookies.TryRemove(
                new CookieKey(name, NormalizeDomain(domain), NormalizePath(path)),
                out _);
        }

        public void ClearCookies() => _cookies.Clear();

        public IEnumerable<Cookie> GetAllCookies()
        {
            PurgeExpiredCookies();
            return _cookies.Values.Select(CloneCookie).ToArray();
        }

        /// <summary>
        /// Clear all sandbox data.
        /// </summary>
        public void Clear()
        {
            _localStorage.Clear();
            _sessionStorage.Clear();
            _cookies.Clear();
        }

        private void PurgeExpiredCookies()
        {
            foreach (var pair in _cookies)
            {
                if (pair.Value.IsExpired)
                    _cookies.TryRemove(pair.Key, out _);
            }
        }

        private static CookieKey ToCookieKey(Cookie cookie) =>
            new(cookie.Name, NormalizeDomain(cookie.Domain), NormalizePath(cookie.Path));

        private static string NormalizeDomain(string domain) =>
            string.IsNullOrWhiteSpace(domain)
                ? string.Empty
                : domain.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();

        private static string NormalizePath(string path) =>
            string.IsNullOrEmpty(path) || path[0] != '/' ? "/" : path;

        private static Cookie CloneCookie(Cookie cookie) => new()
        {
            Name = cookie.Name ?? string.Empty,
            Value = cookie.Value ?? string.Empty,
            Domain = NormalizeDomain(cookie.Domain),
            Path = NormalizePath(cookie.Path),
            Expiry = cookie.Expiry,
            Secure = cookie.Secure,
            HttpOnly = cookie.HttpOnly,
            SameSite = cookie.SameSite
        };
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

        public bool IsExpired => Expiry.HasValue && Expiry.Value.ToUniversalTime() <= DateTime.UtcNow;
    }
}
