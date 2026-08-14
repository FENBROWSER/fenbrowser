// =============================================================================
// SessionManager.cs
// W3C WebDriver Session Management (Spec-Compliant)
//
// SPEC REFERENCE: W3C WebDriver §8 - Sessions
//                 https://www.w3.org/TR/webdriver2/#sessions
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.WebDriver
{
    /// <summary>
    /// Manages WebDriver sessions with isolation and security.
    /// </summary>
    public class SessionManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, Session> _sessions = new();
        private readonly int _maxSessions;
        private readonly object _lock = new();
        private bool _disposed;

        public SessionManager(int maxSessions = 10)
        {
            if (maxSessions < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSessions), "maxSessions must be at least 1.");
            }

            _maxSessions = maxSessions;
        }

        /// <summary>
        /// Create a new session.
        /// </summary>
        public Session CreateSession(Capabilities requestedCaps)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();

                if (_sessions.Count >= _maxSessions)
                {
                    throw new WebDriverException(
                        ErrorCodes.SessionNotCreated,
                        $"Maximum session limit ({_maxSessions}) reached");
                }

                var sessionId = GenerateSessionId();
                var capabilities = Capabilities.Merge(requestedCaps);
                var session = new Session(sessionId, capabilities);

                if (!_sessions.TryAdd(sessionId, session))
                {
                    session.Dispose();
                    throw new WebDriverException(
                        ErrorCodes.SessionNotCreated,
                        "Failed to create session");
                }

                return session;
            }
        }

        /// <summary>
        /// Get a session by ID.
        /// </summary>
        public Session GetSession(string sessionId)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();

                if (string.IsNullOrEmpty(sessionId))
                {
                    throw new WebDriverException(
                        ErrorCodes.InvalidSessionId,
                        "Session ID is required");
                }

                if (!_sessions.TryGetValue(sessionId, out var session))
                {
                    throw new WebDriverException(
                        ErrorCodes.InvalidSessionId,
                        $"Session not found: {sessionId}");
                }

                return session;
            }
        }

        /// <summary>
        /// Delete a session.
        /// </summary>
        public void DeleteSession(string sessionId)
        {
            Session session = null;
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                _sessions.TryRemove(sessionId, out session);
            }

            // Session cleanup can fan out into browser/driver resources. Never run it
            // while holding the manager lifecycle lock.
            session?.Dispose();
        }

        /// <summary>
        /// Get all active session IDs.
        /// </summary>
        public IReadOnlyList<string> GetSessionIds()
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                return new List<string>(_sessions.Keys);
            }
        }

        /// <summary>
        /// Check if any sessions are active.
        /// </summary>
        public bool HasActiveSessions
        {
            get
            {
                lock (_lock)
                    return !_disposed && _sessions.Count > 0;
            }
        }

        public int ActiveSessionCount
        {
            get
            {
                lock (_lock)
                    return _disposed ? 0 : _sessions.Count;
            }
        }

        /// <summary>
        /// Checks whether a session with the given id exists.
        /// Used by the BiDi transport to authenticate WebSocket upgrades.
        /// </summary>
        public bool HasSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return false;

            lock (_lock)
                return !_disposed && _sessions.ContainsKey(sessionId);
        }

        private static string GenerateSessionId()
        {
            return Guid.NewGuid().ToString("N");
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SessionManager));
            }
        }

        public void Dispose()
        {
            Session[] sessions;
            lock (_lock)
            {
                if (_disposed)
                    return;

                // Linearization point: once this flips under the same lock used by
                // creation/lookup/deletion, no new session can escape the disposal set.
                _disposed = true;
                sessions = new List<Session>(_sessions.Values).ToArray();
                _sessions.Clear();
            }

            foreach (var session in sessions)
                session.Dispose();
        }
    }

    /// <summary>
    /// Represents a WebDriver session.
    /// </summary>
    public class Session : IDisposable
    {
        public enum ElementReferenceKind
        {
            Element = 0,
            ShadowRoot = 1,
            Frame = 2,
            Window = 3
        }

        public string Id { get; }
        public Capabilities Capabilities { get; }
        public DateTime CreatedAt { get; }
        public Timeouts Timeouts { get; set; }

        // Browser state
        public string? CurrentWindowHandle { get; set; }
        public List<string> WindowHandles { get; } = new();
        public bool WindowStateInitialized { get; set; }

        // Element cache and reference maps
        private readonly ConcurrentDictionary<string, WeakReference<object>> _elementCache = new();
        private readonly ConcurrentDictionary<string, ElementReferenceKind> _referenceKinds = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _nativeReferenceMap = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _nativeStringReferenceMap = new(StringComparer.Ordinal);
        private int _elementCounter;

        public Session(string id, Capabilities capabilities)
        {
            Id = id;
            Capabilities = capabilities;
            CreatedAt = DateTime.UtcNow;
            Timeouts = capabilities.Timeouts ?? new Timeouts();

            // Initialize with default window
            CurrentWindowHandle = Guid.NewGuid().ToString("N");
            WindowHandles.Add(CurrentWindowHandle);
            WindowStateInitialized = false;
        }

        /// <summary>
        /// Register an element and get its reference ID.
        /// </summary>
        public string RegisterElement(object element)
            => RegisterReference(element, ElementReferenceKind.Element);

        public string RegisterShadowRoot(object shadowRoot)
            => RegisterReference(shadowRoot, ElementReferenceKind.ShadowRoot);

        public string RegisterFrame(object frame)
            => RegisterReference(frame, ElementReferenceKind.Frame);

        public string RegisterWindow(object window)
            => RegisterReference(window, ElementReferenceKind.Window);

        private string RegisterReference(object referenceObject, ElementReferenceKind kind)
        {
            if (referenceObject == null)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Cannot register a null element reference");
            }

            if (TryGetElementReferenceId(referenceObject, out var existingId))
            {
                _referenceKinds[existingId] = kind;
                return existingId;
            }

            var id = $"e{Interlocked.Increment(ref _elementCounter)}";
            _elementCache[id] = new WeakReference<object>(referenceObject);
            _referenceKinds[id] = kind;

            var nativeKey = BuildNativeReferenceKey(referenceObject);
            if (!string.IsNullOrWhiteSpace(nativeKey))
            {
                _nativeReferenceMap[nativeKey] = id;
            }

            if (referenceObject is string nativeString && !string.IsNullOrWhiteSpace(nativeString))
            {
                _nativeStringReferenceMap[nativeString] = id;

                if (kind == ElementReferenceKind.Window && !WindowHandles.Contains(nativeString))
                {
                    WindowHandles.Add(nativeString);
                }
            }

            return id;
        }

        /// <summary>
        /// Get a cached element by ID.
        /// </summary>
        public object GetElement(string elementId)
        {
            if (_elementCache.TryGetValue(elementId, out var weakRef))
            {
                if (weakRef.TryGetTarget(out var element))
                {
                    return element;
                }

                // Stale reference
                _elementCache.TryRemove(elementId, out _);
                _referenceKinds.TryRemove(elementId, out _);

                throw new WebDriverException(
                    ErrorCodes.StaleElementReference,
                    "Element is no longer attached to the DOM");
            }

            throw new WebDriverException(
                ErrorCodes.NoSuchElement,
                $"Element not found: {elementId}");
        }

        public object GetElement(string elementId, ElementReferenceKind expectedKind)
        {
            if (_referenceKinds.TryGetValue(elementId, out var actualKind) && actualKind != expectedKind)
            {
                throw new WebDriverException(
                    GetKindErrorCode(expectedKind),
                    $"Reference {elementId} is not of expected kind {expectedKind}");
            }

            return GetElement(elementId);
        }

        public bool TryGetElementReferenceId(object nativeReference, out string referenceId)
        {
            referenceId = string.Empty;
            if (nativeReference == null)
            {
                return false;
            }

            var nativeKey = BuildNativeReferenceKey(nativeReference);
            if (!string.IsNullOrWhiteSpace(nativeKey) &&
                _nativeReferenceMap.TryGetValue(nativeKey, out var mappedRef) &&
                IsReferenceAlive(mappedRef))
            {
                referenceId = mappedRef;
                return true;
            }

            if (nativeReference is string nativeString &&
                _nativeStringReferenceMap.TryGetValue(nativeString, out var mappedStringRef) &&
                IsReferenceAlive(mappedStringRef))
            {
                referenceId = mappedStringRef;
                return true;
            }

            return false;
        }

        public void AssociateNativeReference(object nativeReference, string referenceId)
        {
            if (nativeReference == null || string.IsNullOrWhiteSpace(referenceId))
            {
                return;
            }

            if (!_elementCache.ContainsKey(referenceId))
            {
                throw new WebDriverException(
                    ErrorCodes.NoSuchElement,
                    $"Cannot associate native reference with unknown reference id: {referenceId}");
            }

            var nativeKey = BuildNativeReferenceKey(nativeReference);
            if (!string.IsNullOrWhiteSpace(nativeKey))
            {
                _nativeReferenceMap[nativeKey] = referenceId;
            }

            if (nativeReference is string nativeString && !string.IsNullOrWhiteSpace(nativeString))
            {
                _nativeStringReferenceMap[nativeString] = referenceId;
            }
        }

        public bool TryGetReferenceKind(string referenceId, out ElementReferenceKind kind)
        {
            if (_referenceKinds.TryGetValue(referenceId, out kind))
            {
                return true;
            }

            kind = ElementReferenceKind.Element;
            return false;
        }

        public IReadOnlyDictionary<string, string> GetNativeReferenceMapSnapshot()
        {
            return new Dictionary<string, string>(_nativeStringReferenceMap, StringComparer.Ordinal);
        }

        private bool IsReferenceAlive(string referenceId)
        {
            if (!_elementCache.TryGetValue(referenceId, out var weakRef))
            {
                return false;
            }

            if (weakRef.TryGetTarget(out _))
            {
                return true;
            }

            _elementCache.TryRemove(referenceId, out _);
            _referenceKinds.TryRemove(referenceId, out _);
            return false;
        }

        private static string BuildNativeReferenceKey(object nativeReference)
        {
            return nativeReference switch
            {
                null => string.Empty,
                string s => $"str:{s}",
                _ => $"obj:{nativeReference.GetType().FullName}:{RuntimeHelpers.GetHashCode(nativeReference)}"
            };
        }

        private static string GetKindErrorCode(ElementReferenceKind kind)
        {
            return kind switch
            {
                ElementReferenceKind.ShadowRoot => ErrorCodes.NoSuchShadowRoot,
                ElementReferenceKind.Frame => ErrorCodes.NoSuchFrame,
                ElementReferenceKind.Window => ErrorCodes.NoSuchWindow,
                _ => ErrorCodes.NoSuchElement
            };
        }

        public void Dispose()
        {
            _elementCache.Clear();
            _referenceKinds.Clear();
            _nativeReferenceMap.Clear();
            _nativeStringReferenceMap.Clear();
            WindowHandles.Clear();
        }
    }

    /// <summary>
    /// WebDriver exception with error code.
    /// </summary>
    public class WebDriverException : Exception
    {
        public string ErrorCode { get; }
        public object? ErrorData { get; }

        public WebDriverException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
            ErrorData = null;
        }

        public WebDriverException(string errorCode, string message, object? errorData)
            : base(message)
        {
            ErrorCode = errorCode;
            ErrorData = errorData;
        }

        public int HttpStatus => ErrorCodes.GetHttpStatus(ErrorCode);
    }
}
