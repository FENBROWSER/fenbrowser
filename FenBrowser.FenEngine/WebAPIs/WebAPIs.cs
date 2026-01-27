using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

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
                    // Call error callback with permission denied
                    if (!errorCallback.IsUndefined && errorCallback.AsFunction() is FenFunction errFn)
                    {
                        var error = new FenObject();
                        error.Set("code", FenValue.FromNumber(1)); // PERMISSION_DENIED
                        error.Set("message", FenValue.FromString("Geolocation permission denied"));
                        // Note: actual callback invocation would require context
                    }
                    return FenValue.Undefined;
                }
                
                // Success callback would be invoked with position
                // This requires async handling which needs integration with the JS engine
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
            
            // Static method: Notification.requestPermission()
            notifConstructor.Set("requestPermission", FenValue.FromFunction(new FenFunction("requestPermission", 
                (args, thisVal) =>
            {
                // Return a promise-like object that resolves to permission status
                // For now, default to denied for privacy
                _permission = "denied";
                return FenValue.FromString(_permission);
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
                // Return a promise-like - for now just return undefined
                return FenValue.Undefined;
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
                // Return a promise-like - for now just return undefined
                return FenValue.Undefined;
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
            
            clipboard.Set("writeText", FenValue.FromFunction(new FenFunction("writeText", 
                (args, thisVal) =>
            {
                if (args.Length >= 1 && args[0] is FenValue textVal)
                {
                    string text = textVal.AsString();
                    try
                    {
                        // Use Avalonia clipboard if available
                        // This is a stub - actual implementation needs UI thread access
                        FenLogger.Debug($"[Clipboard] writeText called with: {text?.Substring(0, Math.Min(50, text?.Length ?? 0))}...", LogCategory.JavaScript);
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Debug($"[Clipboard] writeText failed: {ex.Message}", LogCategory.JavaScript);
                    }
                }
                return FenValue.Undefined;
            })));
            
            clipboard.Set("readText", FenValue.FromFunction(new FenFunction("readText", 
                (args, thisVal) =>
            {
                // Return empty string for privacy
                return FenValue.FromString("");
            })));
            
            clipboard.Set("write", FenValue.FromFunction(new FenFunction("write", 
                (args, thisVal) =>
            {
                // Stub for ClipboardItem array
                return FenValue.Undefined;
            })));
            
            clipboard.Set("read", FenValue.FromFunction(new FenFunction("read", 
                (args, thisVal) =>
            {
                // Return empty array for privacy
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(0));
                return FenValue.FromObject(arr);
            })));
            
            return clipboard;
        }
    }
}
