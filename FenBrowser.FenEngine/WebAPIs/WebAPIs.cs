using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Helper that creates a synchronously-resolved thenable FenObject.
    /// Works without an IExecutionContext — suitable for static API factories.
    /// JS code can do .then(cb) and cb fires synchronously on the first .then() call.
    /// </summary>
    public static class ResolvedThenable
    {
        /// <summary>Creates a pre-resolved thenable with the given value.</summary>
        public static FenObject Resolved(FenValue value)
        {
            var p = new FenObject();
            p.Set("__state", FenValue.FromString("fulfilled"));
            p.Set("__result", value);
            AttachThenCatch(p);
            return p;
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
                        try { args[0].AsFunction().Invoke(new[] { result }, (IExecutionContext)null); }
                        catch { }
                    }
                }
                else if (p.Get("__state").ToString() == "rejected")
                {
                    var reason = p.Get("__reason");
                    if (args.Length > 1 && args[1].IsFunction)
                    {
                        try { args[1].AsFunction().Invoke(new[] { reason }, (IExecutionContext)null); }
                        catch { }
                    }
                }
                return FenValue.FromObject(p);
            })));

            p.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, _thisVal) =>
            {
                if (p.Get("__state").ToString() == "rejected" && args.Length > 0 && args[0].IsFunction)
                {
                    var reason = p.Get("__reason");
                    try { args[0].AsFunction().Invoke(new[] { reason }, (IExecutionContext)null); }
                    catch { }
                }
                return FenValue.FromObject(p);
            })));

            p.Set("finally", FenValue.FromFunction(new FenFunction("finally", (args, _thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    try { args[0].AsFunction().Invoke(Array.Empty<FenValue>(), (IExecutionContext)null); }
                    catch { }
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
                        try { errorCallback.AsFunction().Invoke(new[] { FenValue.FromObject(error) }, null); }
                        catch { }
                    }
                    return FenValue.Undefined;
                }

                // Permission granted: invoke success callback with a stub position
                if (successCallback.IsFunction)
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
                    try { successCallback.AsFunction().Invoke(new[] { FenValue.FromObject(position) }, null); }
                    catch { }
                }
                return FenValue.Undefined;
            })));
            
            geo.Set("watchPosition", FenValue.FromFunction(new FenFunction("watchPosition", 
                (args, thisVal) =>
            {
                // Return a watch ID (always 0 for now - stub)
                return FenValue.FromNumber(0);
            })));
            
            geo.Set("clearWatch", FenValue.FromFunction(new FenFunction("clearWatch", 
                (args, thisVal) =>
            {
                return FenValue.Undefined;
            })));
            
            return geo;
        }
        
        public static void SetPermission(bool granted)
        {
            _permissionGranted = granted;
        }
    }
    
    /// <summary>
    /// Notifications API implementation
    /// </summary>
    public static class NotificationsAPI
    {
        private static string _permission = "default"; // "default", "granted", "denied"
        
        public static FenObject CreateNotificationConstructor()
        {
            var notifConstructor = new FenObject();
            
            // Static property: Notification.permission
            notifConstructor.Set("permission", FenValue.FromString(_permission));
            
            // Static method: Notification.requestPermission() — returns Promise<string> per spec
            notifConstructor.Set("requestPermission", FenValue.FromFunction(new FenFunction("requestPermission",
                (args, thisVal) =>
            {
                _permission = "denied"; // privacy-first default
                notifConstructor.Set("permission", FenValue.FromString(_permission));
                // Spec: returns Promise<"granted"|"denied"|"default">
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromString(_permission)));
            })));
            
            return notifConstructor;
        }
        
        public static void SetPermission(string permission)
        {
            _permission = permission;
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
        
        public static FenObject CreateDocumentFullscreenMethods()
        {
            var methods = new FenObject();
            
            methods.Set("fullscreenElement", _isFullscreen ? FenValue.FromString("[element]") : FenValue.Null);
            methods.Set("fullscreenEnabled", FenValue.FromBoolean(true));
            
            methods.Set("exitFullscreen", FenValue.FromFunction(new FenFunction("exitFullscreen",
                (args, thisVal) =>
            {
                _isFullscreen = false;
                _onFullscreenChange?.Invoke(false);
                // Spec: returns Promise<void>
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined));
            })));
            
            return methods;
        }
        
        public static FenObject CreateElementFullscreenMethod()
        {
            var method = new FenObject();
            
            method.Set("requestFullscreen", FenValue.FromFunction(new FenFunction("requestFullscreen",
                (args, thisVal) =>
            {
                _isFullscreen = true;
                _onFullscreenChange?.Invoke(true);
                // Spec: returns Promise<void>
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined));
            })));
            
            return method;
        }
    }
    
    /// <summary>
    /// Clipboard API implementation (enhanced)
    /// </summary>
    public static class ClipboardAPI
    {
        public static FenObject CreateClipboardObject()
        {
            var clipboard = new FenObject();
            
            // Spec: all clipboard methods return Promise<void|string|ClipboardItem[]>
            clipboard.Set("writeText", FenValue.FromFunction(new FenFunction("writeText",
                (args, thisVal) =>
            {
                if (args.Length >= 1)
                {
                    string text = args[0].ToString();
                    FenLogger.Debug($"[Clipboard] writeText: {text?.Substring(0, Math.Min(50, text?.Length ?? 0))}", LogCategory.JavaScript);
                }
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined));
            })));

            clipboard.Set("readText", FenValue.FromFunction(new FenFunction("readText",
                (args, thisVal) =>
            {
                // Returns Promise<string> — empty for privacy
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromString("")));
            })));

            clipboard.Set("write", FenValue.FromFunction(new FenFunction("write",
                (args, thisVal) =>
            {
                // Returns Promise<void>
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined));
            })));

            clipboard.Set("read", FenValue.FromFunction(new FenFunction("read",
                (args, thisVal) =>
            {
                // Returns Promise<ClipboardItem[]> — empty for privacy
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(0));
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(arr)));
            })));
            
            return clipboard;
        }
    }
}
