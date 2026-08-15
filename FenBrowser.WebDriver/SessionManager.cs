// =============================================================================
// SessionManager.cs
// W3C WebDriver Session Management
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

        public void DeleteSession(string sessionId)
        {
            Session session = null;
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                _sessions.TryRemove(sessionId, out session);
            }

            session?.Dispose();
        }

        public IReadOnlyList<string> GetSessionIds()
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                return new List<string>(_sessions.Keys);
            }
        }

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

        public bool HasSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return false;

            lock (_lock)
                return !_disposed && _sessions.ContainsKey(sessionId);
        }

        private static string GenerateSessionId() => Guid.NewGuid().ToString("N");

        private void ThrowIfDisposedLocked()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SessionManager));
        }

        public void Dispose()
        {
            Session[] sessions;
            lock (_lock)
            {
                if (_disposed)
                    return;

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
        private const int ReferenceCleanupInterval = 256;

        public enum ElementReferenceKind
        {
            Element = 0,
            ShadowRoot = 1,
            Frame = 2,
            Window = 3
        }

        private sealed class NativeReferenceHolder
        {
            private readonly object _gate = new();
            private readonly Dictionary<ElementReferenceKind, string> _referenceIds = new();

            public bool TryGet(ElementReferenceKind kind, out string referenceId)
            {
                lock (_gate)
                    return _referenceIds.TryGetValue(kind, out referenceId);
            }

            public void Set(ElementReferenceKind kind, string referenceId)
            {
                lock (_gate)
                    _referenceIds[kind] = referenceId;
            }

            public void RemoveIfMatches(ElementReferenceKind kind, string referenceId)
            {
                lock (_gate)
                {
                    if (_referenceIds.TryGetValue(kind, out var existing) &&
                        string.Equals(existing, referenceId, StringComparison.Ordinal))
                    {
                        _referenceIds.Remove(kind);
                    }
                }
            }
        }

        private readonly record struct NativeStringReferenceKey(
            ElementReferenceKind Kind,
            string NativeReference);

        public string Id { get; }
        public Capabilities Capabilities { get; }
        public DateTime CreatedAt { get; }
        public Timeouts Timeouts { get; set; }

        public string? CurrentWindowHandle { get; set; }
        public List<string> WindowHandles { get; } = new();
        public bool WindowStateInitialized { get; set; }

        private readonly ConcurrentDictionary<string, WeakReference<object>> _elementCache = new();
        private readonly ConcurrentDictionary<string, ElementReferenceKind> _referenceKinds = new(StringComparer.Ordinal);
        private readonly ConditionalWeakTable<object, NativeReferenceHolder> _nativeObjectReferenceMap = new();
        private readonly ConcurrentDictionary<NativeStringReferenceKey, string> _nativeStringReferenceMap = new();
        private readonly object _referenceRegistrationGate = new();
        private int _elementCounter;
        private int _registrationsSinceCleanup;
        private int _disposed;

        public Session(string id, Capabilities capabilities)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Session ID is required.", nameof(id));

            Id = id;
            Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
            CreatedAt = DateTime.UtcNow;
            Timeouts = capabilities.Timeouts ?? new Timeouts();

            CurrentWindowHandle = Guid.NewGuid().ToString("N");
            WindowHandles.Add(CurrentWindowHandle);
            WindowStateInitialized = false;
        }

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
            ThrowIfDisposed();
            if (referenceObject == null)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Cannot register a null element reference");
            }

            // Registration is a check-then-create transaction spanning several
            // concurrent/weak maps. Without this gate, two command threads can both
            // miss the same native object and publish different WebDriver IDs for it.
            lock (_referenceRegistrationGate)
            {
                ThrowIfDisposed();
                if (TryGetElementReferenceId(referenceObject, kind, out var existingId))
                    return existingId;

                var next = Interlocked.Increment(ref _elementCounter);
                if (next <= 0)
                {
                    throw new WebDriverException(
                        ErrorCodes.UnknownError,
                        "WebDriver element reference ID space is exhausted");
                }

                var id = $"e{next}";
                _elementCache[id] = new WeakReference<object>(referenceObject);
                _referenceKinds[id] = kind;

                AssociateNativeReferenceCore(referenceObject, id, kind);

                if (referenceObject is string nativeString &&
                    kind == ElementReferenceKind.Window &&
                    !string.IsNullOrWhiteSpace(nativeString) &&
                    !WindowHandles.Contains(nativeString))
                {
                    WindowHandles.Add(nativeString);
                }

                if (++_registrationsSinceCleanup >= ReferenceCleanupInterval)
                {
                    _registrationsSinceCleanup = 0;
                    CleanupDeadReferences();
                }

                return id;
            }
        }

        public object GetElement(string elementId)
        {
            ThrowIfDisposed();
            if (_elementCache.TryGetValue(elementId, out var weakRef))
            {
                if (weakRef.TryGetTarget(out var element))
                    return element;

                RemoveDeadReference(elementId);
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
            ThrowIfDisposed();
            if (_referenceKinds.TryGetValue(elementId, out var actualKind) && actualKind != expectedKind)
            {
                throw new WebDriverException(
                    GetKindErrorCode(expectedKind),
                    $"Reference {elementId} is not of expected kind {expectedKind}");
            }

            return GetElement(elementId);
        }

        // Backward-compatible element-specific lookup used by element command paths.
        public bool TryGetElementReferenceId(object nativeReference, out string referenceId)
            => TryGetElementReferenceId(nativeReference, ElementReferenceKind.Element, out referenceId);

        public bool TryGetElementReferenceId(
            object nativeReference,
            ElementReferenceKind kind,
            out string referenceId)
        {
            referenceId = string.Empty;
            if (Volatile.Read(ref _disposed) != 0 || nativeReference == null)
                return false;

            if (nativeReference is string nativeString)
            {
                var key = new NativeStringReferenceKey(kind, nativeString);
                if (_nativeStringReferenceMap.TryGetValue(key, out var mappedStringRef))
                {
                    if (IsReferenceAlive(mappedStringRef, kind))
                    {
                        referenceId = mappedStringRef;
                        return true;
                    }

                    _nativeStringReferenceMap.TryRemove(key, out _);
                }

                return false;
            }

            if (_nativeObjectReferenceMap.TryGetValue(nativeReference, out var holder) &&
                holder.TryGet(kind, out var mappedRef))
            {
                if (IsReferenceAlive(mappedRef, kind))
                {
                    referenceId = mappedRef;
                    return true;
                }

                holder.RemoveIfMatches(kind, mappedRef);
            }

            return false;
        }

        // Backward-compatible association for element references.
        public void AssociateNativeReference(object nativeReference, string referenceId)
            => AssociateNativeReference(nativeReference, referenceId, ElementReferenceKind.Element);

        public void AssociateNativeReference(
            object nativeReference,
            string referenceId,
            ElementReferenceKind kind)
        {
            ThrowIfDisposed();
            lock (_referenceRegistrationGate)
            {
                ThrowIfDisposed();
                AssociateNativeReferenceCore(nativeReference, referenceId, kind);
            }
        }

        private void AssociateNativeReferenceCore(
            object nativeReference,
            string referenceId,
            ElementReferenceKind kind)
        {
            if (nativeReference == null || string.IsNullOrWhiteSpace(referenceId))
                return;

            if (!_elementCache.ContainsKey(referenceId))
            {
                throw new WebDriverException(
                    ErrorCodes.NoSuchElement,
                    $"Cannot associate native reference with unknown reference id: {referenceId}");
            }

            if (_referenceKinds.TryGetValue(referenceId, out var actualKind) && actualKind != kind)
            {
                throw new WebDriverException(
                    ErrorCodes.InvalidArgument,
                    $"Cannot associate {kind} native reference with {actualKind} reference id: {referenceId}");
            }

            if (nativeReference is string nativeString)
            {
                if (!string.IsNullOrWhiteSpace(nativeString))
                    _nativeStringReferenceMap[new NativeStringReferenceKey(kind, nativeString)] = referenceId;
                return;
            }

            var holder = _nativeObjectReferenceMap.GetValue(
                nativeReference,
                static _ => new NativeReferenceHolder());
            holder.Set(kind, referenceId);
        }

        public bool TryGetReferenceKind(string referenceId, out ElementReferenceKind kind)
        {
            if (Volatile.Read(ref _disposed) == 0 && _referenceKinds.TryGetValue(referenceId, out kind))
                return true;

            kind = ElementReferenceKind.Element;
            return false;
        }

        /// <summary>
        /// Element-only native string mapping snapshot used for element fingerprint
        /// equivalence. Frame/window/shadow identifiers must never participate in it.
        /// </summary>
        public IReadOnlyDictionary<string, string> GetNativeReferenceMapSnapshot()
        {
            ThrowIfDisposed();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in _nativeStringReferenceMap)
            {
                if (pair.Key.Kind != ElementReferenceKind.Element)
                    continue;

                if (IsReferenceAlive(pair.Value, ElementReferenceKind.Element))
                {
                    result[pair.Key.NativeReference] = pair.Value;
                }
                else
                {
                    _nativeStringReferenceMap.TryRemove(pair.Key, out _);
                }
            }

            return result;
        }

        private bool IsReferenceAlive(string referenceId, ElementReferenceKind expectedKind)
        {
            if (!_elementCache.TryGetValue(referenceId, out var weakRef))
                return false;

            if (!_referenceKinds.TryGetValue(referenceId, out var actualKind) || actualKind != expectedKind)
                return false;

            if (weakRef.TryGetTarget(out _))
                return true;

            RemoveDeadReference(referenceId);
            return false;
        }

        private void CleanupDeadReferences()
        {
            foreach (var pair in _elementCache)
            {
                if (!pair.Value.TryGetTarget(out _))
                    RemoveDeadReference(pair.Key);
            }

            foreach (var pair in _nativeStringReferenceMap)
            {
                if (!IsReferenceAlive(pair.Value, pair.Key.Kind))
                    _nativeStringReferenceMap.TryRemove(pair.Key, out _);
            }
        }

        private void RemoveDeadReference(string referenceId)
        {
            _elementCache.TryRemove(referenceId, out _);
            _referenceKinds.TryRemove(referenceId, out _);

            // String native references are held strongly by their reverse-map keys.
            // Weak element storage alone therefore did not prevent one historical key
            // from accumulating per stale string-backed reference.
            foreach (var pair in _nativeStringReferenceMap)
            {
                if (string.Equals(pair.Value, referenceId, StringComparison.Ordinal))
                    _nativeStringReferenceMap.TryRemove(pair.Key, out _);
            }
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

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(Session));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (_referenceRegistrationGate)
            {
                _elementCache.Clear();
                _referenceKinds.Clear();
                _nativeObjectReferenceMap.Clear();
                _nativeStringReferenceMap.Clear();
                WindowHandles.Clear();
                CurrentWindowHandle = null;
            }
        }
    }

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
