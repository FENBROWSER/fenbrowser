using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Helper that creates a synchronously-resolved thenable FenObject.
    /// Works without an IExecutionContext â€" suitable for static API factories.
    /// JS code can do .then(cb) and cb fires synchronously on the first .then() call.
    /// </summary>
    public static class ResolvedThenable
    {
        /// <summary>Creates a pre-resolved promise with real JsPromise when context is available.</summary>
        public static FenObject Resolved(FenValue value, IExecutionContext context)
        {
            if (context != null)
                return Core.Types.JsPromise.Resolve(value, context);
            return Resolved(value);
        }

        /// <summary>Creates a pre-resolved thenable with the given value.</summary>
        public static FenObject Resolved(FenValue value)
        {
            var p = new FenObject();
            p.Set("__state", FenValue.FromString("fulfilled"));
            p.Set("__result", value);
            AttachThenCatch(p);
            return p;
        }

        /// <summary>Creates a pre-rejected promise with real JsPromise when context is available.</summary>
        public static FenObject Rejected(string reason, IExecutionContext context)
        {
            if (context != null)
                return Core.Types.JsPromise.Reject(FenValue.FromString(reason), context);
            return Rejected(reason);
        }

        /// <summary>Creates a pre-rejected thenable with the given reason string.</summary>
        public static FenObject Rejected(string reason)
        {
            var p = new FenObject();
            p.Set("__state", FenValue.FromString("rejected"));
            p.Set("__reason", FenValue.FromString(reason));
            AttachThenCatch(p);
            return p;
        }

        private static void TryInvokeHandler(FenValue callback, FenValue[] args, string operation)
        {
            if (!callback.IsFunction) return;
            try
            {
                callback.AsFunction().Invoke(args, (IExecutionContext)null);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[ResolvedThenable] {operation} callback failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private static void AttachThenCatch(FenObject p)
        {
            // Note: native delegate is (FenValue[] args, FenValue thisVal) -> FenValue.
            // The second param of the lambda is thisVal (FenValue), not IExecutionContext.
            // Invoke calls inside need IExecutionContext; pass null so Invoke creates a default context.
            p.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _thisVal) =>
            {
                if (p.Get("__state").ToString() == "fulfilled")
                {
                    var result = p.Get("__result");
                    if (args.Length > 0 && args[0].IsFunction)
                    {
                        TryInvokeHandler(args[0], new[] { result }, "then.fulfilled");
                        // callback faults are non-fatal but logged via TryInvokeHandler
                    }
                }
                else if (p.Get("__state").ToString() == "rejected")
                {
                    var reason = p.Get("__reason");
                    if (args.Length > 1 && args[1].IsFunction)
                    {
                        TryInvokeHandler(args[1], new[] { reason }, "then.rejected");
                        // callback faults are non-fatal but logged via TryInvokeHandler
                    }
                }
                return FenValue.FromObject(p);
            })));

            p.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, _thisVal) =>
            {
                if (p.Get("__state").ToString() == "rejected" && args.Length > 0 && args[0].IsFunction)
                {
                    var reason = p.Get("__reason");
                    TryInvokeHandler(args[0], new[] { reason }, "catch");
                    // callback faults are non-fatal but logged via helper wrappers
                }
                return FenValue.FromObject(p);
            })));

            p.Set("finally", FenValue.FromFunction(new FenFunction("finally", (args, _thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    TryInvokeHandler(args[0], Array.Empty<FenValue>(), "finally");
                    // callback faults are non-fatal but logged via helper wrappers
                }
                return FenValue.FromObject(p);
            })));
        }
    }
}

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Geolocation API implementation
    /// Privacy-first: uses approximate/randomized location or denies by default
    /// </summary>
    public static class GeolocationAPI
    {
        // Default to a privacy-preserving approximate location (city center, not exact)
        private static bool _permissionGranted = false;
        private static readonly Dictionary<int, Timer> _watches = new Dictionary<int, Timer>();
        private static readonly object _watchLock = new object();
        private static int _nextWatchId = 0;
        
        private static void TryInvokeCallback(FenValue callback, FenValue[] args, string operation)
        {
            if (!callback.IsFunction) return;
            try
            {
                callback.AsFunction().Invoke(args, null);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[GeolocationAPI] {operation} callback failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private static FenObject CreatePosition()
        {
            var coords = new FenObject();
            coords.Set("latitude", FenValue.FromNumber(0));
            coords.Set("longitude", FenValue.FromNumber(0));
            coords.Set("accuracy", FenValue.FromNumber(1000));
            coords.Set("altitude", FenValue.Null);
            coords.Set("altitudeAccuracy", FenValue.Null);
            coords.Set("heading", FenValue.Null);
            coords.Set("speed", FenValue.Null);

            var position = new FenObject();
            position.Set("coords", FenValue.FromObject(coords));
            position.Set("timestamp", FenValue.FromNumber(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return position;
        }

        private static int ParseWatchInterval(FenValue options)
        {
            const int defaultIntervalMs = 1000;
            const int minIntervalMs = 250;
            if (!options.IsObject)
            {
                return defaultIntervalMs;
            }

            var optionsObject = options.AsObject();
            var intervalCandidate = optionsObject?.Get("timeout") ?? FenValue.Undefined;
            if (!intervalCandidate.IsNumber)
            {
                intervalCandidate = optionsObject?.Get("maximumAge") ?? FenValue.Undefined;
            }

            if (!intervalCandidate.IsNumber)
            {
                return defaultIntervalMs;
            }

            var parsed = (int)Math.Round(intervalCandidate.ToNumber());
            if (parsed <= 0)
            {
                return defaultIntervalMs;
            }

            return Math.Max(minIntervalMs, parsed);
        }

        private static void ClearWatchInternal(int watchId)
        {
            Timer timer = null;
            lock (_watchLock)
            {
                if (_watches.TryGetValue(watchId, out timer))
                {
                    _watches.Remove(watchId);
                }
            }

            timer?.Dispose();
        }

        public static FenObject CreateGeolocationObject()
        {
            var geo = new FenObject();
            
            geo.Set("getCurrentPosition", FenValue.FromFunction(new FenFunction("getCurrentPosition", 
                (args, thisVal) =>
            {
                if (args.Length < 1) return FenValue.Undefined;
                
                var successCallback = args[0];
                var errorCallback = args.Length > 1 ? args[1] : FenValue.Undefined;
                
                if (!_permissionGranted)
                {
                    if (!errorCallback.IsUndefined && errorCallback.IsFunction)
                    {
                        var error = new FenObject();
                        error.Set("code", FenValue.FromNumber(1)); // PERMISSION_DENIED
                        error.Set("message", FenValue.FromString("Geolocation permission denied"));
                        TryInvokeCallback(errorCallback, new[] { FenValue.FromObject(error) }, "getCurrentPosition.error");
                        // callback faults are non-fatal but logged via TryInvokeHandler
                    }
                    return FenValue.Undefined;
                }

                // Permission granted: invoke success callback with an approximate position snapshot
                if (successCallback.IsFunction)
                {
                    var position = CreatePosition();
                    TryInvokeCallback(successCallback, new[] { FenValue.FromObject(position) }, "getCurrentPosition.success");
                }
                return FenValue.Undefined;
            })));
            
            geo.Set("watchPosition", FenValue.FromFunction(new FenFunction("watchPosition", 
                (args, thisVal) =>
            {
                var successCallback = args.Length > 0 ? args[0] : FenValue.Undefined;
                var errorCallback = args.Length > 1 ? args[1] : FenValue.Undefined;
                var intervalMs = ParseWatchInterval(args.Length > 2 ? args[2] : FenValue.Undefined);
                var watchId = Interlocked.Increment(ref _nextWatchId);

                if (!_permissionGranted)
                {
                    if (!errorCallback.IsUndefined && errorCallback.IsFunction)
                    {
                        var error = new FenObject();
                        error.Set("code", FenValue.FromNumber(1));
                        error.Set("message", FenValue.FromString("Geolocation permission denied"));
                        TryInvokeCallback(errorCallback, new[] { FenValue.FromObject(error) }, "watchPosition.error");
                    }
                    return FenValue.FromNumber(watchId);
                }

                if (!successCallback.IsFunction)
                {
                    return FenValue.FromNumber(watchId);
                }

                Timer timer = null;
                timer = new Timer(_ =>
                {
                    var position = CreatePosition();
                    TryInvokeCallback(successCallback, new[] { FenValue.FromObject(position) }, "watchPosition.success");
                }, null, 0, intervalMs);

                lock (_watchLock)
                {
                    _watches[watchId] = timer;
                }

                return FenValue.FromNumber(watchId);
            })));
            
            geo.Set("clearWatch", FenValue.FromFunction(new FenFunction("clearWatch", 
                (args, thisVal) =>
            {
                if (args.Length > 0)
                {
                    ClearWatchInternal((int)args[0].ToNumber());
                }
                return FenValue.Undefined;
            })));
            
            return geo;
        }
        
        public static void SetPermission(bool granted)
        {
            _permissionGranted = granted;
            if (!granted)
            {
                List<int> watchIds;
                lock (_watchLock)
                {
                    watchIds = new List<int>(_watches.Keys);
                }

                foreach (var watchId in watchIds)
                {
                    ClearWatchInternal(watchId);
                }
            }
        }
    }
    /// <summary>
    /// Notifications API implementation.
    /// Privacy-first by default: permission remains denied unless explicitly granted.
    /// </summary>
    public static class NotificationsAPI
    {
        private const int MaxNotificationTitleLength = 256;
        private const int MaxNotificationBodyLength = 2048;
        private const int MaxNotificationTagLength = 128;
        private const int MaxNotificationIconLength = 2048;
        private const int MaxNotificationDataUriLength = 65536;
        private const int MaxActiveNotifications = 64;

        private static readonly object _stateLock = new object();
        private static readonly Dictionary<int, FenObject> _activeNotifications = new Dictionary<int, FenObject>();
        private static readonly Queue<int> _notificationOrder = new Queue<int>();
        private static readonly List<WeakReference<FenObject>> _constructors = new List<WeakReference<FenObject>>();

        private static int _nextNotificationId = 0;
        private static string _permission = "default"; // "default", "granted", "denied"

        public static FenObject CreateNotificationConstructor(IExecutionContext context = null)
        {
            var execContext = EnsureExecutionContext(context);

            var ctor = new FenFunction("Notification", (args, thisVal) =>
            {
                if (!string.Equals(GetPermission(), "granted", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Notification permission is not granted.");
                }

                var title = SanitizeText(args.Length > 0 ? args[0].AsString() : string.Empty, MaxNotificationTitleLength);
                var options = args.Length > 1 && args[1].IsObject ? args[1].AsObject() : null;
                var body = SanitizeText(options?.Get("body").AsString(), MaxNotificationBodyLength);
                var tag = SanitizeText(options?.Get("tag").AsString(), MaxNotificationTagLength);
                var iconRaw = options?.Get("icon").AsString();

                if (!TryValidateNotificationIcon(iconRaw, execContext.CurrentUrl, out var normalizedIcon, out var validationError))
                {
                    throw new InvalidOperationException(validationError ?? "Invalid notification icon source.");
                }

                var data = options?.Get("data") ?? FenValue.Undefined;
                var instance = CreateNotificationInstance(execContext, title, body, tag, normalizedIcon, data);
                return FenValue.FromObject(instance);
            })
            {
                IsConstructor = true,
                NativeLength = 1
            };

            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(ctor));
            ctor.Set("prototype", FenValue.FromObject(prototype));

            ctor.Set("permission", FenValue.FromString(GetPermission()));
            ctor.Set("maxActions", FenValue.FromNumber(0));
            ctor.Set("requestPermission", FenValue.FromFunction(new FenFunction("requestPermission", (args, thisVal) =>
            {
                var resolvedPermission = ResolveRequestedPermission();
                if (args.Length > 0 && args[0].IsFunction)
                {
                    TryInvokeNotificationHandler(args[0], new[] { FenValue.FromString(resolvedPermission) }, execContext, "Notification.requestPermission.callback");
                }

                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromString(resolvedPermission), context));
            })));

            RegisterConstructor(ctor);
            return ctor;
        }

        public static void SetPermission(string permission)
        {
            lock (_stateLock)
            {
                _permission = NormalizePermission(permission);
                if (!string.Equals(_permission, "granted", StringComparison.OrdinalIgnoreCase))
                {
                    CloseAllNotificationsUnsafe();
                }

                UpdateConstructorPermissionUnsafe();
            }
        }

        private static FenObject CreateNotificationInstance(
            IExecutionContext context,
            string title,
            string body,
            string tag,
            string icon,
            FenValue data)
        {
            var notificationId = Interlocked.Increment(ref _nextNotificationId);
            var notification = new FenObject();
            var listeners = new Dictionary<string, List<FenFunction>>(StringComparer.OrdinalIgnoreCase);
            var notificationLock = new object();
            var closed = false;

            void Dispatch(string eventType)
            {
                if (string.IsNullOrWhiteSpace(eventType))
                {
                    return;
                }

                var normalizedType = eventType.Trim().ToLowerInvariant();
                var inline = notification.Get("on" + normalizedType);
                TryInvokeNotificationHandler(inline, Array.Empty<FenValue>(), context, $"Notification.{normalizedType}.inline");

                List<FenFunction> snapshot = null;
                lock (listeners)
                {
                    if (listeners.TryGetValue(normalizedType, out var list) && list.Count > 0)
                    {
                        snapshot = new List<FenFunction>(list);
                    }
                }

                if (snapshot == null)
                {
                    return;
                }

                foreach (var fn in snapshot)
                {
                    TryInvokeNotificationHandler(FenValue.FromFunction(fn), Array.Empty<FenValue>(), context, $"Notification.{normalizedType}.listener");
                }
            }

            void CloseInternal(bool dispatchClose)
            {
                lock (notificationLock)
                {
                    if (closed)
                    {
                        return;
                    }

                    closed = true;
                    notification.Set("closed", FenValue.FromBoolean(true));
                }

                UnregisterNotification(notificationId);
                if (dispatchClose)
                {
                    Dispatch("close");
                }
            }

            notification.Set("title", FenValue.FromString(title));
            notification.Set("body", FenValue.FromString(body));
            notification.Set("tag", FenValue.FromString(tag));
            notification.Set("icon", FenValue.FromString(icon));
            notification.Set("data", data);
            notification.Set("timestamp", FenValue.FromNumber(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            notification.Set("silent", FenValue.FromBoolean(false));
            notification.Set("requireInteraction", FenValue.FromBoolean(false));
            notification.Set("renotify", FenValue.FromBoolean(false));
            notification.Set("dir", FenValue.FromString("auto"));
            notification.Set("lang", FenValue.FromString(string.Empty));
            notification.Set("closed", FenValue.FromBoolean(false));

            notification.Set("onclick", FenValue.Null);
            notification.Set("onerror", FenValue.Null);
            notification.Set("onshow", FenValue.Null);
            notification.Set("onclose", FenValue.Null);

            notification.Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
            {
                CloseInternal(dispatchClose: true);
                return FenValue.Undefined;
            })));

            notification.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[1].IsFunction)
                {
                    return FenValue.Undefined;
                }

                var type = args[0].AsString()?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(type))
                {
                    return FenValue.Undefined;
                }

                var callback = args[1].AsFunction();
                lock (listeners)
                {
                    if (!listeners.TryGetValue(type, out var list))
                    {
                        list = new List<FenFunction>();
                        listeners[type] = list;
                    }

                    if (!list.Contains(callback))
                    {
                        list.Add(callback);
                    }
                }

                return FenValue.Undefined;
            })));

            notification.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[1].IsFunction)
                {
                    return FenValue.Undefined;
                }

                var type = args[0].AsString()?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(type))
                {
                    return FenValue.Undefined;
                }

                var callback = args[1].AsFunction();
                lock (listeners)
                {
                    if (listeners.TryGetValue(type, out var list))
                    {
                        list.RemoveAll(item => ReferenceEquals(item, callback));
                        if (list.Count == 0)
                        {
                            listeners.Remove(type);
                        }
                    }
                }

                return FenValue.Undefined;
            })));

            notification.Set("dispatchEvent", FenValue.FromFunction(new FenFunction("dispatchEvent", (args, thisVal) =>
            {
                string type = string.Empty;
                if (args.Length > 0)
                {
                    if (args[0].IsString)
                    {
                        type = args[0].AsString();
                    }
                    else if (args[0].IsObject)
                    {
                        type = args[0].AsObject()?.Get("type").AsString();
                    }
                }

                if (string.IsNullOrWhiteSpace(type))
                {
                    return FenValue.FromBoolean(false);
                }

                Dispatch(type);
                return FenValue.FromBoolean(true);
            })));

            RegisterNotification(notificationId, notification);

            try
            {
                if (context?.ScheduleCallback != null)
                {
                    context.ScheduleCallback(() => Dispatch("show"), 0);
                }
                else
                {
                    Dispatch("show");
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[NotificationsAPI] Failed to dispatch show event: {ex.Message}", LogCategory.JavaScript);
            }

            return notification;
        }

        private static IExecutionContext EnsureExecutionContext(IExecutionContext context)
        {
            return context ?? new FenBrowser.FenEngine.Core.ExecutionContext();
        }

        private static string NormalizePermission(string permission)
        {
            if (string.Equals(permission, "granted", StringComparison.OrdinalIgnoreCase)) return "granted";
            if (string.Equals(permission, "denied", StringComparison.OrdinalIgnoreCase)) return "denied";
            return "default";
        }

        private static string GetPermission()
        {
            lock (_stateLock)
            {
                return _permission;
            }
        }

        private static string ResolveRequestedPermission()
        {
            lock (_stateLock)
            {
                if (string.Equals(_permission, "granted", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_permission, "denied", StringComparison.OrdinalIgnoreCase))
                {
                    return _permission;
                }

                // Privacy-first default without interactive UI permission prompt.
                _permission = "denied";
                UpdateConstructorPermissionUnsafe();
                return _permission;
            }
        }

        private static void RegisterConstructor(FenObject ctor)
        {
            lock (_stateLock)
            {
                _constructors.Add(new WeakReference<FenObject>(ctor));
                UpdateConstructorPermissionUnsafe();
            }
        }

        private static void UpdateConstructorPermissionUnsafe()
        {
            for (var i = _constructors.Count - 1; i >= 0; i--)
            {
                if (_constructors[i].TryGetTarget(out var ctor))
                {
                    ctor.Set("permission", FenValue.FromString(_permission));
                }
                else
                {
                    _constructors.RemoveAt(i);
                }
            }
        }

        private static void RegisterNotification(int notificationId, FenObject notification)
        {
            lock (_stateLock)
            {
                while (_activeNotifications.Count >= MaxActiveNotifications && _notificationOrder.Count > 0)
                {
                    var oldestId = _notificationOrder.Dequeue();
                    if (_activeNotifications.TryGetValue(oldestId, out var evicted))
                    {
                        _activeNotifications.Remove(oldestId);
                        evicted.Set("closed", FenValue.FromBoolean(true));
                    }
                }

                _activeNotifications[notificationId] = notification;
                _notificationOrder.Enqueue(notificationId);
            }
        }

        private static void UnregisterNotification(int notificationId)
        {
            lock (_stateLock)
            {
                _activeNotifications.Remove(notificationId);
            }
        }

        private static void CloseAllNotificationsUnsafe()
        {
            foreach (var notification in _activeNotifications.Values)
            {
                notification.Set("closed", FenValue.FromBoolean(true));
            }

            _activeNotifications.Clear();
            _notificationOrder.Clear();
        }

        private static string SanitizeText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var trimmed = text.Trim();
            if (trimmed.Length <= maxLength)
            {
                return trimmed;
            }

            return trimmed.Substring(0, maxLength);
        }

        private static void TryInvokeNotificationHandler(FenValue callback, FenValue[] args, IExecutionContext context, string operation)
        {
            if (!callback.IsFunction)
            {
                return;
            }

            try
            {
                callback.AsFunction().Invoke(args, context);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[NotificationsAPI] {operation} handler failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private static bool TryValidateNotificationIcon(
            string source,
            string currentUrl,
            out string normalizedSource,
            out string error)
        {
            normalizedSource = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(source))
            {
                return true;
            }

            var trimmed = source.Trim();
            if (trimmed.Length > MaxNotificationIconLength)
            {
                error = "Notification icon source exceeds maximum length.";
                return false;
            }

            foreach (var ch in trimmed)
            {
                if (char.IsControl(ch))
                {
                    error = "Notification icon source contains control characters.";
                    return false;
                }
            }

            if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (trimmed.Length > MaxNotificationDataUriLength)
                {
                    error = "Notification data URI exceeds maximum length.";
                    return false;
                }

                if (!trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Only data:image/* icons are allowed for Notification.";
                    return false;
                }

                normalizedSource = trimmed;
                return true;
            }

            Uri uri;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri))
            {
                Uri baseUri = null;
                if (!string.IsNullOrWhiteSpace(currentUrl))
                {
                    Uri.TryCreate(currentUrl, UriKind.Absolute, out baseUri);
                }

                if (baseUri == null || !Uri.TryCreate(baseUri, trimmed, out uri))
                {
                    error = "Notification icon URL is invalid or cannot be resolved.";
                    return false;
                }
            }

            var scheme = uri.Scheme?.ToLowerInvariant() ?? string.Empty;
            if (scheme != "http" && scheme != "https" && scheme != "blob")
            {
                error = $"Notification icon URL scheme '{scheme}' is not allowed.";
                return false;
            }

            if ((scheme == "http" || scheme == "https") && IsPrivateOrReservedHost(uri.Host))
            {
                error = "Notification icon URL targets a private or reserved host.";
                return false;
            }

            normalizedSource = uri.AbsoluteUri;
            return true;
        }

        private static bool IsPrivateOrReservedHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return true;
            }

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!IPAddress.TryParse(host, out var ipAddress))
            {
                return false;
            }

            if (IPAddress.IsLoopback(ipAddress))
            {
                return true;
            }

            if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ipAddress.GetAddressBytes();
                return bytes[0] == 10 ||
                       bytes[0] == 127 ||
                       (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                       (bytes[0] == 192 && bytes[1] == 168) ||
                       (bytes[0] == 169 && bytes[1] == 254);
            }

            if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = ipAddress.GetAddressBytes();
                var isUniqueLocal = (bytes[0] & 0xFE) == 0xFC;
                return isUniqueLocal || ipAddress.IsIPv6LinkLocal || IPAddress.IPv6Loopback.Equals(ipAddress);
            }

            return false;
        }
    }


    
    /// <summary>
    /// Fullscreen API implementation
    /// </summary>
    public static class FullscreenAPI
    {
        private static bool _isFullscreen = false;
        private static Action<bool> _onFullscreenChange;
        
        public static void RegisterFullscreenHandler(Action<bool> handler)
        {
            _onFullscreenChange = handler;
        }
        
        public static FenObject CreateDocumentFullscreenMethods(IExecutionContext context = null)
        {
            var methods = new FenObject();

            methods.Set("fullscreenElement", _isFullscreen ? FenValue.FromString("[element]") : FenValue.Null);
            methods.Set("fullscreenEnabled", FenValue.FromBoolean(true));

            methods.Set("exitFullscreen", FenValue.FromFunction(new FenFunction("exitFullscreen",
                (args, thisVal) =>
            {
                _isFullscreen = false;
                _onFullscreenChange?.Invoke(false);
                // Spec: returns Promise<void> — Fullscreen API §4.9
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, context));
            })));

            return methods;
        }

        public static FenObject CreateElementFullscreenMethod(IExecutionContext context = null)
        {
            var method = new FenObject();

            method.Set("requestFullscreen", FenValue.FromFunction(new FenFunction("requestFullscreen",
                (args, thisVal) =>
            {
                _isFullscreen = true;
                _onFullscreenChange?.Invoke(true);
                // Spec: returns Promise<void> — Fullscreen API §4.9
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, context));
            })));

            return method;
        }
    }
    
    /// <summary>
    /// Clipboard API implementation (enhanced)
    /// </summary>
    public static class ClipboardAPI
    {
        public static FenObject CreateClipboardObject(IExecutionContext context = null)
        {
            var clipboard = new FenObject();

            // Spec: all clipboard methods return Promise<void|string|ClipboardItem[]>
            // Clipboard API §2.2-2.5
            clipboard.Set("writeText", FenValue.FromFunction(new FenFunction("writeText",
                (args, thisVal) =>
            {
                if (args.Length >= 1)
                {
                    string text = args[0].ToString();
                    EngineLogCompat.Debug($"[Clipboard] writeText: {text?.Substring(0, Math.Min(50, text?.Length ?? 0))}", LogCategory.JavaScript);
                }
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, context));
            })));

            clipboard.Set("readText", FenValue.FromFunction(new FenFunction("readText",
                (args, thisVal) =>
            {
                // Returns Promise<string> — empty for privacy
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromString(""), context));
            })));

            clipboard.Set("write", FenValue.FromFunction(new FenFunction("write",
                (args, thisVal) =>
            {
                // Returns Promise<void>
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, context));
            })));

            clipboard.Set("read", FenValue.FromFunction(new FenFunction("read",
                (args, thisVal) =>
            {
                // Returns Promise<ClipboardItem[]> — empty for privacy
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(0));
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(arr), context));
            })));
            
            return clipboard;
        }
    }

    /// <summary>
    /// Storage Manager API implementation.
    /// </summary>
    public static class StorageManagerAPI
    {
        private const long IndexedDbQuotaBytes = 50L * 1024 * 1024;

        public static FenObject CreateStorageManagerObject(Func<string> originProvider, Func<string> sessionScopeProvider, IExecutionContext context = null)
        {
            var storage = new FenObject();

            storage.Set("estimate", FenValue.FromFunction(new FenFunction("estimate", (args, thisVal) =>
            {
                var origin = originProvider?.Invoke();
                var sessionScope = sessionScopeProvider?.Invoke();
                var localStorageUsage = StorageApi.GetLocalStorageUsageBytes(origin);
                var sessionStorageUsage = StorageApi.GetSessionStorageUsageBytes(sessionScope);
                var indexedDbUsage = IndexedDBService.EstimateUsageBytes();

                var usageDetails = new FenObject();
                usageDetails.Set("localStorage", FenValue.FromNumber(localStorageUsage));
                usageDetails.Set("sessionStorage", FenValue.FromNumber(sessionStorageUsage));
                usageDetails.Set("indexedDB", FenValue.FromNumber(indexedDbUsage));

                var estimate = new FenObject();
                estimate.Set("usage", FenValue.FromNumber(localStorageUsage + sessionStorageUsage + indexedDbUsage));
                estimate.Set("quota", FenValue.FromNumber((StorageApi.QuotaBytes * 2) + IndexedDbQuotaBytes));
                estimate.Set("usageDetails", FenValue.FromObject(usageDetails));

                // Storage API §4.2 — returns Promise<StorageEstimate>
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(estimate), context));
            })));

            storage.Set("persisted", FenValue.FromFunction(new FenFunction("persisted", (args, thisVal) =>
            {
                // Storage API §4.3 — returns Promise<boolean>
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromBoolean(false), context));
            })));

            storage.Set("persist", FenValue.FromFunction(new FenFunction("persist", (args, thisVal) =>
            {
                // Storage API §4.4 — returns Promise<boolean>
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromBoolean(false), context));
            })));

            return storage;
        }
    }

    /// <summary>
    /// Web Share API implementation.
    /// </summary>
    public static class WebShareAPI
    {
        private const int MaxShareTitleLength = 512;
        private const int MaxShareTextLength = 8192;
        private const int MaxShareUrlLength = 4096;

        public static FenObject CreateShareObject(IExecutionContext context = null)
        {
            var execContext = context ?? new FenBrowser.FenEngine.Core.ExecutionContext();
            var share = new FenObject();

            share.Set("canShare", FenValue.FromFunction(new FenFunction("canShare", (args, thisVal) =>
            {
                if (args.Length == 0 || args[0].IsUndefined || args[0].IsNull)
                {
                    return FenValue.FromBoolean(false);
                }

                return FenValue.FromBoolean(TryNormalizeShareData(args[0], execContext, out _, out _));
            })));

            share.Set("share", FenValue.FromFunction(new FenFunction("share", (args, thisVal) =>
            {
                if (args.Length == 0 || args[0].IsUndefined || args[0].IsNull)
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: navigator.share requires share data", execContext));
                }

                if (!TryNormalizeShareData(args[0], execContext, out var payload, out var error))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected(error ?? "TypeError: Invalid share data", execContext));
                }

                EngineLogCompat.Debug(
                    $"[WebShare] share title='{payload.Title}' textLen={payload.Text.Length} url='{payload.Url}'",
                    LogCategory.JavaScript);

                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, execContext));
            })));

            return share;
        }

        private static bool TryNormalizeShareData(
            FenValue shareDataValue,
            IExecutionContext context,
            out SharePayload payload,
            out string error)
        {
            payload = default;
            error = null;

            if (!shareDataValue.IsObject)
            {
                error = "TypeError: Share data must be an object";
                return false;
            }

            var shareData = shareDataValue.AsObject();
            var title = NormalizeShareText(shareData.Get("title"), MaxShareTitleLength, "Share title", out error);
            if (error != null)
            {
                return false;
            }

            var text = NormalizeShareText(shareData.Get("text"), MaxShareTextLength, "Share text", out error);
            if (error != null)
            {
                return false;
            }

            var url = NormalizeShareUrl(shareData.Get("url"), context?.CurrentUrl, out error);
            if (error != null)
            {
                return false;
            }

            var filesValue = shareData.Get("files");
            if (!filesValue.IsUndefined && !filesValue.IsNull)
            {
                if (!filesValue.IsObject)
                {
                    error = "TypeError: Share files must be an array-like object";
                    return false;
                }

                var filesObj = filesValue.AsObject();
                var lengthValue = filesObj.Get("length");
                var filesLength = lengthValue.IsNumber ? Math.Max(0, (int)lengthValue.ToNumber()) : 0;
                if (filesLength > 0)
                {
                    error = "NotSupportedError: File sharing is not supported";
                    return false;
                }
            }

            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(text) && string.IsNullOrEmpty(url))
            {
                error = "TypeError: Share data must include title, text, url, or files";
                return false;
            }

            payload = new SharePayload(title, text, url);
            return true;
        }

        private static string NormalizeShareText(FenValue value, int maxLength, string fieldName, out string error)
        {
            error = null;
            if (value.IsUndefined || value.IsNull)
            {
                return string.Empty;
            }

            var text = value.ToString()?.Trim() ?? string.Empty;
            foreach (var ch in text)
            {
                if (char.IsControl(ch) && ch != '\r' && ch != '\n' && ch != '\t')
                {
                    error = $"TypeError: {fieldName} contains control characters";
                    return string.Empty;
                }
            }

            if (text.Length > maxLength)
            {
                error = $"TypeError: {fieldName} exceeds maximum length";
                return string.Empty;
            }

            return text;
        }

        private static string NormalizeShareUrl(FenValue value, string currentUrl, out string error)
        {
            error = null;
            if (value.IsUndefined || value.IsNull)
            {
                return string.Empty;
            }

            var raw = value.ToString()?.Trim() ?? string.Empty;
            if (raw.Length == 0)
            {
                return string.Empty;
            }

            foreach (var ch in raw)
            {
                if (char.IsControl(ch))
                {
                    error = "TypeError: Share URL contains control characters";
                    return string.Empty;
                }
            }

            if (raw.Length > MaxShareUrlLength)
            {
                error = "TypeError: Share URL exceeds maximum length";
                return string.Empty;
            }

            Uri baseUri = null;
            if (!string.IsNullOrWhiteSpace(currentUrl))
            {
                Uri.TryCreate(currentUrl, UriKind.Absolute, out baseUri);
            }

            Uri resolvedUri;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out resolvedUri))
            {
                if (baseUri == null || !Uri.TryCreate(baseUri, raw, out resolvedUri))
                {
                    error = "TypeError: Share URL is invalid";
                    return string.Empty;
                }
            }

            var scheme = resolvedUri.Scheme?.ToLowerInvariant();
            if (scheme != Uri.UriSchemeHttp &&
                scheme != Uri.UriSchemeHttps &&
                scheme != Uri.UriSchemeMailto &&
                scheme != "tel")
            {
                error = "NotSupportedError: Share URL scheme is not supported";
                return string.Empty;
            }

            return resolvedUri.ToString();
        }

        private readonly struct SharePayload
        {
            public SharePayload(string title, string text, string url)
            {
                Title = title ?? string.Empty;
                Text = text ?? string.Empty;
                Url = url ?? string.Empty;
            }

            public string Title { get; }
            public string Text { get; }
            public string Url { get; }
        }
    }
}





