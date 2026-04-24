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
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;
using FenBrowser.WebDriver.Security;

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
            ThrowIfDisposed();
            
            lock (_lock)
            {
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
            ThrowIfDisposed();
            
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
        
        /// <summary>
        /// Delete a session.
        /// </summary>
        public void DeleteSession(string sessionId)
        {
            ThrowIfDisposed();
            
            if (_sessions.TryRemove(sessionId, out var session))
            {
                session.Dispose();
            }
        }
        
        /// <summary>
        /// Get all active session IDs.
        /// </summary>
        public IReadOnlyList<string> GetSessionIds()
        {
            return new List<string>(_sessions.Keys);
        }
        
        /// <summary>
        /// Check if any sessions are active.
        /// </summary>
        public bool HasActiveSessions => _sessions.Count > 0;
        public int ActiveSessionCount => _sessions.Count;
        
        private static string GenerateSessionId()
        {
            return Guid.NewGuid().ToString("N");
        }
        
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SessionManager));
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();
        }
    }
    
    /// <summary>
    /// Represents a WebDriver session.
    /// </summary>
    public class Session : IDisposable
    {
        public enum ElementReferenceKind
        {
            Element,
            ShadowRoot,
            Frame,
            Window
        }

        public string Id { get; }
        public Capabilities Capabilities { get; }
        public DateTime CreatedAt { get; }
        public Timeouts Timeouts { get; set; }
        
        // Browser state
        public string CurrentWindowHandle { get; set; }
        public List<string> WindowHandles { get; } = new();
        public bool WindowStateInitialized { get; set; }
        
        // Element cache for references
        private readonly ConcurrentDictionary<string, WeakReference<object>> _elementCache = new();
        private readonly ConcurrentDictionary<string, ElementReferenceKind> _elementReferenceKinds = new();
        private readonly ConditionalWeakTable<object, ElementReferenceToken> _elementIdsByObject = new();
        private readonly ConcurrentDictionary<string, string> _elementRefsByNativeString = new(StringComparer.Ordinal);
        private readonly object _elementRegistrationLock = new();
        private int _elementCounter;
        private readonly string _elementIdPrefix;
        
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
            _elementIdPrefix = Id.Length >= 8 ? Id[..8] : Id;
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

        private string RegisterReference(object element, ElementReferenceKind kind)
        {
            if (element == null)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Cannot register a null element reference");
            }

            lock (_elementRegistrationLock)
            {
                if (_elementIdsByObject.TryGetValue(element, out var existingToken))
                {
                    _elementReferenceKinds.TryAdd(existingToken.Id, kind);
                    return existingToken.Id;
                }

                var id = $"{_elementIdPrefix}-e{Interlocked.Increment(ref _elementCounter)}";
                _elementCache[id] = new WeakReference<object>(element);
                _elementReferenceKinds[id] = kind;
                _elementIdsByObject.Add(element, new ElementReferenceToken(id));
                if (element is string nativeString && !string.IsNullOrEmpty(nativeString))
                {
                    _elementRefsByNativeString[nativeString] = id;
                }
                return id;
            }
        }

        public bool TryGetElementReferenceId(object element, out string elementReferenceId)
        {
            elementReferenceId = null;
            if (element == null)
            {
                return false;
            }

            if (_elementIdsByObject.TryGetValue(element, out var knownToken))
            {
                elementReferenceId = knownToken.Id;
                return true;
            }

            if (element is string nativeString &&
                !string.IsNullOrEmpty(nativeString) &&
                _elementRefsByNativeString.TryGetValue(nativeString, out var knownByString))
            {
                elementReferenceId = knownByString;
                return true;
            }

            foreach (var entry in _elementCache)
            {
                if (!entry.Value.TryGetTarget(out var target) || target == null)
                {
                    continue;
                }

                if (ReferenceEquals(target, element) || Equals(target, element))
                {
                    elementReferenceId = entry.Key;
                    return true;
                }
            }

            return false;
        }
        
        /// <summary>
        /// Get a cached element by ID.
        /// </summary>
        public object GetElement(string elementId, ElementReferenceKind? expectedKind = null)
        {
            if (string.IsNullOrWhiteSpace(elementId))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Element reference is required");
            }

            if (!elementId.StartsWith($"{_elementIdPrefix}-e", StringComparison.Ordinal))
            {
                throw new WebDriverException(
                    ErrorCodes.NoSuchElement,
                    "Element reference does not belong to current session",
                    SecurityAudit.CreateFailureData(
                        SecurityBlockReasons.SessionIsolationViolation,
                        "Attempted to dereference an element ID owned by another session",
                        Id));
            }

            if (expectedKind.HasValue && _elementReferenceKinds.TryGetValue(elementId, out var actualKind) &&
                actualKind != expectedKind.Value)
            {
                if (expectedKind.Value == ElementReferenceKind.ShadowRoot)
                {
                    throw new WebDriverException(ErrorCodes.NoSuchShadowRoot, "Element does not have an open shadow root");
                }

                if (expectedKind.Value == ElementReferenceKind.Frame)
                {
                    throw new WebDriverException(ErrorCodes.NoSuchFrame, "Frame not found");
                }

                if (expectedKind.Value == ElementReferenceKind.Window)
                {
                    throw new WebDriverException(ErrorCodes.NoSuchWindow, "Current browsing context is no longer open");
                }

                throw new WebDriverException(ErrorCodes.NoSuchElement, $"Element not found: {elementId}");
            }

            if (_elementCache.TryGetValue(elementId, out var weakRef))
            {
                if (weakRef.TryGetTarget(out var element))
                    return element;
                    
                // Stale reference
                _elementCache.TryRemove(elementId, out _);
                _elementReferenceKinds.TryRemove(elementId, out _);
                throw new WebDriverException(
                    ErrorCodes.StaleElementReference,
                    "Element is no longer attached to the DOM");
            }
            
            throw new WebDriverException(
                ErrorCodes.NoSuchElement,
                $"Element not found: {elementId}");
        }
        
        public void Dispose()
        {
            _elementCache.Clear();
            _elementReferenceKinds.Clear();
            _elementRefsByNativeString.Clear();
            WindowHandles.Clear();
        }

        private sealed class ElementReferenceToken
        {
            public ElementReferenceToken(string id)
            {
                Id = id;
            }

            public string Id { get; }
        }
    }
    
    /// <summary>
    /// WebDriver exception with error code.
    /// </summary>
    public class WebDriverException : Exception
    {
        public string ErrorCode { get; }
        public object ErrorData { get; }
        
        public WebDriverException(string errorCode, string message, object errorData = null)
            : base(message)
        {
            ErrorCode = errorCode;
            ErrorData = errorData;
        }
        
        public int HttpStatus => ErrorCodes.GetHttpStatus(ErrorCode);
    }
}
