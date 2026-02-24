using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
// using FenBrowser.Engine;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Globalization;
using Math = System.Math;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType; // Added for IExecutionContext
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.WebAPIs;
using FenBrowser.Core.Network.Handlers;
using SkiaSharp;
using FenBrowser.FenEngine.DOM;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// JavaScriptEngine - powered by FenEngine
    /// Provides JavaScript execution with DOM/Web APIs support
    /// </summary>
    public sealed partial class JavaScriptEngine : FenBrowser.FenEngine.Core.IDomBridge
    {
        public Func<Uri, Task<string>> FetchOverride { get; set; }
        public Func<Uri, string, bool> SubresourceAllowed { get; set; }
        public Func<string, bool> NonceAllowed { get; set; }
        
        // Permission Request Event
        public event Func<string, JsPermissions, Task<bool>> PermissionRequested;

        // FenEngine runtime
        private FenRuntime _fenRuntime;

#if USE_ECMA_EXPERIMENTAL
        private JsInterpreter _exp;
        public bool UseExperimentalEcmaEngine { get; set; } = false;
#endif
        private readonly IJsHost _host;
        private readonly FenBrowser.FenEngine.Storage.IStorageBackend _storageBackend;
        // private MiniJs.Engine _mini;      // MiniJS interpreter instance - DISABLED
        public IExecutionContext GlobalContext => _fenRuntime?.Context;
        private JsContext _ctx;

        public JavaScriptEngine(IJsHost host)
        {
            try { FenLogger.Debug("[JavaScriptEngine] Constructor Start", LogCategory.JavaScript); } catch { }
            _host = host;
            _storageBackend = new FenBrowser.FenEngine.Storage.FileStorageBackend();

            InitRuntime();
            // Service workers share the same storage and centralized network path as the runtime.
            FenBrowser.FenEngine.Workers.ServiceWorkerManager.Instance.Initialize(
                _storageBackend,
                FetchThroughNetworkHandlerAsync,
                IsWorkerScriptUriAllowed);
            DocumentWrapper.CookieReadBridge = scope => GetCookieString(scope);
            DocumentWrapper.CookieWriteBridge = (scope, cookieString) => SetCookieString(scope, cookieString);
            try { FenLogger.Debug("[JavaScriptEngine] Constructor: InitRuntime Done", LogCategory.JavaScript); } catch { }
            SetupMutationObserver();
            // _mini = new MiniJs.Engine();
        }

        public Func<System.Net.Http.HttpRequestMessage, System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage>> FetchHandler { get; set; }



        private FenBrowser.FenEngine.Core.Interfaces.IHistoryBridge _historyBridge;
        
        public void SetHistoryBridge(FenBrowser.FenEngine.Core.Interfaces.IHistoryBridge bridge)
        {
            _historyBridge = bridge;
            if (_fenRuntime != null) _fenRuntime.SetHistoryBridge(bridge);
        }

        public void NotifyPopState(object state)
        {
             _fenRuntime?.NotifyPopState(state);
        }

        private void InitRuntime()
        {
            _ctx = _ctx ?? new JsContext();
            var permissions = new PermissionManager(JsPermissions.StandardWeb);
            
            // Wire up the permission handler
            permissions.PermissionRequestedHandler = async (origin, perm) =>
            {
                if (PermissionRequested != null)
                {
                    // Dispatch to UI thread if needed? The handler in MainWindow is likely UI bound.
                    // But here we are in JS thread context potentially.
                    // The event handler in MainWindow should handle thread safety.
                    return await PermissionRequested(origin, perm);
                }
                return false; // Default deny if no handler
            };

            var context = new FenBrowser.FenEngine.Core.ExecutionContext(permissions);

            // Configure layout engine provider
            context.LayoutEngineProvider = () => _host?.GetLayoutEngine();

            // Configure function execution delegate
            context.ExecuteFunction = (fn, args) => 
            {
                var interpreter = new FenBrowser.FenEngine.Core.Interpreter();
                return interpreter.ApplyFunction(fn, new System.Collections.Generic.List<FenBrowser.FenEngine.Core.FenValue>(args), context);
            };
            
            // Configure callbacks to run via EventLoop
            context.ScheduleCallback = (action, delay) => 
            {
                FenLogger.Debug($"[ScheduleCallback] Scheduled for {delay}ms", LogCategory.JavaScript);
                Task.Run(async () => 
                {
                    await Task.Delay(delay);
                    FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance.EnqueueTask(() => 
                    {
                        FenLogger.Debug("[EventLoop] Executing scheduled callback", LogCategory.JavaScript);
                        action?.Invoke();
                    });
                });
            };

            // Configure Microtasks (Promises)
            context.ScheduleMicrotask = (action) =>
            {
                FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance.EnqueueMicrotask(() => action?.Invoke());
            };



            try { FenLogger.Debug("[JavaScriptEngine] InitRuntime: Creating FenRuntime...", LogCategory.JavaScript); } catch { }
            _fenRuntime = new FenRuntime(context, _storageBackend, this);
            _fenRuntime.NetworkFetchHandler = async (req) =>
            {
                if (FetchHandler == null) throw new Exception("FetchHandler not configured on engine");
                return await FetchHandler(req).ConfigureAwait(false);
            };
            try { FenLogger.Debug("[JavaScriptEngine] InitRuntime: FenRuntime Created", LogCategory.JavaScript); } catch { }
            // Connect console messages to BrowserHost
            _fenRuntime.OnConsoleMessage = msg => 
            {
                FenLogger.Debug($"[JavaScriptEngine] Received console message from runtime: {msg}", LogCategory.JavaScript);
                // Console messages are logged via FenLogger and passed to the host
                try { _host?.Log(msg); } catch (Exception ex) { FenLogger.Error($"[JavaScriptEngine] Host log error: {ex}", LogCategory.JavaScript); }
            };

            context.OnMutation = RecordMutation;
            
            if (_host != null)
            {
                _fenRuntime.SetAlert(msg => _host.Alert(msg));
            }
            
            SetupPermissions();
            SetupWindowEvents();
            SetupModernAPIs();
            
            // Register Fetch API with lazy delegate resolution to support property injection after constructor
            FenBrowser.FenEngine.WebAPIs.FetchApi.Register(_fenRuntime.Context, async (req) => 
            {
                 if (FetchHandler  == null) throw new Exception("FetchHandler not configured on engine");
                 return await FetchHandler(req);
            });
        }
        
        // IDomBridge Implementation
        public FenValue GetElementById(string id)
        {
            if (_domRoot  == null) return FenValue.Null;
            var doc = new JsDocument(this, _domRoot);
            var result = doc.getElementById(id);
            return result is IObject obj ? FenValue.FromObject(obj) : FenValue.Null;
        }

        public FenValue QuerySelector(string selector)
        {
             if (_domRoot  == null) return FenValue.Null;
             var doc = new JsDocument(this, _domRoot);
             var result = doc.querySelector(selector);
             return result is IObject obj ? FenValue.FromObject(obj) : FenValue.Null;
        }

        public void AddEventListener(string elementId, string eventName, FenValue callback)
        {
             var elVal = GetElementById(elementId);
             if (elVal.IsObject)
             {
                 AddEventListenerNative(
                     new FenValue[] { FenValue.FromString(eventName), callback },
                     elVal);
             }
        }

        public FenValue CreateElement(string tagName)
        {
            if (_domRoot  == null) return FenValue.Null;
            var doc = new JsDocument(this, _domRoot);
            var el = doc.createElement(tagName) as IObject;
            return el != null ? FenValue.FromObject(el) : FenValue.Null;
        }

        public FenValue CreateTextNode(string text)
        {
             if (_domRoot  == null) return FenValue.Null;
             var doc = new JsDocument(this, _domRoot);
             var txt = doc.createTextNode(text);
             if (txt is IObject obj) return FenValue.FromObject(obj);
             return FenValue.Null; 
        }

        public void AppendChild(FenValue parent, FenValue child)
        {
            if (parent.IsObject && child.IsObject)
            {
                var pObj = parent.AsObject();
                if (pObj is JsDomElement el)
                {
                     el.appendChild(child.AsObject());
                }
            }
        }

        public void SetAttribute(FenValue element, string name, string value)
        {
            if (element.IsObject)
            {
                var obj = element.AsObject();
                if (obj is JsDomElement el)
                {
                    el.setAttribute(name, value);
                }
            }
        }

        private void SetupModernAPIs()
        {
            try 
            {
                FenLogger.Debug("[JavaScriptEngine] Setting up Modern APIs (Proxy, Reflect)...", LogCategory.JavaScript);
                _fenRuntime.SetGlobal("Proxy", FenBrowser.FenEngine.Scripting.ProxyAPI.CreateProxyConstructor());
                _fenRuntime.SetGlobal("Reflect", FenValue.FromObject(FenBrowser.FenEngine.Scripting.ReflectAPI.CreateReflectObject()));
            }
            catch (Exception ex)
            {
                 FenLogger.Error($"[JavaScriptEngine] Modern API Setup Failed: {ex.Message}", LogCategory.JavaScript, ex);
            }
        }

        public FenValue AddEventListenerNative(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2) return FenValue.Undefined;
            var evt = args[0].ToString();
            
            // Check for function callback
            FenFunction callback = null;
            if (args[1].IsFunction) callback = args[1].AsFunction() as FenFunction;
            else if (args[1].IsObject) 
            {
                // handle { handleEvent: function() ... } object interface?
                var obj = args[1].AsObject();
                // simplified: ignore for now, standard requires function or object with handleEvent
            }
            
            if (callback  == null) return FenValue.Undefined;

            try { FenLogger.Debug($"[JS_API] addEventListener called for '{evt}' on {thisVal.ToString()}", LogCategory.JavaScript); } catch { }

            // Normalize target key
            // If thisVal is a wrapper (JsDomElement), key by the underlying Node to ensure identity persistence
            object key = thisVal.IsObject ? thisVal.AsObject() : null;
            if (thisVal.IsObject)
            {
                 var obj = thisVal.AsObject();
                 if (obj is JsDomElement elWrapper) key = elWrapper._node;
                 else if (obj is JsDomText textWrapper) key = textWrapper._node;
                 else key = obj;
            }

            if (key != null)
            {
                // Get or create listener dictionary for this object
                Dictionary<string, List<FenFunction>> listeners;
                if (!_objectEventListeners.TryGetValue(key, out listeners))
                {
                    listeners = new Dictionary<string, List<FenFunction>>(StringComparer.OrdinalIgnoreCase);
                    _objectEventListeners.Add(key, listeners);
                }

                if (!listeners.ContainsKey(evt)) listeners[evt] = new List<FenFunction>();
                
                // Avoid duplicates? Standard says yes.
                // Assuming simple linear scan is fine for now.
                // Note: FenFunction equality might reference underlying delegate, which should be fine.
                if (!listeners[evt].Contains(callback)) 
                {
                    listeners[evt].Add(callback);
                }
            }
            
            return FenValue.Undefined;
        }

        /// <summary>
        /// Dispatch a mechanism event to stored listeners on the target.
        /// </summary>
        public void DispatchEvent(object target, string eventName, FenObject eventArgs = null)
        {
            if (target  == null || string.IsNullOrEmpty(eventName)) return;

            // Normalize key
            object key = target;
            if (target is JsDomElement elWrapper) key = elWrapper._node;

            if (_objectEventListeners.TryGetValue(key, out var listeners) && listeners.ContainsKey(eventName))
            {
                var list = listeners[eventName].ToArray(); 
                
                // Wrap 'this' context if needed
                // If we keyed by Element, the 'this' passed to handler should be a JsDomElement wrapper
                object thisContext = target;
                if (key is Element domEl && !(target is JsDomElement))
                {
                    thisContext = new JsDomElement(this, domEl);
                }
                // If 'thisContext' is not IValue/FenObject/JsObject, the interpreter might complain.
                // JsDomElement implements IObject.

                var args = new FenValue[] {
                    eventArgs != null ? FenValue.FromObject(eventArgs) : FenValue.Undefined
                };

                foreach (var handler in list)
                {
                        _fenRuntime.Context.ScheduleCallback(() => {
                        try { 
                            // Check if 'thisContext' is valid for Invoke?
                            // FenFunction.Invoke mainly uses context, 'this' is implicit in how it's called unless we pass it?
                            // FenRuntime.ExecuteFunction / FenFunction.Invoke signature: (args, context). 
                            // It doesn't take 'this' explicitly unless the function logic uses it.
                            // The closure might capture 'this'.
                            // BUT 'addEventListener' callbacks usually expect 'this' to be the element.
                            // The MiniPratt engine implementation of FenFunction might not support 'this' binding easily 
                            // without 'call'/'apply'.
                            // However, let's assume standard behavior: handler(event).
                            handler.Invoke(args, _fenRuntime.Context); 
                        } catch (Exception ex) {
                            FenLogger.Error($"[DispatchEvent] Error in handler for {eventName}: {ex}", LogCategory.JavaScript);
                        }
                        }, 0);
                }
            }
        }

        /// <summary>
        /// Dispatches an event with bubbling (Element -> Parent -> ... -> Document -> Window)
        /// </summary>
        public void DispatchEventForElement(Element el, string eventName)
        {
            if (el  == null) return;
            
            try 
            {
                // Create Event Object
                // In a perfect world we reuse the same object and update currentTarget.
                var jsEl = new JsDomElement(this, el);
                
                var evtObj = new FenBrowser.FenEngine.Core.FenObject();
                evtObj.Set("type", FenValue.FromString(eventName));
                evtObj.Set("target", FenValue.FromObject(jsEl));
                evtObj.Set("timeStamp", FenValue.FromNumber((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds));
                evtObj.Set("bubbles", FenValue.FromBoolean(true));

                // 1. Capture Phase (skipped for now)

                // 2. Target Phase & 3. Bubble Phase
                var current = el;
                while (current != null)
                {
                    // Update currentTarget if we were reusing event object
                    // For now, simple dispatch
                    DispatchEvent(current, eventName, evtObj);
                    current = current.ParentElement;
                }

                // 4. Document & Window
                var doc = _fenRuntime.GetGlobal("document");
                if (doc.IsObject) 
                {
                    var dObj = doc.AsObject();
                    object dNative = dObj;
                    if (dObj is FenObject fo) dNative = fo.NativeObject;
                    else if (dObj is JsDocument jd) dNative = jd.NativeObject;
                    DispatchEvent(dNative ?? dObj, eventName, evtObj);
                }
                
                var win = _fenRuntime.GetGlobal("window");
                if (win.IsObject) 
                {
                    var wObj = win.AsObject();
                    object wNative = wObj;
                    if (wObj is FenObject fo) wNative = fo.NativeObject;
                    DispatchEvent(wNative ?? wObj, eventName, evtObj);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[DispatchEventForElement] Failed: {ex}", LogCategory.JavaScript);
            }
        }

        private void SetupWindowEvents()
        {
            try { FenLogger.Debug("[SetupWindowEvents] Configuring window/document events", LogCategory.JavaScript); } catch { }
            
            // Ensure 'window' exists
            var win = _fenRuntime.GetGlobal("window");
            FenBrowser.FenEngine.Core.FenObject winObj;
            if (win.IsObject) winObj = (FenBrowser.FenEngine.Core.FenObject)win.AsObject();
            else 
            {
                winObj = new FenBrowser.FenEngine.Core.FenObject();
                _fenRuntime.SetGlobal("window", FenValue.FromObject(winObj));
            }
            // Alias self -> window (Critical for WPT testharness.js)
            _fenRuntime.SetGlobal("self", FenValue.FromObject(winObj));
            
            // Add addEventListener to window object using shared implementation
            var fnVal = FenValue.FromFunction(new FenFunction("addEventListener", AddEventListenerNative));
            winObj.Set("addEventListener", fnVal);
            _fenRuntime.SetGlobal("addEventListener", fnVal);
            
            // Also ensure document is on window
            // Overwrite the specific default document with our wrapper
            var docWrapper = new JsDocument(this, null); // Root will be resolved dynamically via DomRoot
            var docVal = FenValue.FromObject(docWrapper);
            _fenRuntime.SetGlobal("document", docVal);
            winObj.Set("document", docVal);

            // [Compliance] Window Dimensions
            winObj.Set("innerWidth", FenValue.FromFunction(new FenFunction("innerWidth", (args, ctx) => FenValue.FromNumber(WindowWidth))));
            winObj.Set("innerHeight", FenValue.FromFunction(new FenFunction("innerHeight", (args, ctx) => FenValue.FromNumber(WindowHeight))));
            winObj.Set("outerWidth", FenValue.FromFunction(new FenFunction("outerWidth", (args, ctx) => FenValue.FromNumber(WindowWidth)))); // Simplified
            winObj.Set("outerHeight", FenValue.FromFunction(new FenFunction("outerHeight", (args, ctx) => FenValue.FromNumber(WindowHeight))));
            winObj.Set("screenX", FenValue.FromNumber(0));
            winObj.Set("screenY", FenValue.FromNumber(0));
            
            // [Compliance] Screen Object
            var screenObj = new FenBrowser.FenEngine.Core.FenObject();
            screenObj.Set("width", FenValue.FromNumber(ScreenWidth));
            screenObj.Set("height", FenValue.FromNumber(ScreenHeight));
            screenObj.Set("availWidth", FenValue.FromNumber(ScreenWidth));
            screenObj.Set("availHeight", FenValue.FromNumber(ScreenHeight - 40)); // Taskbar?
            screenObj.Set("colorDepth", FenValue.FromNumber(24));
            screenObj.Set("pixelDepth", FenValue.FromNumber(24));
            
            winObj.Set("screen", FenValue.FromObject(screenObj));
            _fenRuntime.SetGlobal("screen", FenValue.FromObject(screenObj));

             // Note: DocumentWrapper now exposes addEventListener natively via Get/Has/Keys.
             // We don't need to overwrite it here.
        }

        public double WindowWidth { get; set; } = 1024;
        public double WindowHeight { get; set; } = 768;
        public double ScreenWidth { get; set; } = 1920;
        public double ScreenHeight { get; set; } = 1080;


        // timers
        private readonly Dictionary<int, System.Threading.Timer> _timers = new Dictionary<int, System.Threading.Timer>();
        // fields restored
        private int _nextTimerId = 1;
        private List<MutationObserverWrapper> _fenMutationObservers = new List<MutationObserverWrapper>();
        private List<string> _mutationObservers = new List<string>(); // Legacy observers
        private List<MutationRecord> _pendingMutations = new List<MutationRecord>();
        private bool _repaintRequested = false;
        private List<Uri> _history = new List<Uri>();
        private int _historyIndex = -1;
        private string _docTitle = "";
        
        // script cache
        private object _responseLock = new object();

        private Dictionary<string, ResponseEntry> _responseRegistry = new Dictionary<string, ResponseEntry>();
        private int _responseCapacity = 100;
        private LinkedList<string> _responseLru = new LinkedList<string>();
        // private int _scriptCap = 0; // Removing unused
        private int _inlineThreshold = 1024;
        

        private void SetupMutationObserver()
        {
            var moConstructor = new FenFunction("MutationObserver", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction)
                    return FenValue.FromError("MutationObserver constructor requires a callback function");

                // Use the new DOM-compliant wrapper
                var callback = args[0].AsFunction();
                var wrapper = new MutationObserverWrapper(callback);

                lock (_mutationLock)
                {
                    _fenMutationObservers.Add(wrapper);
                }

                // Return the FenObject interface provided by the wrapper
                return FenValue.FromObject(wrapper.ToFenObject(_fenRuntime.Context));
            });

            _fenRuntime.SetGlobal("MutationObserver", FenValue.FromFunction(moConstructor));
        }

        private void SetupPermissions()
        {
            // 1. navigator.permissions
            var permissionsObj = new FenBrowser.FenEngine.Core.FenObject();
            permissionsObj.Set("query", FenValue.FromFunction(new FenFunction("query", (args, thisVal) =>
            {
                // Returns a Thenable (Promise-like)
                var thenable = new FenBrowser.FenEngine.Core.FenObject();
                thenable.Set("then", FenValue.FromFunction(new FenFunction("then", (thenArgs, thenThis) =>
                {
                    var onFulfilled = ((thenArgs != null && thenArgs.Length > 0 && thenArgs[0].IsFunction) ? thenArgs[0].AsFunction() : null);
                    
                    if (onFulfilled  == null) return FenValue.FromObject(thenable); // Chain?

                    // Async execution
                    var desc = (args.Length > 0 && args[0].IsObject) ? args[0].AsObject() : null;
                    var origin = OriginKey(_ctx?.BaseUri);

                    Task.Run(() =>
                    {
                        try
                        {
                            var name = "";
                            if (desc != null)
                            {
                                var val = desc.Get("name");
                                if (val is FenValue fv) name = fv.AsString();
                                else name = val.ToString();
                            }
                            
                            JsPermissions permFlag = JsPermissions.None;
                            if (name == "geolocation") permFlag = JsPermissions.Geolocation;
                            else if (name == "notifications") permFlag = JsPermissions.Notifications;
                            else if (name == "camera" || name == "microphone") permFlag = JsPermissions.Camera;

                            var state = "granted";
                            if (permFlag != JsPermissions.None)
                            {
                                var ps = PermissionStore.Instance.GetState(origin, permFlag);
                                state = ps.ToString().ToLowerInvariant(); // "granted", "denied", "prompt"
                            }
                            else if (string.IsNullOrEmpty(name))
                            {
                                state = "prompt";
                            }

                            // Return PermissionStatus object
                            var statusObj = new FenBrowser.FenEngine.Core.FenObject();
                            statusObj.Set("state", FenValue.FromString(state));
                            statusObj.Set("onchange", FenValue.Null); 

                            _fenRuntime.Context.ScheduleCallback(() => {
                                try { onFulfilled.Invoke(new FenValue[] { FenValue.FromObject(statusObj) }, _fenRuntime.Context); } catch {}
                            }, 0);
                        }
                        catch { }
                    });
                    
                    return FenValue.FromObject(thenable);
                })));
                return FenValue.FromObject(thenable);
            })));

            // 2. navigator.geolocation
            var geoObj = new FenBrowser.FenEngine.Core.FenObject();
            geoObj.Set("getCurrentPosition", FenValue.FromFunction(new FenFunction("getCurrentPosition", (args, thisVal) =>
            {
                if (args.Length < 1) return FenValue.Undefined;
                var successCb = args[0].AsFunction();
                var errorCb = (args.Length > 1 && args[1].IsFunction) ? args[1].AsFunction() : null;
                var origin = OriginKey(_ctx?.BaseUri);

                Task.Run(async () =>
                {
                    bool granted = false;
                    try
                    {
                        granted = await _fenRuntime.Context.Permissions.RequestPermissionAsync(JsPermissions.Geolocation, origin);
                    }
                    catch { }

                    if (granted)
                    {
                         // Mock position
                         var pos = new FenBrowser.FenEngine.Core.FenObject();
                         var coords = new FenBrowser.FenEngine.Core.FenObject();
                         coords.Set("latitude", FenValue.FromNumber(37.422));
                         coords.Set("longitude", FenValue.FromNumber(-122.084));
                         coords.Set("accuracy", FenValue.FromNumber(100));
                         pos.Set("coords", FenValue.FromObject(coords));
                         pos.Set("timestamp", FenValue.FromNumber((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
                         
                         _fenRuntime.Context.ScheduleCallback(() => {
                            try { successCb.Invoke(new FenValue[] { FenValue.FromObject(pos) }, _fenRuntime.Context); } catch {}
                         }, 0);
                    }
                    else
                    {
                        if (errorCb != null)
                        {
                             var err = new FenBrowser.FenEngine.Core.FenObject();
                             err.Set("code", FenValue.FromNumber(1)); // PERMISSION_DENIED
                             err.Set("message", FenValue.FromString("User denied Geolocation"));
                             _fenRuntime.Context.ScheduleCallback(() => {
                                try { errorCb.Invoke(new FenValue[] { FenValue.FromObject(err) }, _fenRuntime.Context); } catch {}
                             }, 0);
                        }
                    }
                });
                return FenValue.Undefined;
            })));

            // Check if 'navigator' exists, create or extend
            var existingNav = _fenRuntime.GetGlobal("navigator");
            FenBrowser.FenEngine.Core.FenObject navObj = null;
            if (existingNav.IsObject) navObj = (FenBrowser.FenEngine.Core.FenObject)existingNav.AsObject();
            else 
            {
                navObj = new FenBrowser.FenEngine.Core.FenObject();
                _fenRuntime.SetGlobal("navigator", FenValue.FromObject(navObj));
            }

            navObj.Set("permissions", FenValue.FromObject(permissionsObj));
            navObj.Set("geolocation", FenValue.FromObject(geoObj));
            
            // Basic navigator properties for detection
            navObj.Set("javaEnabled", FenValue.FromFunction(new FenFunction("javaEnabled", (args, ctx) => FenValue.FromBoolean(false))));
            navObj.Set("cookieEnabled", FenValue.FromBoolean(true));
            // Detection Logic: 
            // Chrome: appName="Netscape", vendor="Google Inc.", product="Gecko", appVersion="5.0 (...)"
            navObj.Set("userAgent", FenValue.FromString(BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent)));
            navObj.Set("appName", FenValue.FromString("Netscape")); // Standard for modern browsers
            
            // appVersion usually matches UA but without "Mozilla/" prefix
            var fullUa = BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent);
            var appVer = fullUa.StartsWith("Mozilla/") ? fullUa.Substring(8) : fullUa;
            navObj.Set("appVersion", FenValue.FromString(appVer));
            
            navObj.Set("platform", FenValue.FromString("Win32"));
            navObj.Set("vendor", FenValue.FromString("Google Inc.")); // Required for Chrome detection
            navObj.Set("product", FenValue.FromString("Gecko"));      // Historical artifact required by many sites
            navObj.Set("language", FenValue.FromString(CultureInfo.CurrentCulture.Name));
            
            var langsObj = new FenBrowser.FenEngine.Core.FenObject();
            langsObj.Set("0", FenValue.FromString(CultureInfo.CurrentCulture.Name));
            langsObj.Set("length", FenValue.FromNumber(1));
            navObj.Set("languages", FenValue.FromObject(langsObj));

            navObj.Set("hardwareConcurrency", FenValue.FromNumber(Environment.ProcessorCount));
            navObj.Set("deviceMemory", FenValue.FromNumber(8));
            navObj.Set("onLine", FenValue.FromBoolean(true));
            navObj.Set("pdfViewerEnabled", FenValue.FromBoolean(true));
            navObj.Set("webdriver", FenValue.FromBoolean(false)); 
            
            // [Compliance] userAgentData (Client Hints) - Simplified mock
            var uaData = new FenBrowser.FenEngine.Core.FenObject();
            uaData.Set("mobile", FenValue.FromBoolean(false));
            uaData.Set("platform", FenValue.FromString("Windows"));
            navObj.Set("userAgentData", FenValue.FromObject(uaData));

            // [Compliance] Log Client-Side Identity
            try
            {
                var ua = navObj.Get("userAgent").AsString();
                var platform = navObj.Get("platform").AsString();
                var vendor = navObj.Get("vendor").AsString();
                var cookie = navObj.Get("cookieEnabled").AsBoolean();
                FenLogger.Debug($"[Compliance] JS Navigator: UA='{ua}' Platform='{platform}' Vendor='{vendor}' CookieEnabled={cookie}", LogCategory.JavaScript);
            }
            catch { }

            // Service Workers API - navigator.serviceWorker
            // Service Workers API - navigator.serviceWorker
            var swOrigin = OriginKey(_ctx?.BaseUri);
            navObj.Set("serviceWorker", FenValue.FromObject(new FenBrowser.FenEngine.Workers.ServiceWorkerContainer(swOrigin)));
            
            // Clipboard API - navigator.clipboard
            navObj.Set("clipboard", FenValue.FromObject(FenBrowser.FenEngine.WebAPIs.ClipboardAPI.CreateClipboardObject()));
            
            // Web Audio API - AudioContext constructor
            _fenRuntime.SetGlobal("AudioContext", FenValue.FromObject(FenBrowser.FenEngine.WebAPIs.WebAudioAPI.CreateAudioContextConstructor(_fenRuntime.Context)));
            _fenRuntime.SetGlobal("webkitAudioContext", FenValue.FromObject(FenBrowser.FenEngine.WebAPIs.WebAudioAPI.CreateAudioContextConstructor(_fenRuntime.Context)));
            
            // WebRTC API - RTCPeerConnection constructor
            _fenRuntime.SetGlobal("RTCPeerConnection", FenValue.FromObject(FenBrowser.FenEngine.WebAPIs.WebRTCAPI.CreateRTCPeerConnectionConstructor(_fenRuntime.Context)));
            _fenRuntime.SetGlobal("webkitRTCPeerConnection", FenValue.FromObject(FenBrowser.FenEngine.WebAPIs.WebRTCAPI.CreateRTCPeerConnectionConstructor(_fenRuntime.Context)));
            _fenRuntime.SetGlobal("MediaStream", FenValue.FromObject(FenBrowser.FenEngine.WebAPIs.WebRTCAPI.CreateMediaStreamConstructor()));
            
            // Notifications API - Notification constructor
            _fenRuntime.SetGlobal("Notification", FenValue.FromObject(FenBrowser.FenEngine.WebAPIs.NotificationsAPI.CreateNotificationConstructor()));
        }

        private async Task<string> FetchThroughNetworkHandlerAsync(Uri uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            if (FetchHandler == null) throw new InvalidOperationException("FetchHandler not configured on engine");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await FetchHandler(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        private bool IsWorkerScriptUriAllowed(Uri scriptUri)
        {
            if (scriptUri == null) return false;
            if (!(scriptUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  scriptUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (SubresourceAllowed != null)
            {
                try
                {
                    if (!SubresourceAllowed(scriptUri, "worker"))
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            var baseUri = _ctx?.BaseUri;
            if (baseUri != null && !CorsHandler.IsSameOrigin(scriptUri, baseUri))
                return false;

            return true;
        }


        // XHR state
        // (XhrState is defined later)
        private readonly Dictionary<string, XhrState> _xhr = new Dictionary<string, XhrState>(StringComparer.Ordinal);
        private readonly object _xhrLock = new object();

        // flags
        private bool _allowExternalScripts;
        private bool _executeInlineScriptsOnInnerHTML;
        private SandboxPolicy _sandbox = SandboxPolicy.AllowAll;

        public SandboxPolicy Sandbox
        {
            get { return _sandbox; }
            set { _sandbox = value ?? SandboxPolicy.AllowAll; }
        }

        public bool AllowExternalScripts
        {
            get { return _allowExternalScripts && _sandbox.Allows(SandboxFeature.ExternalScripts); }
            set { _allowExternalScripts = value; }
        }

        public bool ExecuteInlineScriptsOnInnerHTML
        {
            get { return _executeInlineScriptsOnInnerHTML && _sandbox.Allows(SandboxFeature.InlineScripts); }
            set { _executeInlineScriptsOnInnerHTML = value; }
        }

        public bool UseMiniPrattEngine { get; set; } = true;
        
        public Action RequestRender
        {
            get => _fenRuntime?.RequestRender;
            set { if (_fenRuntime != null) _fenRuntime.RequestRender = value; }
        }
        
        /// <summary>Get the DOM root that JS is using (for repaint sync)</summary>
        public Node DomRoot => _domRoot;
        
        // Document ready state (for document.readyState property)

        public JsVal OnPopState { get; set; }
        public JsVal OnHashChange { get; set; }
        


        // Canvas persistence
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Element, SKBitmap> _canvasBitmaps 
            = new System.Runtime.CompilerServices.ConditionalWeakTable<Element, SKBitmap>();

        public static void RegisterCanvasBitmap(Element element, SKBitmap bitmap)
        {
            if (element  == null || bitmap  == null) return;
            // ConditionalWeakTable doesn't have indexer setter, use Remove/Add or GetValue to set
            _canvasBitmaps.Remove(element);
            _canvasBitmaps.Add(element, bitmap);
        }

        public static SKBitmap GetCanvasBitmap(Element element)
        {
            if (element  == null) return null;
            _canvasBitmaps.TryGetValue(element, out var bitmap);
            return bitmap;
        }
        public int CallStackDepth; // Recursion limit counter

        internal bool SandboxAllows(SandboxFeature feature, string detail = null)
        {
            if (_sandbox.Allows(feature)) return true;
            RecordSandboxBlock(feature, detail);
            return false;
        }

        private void RecordSandboxBlock(SandboxFeature feature, string detail)
        {
            var messageDetail = detail ?? string.Empty;
            try { TraceFeatureGap("Sandbox", feature.ToString(), messageDetail); } catch { }
            try
            {
                var status = string.IsNullOrWhiteSpace(messageDetail)
                    ? "[Sandbox] Blocked " + feature
                    : "[Sandbox] Blocked " + feature + " : " + messageDetail;
                _host?.SetStatus(status);
            }
            catch { }

            lock (_sandboxLogLock)
            {
                if (_sandboxBlocks.Count >= SandboxBlockCapacity) _sandboxBlocks.Dequeue();
                _sandboxBlocks.Enqueue(new SandboxBlockRecord
                {
                    Feature = feature,
                    Detail = messageDetail,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        public SandboxBlockRecord[] GetSandboxBlocksSnapshot()
        {
            lock (_sandboxLogLock) { return _sandboxBlocks.ToArray(); }
        }

        public void ClearSandboxBlockLog()
        {
            lock (_sandboxLogLock) { _sandboxBlocks.Clear(); }
        }

        // in JavaScriptEngine fields
        private readonly HashSet<string> _script404 = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _script404Lock = new object();

        // element-level listeners - REPLACED with generic object storage
        // private readonly Dictionary<string, Dictionary<string, List<string>>> _evtEl = ...

        // Centralized event listener storage for all objects (Window, Document, Element)
        // Key: The target object (Element, or FenObject for Window/Document)
        // Value: Dictionary of EventName -> List of Handlers
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, List<FenFunction>>> _objectEventListeners =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, List<FenFunction>>>();

        private readonly Dictionary<string, Dictionary<string, List<string>>> _evtEl =
            new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);

        // Optional bridge supplied by host to provide a CookieContainer for managed HttpClient fallbacks.
        public Func<Uri, System.Net.CookieContainer> CookieBridge { get; set; }
        
        // --- lightweight fields that may be missing in some merge states ---
        // DOM visual registry
        private static readonly System.Collections.Generic.Dictionary<Element, System.WeakReference> _visualMap =
            new System.Collections.Generic.Dictionary<Element, System.WeakReference>(System.Collections.Generic.EqualityComparer<Element>.Default);
        private static System.WeakReference _visualRoot;

        // DOM root exposed to the engine
        private Node _domRoot;

        private readonly object _sandboxLogLock = new object();
        private readonly Queue<SandboxBlockRecord> _sandboxBlocks = new Queue<SandboxBlockRecord>();
        private const int SandboxBlockCapacity = 32;

        public struct SandboxBlockRecord
        {
            public SandboxFeature Feature;
            public string Detail;
            public DateTime Timestamp;
        }

    // Microtask queue
    private readonly System.Collections.Generic.Queue<System.Action> _microtasks = new System.Collections.Generic.Queue<System.Action>();
    private readonly object _microtaskLock = new object();
    private bool _microtaskPumpScheduled = false;

    // Macro-task queue (setTimeout, setInterval, etc.)
    
    // Feature gap tracing throttling
    private readonly object _featureTraceLock = new object();
    private string _lastFeatureTraceKey;
    private System.DateTime _lastFeatureTraceTime = System.DateTime.MinValue;

        // Response registry (tokenized large response bodies)
        

        // Inline thresholds / repaint flags

        

        // --- Mobile-oriented JS limits ---
        // Soft cap on total bytes of script source executed per page. This is
        // intended to keep mobile-class devices responsive by skipping
        // extremely large desktop-style bundles while still running typical
        // mobile-sized scripts.
        private int _pageScriptByteBudget = 10 * 1024 * 1024; // 10 MB default for modern sites
        

        // Small allowance for very tiny inline handlers (e.g., "return false")
        // that should work even when the main script budget is exhausted.
        private const int TinyInlineFreeThreshold = 256;

        // Optional external script fetcher (e.g., wired to ResourceManager.FetchTextAsync)
        // Signature: (uri, referer) => script text or null.
        public Func<Uri, Uri, Task<string>> ExternalScriptFetcher { get; set; }

        // Small in-memory LRU cache for script text, keyed by absolute URL.
        private sealed class ScriptCacheEntry { public string Body; }
        private readonly Dictionary<string, LinkedListNode<Tuple<string, ScriptCacheEntry>>> _scriptMap =
            new Dictionary<string, LinkedListNode<Tuple<string, ScriptCacheEntry>>>(StringComparer.Ordinal);
        private readonly LinkedList<Tuple<string, ScriptCacheEntry>> _scriptLru =
            new LinkedList<Tuple<string, ScriptCacheEntry>>();

        // Prefetched module source cache keyed by absolute module URI.
        // This avoids sync-blocking network bridges in module-loader hot paths.
        private readonly Dictionary<string, string> _prefetchedModuleSource =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _prefetchedModuleSourceLock = new object();
        private const int MaxModulePrefetchDepth = 32;

        private static readonly Regex ModuleImportFromRegex = new Regex(
            @"\b(?:import|export)\b[\s\S]*?\bfrom\s*['""](?<spec>[^'""]+)['""]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ModuleImportSideEffectRegex = new Regex(
            @"\bimport\s*['""](?<spec>[^'""]+)['""]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ModuleDynamicImportLiteralRegex = new Regex(
            @"\bimport\s*\(\s*['""](?<spec>[^'""]+)['""]\s*\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);


        /// <summary>
        /// Gets or sets the approximate per-page script byte budget. When the
        /// total executed script source exceeds this value, subsequent
        /// external/inline scripts are skipped for performance. Set to 0 or a
        /// negative value to disable the budget.
        /// </summary>
        public int PageScriptByteBudget
        {
            get { return _pageScriptByteBudget; }
            set { _pageScriptByteBudget = value; }
        }

        // ECMAScript modules
        

        // Mutation observers / pending mutations
        
        private readonly object _mutationLock = new object();
        
        // --- DOM visual registry for approximate layout metrics ---
        // [MIGRATION] Avalonia Visual Registry removed. Layout metrics should be retrieved from SkiaDomRenderer.
        
        public static void RegisterDomVisual(Element node, object fe)
        {
            // No-op
        }

        public static object GetControlForElement(Element node)
        {
            return null;
        }

        public static bool TryGetVisualRect(Element node, out double x, out double y, out double w, out double h)
        {
            x = y = w = h = 0;
            // TODO: Query SkiaDomRenderer for layout box
            return false;
        }

        internal static object GetVisual(Element node)
        {
            return null;
        }
        public static void RegisterVisualRoot(object root)
        {
            // No-op
        }
        
        // ---- Phase 1/2/3 state ----
        private readonly Dictionary<string, List<string>> _evtDoc = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _evtWin = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // Mini interpreter event listeners - DISABLED
        // private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>> _miniEvtDoc = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>>(System.StringComparer.OrdinalIgnoreCase);
        // private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>> _miniEvtWin = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>>(System.StringComparer.OrdinalIgnoreCase);
        // private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>>> _miniEvtEl = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>>>(System.StringComparer.Ordinal);

    // Track stopPropagation requests inside a single handler execution (JS-0 allowlist)
    private volatile bool _stopPropagationRequested;
    // Track preventDefault requests surfaced via runtime event wrappers
    

        private void RegisterListener(Dictionary<string, List<string>> bag, string evt, string fnName)
        {
            if (string.IsNullOrWhiteSpace(evt) || string.IsNullOrWhiteSpace(fnName)) return;
            List<string> list;
            if (!bag.TryGetValue(evt, out list) || list  == null) { list = new List<string>(); bag[evt] = list; }
            if (!list.Contains(fnName)) list.Add(fnName);
        }

        private void RemoveListener(Dictionary<string, List<string>> bag, string evt, string fnName)
        {
            List<string> list; if (!bag.TryGetValue(evt, out list) || list  == null) return;
            list.Remove(fnName);
        }

        private void FireDocumentEvent(string evt)
        {
            try
            {
                List<string> list; if (_evtDoc.TryGetValue(evt, out list) && list != null)
                {
                    foreach (var fn in list.ToArray())
                    {
                        EnqueueMicrotask(() => { try { RunInline(fn + "({ type:'" + evt + "', target:'document' })", _ctx, evt, "document"); } catch { } });
                    }
                }
            }
            catch { }

            // MiniJs event support - DISABLED
            /*
            try
            {
                System.Collections.Generic.List<MiniJs.JsFunction> mlist; if (_miniEvtDoc.TryGetValue(evt, out mlist) && mlist != null)
                {
                    foreach (var fn in mlist.ToArray())
                    {
                        try { var args = new System.Collections.Generic.List<MiniJs.JsValue>(); var ev = MiniEvent("document", evt); args.Add(ev); _mini?.Invoke(fn, args); } catch { }
                    }
                }
            }
            catch { }
            */
        }

        private void FireWindowEvent(string evt)
        {
            try
            {
                List<string> list; if (_evtWin.TryGetValue(evt, out list) && list != null)
                {
                    foreach (var fn in list.ToArray())
                    {
                        EnqueueMicrotask(() => { try { RunInline(fn + "({ type:'" + evt + "', target:'window' })", _ctx, evt, "window"); } catch { } });
                    }
                }
            }
            catch { }

            // MiniJs event support - DISABLED
            /*
            try
            {
                System.Collections.Generic.List<MiniJs.JsFunction> mlist; if (_miniEvtWin.TryGetValue(evt, out mlist) && mlist != null)
                {
                    foreach (var fn in mlist.ToArray())
                    {
                        try { var args = new System.Collections.Generic.List<MiniJs.JsValue>(); var ev = MiniEvent("window", evt); args.Add(ev); _mini?.Invoke(fn, args); } catch { }
                    }
                }
            }
            catch { }
            */
        }

        // intervals & rAF
        private readonly Dictionary<int, Timer> _intervals = new Dictionary<int, Timer>();
        private int _nextIntervalId;
        private readonly Dictionary<int, Timer> _rafs = new Dictionary<int, Timer>();
        private int _nextRafId;

        // storage (origin-scoped, in-memory)
        private readonly Dictionary<string, Dictionary<string, string>> _localStorageMap =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _sessionStorageMap =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _storageLock = new object();
        private readonly string _sessionStoragePartitionId = Guid.NewGuid().ToString("N");
        

        // loader/event firing



        // lightweight navigation history
        // Enqueue a microtask to run after current synchronous work
        private void EnqueueMicrotask(Action a)
        {
            EnqueueMicrotaskInternal(a);
        }
        private void RegisterElementListener(string id, string evt, string fnName)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(evt) || string.IsNullOrWhiteSpace(fnName)) return;
            Dictionary<string, List<string>> byEvt;
            if (!_evtEl.TryGetValue(id, out byEvt))
            {
                byEvt = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                _evtEl[id] = byEvt;
            }
            List<string> list;
            if (!byEvt.TryGetValue(evt, out list)) { list = new List<string>(); byEvt[evt] = list; }
            if (!list.Contains(fnName)) list.Add(fnName);
        }

        private void RemoveElementListener(string id, string evt, string fnName)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(evt) || string.IsNullOrWhiteSpace(fnName))
                return;

            try
            {
                Dictionary<string, List<string>> byEvt;
                if (!_evtEl.TryGetValue(id, out byEvt) || byEvt  == null)
                    return;

                List<string> list;
                if (!byEvt.TryGetValue(evt, out list) || list  == null)
                    return;

                list.Remove(fnName);

                // cleanup empty collections to keep the structure tidy
                if (list.Count == 0)
                    byEvt.Remove(evt);

                if (byEvt.Count == 0)
                    _evtEl.Remove(id);
            }
            catch
            {
                // swallow errors to match existing style
            }
        }


        // MiniJs listener methods - DISABLED
        /*
        public void AddMiniDocumentListener(string evt, MiniJs.JsFunction fn)
        {
            if (string.IsNullOrEmpty(evt) || fn  == null) return;
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!_miniEvtDoc.TryGetValue(evt, out list) || list  == null) { list = new System.Collections.Generic.List<MiniJs.JsFunction>(); _miniEvtDoc[evt] = list; }
            if (!list.Contains(fn)) list.Add(fn);
        }
        public void RemoveMiniDocumentListener(string evt, MiniJs.JsFunction fn)
        {
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!_miniEvtDoc.TryGetValue(evt, out list) || list  == null) return; list.Remove(fn);
        }
        public void AddMiniWindowListener(string evt, MiniJs.JsFunction fn)
        {
            if (string.IsNullOrEmpty(evt) || fn  == null) return;
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!_miniEvtWin.TryGetValue(evt, out list) || list  == null) { list = new System.Collections.Generic.List<MiniJs.JsFunction>(); _miniEvtWin[evt] = list; }
            if (!list.Contains(fn)) list.Add(fn);
        }
        public void RemoveMiniWindowListener(string evt, MiniJs.JsFunction fn)
        {
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!_miniEvtWin.TryGetValue(evt, out list) || list  == null) return; list.Remove(fn);
        }
        public void AddMiniElementListener(string id, string evt, MiniJs.JsFunction fn)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(evt) || fn  == null) return;
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>> byEvt; if (!_miniEvtEl.TryGetValue(id, out byEvt) || byEvt  == null) { byEvt = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>>(System.StringComparer.OrdinalIgnoreCase); _miniEvtEl[id] = byEvt; }
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!byEvt.TryGetValue(evt, out list) || list  == null) { list = new System.Collections.Generic.List<MiniJs.JsFunction>(); byEvt[evt] = list; }
            if (!list.Contains(fn)) list.Add(fn);
        }
        public void RemoveMiniElementListener(string id, string evt, MiniJs.JsFunction fn)
        {
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>> byEvt; if (!_miniEvtEl.TryGetValue(id, out byEvt) || byEvt  == null) return;
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!byEvt.TryGetValue(evt, out list) || list  == null) return; list.Remove(fn);
        }
        */

        /// <summary>
        /// Raise an event on an element (asynchronous, DOM-triggered).
        /// Supports optional value and checked state for form controls.
        /// </summary>
        public void RaiseElementEvent(string id, string evt, string value = null, bool? isChecked = null)
        {
            if (string.IsNullOrWhiteSpace(evt)) return; // Allow empty ID for bubbling
            try
            {
                // For JS-0 style inline handlers (avoid C# 7 'out var' for WP8.1 toolchain)
                List<string> list = null; Dictionary<string, List<string>> byEvt = null;
                if (!string.IsNullOrWhiteSpace(id) && _evtEl.TryGetValue(id, out byEvt) && byEvt != null && byEvt.TryGetValue(evt, out list) && list != null)
                {
                    foreach (var fn in list.ToArray())
                    {
                        try
                        {
                            RunInline(fn + "({ type:'" + evt + "', target:'" + id + "' })", _ctx, evt, id);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            
            // Dispatch to FenRuntime (simulating bubbling to window)
            FenLogger.Debug($"[RaiseElementEvent] Check _fenRuntime: {(_fenRuntime  == null ? "NULL" : "OK")}", LogCategory.Events);
            if (_fenRuntime != null)
            {
                try
                {
                    var eventObj = new FenBrowser.FenEngine.Core.FenObject();
                    eventObj.Set("type", FenValue.FromString(evt));
                    eventObj.Set("timeStamp", FenValue.FromNumber((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds));
                    
                    var targetObj = new FenBrowser.FenEngine.Core.FenObject();
                    targetObj.Set("id", FenValue.FromString(id ?? ""));
                    if (value != null) targetObj.Set("value", FenValue.FromString(value));
                    if (isChecked.HasValue) targetObj.Set("checked", FenValue.FromBoolean(isChecked.Value));
                    
                    eventObj.Set("target", FenValue.FromObject(targetObj));
                    
                    _fenRuntime.DispatchEvent(evt, eventObj);
                }
                catch (Exception ex)
                {
                    /* [PERF-REMOVED] */
                }
            }

            // MiniJs event support - DISABLED
            /*
            try
            {
                System.Collections.Generic.List<MiniJs.JsFunction> list; if (_miniEvtEl.TryGetValue(id, out var byEvt) && byEvt != null && byEvt.TryGetValue(evt, out list) && list != null)
                {
                    foreach (var fn in list.ToArray())
                    {
                        try
                        {
                            var args = new System.Collections.Generic.List<MiniJs.JsValue>();
                            var ev = MiniEvent(id, evt);
                            if (evt == "change" || evt == "input")
                            {
                                if (!string == nullOrEmpty(value)) ev.Obj["value"] = MiniJs.JsValue.From(value);
                                if (isChecked.HasValue) ev.Obj["checked"] = MiniJs.JsValue.From(isChecked.Value);
                            }
                            args.Add(ev);
                            _mini?.Invoke(fn, args);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            */
        }

        /// <summary>
        /// Raise an event on an element synchronously (returns bool for preventDefault check).
        /// Supports additional optional parameters for position/properties.
        /// </summary>
        public bool RaiseElementEventSync(string id, string evt, string value = null, bool? isChecked = null, double? posX = null, double? posY = null)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(evt)) return false;
            _stopPropagationRequested = false;
            try
            {
                // JS-0 style handlers (avoid C# 7 'out var')
                List<string> list = null; Dictionary<string, List<string>> byEvt = null;
                if (_evtEl.TryGetValue(id, out byEvt) && byEvt != null && byEvt.TryGetValue(evt, out list) && list != null)
                {
                    foreach (var fnName in list.ToArray())
                    {
                        try
                        {
                            RunInline(fnName + "({ type:'" + evt + "', target:'" + id + "' })", _ctx, evt, id);
                            if (_stopPropagationRequested) return true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return _stopPropagationRequested;
        }

        /// <summary>
        /// Raise an event by element ID (alternative entry point).
        /// </summary>
        public void RaiseElementEventById(string id, string evt)
        {
            RaiseElementEvent(id, evt);
        }

        // MiniJs.JsValue MiniEvent method - DISABLED
        /*
        private MiniJs.JsValue MiniEvent(string target, string type)
        {
            var e = MiniJs.JsValue.ObjLit();
            e.Obj["target"] = MiniJs.JsValue.From(target ?? "");
            e.Obj["type"] = MiniJs.JsValue.From(type ?? "");
            bool canceled = false; bool stopped = false;
            e.Obj["preventDefault"] = MiniJs.JsValue.Func(new MiniJs.JsFunction { Native = _ => { canceled = true; return MiniJs.JsValue.Undefined(); } });
            e.Obj["stopPropagation"] = MiniJs.JsValue.Func(new MiniJs.JsFunction { Native = _ => { stopped = true; return MiniJs.JsValue.Undefined(); } });
            return e;
        }
        */


        // ---------------- Intervals ----------------
        private int ScheduleInterval(string codeOrFn, int ms, bool isFnName)
        {
            if (ms < 0) ms = 0;
            var id = Interlocked.Increment(ref _nextIntervalId);
            Timer t = null;
            var repaintHost = _host as IJsHostRepaint;
            TimerCallback tick = _ =>
            {
                try
                {
                    Action run = () => {
                        try
                        {
                            if (isFnName) RunInline(codeOrFn + "()", _ctx);
                            else RunInline(codeOrFn, _ctx);
                        }
                        catch { }
                    };
                    if (repaintHost != null) repaintHost.InvokeOnUiThread(run); else run();
                }
                catch { }
            };
            t = new Timer(tick, null, ms, ms <= 0 ? 1 : ms);
            lock (_intervals) _intervals[id] = t;
            return id;
        }

        private void ClearInterval(int id)
        {
            lock (_intervals)
            {
                Timer t; if (_intervals.TryGetValue(id, out t))
                {
                    try { t.Dispose(); } catch { }
                    _intervals.Remove(id);
                }
            }
        }

        // ---------------- rAF (approx 60 FPS using timeout ~16ms) ----------------
        private int RequestAnimationFrame(string fnName)
        {
            var id = Interlocked.Increment(ref _nextRafId);
            var repaintHost = _host as IJsHostRepaint;
            Timer t = new Timer(_ =>
            {
                try
                {
                    Action run = () => { try { RunInline(fnName + "(Date.now&&Date.now()||0)", _ctx); } catch { } };
                    if (repaintHost != null) repaintHost.InvokeOnUiThread(run); else run();
                }
                catch { }
                finally { CancelAnimationFrame(id); }
            }, null, 16, Timeout.Infinite);
            lock (_rafs) _rafs[id] = t;
            return id;
        }

        private void CancelAnimationFrame(int id)
        {
            lock (_rafs)
            {
                Timer t; if (_rafs.TryGetValue(id, out t))
                {
                    try { t.Dispose(); } catch { }
                    _rafs.Remove(id);
                }
            }
        }

        // ---------------- Storage ----------------
        private static string OriginKey(Uri u)
        {
            if (u  == null) return "null://";
            var port = u.IsDefaultPort ? "" : (":" + u.Port);
            return (u.Scheme ?? "http") + "://" + (u.Host ?? "localhost") + port;
        }

        private Dictionary<string, string> GetLocalStorageFor(Uri baseUri)
        {
            var key = OriginKey(baseUri);
            lock (_storageLock)
            {
                Dictionary<string, string> bag;
                if (!_localStorageMap.TryGetValue(key, out bag))
                {
                    bag = new Dictionary<string, string>(StringComparer.Ordinal);
                    _localStorageMap[key] = bag;
                }
                return bag;
            }
        }

        private Dictionary<string, string> GetSessionStorageFor(Uri baseUri)
        {
            var key = OriginKey(baseUri);
            lock (_storageLock)
            {
                Dictionary<string, string> bag;
                if (!_sessionStorageMap.TryGetValue(key, out bag))
                {
                    bag = new Dictionary<string, string>(StringComparer.Ordinal);
                    _sessionStorageMap[key] = bag;
                }
                return bag;
            }
        }

        // Persist localStorage to disk (best-effort)
        private async Task SaveLocalStorageAsync()
        {
            await Task.CompletedTask;
            /*
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(LocalStorageFile, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                var sb = new StringBuilder();
                lock (_storageLock)
                {
                    foreach (var origin in _localStorageMap)
                    {
                        if (origin.Value  == null) continue;
                        foreach (var kv in origin.Value)
                        {
                            var line = (origin.Key ?? "") + "\t" + (kv.Key ?? "") + "\t" + (kv.Value ?? "");
                            sb.AppendLine(line);
                        }
                    }
                }
                await Windows.Storage.FileIO.WriteTextAsync(file, sb.ToString());
            }
            catch { }
            */
        }

        private async Task RestoreLocalStorageAsync()
        {
            await Task.CompletedTask;
            /*
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                // Avoid first-chance FileNotFound by probing existence first
                var item = await folder.GetItemAsync(LocalStorageFile) as Windows.Storage.StorageFile;
                if (item  == null) return;
                var text = await Windows.Storage.FileIO.ReadTextAsync(item);
                if (string == nullOrWhiteSpace(text)) return;
                lock (_storageLock)
                {
                    foreach (var ln in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            var parts = ln.Split('\t');
                            if (parts.Length < 3) continue;
                            Dictionary<string, string> bag;
                            if (!_localStorageMap.TryGetValue(parts[0], out bag) || bag  == null)
                            {
                                bag = new Dictionary<string, string>(StringComparer.Ordinal);
                                _localStorageMap[parts[0]] = bag;
                            }
                            bag[parts[1]] = parts[2];
                        }
                        catch { }
                    }
                }
            }
            catch { }
            */
        }

        // ---------------- Cookies (best-effort via CookieBridge) ----------------
        private void SetCookieString(Uri scope, string cookieString)
        {
            if (!SandboxAllows(SandboxFeature.Storage, "document.cookie set")) return;
            try
            {
                if (CookieBridge  == null || scope  == null || string.IsNullOrWhiteSpace(cookieString)) return;
                var jar = CookieBridge(scope);
                if (jar  == null) return;
                jar.SetCookies(scope, cookieString);
            }
            catch { }
        }

        private string GetCookieString(Uri scope)
        {
            if (!SandboxAllows(SandboxFeature.Storage, "document.cookie get")) return string.Empty;
            try
            {
                if (CookieBridge  == null || scope  == null) return "";
                var jar = CookieBridge(scope);
                if (jar  == null) return "";
                var coll = jar.GetCookies(scope);
                if (coll  == null || coll.Count == 0) return "";

                var sb = new StringBuilder();
                bool first = true;

                foreach (System.Net.Cookie cookie in coll)
                {
                    if (!first) sb.Append("; ");
                    first = false;

                    sb.Append(cookie.Name)
                      .Append('=')
                      .Append(cookie.Value ?? string.Empty);
                }

                return sb.ToString();

            }
            catch { return ""; }
        }

        // ---------------- History ----------------
        private static string BaseWithoutFragment(Uri u)
        {
            try
            {
                if (u  == null) return null;
                return u.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
            }
            catch { return u != null ? ((u.Scheme ?? "") + "://" + (u.Host ?? "") + (u.PathAndQuery ?? "")) : null; }
        }

        private void HistoryPush(Uri u)
        {
            if (u  == null) return;
            if (!SandboxAllows(SandboxFeature.Navigation, "history.pushState -> " + (u?.AbsoluteUri ?? ""))) return;
            Uri prev = null; if (_historyIndex >= 0 && _historyIndex < _history.Count) prev = _history[_historyIndex];
            if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
            _history.Add(u);
            _historyIndex = _history.Count - 1;
            try { if (prev != null && string.Equals(BaseWithoutFragment(prev), BaseWithoutFragment(u), StringComparison.OrdinalIgnoreCase) && !string.Equals(prev.Fragment ?? "", u.Fragment ?? "", StringComparison.Ordinal)) FireWindowEvent("hashchange"); } catch { }
        }

        private void HistoryReplace(Uri u)
        {
            if (u  == null) return;
            if (!SandboxAllows(SandboxFeature.Navigation, "history.replaceState -> " + (u?.AbsoluteUri ?? ""))) return;
            Uri prev = null; if (_historyIndex >= 0 && _historyIndex < _history.Count) prev = _history[_historyIndex];
            if (_historyIndex < 0) { _history.Add(u); _historyIndex = _history.Count - 1; }
            else _history[_historyIndex] = u;
            try { if (prev != null && string.Equals(BaseWithoutFragment(prev), BaseWithoutFragment(u), StringComparison.OrdinalIgnoreCase) && !string.Equals(prev.Fragment ?? "", u.Fragment ?? "", StringComparison.Ordinal)) FireWindowEvent("hashchange"); } catch { }
        }

        private void HistoryGo(int delta)
        {
            var target = _historyIndex + delta;
            if (target < 0 || target >= _history.Count) return;
            if (!SandboxAllows(SandboxFeature.Navigation, "history.go(" + delta + ")")) return;
            _historyIndex = target;
            try { _host.Navigate(_history[_historyIndex]); } catch { }
            FireWindowEvent("popstate");
        }
        // Handles Phase 1/2/3 builtins; returns true if the line was handled.
        private bool HandlePhase123Builtins(string line, JsContext ctx)
        {
            // ---------- addEventListener / removeEventListener ----------
            var mAddEvt = Regex.Match(line, @"^(?<tgt>document|window)\s*\.addEventListener\s*\(\s*['""](?<evt>[^'""]+)['""]\s*,\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mAddEvt.Success)
            {
                var tgt = mAddEvt.Groups["tgt"].Value.ToLowerInvariant();
                var evt = mAddEvt.Groups["evt"].Value;
                var fn = mAddEvt.Groups["fn"].Value;
                if (tgt == "document") RegisterListener(_evtDoc, evt, fn); else RegisterListener(_evtWin, evt, fn);
                return true;
            }
            var mRemEvt = Regex.Match(line, @"^(?<tgt>document|window)\s*\.removeEventListener\s*\(\s*['""](?<evt>[^'""]+)['""]\s*,\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mRemEvt.Success)
            {
                var tgt = mRemEvt.Groups["tgt"].Value.ToLowerInvariant();
                var evt = mRemEvt.Groups["evt"].Value;
                var fn = mRemEvt.Groups["fn"].Value;
                if (tgt == "document") RemoveListener(_evtDoc, evt, fn); else RemoveListener(_evtWin, evt, fn);
                return true;
            }

            // ---------- setInterval / clearInterval ----------
            var mSI = Regex.Match(line, @"^\s*setInterval\s*\(\s*(?:['""](?<code>.*?)['""]|(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*))\s*,\s*(?<ms>\d+)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mSI.Success)
            {
                var ms = 0; int.TryParse(mSI.Groups["ms"].Value, out ms);
                var code = mSI.Groups["code"].Success ? mSI.Groups["code"].Value : null;
                var fn = mSI.Groups["fn"].Success ? mSI.Groups["fn"].Value : null;
                var id = ScheduleInterval(code ?? fn, ms, isFnName: fn != null);
                try { _host.SetStatus("setInterval id=" + id); } catch { }
                return true;
            }
            var mCI = Regex.Match(line, @"^\s*clearInterval\s*\(\s*(?<id>\d+)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mCI.Success) { int id; if (int.TryParse(mCI.Groups["id"].Value, out id)) ClearInterval(id); return true; }

            // ---------- requestAnimationFrame / cancelAnimationFrame ----------
            var mRaf = Regex.Match(line, @"^\s*requestAnimationFrame\s*\(\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mRaf.Success) { var id = RequestAnimationFrame(mRaf.Groups["fn"].Value); try { _host.SetStatus("rAF id=" + id); } catch { } return true; }
            var mCRaf = Regex.Match(line, @"^\s*cancelAnimationFrame\s*\(\s*(?<id>\d+)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mCRaf.Success) { int id; if (int.TryParse(mCRaf.Groups["id"].Value, out id)) CancelAnimationFrame(id); return true; }

            // ---------- localStorage ----------
            var mLSset = Regex.Match(line, @"^\s*localStorage\s*\.setItem\s*\(\s*['""](?<k>.+?)['""]\s*,\s*['""](?<v>.*?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mLSset.Success)
            {
                var originKey = OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri);
                StorageApi.SetLocalStorageItem(originKey, mLSset.Groups["k"].Value, mLSset.Groups["v"].Value);
                return true;
            }
            var mLSgetCb = Regex.Match(line, @"^\s*localStorage\s*\.getItem\s*\(\s*['""](?<k>.+?)['""]\s*,\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mLSgetCb.Success)
            {
                var originKey = OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri);
                var val = StorageApi.GetLocalStorageItem(originKey, mLSgetCb.Groups["k"].Value);
                var esc = JsEscape(val ?? "", '\''); EnqueueMicrotask(() => { try { RunInline(mLSgetCb.Groups["fn"].Value + "('" + esc + "')", _ctx); } catch { } });
                return true;
            }
            var mLSrem = Regex.Match(line, @"^\s*localStorage\s*\.removeItem\s*\(\s*['""](?<k>.+?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mLSrem.Success)
            {
                var originKey = OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri);
                StorageApi.RemoveLocalStorageItem(originKey, mLSrem.Groups["k"].Value);
                return true;
            }
            if (Regex.IsMatch(line, @"^\s*localStorage\s*\.clear\s*\(\s*\)\s*;?$", RegexOptions.IgnoreCase))
            {
                var originKey = OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri);
                StorageApi.ClearLocalStorage(originKey);
                return true;
            }

            // ---------- sessionStorage (in-memory only) ----------
            var mSSset = Regex.Match(line, @"^\s*sessionStorage\s*\.setItem\s*\(\s*['\""](?<k>.+?)['\""]\s*,\s*['\""](?<v>.*?)['\""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mSSset.Success)
            {
                var originKey = OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri);
                var sessionScope = StorageApi.BuildSessionScope(_sessionStoragePartitionId, originKey);
                StorageApi.SetSessionStorageItem(sessionScope, mSSset.Groups["k"].Value, mSSset.Groups["v"].Value);
                return true;
            }
            var mSSget = Regex.Match(line, @"^\s*sessionStorage\s*\.getItem\s*\(\s*['\""](?<k>.+?)['\""]\s*,\s*(?<fn>[A-ZaZ_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mSSget.Success)
            {
                var originKey = OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri);
                var sessionScope = StorageApi.BuildSessionScope(_sessionStoragePartitionId, originKey);
                var vSS = StorageApi.GetSessionStorageItem(sessionScope, mSSget.Groups["k"].Value);
                var escSS = JsEscape(vSS ?? "", '\''); EnqueueMicrotask(() => { try { RunInline(mSSget.Groups["fn"].Value + "('" + escSS + "')", _ctx); } catch { } });
                return true;
            }
            var mSSrem = Regex.Match(line, @"^\s*sessionStorage\s*\.removeItem\s*\(\s*['\""](?<k>.+?)['\""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mSSrem.Success)
            {
                var originKey = OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri);
                var sessionScope = StorageApi.BuildSessionScope(_sessionStoragePartitionId, originKey);
                StorageApi.RemoveSessionStorageItem(sessionScope, mSSrem.Groups["k"].Value);
                return true;
            }
            if (Regex.IsMatch(line, @"^\s*sessionStorage\s*\.clear\s*\(\s*\)\s*;?$", RegexOptions.IgnoreCase))
            {
                var originKey = OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri);
                var sessionScope = StorageApi.BuildSessionScope(_sessionStoragePartitionId, originKey);
                StorageApi.ClearSessionStorage(sessionScope);
                return true;
            }

            // ---------- document.cookie ----------
            var mCset = Regex.Match(line, @"^\s*document\s*\.cookie\s*=\s*['""](?<c>.+?)['""]\s*;?$", RegexOptions.IgnoreCase);
            if (mCset.Success) { SetCookieString(ctx?.BaseUri ?? _ctx?.BaseUri, mCset.Groups["c"].Value); return true; }
            var mCget = Regex.Match(line, @"^\s*__getCookie\s*\(\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase); // host helper
            if (mCget.Success)
            {
                var s = GetCookieString(ctx?.BaseUri ?? _ctx?.BaseUri);
                var esc = JsEscape(s ?? "", '\'');
                EnqueueMicrotask(() => { try { RunInline(mCget.Groups["fn"].Value + "('" + esc + "')", _ctx); } catch { } });
                return true;
            }

            // ---------- classList on element by id ----------
            var mCls = Regex.Match(line, @"^\s*document\s*\.\s*getElementById\s*\(\s*['""](?<id>.+?)['""]\s*\)\s*\.classList\s*\.(?<op>add|remove|toggle)\s*\(\s*['""](?<cls>.+?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mCls.Success && _domRoot != null)
            {
                var id = mCls.Groups["id"].Value; var op = mCls.Groups["op"].Value; var cls = mCls.Groups["cls"].Value;
                var doc = new JsDocument(this, _domRoot);
                var el = doc.getElementById(id) as JsDomElement;
                if (el != null)
                {
                    var classes = el.getAttribute("class") ?? "";
                    var set = new HashSet<string>((classes ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                    if (op == "add") set.Add(cls);
                    else if (op == "remove") set.Remove(cls);
                    else if (op == "toggle") { if (!set.Add(cls)) set.Remove(cls); }
                    el.setAttribute("class", string.Join(" ", set.ToArray()));
                }
                return true;
            }

            // ---------- history ----------
            var mPush = Regex.Match(line, @"^\s*history\s*\.pushState\s*\(\s*.*?,\s*['""](?<url>.*?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mPush.Success)
            {
                var u = Resolve(ctx?.BaseUri ?? _ctx?.BaseUri, mPush.Groups["url"].Value);
                if (u != null) { HistoryPush(u); try { _host.SetStatus("pushState -> " + u); } catch { } }
                return true;
            }
            var mRep = Regex.Match(line, @"^\s*history\s*\.replaceState\s*\(\s*.*?,\s*['""](?<url>.*?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mRep.Success)
            {
                var u = Resolve(ctx?.BaseUri ?? _ctx?.BaseUri, mRep.Groups["url"].Value);
                if (u != null) { HistoryReplace(u); try { _host.SetStatus("replaceState -> " + u); } catch { } }
                return true;
            }
            if (Regex.IsMatch(line, @"^\s*history\s*\.back\s*\(\s*\)\s*;?$", RegexOptions.IgnoreCase)) { HistoryGo(-1); return true; }
            if (Regex.IsMatch(line, @"^\s*history\s*\.forward\s*\(\s*\)\s*;?$", RegexOptions.IgnoreCase)) { HistoryGo(1); return true; }
            var mGo = Regex.Match(line, @"^\s*history\s*\.go\s*\(\s*(?<n>-?\d+)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mGo.Success) { int n; if (int.TryParse(mGo.Groups["n"].Value, out n)) HistoryGo(n); return true; }

            // ---------- new Audio("url").play() stub (no-op) ----------
            var mAudioNewPlay = System.Text.RegularExpressions.Regex.Match(
                line,
                "^\\s*new\\s+Audio\\s*\\(\\s*(['\"'])(?<url>.*?)\\1\\s*\\)\\s*\\.\\s*play\\s*\\(\\s*\\)\\s*;?\\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mAudioNewPlay.Success)
            {
                try
                {
                    TraceFeatureGap("Audio", "new Audio().play", mAudioNewPlay.Groups["url"].Value ?? string.Empty);
                }
                catch { }
                return true;
            }

            // ---------- atob / btoa for innerText ----------
            var mAtob = Regex.Match(line, @"^\s*document\s*\.\s*getElementById\s*\(\s*['""](?<id>.+?)['""]\s*\)\s*\.innerText\s*=\s*atob\s*\(\s*['""](?<b64>.*?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mAtob.Success && _domRoot != null)
            {
                try
                {
                    var id = mAtob.Groups["id"].Value; var b = mAtob.Groups["b64"].Value;
                    var bytes = Convert.FromBase64String(b);
                    var txt = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
                    var doc = new JsDocument(this, _domRoot);
                    var el = doc.getElementById(id) as JsDomElement;
                    if (el != null) { el.innerText = txt; RequestRepaint(); }
                }
                catch { }
                return true;
            }
            // document.getElementById('id').addEventListener('evt', fn)
            var mElAdd = Regex.Match(line,
                @"^\s*document\s*\.getElementById\s*\(\s*['""](?<id>.+?)['""]\s*\)\s*\.addEventListener\s*\(\s*['""](?<evt>[^'""]+)['""]\s*,\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$",
                RegexOptions.IgnoreCase);
            if (mElAdd.Success)
            {
                RegisterElementListener(mElAdd.Groups["id"].Value, mElAdd.Groups["evt"].Value, mElAdd.Groups["fn"].Value);
                return true;
            }

            // document.getElementById('id').removeEventListener('evt', fn)
            var mElRem = Regex.Match(line,
                @"^\s*document\s*\.getElementById\s*\(\s*['""](?<id>.+?)['""]\s*\)\s*\.removeEventListener\s*\(\s*['""](?<evt>[^'""]+)['""]\s*,\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$",
                RegexOptions.IgnoreCase);
            if (mElRem.Success)
            {
                RemoveElementListener(mElRem.Groups["id"].Value, mElRem.Groups["evt"].Value, mElRem.Groups["fn"].Value);
                return true;
            }
            var mBtoa = Regex.Match(line, @"^\s*document\s*\.\s*getElementById\s*\(\s*['""](?<id>.+?)['""]\s*\)\s*\.innerText\s*=\s*btoa\s*\(\s*['""](?<txt>.*?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mBtoa.Success && _domRoot != null)
            {
                try
                {
                    var id = mBtoa.Groups["id"].Value; var t = mBtoa.Groups["txt"].Value;
                    var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(t ?? ""));
                    var doc = new JsDocument(this, _domRoot);
                    var el = doc.getElementById(id) as JsDomElement;
                    if (el != null) { el.innerText = b64; RequestRepaint(); }
                }
                catch { }
                return true;
            }

            // ---------- fetch(url).then(fn) ----------
            var mFetch = Regex.Match(line, @"^\s*fetch\s*\(\s*(['""])(?<url>.*?)\1\s*\)\s*\.then\s*\(\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?\s*$", RegexOptions.IgnoreCase);
            if (mFetch.Success)
            {
                var url = mFetch.Groups["url"].Value;
                var fn = mFetch.Groups["fn"].Value;
                var uri = Resolve(_ctx?.BaseUri, url);
                if (uri != null)
                {
                    var pageOrigin = _ctx?.BaseUri;
                    Task.Run(async () =>
                    {
                        try
                        {
                            using (var client = new System.Net.Http.HttpClient(CreateManagedHandler()))
                            {
                                client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent));
                                
                                // Add Origin header for CORS
                                if (pageOrigin != null)
                                {
                                    var originStr = $"{pageOrigin.Scheme}://{pageOrigin.Host}";
                                    if (!pageOrigin.IsDefaultPort && pageOrigin.Port != -1)
                                        originStr += $":{pageOrigin.Port}";
                                    client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", originStr);
                                }
                                
                                var response = await client.GetAsync(uri);
                                var text = await response.Content.ReadAsStringAsync();
                                
                                // CORS check for cross-origin requests
                                bool corsOk = true;
                                if (pageOrigin != null && !FenBrowser.Core.Network.Handlers.CorsHandler.IsSameOrigin(uri, pageOrigin))
                                {
                                    corsOk = FenBrowser.Core.Network.Handlers.CorsHandler.IsCorsAllowed(response, uri, pageOrigin);
                                }
                                
                                if (corsOk && text != null)
                                {
                                    var token = RegisterResponseBody(text);
                                    EnqueueMicrotask(() =>
                                    {
                                        try { RunInline(fn + "({ ok:true, status:200, text:function(){ return '" + JsEscape(text) + "'; }, json:function(){ return JSON.parse('" + JsEscape(text) + "'); } })", _ctx); } catch { }
                                    });
                                }
                                else if (!corsOk)
                                {
                                    // CORS blocked
                                    EnqueueMicrotask(() =>
                                    {
                                        try { RunInline(fn + "({ ok:false, status:0, statusText:'CORS error' })", _ctx); } catch { }
                                    });
                                }
                            }
                        }
                        catch { }
                    });
                }
                return true;
            }

// ---------- setTimeout / clearTimeout ----------
var mST = System.Text.RegularExpressions.Regex.Match(line, @"^\s*setTimeout\s*\(\s*(?:['""](?<code>.*?)['""]|(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*))\s*,\s*(?<ms>\d+)\s*\)\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mST.Success)
            {
                var ms = 0; int.TryParse(mST.Groups["ms"].Value, out ms);
                var code = mST.Groups["code"].Success ? mST.Groups["code"].Value : null;
                var fn = mST.Groups["fn"].Success ? mST.Groups["fn"].Value : null;
                var id = System.Threading.Interlocked.Increment(ref _nextTimerId);
                System.Threading.Timer t = null;
                System.Threading.TimerCallback fire = _ =>
                {
                    try
                    {
                        var repaintHost = _host as IJsHostRepaint;
                        System.Action run = () => {
                            try
                            {
                                if (fn != null) RunInline(fn + "()", _ctx);
                                else if (!string.IsNullOrEmpty(code)) RunInline(code, _ctx);
                            }
                            catch { }
                        };
                        if (repaintHost != null) repaintHost.InvokeOnUiThread(run); else run();
                    }
                    catch { }
                    finally
                    {
                        lock (_timers) { try { if (_timers.ContainsKey(id)) { _timers[id].Dispose(); } } catch { } _timers.Remove(id); }
                    }
                };
                t = new System.Threading.Timer(fire, null, ms, System.Threading.Timeout.Infinite);
                lock (_timers) _timers[id] = t;
                try { _host.SetStatus("setTimeout id=" + id); } catch { }
                return true;
            }
            var mCT = System.Text.RegularExpressions.Regex.Match(line, @"^\s*clearTimeout\s*\(\s*(?<id>\d+)\s*\)\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mCT.Success)
            {
                int id; if (int.TryParse(mCT.Groups["id"].Value, out id))
                {
                    lock (_timers)
                    {
                        System.Threading.Timer t; if (_timers.TryGetValue(id, out t)) { try { t.Dispose(); } catch { } _timers.Remove(id); }
                    }
                }
                return true;
            }

            // ---------- console.log / console.error ----------
            var mLog = System.Text.RegularExpressions.Regex.Match(line, @"^\s*console\s*\.\s*(?<kind>log|error|warn)\s*\(\s*['""](?<msg>.*?)['""]\s*\)\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mLog.Success)
            {
                try { _host.SetStatus((mLog.Groups["kind"].Value.ToLowerInvariant()) + ": " + mLog.Groups["msg"].Value); } catch { }
                return true;
            }

            // ---------- location.assign / replace / href= ----------
            var mWindowOpen = System.Text.RegularExpressions.Regex.Match(
                line,
                @"^\s*(?:window\s*\.\s*)?open\s*\(\s*['""](?<u>.+?)['""](?:\s*,.*)?\)\s*;?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mWindowOpen.Success)
            {
                if (BrowserSettings.Instance.BlockPopups)
                {
                    try { _host.SetStatus("popup blocked by policy"); } catch { }
                    return true;
                }

                var u = Resolve(ctx?.BaseUri ?? _ctx?.BaseUri, mWindowOpen.Groups["u"].Value);
                if (u != null) { try { _host.Navigate(u); } catch { } }
                return true;
            }

            var mAssign = System.Text.RegularExpressions.Regex.Match(line, @"^\s*location\s*\.\s*assign\s*\(\s*['""](?<u>.+?)['""]\s*\)\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mAssign.Success)
            {
                var u = Resolve(ctx?.BaseUri ?? _ctx?.BaseUri, mAssign.Groups["u"].Value);
                if (u != null) { try { _host.Navigate(u); } catch { } }
                return true;
            }
            var mReplace = System.Text.RegularExpressions.Regex.Match(line, @"^\s*location\s*\.\s*replace\s*\(\s*['""](?<u>.+?)['""]\s*\)\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mReplace.Success)
            {
                var u = Resolve(ctx?.BaseUri ?? _ctx?.BaseUri, mReplace.Groups["u"].Value);
                if (u != null) { try { _host.Navigate(u); } catch { } }
                return true;
            }
            var mHrefSet = System.Text.RegularExpressions.Regex.Match(line, @"^\s*location\s*\.\s*href\s*=\s*['""](?<u>.+?)['""]\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mHrefSet.Success)
            {
                var u = Resolve(ctx?.BaseUri ?? _ctx?.BaseUri, mHrefSet.Groups["u"].Value);
                if (u != null) { try { _host.Navigate(u); } catch { } }
                return true;
            }

            // ---------- document.title = "..." ----------
            var mTitle = System.Text.RegularExpressions.Regex.Match(
                line,
                @"^\s*document\s*\.\s*title\s*=\s*['""](?<t>.*?)['""]\s*;?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (mTitle.Success)
            {
                try
                {
                    string tval = "";
                    if (mTitle.Groups["t"] != null && mTitle.Groups["t"].Value != null)
                        tval = mTitle.Groups["t"].Value;
                    _host.SetTitle(tval);
                }
                catch { }
                return true;
            }

            return false;
        }

        public object Evaluate(string script)
        {
            try { FenLogger.Debug($"[JavaScriptEngine] Evaluate called with script length: {script?.Length ?? 0}", LogCategory.JavaScript); } catch { }

            // Use FenEngine for evaluation
            if (_fenRuntime != null)
            {
                try
                {
                    var rawResult = _fenRuntime.ExecuteSimple(script);
                    
                    // Unwrap ReturnValue wrapper (from return statements)
                    FenBrowser.FenEngine.Core.FenValue result = (FenBrowser.FenEngine.Core.FenValue)rawResult;
                    while (result.Type == JsValueType.ReturnValue)
                    {
                        result = (FenBrowser.FenEngine.Core.FenValue)result.ToNativeObject();
                    }
                    
                    // Now result is the actual value (FenValue)
                    if (result is FenBrowser.FenEngine.Core.FenValue fv)
                    {
                        if (fv.IsNumber) return fv.ToNumber();
                        if (fv.IsString) return fv.ToString();
                        if (fv.IsBoolean) return fv.ToBoolean();
                        if (fv == null) return null;
                        if (fv.IsUndefined) return null;
                        // Return FenValue for objects/arrays so ToNativeObject can convert them
                        return fv;
                    }
                    
                    // Fallback for non-FenValue IValue types
                    if (result.IsNumber) return result.ToNumber();
                    if (result.IsString) return result.ToString();
                    if (result.IsBoolean) return result.ToBoolean();
                    if (result == null) return null;
                    if (result.IsUndefined) return null;
                    return result;
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[JS] Evaluate error: {ex.Message}", LogCategory.JavaScript, ex);
                    return "Error: " + ex.Message;
                }
            }
            
            return null;
        }









        // Legacy JSON-based localStorage persistence (no longer used; kept for compatibility reference)
        // The engine now uses a simple line-based format via SaveLocalStorageAsync/RestoreLocalStorageAsync
        // against the _localStorageMap dictionary. This stub remains to avoid breaking older call sites.
        private async void PersistLocalStorage()
        {
            try
            {
                await SaveLocalStorageAsync().ConfigureAwait(false);
            }
            catch { }
        }

    public void LocalStorageSet(string key, string value, JsContext ctx)
    {
        try
        {
            if (!SandboxAllows(SandboxFeature.Storage, "localStorage.setItem")) return;
            StorageApi.SetLocalStorageItem(OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri), key, value);
        }
        catch { }
    }

    public string LocalStorageGet(string key, JsContext ctx)
    {
        try
        {
            if (!SandboxAllows(SandboxFeature.Storage, "localStorage.getItem")) return null;
            return StorageApi.GetLocalStorageItem(OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri), key);
        }
        catch { return null; }
    }

    public void LocalStorageRemove(string key, JsContext ctx)
    {
        try
        {
            if (!SandboxAllows(SandboxFeature.Storage, "localStorage.removeItem")) return;
            StorageApi.RemoveLocalStorageItem(OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri), key);
        }
        catch { }
    }

    public void LocalStorageClear(JsContext ctx)
    {
        try
        {
            if (!SandboxAllows(SandboxFeature.Storage, "localStorage.clear")) return;
            StorageApi.ClearLocalStorage(OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri));
        }
        catch { }
    }

    public void Reset(JsContext ctx)
        {
            _ctx = ctx ?? new JsContext();
            ClearSandboxBlockLog();
            
            InitRuntime();

            // Recreate MiniJS and (re)bootstrap the environment - DISABLED
            /*
            try
            {
                _mini = new MiniJs.Engine();
            }
            catch { _mini = null; }
            */

            // try { EnsureMiniEnvironment(); } catch { }
        }
        
        // MiniJs environment setup - DISABLED
        /*
        private void EnsureMiniEnvironment()
        {
            try
            {
                if (_mini  == null) return;

                // Wire standard environment & bridges
                MiniJs.Bootstrap.InitEnvironment(
                    _mini,
                    _host,
                    FetchTextSync,
                    // setTimeout
                    (code, ms) =>
                    {
                        try
                        {
                            int id = Interlocked.Increment(ref _nextTimerId);
                            System.Threading.Timer t = null;
                            System.Threading.TimerCallback fire = _ =>
                            {
                                try
                                {
                                    if (_mini != null) _mini.Execute(code ?? "");
                                }
                                catch { }
                                finally
                                {
                                    lock (_timers)
                                    {
                                        try { if (_timers.ContainsKey(id)) _timers[id].Dispose(); } catch { }
                                        _timers.Remove(id);
                                    }
                                }
                            };
                            t = new System.Threading.Timer(fire, null, ms, Timeout.Infinite);
                            lock (_timers) _timers[id] = t;
                            return id;
                        }
                        catch { }
                        return -1;
                    },
                    // clearTimeout
                    (id) =>
                    {
                        try
                        {
                            lock (_timers)
                            {
                                System.Threading.Timer t;
                                if (_timers.TryGetValue(id, out t))
                                {
                                    try { t.Dispose(); } catch { }
                                    _timers.Remove(id);
                                }
                            }
                        }
                        catch { }
                    }
                );
            }
            catch { }
        }
        */
        private void ResetPrefetchedModuleSource()
        {
            lock (_prefetchedModuleSourceLock)
            {
                _prefetchedModuleSource.Clear();
            }
        }

        private void CachePrefetchedModuleSource(Uri uri, string content)
        {
            if (uri == null || string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            lock (_prefetchedModuleSourceLock)
            {
                _prefetchedModuleSource[uri.AbsoluteUri] = content;
            }
        }

        private bool TryGetPrefetchedModuleSource(Uri uri, out string content)
        {
            content = null;
            if (uri == null)
            {
                return false;
            }

            lock (_prefetchedModuleSourceLock)
            {
                return _prefetchedModuleSource.TryGetValue(uri.AbsoluteUri, out content);
            }
        }

        private async Task<string> FetchModuleTextForLoaderAsync(Uri uri, Uri referer)
        {
            if (uri == null) return string.Empty;
            try
            {
                if (FetchOverride != null)
                {
                    var viaOverride = await FetchOverride(uri).ConfigureAwait(false);
                    if (viaOverride != null) return viaOverride;
                }

                if (ExternalScriptFetcher != null)
                {
                    var viaExternal = await ExternalScriptFetcher(uri, referer ?? _ctx?.BaseUri).ConfigureAwait(false);
                    if (viaExternal != null) return viaExternal;
                }

                if (uri.IsFile && File.Exists(uri.LocalPath))
                {
                    return await File.ReadAllTextAsync(uri.LocalPath).ConfigureAwait(false);
                }

                FenLogger.Warn($"[JavaScriptEngine] Blocked module fetch for unsupported URI without browser fetch pipeline: {uri}", LogCategory.Network);
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[JavaScriptEngine] Module fetch failed for '{uri}': {ex.Message}", LogCategory.Network);
            }

            return string.Empty;
        }

        private IEnumerable<string> ExtractModuleSpecifiers(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                yield break;
            }

            foreach (Match match in ModuleImportFromRegex.Matches(source))
            {
                var spec = match.Groups["spec"]?.Value;
                if (!string.IsNullOrWhiteSpace(spec))
                {
                    yield return spec.Trim();
                }
            }

            foreach (Match match in ModuleImportSideEffectRegex.Matches(source))
            {
                var spec = match.Groups["spec"]?.Value;
                if (!string.IsNullOrWhiteSpace(spec))
                {
                    yield return spec.Trim();
                }
            }

            foreach (Match match in ModuleDynamicImportLiteralRegex.Matches(source))
            {
                var spec = match.Groups["spec"]?.Value;
                if (!string.IsNullOrWhiteSpace(spec))
                {
                    yield return spec.Trim();
                }
            }
        }

        private async Task PrefetchModuleGraphAsync(
            FenBrowser.FenEngine.Core.ModuleLoader moduleLoader,
            Uri moduleUri,
            Uri referrerUri,
            HashSet<string> visited,
            int depth = 0)
        {
            if (moduleLoader == null || moduleUri == null || visited == null)
            {
                return;
            }

            if (depth > MaxModulePrefetchDepth)
            {
                FenLogger.Warn($"[JavaScriptEngine] Module prefetch depth limit reached at {moduleUri}", LogCategory.JavaScript);
                return;
            }

            var moduleKey = moduleUri.AbsoluteUri;
            if (!visited.Add(moduleKey))
            {
                return;
            }

            if (!IsModuleUriAllowed(moduleUri))
            {
                return;
            }

            var source = await FetchModuleTextForLoaderAsync(moduleUri, referrerUri).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            CachePrefetchedModuleSource(moduleUri, source);

            foreach (var specifier in ExtractModuleSpecifiers(source))
            {
                string resolved;
                try
                {
                    resolved = moduleLoader.Resolve(specifier, moduleKey);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(resolved))
                {
                    continue;
                }

                if (!Uri.TryCreate(resolved, UriKind.Absolute, out var dependencyUri))
                {
                    continue;
                }

                if (!IsModuleUriAllowed(dependencyUri))
                {
                    continue;
                }

                await PrefetchModuleGraphAsync(moduleLoader, dependencyUri, moduleUri, visited, depth + 1).ConfigureAwait(false);
            }
        }

        private string BuildInlineModulePseudoPath(Uri baseUri)
        {
            if (baseUri != null &&
                baseUri.IsAbsoluteUri &&
                (baseUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                 baseUri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)))
            {
                return new Uri(baseUri, $".fen-inline/module-{Guid.NewGuid():N}.js").AbsoluteUri;
            }

            return $"https://fen.invalid/.fen-inline/module-{Guid.NewGuid():N}.js";
        }

        private string FetchTextSync(Uri uri)
        {
            if (uri == null) return "";
            try
            {
                if (TryGetPrefetchedModuleSource(uri, out var prefetched))
                {
                    return prefetched;
                }

                if (uri.IsFile && File.Exists(uri.LocalPath))
                {
                    return File.ReadAllText(uri.LocalPath);
                }

                FenLogger.Warn($"[JavaScriptEngine] Missing prefetched module source for '{uri}'. Module graph must be prefetched asynchronously.", LogCategory.Network);
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[JavaScriptEngine] FetchTextSync failed for '{uri}': {ex.Message}", LogCategory.Network);
            }
            return "";
        }

        private bool IsModuleUriAllowed(Uri uri)
        {
            if (uri == null) return false;

            if (SubresourceAllowed != null)
            {
                try
                {
                    if (!SubresourceAllowed(uri, "script"))
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            var scheme = (uri.Scheme ?? string.Empty).ToLowerInvariant();
            if (scheme == "http" || scheme == "https")
            {
                var baseUri = _ctx?.BaseUri;
                if (baseUri != null &&
                    (string.Equals(baseUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) &&
                    !CorsHandler.IsSameOrigin(uri, baseUri))
                {
                    FenLogger.Warn($"[JavaScriptEngine] Blocked cross-origin module without explicit CORS pipeline: {uri}", LogCategory.Network);
                    return false;
                }
            }

            return scheme == "https" || scheme == "http" || scheme == "file" || scheme == "data";
        }

        private void ApplyImportMapsFromDom(FenBrowser.FenEngine.Core.ModuleLoader moduleLoader, Uri baseUri)
        {
            if (moduleLoader == null || _domRoot == null)
            {
                return;
            }

            var imports = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in _domRoot.SelfAndDescendants())
            {
                if (node is not Element element)
                {
                    continue;
                }

                if (!string.Equals(element.TagName, "script", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var type = element.GetAttribute("type")?.Trim();
                if (!string.Equals(type, "importmap", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var json = element.TextContent ?? string.Empty;
                TryReadImportMapEntries(json, imports);
            }

            if (imports.Count == 0)
            {
                return;
            }

            moduleLoader.SetImportMap(imports, baseUri);
            FenLogger.Info($"[JavaScriptEngine] Applied import map entries: {imports.Count}", LogCategory.JavaScript);
        }

        private static void TryReadImportMapEntries(string json, Dictionary<string, string> target)
        {
            if (target == null || string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("imports", out var importsElement) ||
                    importsElement.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                foreach (var entry in importsElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var key = entry.Name?.Trim();
                    var value = entry.Value.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    target[key] = value;
                }
            }
            catch
            {
                // Import map parse failure is non-fatal for script execution.
            }
        }

        // Request the host to re-render if available (non-breaking: optional interface)
        private void RequestRepaint()
        {
            // Coalesce frequent requests
            if (_repaintRequested) return;
            _repaintRequested = true;
            var repaint = _host as IJsHostRepaint;
            if (repaint != null)
            {
                try { repaint.RequestRender(); }
                catch { /* swallow */ }
            }
            else
            {
                // fallback: set status so developer sees mutations
                try { _host.SetStatus("[DOM mutated]"); } catch { }
            }
            // after the host is requested to repaint, schedule any MutationObserver callbacks
            try { InvokeMutationObservers(); } catch { }
            _repaintRequested = false;
        }

        // When the DOM changes, invoke any registered MutationObserver callbacks as microtasks
        private void InvokeMutationObservers()
        {
            // 1. Legacy string-based observers (keep for backward compatibility if any)
            try
            {
                List<MutationRecord> mutations = null;
                lock (_mutationLock)
                {
                    if (_pendingMutations.Count > 0)
                    {
                        mutations = new List<MutationRecord>(_pendingMutations);
                        _pendingMutations.Clear();
                    }
                }

                if (mutations != null && mutations.Count > 0)
                {
                    string[] legacyObservers;
                    lock (_mutationObservers) legacyObservers = _mutationObservers.ToArray();

                    if (legacyObservers.Length > 0)
                    {
                        var sb = new StringBuilder();
                        sb.Append("[");
                        bool first = true;
                        foreach (var mr in mutations)
                        {
                            if (!first) sb.Append(",");
                            sb.Append("{");
                            sb.Append($"'type':'{mr.Type}'");
                            sb.Append("}");
                            first = false;
                        }
                        sb.Append("]");
                        var json = sb.ToString();
                        
                        foreach (var fn in legacyObservers)
                        {
                            EnqueueMicrotask(() => { try { RunInline(fn + "(" + json + ")", _ctx); } catch { } });
                        }
                    }
                }
            }
            catch { }

            // 2. New Wrapper-based observers (Standard MutationObserver)
            try
            {
                MutationObserverWrapper[] wrappers;
                lock (_mutationLock) wrappers = _fenMutationObservers.ToArray();

                foreach (var wrapper in wrappers)
                {
                    if (wrapper.HasPendingRecords)
                    {
                        // Safely get context
                        var context = _fenRuntime?.Context;
                        if (context  == null) continue;

                        // Get records as FenObjects, correctly serialized with ElementWrappers
                        var recordObjs = wrapper.TakeRecords(context);
                        
                        // Create JS Array
                        var jsArray = new FenObject();
                        jsArray.Set("length", FenValue.FromNumber(recordObjs.Length));
                        for (int i = 0; i < recordObjs.Length; i++)
                        {
                            jsArray.Set(i.ToString(), FenValue.FromObject(recordObjs[i]));
                        }

                        // Call callback: callback(mutations, observer)
                        // Note: We don't have the observer fen object handy here implies 'this' binding might be loose, 
                        // but 2nd arg is passed.
                        var args = new[] { FenValue.FromObject(jsArray), FenValue.Undefined }; 
                        
                        EnqueueMicrotask(() => 
                        {
                            try { _fenRuntime.ExecuteFunction(wrapper.Callback, args); } catch { }
                        });
                    }
                }
            }
            catch { }
        }

        private void RecordMutation(MutationRecord record)
        {
            // NEW: Intercept dynamic scripts for WPT
            if (record.AddedNodes != null && record.AddedNodes.Count > 0)
            {
                foreach (var node in record.AddedNodes)
                {
                    if (node is Element el && string.Equals(el.TagName, "script", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleDynamicScript(el);
                    }
                }
            }

            // 1. Dispatch to wrappers immediately (they filter internally and queue records)
            lock (_mutationLock)
            {
                foreach (var wrapper in _fenMutationObservers)
                {
                    string typeString = record.Type switch
                    {
                        MutationRecordType.ChildList => "childList",
                        MutationRecordType.Attributes => "attributes",
                        MutationRecordType.CharacterData => "characterData",
                        _ => record.Type.ToString().ToLowerInvariant()
                    };
                    wrapper.RecordMutation(record.Target, typeString, record.AttributeName, record.OldValue, record.AddedNodes?.ToList(), record.RemovedNodes?.ToList());
                }
            }
            
            // 2. Keep queue for legacy string observers (only if any exist to save memory)
            lock (_mutationObservers) 
            {
                if (_mutationObservers.Count > 0)
                {
                    lock (_mutationLock) _pendingMutations.Add(record);
                }
            }

            // Schedule microtask to deliver mutations
            EnqueueMicrotask(InvokeMutationObservers);
        }

        private void HandleDynamicScript(Element scriptEl)
        {
            if (scriptEl  == null) return;

            // Check src and integrity
            string src = null;
            if (scriptEl.Attr != null) scriptEl.Attr.TryGetValue("src", out src);
            string sriIntegrity = null;
            if (scriptEl.Attr != null) scriptEl.Attr.TryGetValue("integrity", out sriIntegrity);

            if (!string.IsNullOrWhiteSpace(src))
            {
                // External Script
                var baseUri = _ctx?.BaseUri;
                var uri = Resolve(baseUri, src);
                
                if (uri != null && (AllowExternalScripts || SandboxAllows(SandboxFeature.ExternalScripts)))
                {
                    // Run fetch-and-execute on background, then post back to main loop
                    Task.Run(async () => 
                    {
                        try
                        {
                            string content = null;
                            
                            // Use FetchOverride (set by CustomHtmlEngine/BrowserApi for WPT)
                            if (FetchOverride != null)
                                content = await FetchOverride(uri);
                            
                            // Legacy generic fetcher
                            if (content  == null && ExternalScriptFetcher != null) 
                                content = await ExternalScriptFetcher(uri, baseUri);
                            
                            // Fallback to internal fetch
                            if (content  == null) 
                                content = await FetchAsync(uri);

                            // Dispatch result on main thread
                            EnqueueMicrotask(() =>
                            {
                                if (content != null)
                                {
                                    // SRI check for dynamically-inserted external scripts
                                    if (!VerifySriIntegrity(content, sriIntegrity))
                                    {
                                        FenLogger.Warn($"[SRI] Blocked dynamic script (integrity mismatch): {uri}", LogCategory.JavaScript);
                                        DispatchEvent(scriptEl, "error");
                                        return;
                                    }
                                    try
                                    {
                                        Evaluate(content);
                                        DispatchEvent(scriptEl, "load");
                                    }
                                    catch (Exception ex)
                                    {
                                        FenLogger.Error($"[DynamicScript] Exec failed: {ex.Message}", LogCategory.JavaScript);
                                        DispatchEvent(scriptEl, "error");
                                    }
                                }
                                else
                                {
                                    FenLogger.Error($"[DynamicScript] Fetch failed for {uri}", LogCategory.JavaScript);
                                    DispatchEvent(scriptEl, "error");
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                             FenLogger.Error($"[DynamicScript] Background error: {ex.Message}", LogCategory.JavaScript);
                             EnqueueMicrotask(() => DispatchEvent(scriptEl, "error"));
                        }
                    });
                }
            }
            else
            {
                // Inline Script
                if (ExecuteInlineScriptsOnInnerHTML || SandboxAllows(SandboxFeature.InlineScripts))
                {
                    var text = scriptEl.Text;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Evaluate(text);
                    }
                }
            }
        }

        // ---- XHR shim state ----
        private sealed class XhrState
        {
            public string Id;
            public string Method;
            public Uri Url;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Body;
            public string OnLoadFn;
            public string OnErrorFn;
        }

        private System.Net.Http.HttpMessageHandler CreateManagedHandler(Uri uri = null, System.Net.CookieContainer cookies = null)
        {
            var handler = new System.Net.Http.HttpClientHandler();
            if (handler.SupportsAutomaticDecompression)
                handler.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
            
            if (cookies  == null && uri != null && CookieBridge != null)
                cookies = CookieBridge(uri);

            if (cookies != null && handler.SupportsRedirectConfiguration)
                handler.CookieContainer = cookies;
            return handler;
        }

        private async Task<string> FetchAsync(Uri uri)
        {
            try
            {
                using (var client = new System.Net.Http.HttpClient(CreateManagedHandler()))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent));
                    return await client.GetStringAsync(uri);
                }
            }
            catch { return null; }
        }

        public class HostWindow
        {
            private JavaScriptEngine _engine;
            public HostWindow(JavaScriptEngine engine) { _engine = engine; }
            public void alert(string msg) { _engine._host.SetStatus("[Alert] " + msg); _engine._host.Alert(msg); }
            
            public object document => new HostDocument(_engine);
            public object location => new HostLocation(_engine);
            public object history => new HostHistory(_engine);
            public object navigator => new HostNavigator(_engine);
            public object console => new HostConsole(_engine);
            public object self => this;
            public object window => this;
            public object top => this;
            public object parent => this;

            public int setTimeout(string code, int ms) => _engine.ScheduleTimeout(code, ms);
            public void clearTimeout(int id) => _engine.ClearTimeout(id);
            public int setInterval(string code, int ms) => _engine.ScheduleInterval(code, ms, false);
            public void clearInterval(int id) => _engine.ClearTimeout(id);
            
            public object crypto => new JsCrypto();
            public object performance => new JsPerformance();

            public JsVal onpopstate
            {
                get { return _engine.OnPopState ?? JsVal.Undefined; }
                set { _engine.OnPopState = value; }
            }

            public JsVal onhashchange
            {
                get { return _engine.OnHashChange ?? JsVal.Undefined; }
                set { _engine.OnHashChange = value; }
            }
        }

        public class HostDocument
        {
            private JavaScriptEngine _engine;
            public HostDocument(JavaScriptEngine engine) { _engine = engine; }
            
            public object getElementById(string id) 
            { 
                var el = _engine._domRoot?.Descendants().OfType<Element>().FirstOrDefault(e => e.GetAttribute("id") == id);
                return el != null ? new JsDomElement(_engine, el) : null;
            }

            public object body => _engine._domRoot != null ? new JsDomElement(_engine, (_engine._domRoot as Element)?.Descendants().OfType<Element>().FirstOrDefault(e => e.GetAttribute("id") == "body") ?? (_engine._domRoot as Element)?.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName.Equals("body", StringComparison.OrdinalIgnoreCase)) ?? _engine._domRoot as Element) : null;
            
            public object head => _engine._domRoot != null ? new JsDomElement(_engine, (_engine._domRoot as Element)?.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName.Equals("head", StringComparison.OrdinalIgnoreCase))) : null;

            public string title 
            { 
                get => _engine._docTitle; 
                set { /* _engine.SetTitle(value); */ } 
            }

            public string cookie 
            { 
                get => _engine.GetCookieString(_engine._ctx?.BaseUri); 
                set => _engine.SetCookieString(_engine._ctx?.BaseUri, value); 
            }

            public object createElement(string tagName)
            {
                return new JsDomElement(_engine, new Element(tagName));
            }

            public object createTextNode(string data)
            {
                var t = new Element("#text");
                t.Text = data;
                return new JsDomText(_engine, t);
            }

            public object querySelector(string sel)
            {
                if (_engine._domRoot  == null) return null;
                return new JsDocument(_engine, _engine._domRoot).querySelector(sel);
            }

            public object[] querySelectorAll(string sel)
            {
                if (_engine._domRoot  == null) return new object[0];
                return new JsDocument(_engine, _engine._domRoot).querySelectorAll(sel);
            }
        }

        public class HostConsole
        {
            private JavaScriptEngine _engine;
            public HostConsole(JavaScriptEngine engine) { _engine = engine; }
            public void log(string msg) { LogToFile("INFO", msg); }
            public void error(string msg) { LogToFile("ERROR", msg); }
            public void warn(string msg) { LogToFile("WARN", msg); }
            public void info(string msg) { LogToFile("INFO", msg); }
            public void debug(string msg) { LogToFile("DEBUG", msg); }
            
            private void LogToFile(string level, string msg)
            {
                DiagnosticPaths.AppendRootText("js_debug.log", $"[Console:{level}] {msg}\n");
                try { _engine._host.Log($"[{level}] {msg}"); } catch {}
            }
        }

        public class HostNavigator
        {
            private JavaScriptEngine _engine;
            public HostNavigator(JavaScriptEngine engine) { _engine = engine; }
            public string userAgent => BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent);
            public string appName => "Fenbrowser";
            public string appVersion => "1.0.0";
            public string product => "FenEngine";
            public string vendor => "Fenbrowser Project";
            public object userAgentData => new Dictionary<string, object>
            {
                { "brands", new [] { new { brand = "Fenbrowser", version = "1.0" }, new { brand = "FenEngine", version = "1.0" } } },
                { "mobile", false },
                { "platform", "Windows" }
            };
        }

        public sealed class JsVal
        {
            public double? Num; public string Str; public bool? Bool; public object Obj;
            public static JsVal FromNum(double n) { var v = new JsVal(); v.Num = n; return v; }
            public static JsVal FromStr(string s) { var v = new JsVal(); v.Str = s; return v; }
            public static JsVal FromBool(bool b) { var v = new JsVal(); v.Bool = b; return v; }
            public static JsVal Null() { return new JsVal(); }
            public override string ToString() { if (Str != null) return Str; if (Num.HasValue) return Num.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); if (Bool.HasValue) return Bool.Value ? "true" : "false"; return "null"; }
            public bool Truthy() { if (Bool.HasValue) return Bool.Value; if (Num.HasValue) return Math.Abs(Num.Value) > 1e-9; if (Str != null) return Str.Length > 0; return false; }
            public static JsVal Marshal(object o) { return new JsVal { Obj = o }; }
            public static JsVal Undefined => new JsVal();
        }

        public class HostHistory
        {
            private JavaScriptEngine _engine;
            public HostHistory(JavaScriptEngine engine) { _engine = engine; }
            
            public void pushState(JsVal state, string title, string url) 
            { 
                System.Diagnostics.Debug.WriteLine($"[History] pushState url={url}");
                // In a real implementation, we would update the browser history stack
            }

            public void replaceState(JsVal state, string title, string url) 
            { 
                System.Diagnostics.Debug.WriteLine($"[History] replaceState url={url}");
            }

            public void go(int delta) 
            { 
                System.Diagnostics.Debug.WriteLine($"[History] go({delta})");
                TriggerPopState();
            }
            public void back() 
            { 
                System.Diagnostics.Debug.WriteLine("[History] back()");
                TriggerPopState();
            }
            public void forward() 
            { 
                System.Diagnostics.Debug.WriteLine("[History] forward()");
                TriggerPopState();
            }
            
            private void TriggerPopState()
            {
                // Pop state handling (currently not implemented)
            }

            public int length => 1;
            public JsVal state => JsVal.Undefined;
        }

        public class HostLocation
        {
            private JavaScriptEngine _engine;
            public HostLocation(JavaScriptEngine engine) { _engine = engine; }
            public string href => _engine._ctx?.BaseUri?.ToString() ?? "";
        }

        public class HostLocalStorage
        {
            private JavaScriptEngine _engine;
            private bool _session;
            public HostLocalStorage(JavaScriptEngine engine, bool session) { _engine = engine; _session = session; }
            public string getItem(string key) { return _engine.LocalStorageGet(key, _engine._ctx); }
            public void setItem(string key, string value) { _engine.LocalStorageSet(key, value, _engine._ctx); }
            public void removeItem(string key) { _engine.LocalStorageRemove(key, _engine._ctx); }
            public void clear() { _engine.LocalStorageClear(_engine._ctx); }
        }

        private sealed class JsFuncDef
        {
            public List<string> Params = new List<string>();
            public List<JsFuncParam> Parameters = new List<JsFuncParam>();
            public string Body;        // block body source
            public string Expr;        // expression body source (arrow)
        }

        // NilJS host classes removed

        private abstract class JsBindingPattern
        {
            public string DefaultExpr;
        }

        private sealed class JsIdentifierPattern : JsBindingPattern
        {
            public string Name;
        }

        private sealed class JsObjectPattern : JsBindingPattern
        {
            public sealed class PropertyBinding
            {
                public string Key;
                public JsBindingPattern Target;
            }

            public List<PropertyBinding> Properties = new List<PropertyBinding>();
            public string RestIdentifier;
        }

        private sealed class JsArrayPattern : JsBindingPattern
        {
            public sealed class ElementBinding
            {
                public bool IsHole;
                public JsBindingPattern Target;
            }

            public List<ElementBinding> Elements = new List<ElementBinding>();
            public JsIdentifierPattern RestTarget;
        }

        private sealed class JsFuncParam
        {
            public string Raw;
            public JsBindingPattern Pattern;
            public bool IsRest;
        }

        private static JsFuncParam CreateIdentifierParam(string name, bool isRest = false)
        {
            return new JsFuncParam
            {
                Raw = name ?? string.Empty,
                Pattern = new JsIdentifierPattern { Name = name },
                IsRest = isRest
            };
        }

        private readonly Dictionary<string, JsFuncDef> _userFunctionsEx = new Dictionary<string, JsFuncDef>(StringComparer.Ordinal);

        /// <summary>Expose current DOM to the engine (for document.* bridge).</summary>
        public async Task SetDomAsync(Node domRoot, Uri baseUri = null)
        {
            /* [PERF-REMOVED] */
            _domRoot = domRoot;
            
            // Initialize FenEngine with DOM
            if (_fenRuntime != null)
            {
                try
                {
                    /* [PERF-REMOVED] */
                    _fenRuntime.SetDom(domRoot, baseUri);
                    /* [PERF-REMOVED] */
                    SetupPermissions(); // Re-apply permissions to new context if needed
                    /* [PERF-REMOVED] */
                    SetupWindowEvents(); // Re-apply addEventListener to new document
                    /* [PERF-REMOVED] */
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FenEngine] Error setting DOM: {ex.Message}");
                    /* [PERF-REMOVED] */
                }
                
                // Execute scripts (Inline + External + Modules)
                try
                {
                    /* [PERF-REMOVED] */
                    
                    ResetPrefetchedModuleSource();

                    // Create ModuleLoader if needed (lazy init)
                    var moduleLoader = new FenBrowser.FenEngine.Core.ModuleLoader(
                        _fenRuntime.GlobalEnv, 
                        _fenRuntime.Context,
                        uri => FetchTextSync(uri),
                        uri => IsModuleUriAllowed(uri)
                    );
                    ApplyImportMapsFromDom(moduleLoader, baseUri);
                    _fenRuntime.SetModuleLoader(moduleLoader);

                    // Inject global error handler
                    try
                    {
                        var errorHandler = "window.onerror = function(msg, url, line, col, error) { console.error('GLOBAL JS ERROR: ' + msg + ' at ' + url + ':' + line + ':' + col); if (error && error.stack) console.error(error.stack); };";
                        _fenRuntime.ExecuteSimple(errorHandler, "debug-handler");
                        var consoleTest = "console.log('Console test verify'); console.warn('Console warn verify');";
                        _fenRuntime.ExecuteSimple(consoleTest, "debug-console-test");
                    }
                    catch (Exception ex) { DiagnosticPaths.AppendRootText("js_debug.log", $"[SetupError] {ex}\n"); }

                    int scriptIndex = 0;
                    foreach (var s in _domRoot.SelfAndDescendants())
                    {
                        if (s is Element el)
                        {
                            string tagName = el.TagName?.ToLowerInvariant() ?? "";
                            if (string.Equals(tagName, "script", StringComparison.OrdinalIgnoreCase))
                            {
                                DiagnosticPaths.AppendRootText("js_debug.log", "[ScriptFound] Found script tag.\n");
                                scriptIndex++;
                                string code = null;
                                string srcInfo = "inline";

                                // Attribute checks
                                string type = el.GetAttribute("type")?.ToLowerInvariant() ?? "";
                                string src = el.GetAttribute("src");
                                string nonce = el.GetAttribute("nonce");
                                string integrity = el.GetAttribute("integrity"); // SRI
                                bool isModule = type == "module";
                            
                                // Filter invalid types
                                if (!string.IsNullOrEmpty(type) && 
                                    type != "text/javascript" && 
                                    type != "application/javascript" && 
                                    type != "module")
                                {
                                    continue;
                                }
                                
                                if (el.HasAttribute("nomodule")) continue;

                                // CSP Check (Enhanced with Nonce)
                                if (SubresourceAllowed != null) 
                                {
                                    Uri checkUri = null;
                                    if (!string.IsNullOrEmpty(src) && baseUri != null) Uri.TryCreate(baseUri, src, out checkUri);
                                    
                                    if (NonceAllowed != null)
                                    {
                                        bool isAllowed = NonceAllowed(nonce);

                                        if (string.IsNullOrEmpty(src))
                                        {
                                             // Inline Script: Check nonce
                                             if (!isAllowed) continue;
                                        }
                                        else
                                        {
                                            // External Script: If nonce check PASSED, we allow it immediately.
                                            // If failed or missing, we fall back to URL whitelist.
                                            if (!isAllowed || string.IsNullOrEmpty(nonce)) 
                                            {
                                                // Fallback to URL check
                                                 if (!string.IsNullOrEmpty(src) && checkUri != null && !SubresourceAllowed(checkUri, "script")) 
                                                 {
                                                     continue;
                                                 }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // URL Check (External Only) if NonceAllowed not available
                                        if (!string.IsNullOrEmpty(src) && checkUri != null && !SubresourceAllowed(checkUri, "script")) 
                                        {
                                            continue;
                                        }
                                    }
                                }

                                // 1. External Script
                                if (!string.IsNullOrEmpty(src)) 
                                {
                                    if (!SandboxAllows(SandboxFeature.ExternalScripts, "script src")) continue;

                                    if (baseUri != null)
                                    {
                                        try 
                                        {
                                            var scriptUri = new Uri(baseUri, src);
                                            srcInfo = scriptUri.ToString();
                                            
                                            if (isModule)
                                            {
                                                try
                                                {
                                                    await PrefetchModuleGraphAsync(
                                                        moduleLoader,
                                                        scriptUri,
                                                        baseUri,
                                                        new HashSet<string>(StringComparer.Ordinal))
                                                        .ConfigureAwait(false);
                                                    moduleLoader.LoadModule(scriptUri.AbsoluteUri);
                                                }
                                                catch { }
                                                continue;
                                            }

                                            if (ExternalScriptFetcher != null)
                                            {
                                                code = await ExternalScriptFetcher(scriptUri, baseUri).ConfigureAwait(false);
                                            }
                                            else
                                            {
                                                using (var client = new System.Net.Http.HttpClient())
                                                {
                                                    client.Timeout = TimeSpan.FromSeconds(5);
                                                    code = await client.GetStringAsync(scriptUri).ConfigureAwait(false);
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                // 2. Inline Script
                                else
                                {
                                    if (!SandboxAllows(SandboxFeature.InlineScripts, "inline script")) continue;
                                    code = CollectScriptText(s);
                                    
                                    if (isModule && !string.IsNullOrWhiteSpace(code))
                                    {
                                        try
                                        {
                                            var inlineModulePath = BuildInlineModulePseudoPath(baseUri);
                                            foreach (var specifier in ExtractModuleSpecifiers(code))
                                            {
                                                string resolved;
                                                try
                                                {
                                                    resolved = moduleLoader.Resolve(specifier, inlineModulePath);
                                                }
                                                catch
                                                {
                                                    continue;
                                                }

                                                if (!Uri.TryCreate(resolved, UriKind.Absolute, out var dependencyUri))
                                                {
                                                    continue;
                                                }

                                                await PrefetchModuleGraphAsync(
                                                    moduleLoader,
                                                    dependencyUri,
                                                    new Uri(inlineModulePath),
                                                    new HashSet<string>(StringComparer.Ordinal))
                                                    .ConfigureAwait(false);
                                            }

                                            moduleLoader.LoadModuleSrc(code, inlineModulePath);
                                        }
                                        catch { }
                                        continue; 
                                    }
                                }
                                
                                if (!string.IsNullOrWhiteSpace(code))
                                {
                                    // SRI check — external scripts with an integrity attr must match before execution
                                    if (!string.IsNullOrEmpty(src) && !VerifySriIntegrity(code, integrity))
                                    {
                                        DiagnosticPaths.AppendRootText("js_debug.log", $"[SRI] Blocked script (hash mismatch): {srcInfo}\n");
                                        FenLogger.Warn($"[SRI] Blocked external script due to integrity mismatch: {srcInfo}", LogCategory.JavaScript);
                                        continue;
                                    }
                                    DiagnosticPaths.AppendRootText("js_debug.log", $"[ScriptRun] Executing script: Length={code.Length}, Info={srcInfo}\n");
                                    _fenRuntime.ExecuteSimple(code, srcInfo);
                                    /* [PERF-REMOVED] */
                                }
                                else
                                {
                                    DiagnosticPaths.AppendRootText("js_debug.log", $"[ScriptSkip] Code empty or skipped. Type={type}, Src={src}\n");
                                }
                            }
                        }
                    }
                    
                    FenLogger.Debug("[JavaScriptEngine] Inline script execution complete", LogCategory.JavaScript);
                }
                catch (Exception ex)
                {
                    DiagnosticPaths.AppendRootText("js_debug.log", $"[JSExecError] {ex}\n");
                    FenLogger.Error($"[JavaScriptEngine] Script execution error: {ex.Message}", LogCategory.JavaScript, ex);
                }
            }
            
            // JS is "enabled" in this app; hide server noscript overlays & flip no-js ? js
            this.SanitizeForScriptingEnabled(domRoot);

            // 7. Fire LifeCycle Events (Spec Compliant)
            if (_fenRuntime != null)
            {
                var docIVal = _fenRuntime.GetGlobal("document");
                if (docIVal.IsObject)
                {
                    var docObj = docIVal.AsObject();
                    DocumentWrapper docWrapper = null;
                    if (docObj is DocumentWrapper dw) docWrapper = dw;
                    // Note: If document is a FenObject wrapping DocumentWrapper, we might need to check NativeObject
                    // but FenRuntime.SetDom sets it directly as the object.

                    if (docWrapper != null)
                    {
                        // DOMContentLoaded phase
                        docWrapper.SetReadyState("interactive");
                        DispatchEvent(docObj, "DOMContentLoaded", new DomEvent("DOMContentLoaded", bubbles: true));
                        
                        // load phase
                        docWrapper.SetReadyState("complete");
                        var window = _fenRuntime.GetGlobal("window").AsObject();
                        DispatchEvent(window, "load", new DomEvent("load"));
                    }
                }
            }
        }

        // Backward compatibility wrapper (deprecated)
        public void SetDom(Node domRoot, Uri baseUri = null)
        {
             _ = SetDomAsync(domRoot, baseUri).ContinueWith(
                 t =>
                 {
                     FenLogger.Error(
                         $"[JavaScriptEngine] Deprecated SetDom bridge failed: {t.Exception?.GetBaseException().Message}",
                         LogCategory.JavaScript,
                         t.Exception);
                 },
                 CancellationToken.None,
                 TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                 TaskScheduler.Default);
        }

        private void RunGlobalScript(string js)
        {
            if (string.IsNullOrWhiteSpace(js)) return;
            // No longer used - FenEngine handles script execution in SetDom
        }

        private static string CollectScriptText(Node n, int depth = 0)
        {
            if (n  == null) return "";
            if (depth > 200) // Safety break
            {
                /* [PERF-REMOVED] */
                return "";
            }

            if (n.IsText())
            {
                var tel = n as Element;
                if (tel != null) return tel.Text ?? "";
                
                // If Node.NodeValue is available directly or cast to Text node
                if (n is Text t) return t.Data ?? "";
                return n.NodeValue ?? "";
            }
            
            var sb = new System.Text.StringBuilder();
            if (n.Children != null)
            {
                for (int i = 0; i < n.Children.Count; i++) sb.Append(CollectScriptText(n.Children[i], depth + 1));
            }
            return sb.ToString();
        }

        #region JS enabled sanitizer
        // INSIDE: public sealed class JavaScriptEngine { ... }
        private void SanitizeForScriptingEnabled(Node rootArg = null)
        {
            var root = rootArg ?? _domRoot;          // OK in instance method
            if (root  == null) return;

            // flip no-js ? js on <html>/<body>
            Action<Element> flipClass = n =>
            {
                if (n  == null) return;
                var attrs = n.Attr;
                if (attrs  == null) return;

                string cls;
                if (!attrs.TryGetValue("class", out cls) || string.IsNullOrWhiteSpace(cls)) return;

                var parts = new HashSet<string>(
                    cls.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.OrdinalIgnoreCase);

                var changed = false;
                if (parts.Remove("no-js")) changed = true;
                if (!parts.Contains("js")) { parts.Add("js"); changed = true; }
                if (changed) attrs["class"] = string.Join(" ", parts.ToArray());
            };

            try
            {
                var html = (root as ContainerNode)?.GetElementsByTagName("html").FirstOrDefault();
                if (html != null) flipClass(html);
                var body = (root as ContainerNode)?.GetElementsByTagName("body").FirstOrDefault();
                if (body != null) flipClass(body);
            }
            catch { }

            try
            {
                var toRemove = new List<Element>();
                foreach (var n in root.Descendants().OfType<Element>())
                    if (string.Equals(n.TagName, "noscript", StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(n);
                foreach (var n in toRemove) n.Remove();
            }
            catch { }

            // Do NOT remove <noscript> on this platform; we are a limited JS renderer
            // and <noscript> often contains the only usable fallback content.
            // Leave nodes intact so the renderer can show them.

            this.RequestRepaint();                    // OK in instance method
        }

        #endregion


        void TryUpdateClassList(string id, string op, string cls)
        {
            try
            {
                if (_domRoot  == null || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cls)) return;
                var doc = new JsDocument(this, _domRoot);
                var el = doc.getElementById(id) as JsDomElement; if (el  == null) return;
                var cur = el.getAttribute("class") ?? string.Empty;
                var set = new HashSet<string>(cur.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                bool changed = false;
                if (op == "add") { if (!set.Contains(cls)) { set.Add(cls); changed = true; } }
                else if (op == "remove") { if (set.Remove(cls)) changed = true; }
                else if (op == "toggle") { if (!set.Remove(cls)) { set.Add(cls); } changed = true; }
                if (changed) el.setAttribute("class", string.Join(" ", set.ToArray()));
            }
            catch { }
        }
        internal sealed class ResponseEntry
        {
            public string Body;
            public DateTime Ts;

            public ResponseEntry(string body, DateTime ts)
            {
                Body = body ?? string.Empty;
                Ts = ts;
            }
        }

        /// <summary>
        /// Verifies Subresource Integrity (SRI) for a fetched resource.
        /// Returns true if <paramref name="integrity"/> is absent/empty (no check required) or
        /// if at least one hash token in the space-separated list matches the content.
        /// Returns false if tokens are present and none match — caller must block the resource.
        /// Supported algorithms: sha256, sha384, sha512.
        /// </summary>
        private static bool VerifySriIntegrity(string content, string integrity)
        {
            if (string.IsNullOrWhiteSpace(integrity)) return true;
            var tokens = integrity.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return true;

            var bytes = System.Text.Encoding.UTF8.GetBytes(content ?? "");
            foreach (var token in tokens)
            {
                var dash = token.IndexOf('-');
                if (dash < 0) continue;
                var algo = token.Substring(0, dash).ToLowerInvariant();
                var expectedB64 = token.Substring(dash + 1);

                byte[] hash;
                try
                {
                    using var alg = algo switch
                    {
                        "sha256" => (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.SHA256.Create(),
                        "sha384" => System.Security.Cryptography.SHA384.Create(),
                        "sha512" => System.Security.Cryptography.SHA512.Create(),
                        _ => null
                    };
                    if (alg == null) continue; // Unknown algorithm — skip token
                    hash = alg.ComputeHash(bytes);
                }
                catch { continue; }

                if (Convert.ToBase64String(hash) == expectedB64) return true;
            }
            // No matching token found — block the resource
            return false;
        }
    }

    // ------------------- Host interface + context (stable) -------------------

    public interface IJsHost
    {
        void Navigate(Uri target);
        void PostForm(Uri target, string body);
        void SetStatus(string s);

        void SetTitle(string tval);
        void Alert(string msg);
        void Log(string msg);
        void ScrollToElement(Element element);
        void FocusNode(Element element);

        /// <summary>Returns the active layout engine, if available.</summary>
        FenBrowser.FenEngine.Rendering.Core.ILayoutEngine GetLayoutEngine() => null;
    }

    // Optional host interface: if implemented, JavaScriptEngine will call RequestRender() when DOM changes
    public interface IJsHostRepaint
    {
        // Request the host to schedule a UI re-render. Implementations should marshal to UI thread.
        void RequestRender();

        // Optional: host can provide a helper to invoke code on the UI thread. Timers and
        // other background callbacks should use this to safely mutate UI-bound data.
        void InvokeOnUiThread(Action action);
    }

    public sealed class JsContext
    {
        public Uri BaseUri { get; set; }
    }

    /// <summary>Convenience adapter so callers can pass delegates.</summary>
    public sealed class JsHostAdapter : IJsHost, IJsHostRepaint
    {
    private readonly Action<Uri> _navigate;
    private readonly Action<Uri, string> _post;
    private readonly Action<string> _status;
    private readonly Action _requestRender;
    private readonly Action<Action> _invokeOnUiThread;
    private readonly Action<string> _setTitle;
    private readonly Action<string> _alert;
    private readonly Action<string> _log;
    private readonly Action<Element> _scrollToElement;
    private readonly Action<Element> _focusNode;

        public JsHostAdapter(Action<Uri> navigate, Action<Uri, string> post, Action<string> status, Action requestRender = null, Action<Action> invokeOnUiThread = null, Action<string> setTitle = null, Action<string> alert = null, Action<string> log = null, Action<Element> scrollToElement = null, Action<Element> focusNode = null)
        {
            _navigate = navigate ?? (_ => { });
            _post = post ?? ((_, __) => { });
            _status = status ?? (_ => { });
            _requestRender = requestRender ?? (() => { try { _status("[DOM mutated]"); } catch { } });
            _invokeOnUiThread = invokeOnUiThread ?? (a => { try { a(); } catch { } });
            _setTitle = setTitle ?? (_ => { });
            _alert = alert ?? (_ => { });
            _log = log ?? (_ => { });
            _scrollToElement = scrollToElement ?? (_ => { });
            _focusNode = focusNode ?? (_ => { });
        }

        // ... IJsHost implementation ...
        public void Navigate(Uri target) => _navigate(target);
        public void PostForm(Uri target, string body) => _post(target, body);
        public void SetStatus(string s) => _status(s);
        public void RequestRender() => _requestRender();
        public void InvokeOnUiThread(Action action) => _invokeOnUiThread(action);
        public void SetTitle(string t) => _setTitle(t);
        public void Alert(string msg) => _alert(msg);
        public void Log(string msg) => _log(msg);
        public void ScrollToElement(Element e) => _scrollToElement(e);
        public void FocusNode(Element e) => _focusNode(e);
    }

    public class JsCrypto : FenBrowser.FenEngine.Core.FenObject
    {
        public JsCrypto()
        {
            Set("getRandomValues", FenValue.FromFunction(new FenFunction("getRandomValues", GetRandomValues)));
            Set("subtle", FenValue.FromObject(new FenObject())); // Minimal mock
        }
        
        private FenValue GetRandomValues(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1) return FenValue.Undefined;
            try
            {
                var arr = args[0].IsObject ? args[0].AsObject() as FenBrowser.FenEngine.Core.FenObject : null; // TypedArray usually
                // In NilJS TypedArrays might be FenObjects with numeric keys
                // For now, assume it's a typed array and fill it with random bytes.
                // Since bridging typed arrays is complex, we'll just try to fill standard array-like object
                
                // Real implementation requires bridging TypedArrays properly.
                // Assuming args[0] is the TypedArray instance.
                
                // We'll fill 'length' bytes.
                if (arr != null && arr.Has("length"))
                {
                    int len = (int)arr.Get("length").ToNumber();
                    var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
                    var bytes = new byte[len];
                    rng.GetBytes(bytes);
                    
                    for(int i=0; i<len; i++)
                    {
                        arr.Set(i.ToString(), FenValue.FromNumber(bytes[i]));
                    }
                }
                return args[0];
            }
            catch { return args[0]; }
        }
    }

    public class JsPerformance : FenBrowser.FenEngine.Core.FenObject
    {
        private static readonly DateTime _start = DateTime.UtcNow;
        
        public JsPerformance()
        {
            Set("now", FenValue.FromFunction(new FenFunction("now", (args, _) => 
                FenValue.FromNumber((DateTime.UtcNow - _start).TotalMilliseconds))));
                
            Set("mark", FenValue.FromFunction(new FenFunction("mark", (args, _) => FenValue.Undefined)));
            Set("measure", FenValue.FromFunction(new FenFunction("measure", (args, _) => FenValue.Undefined)));
            Set("clearMarks", FenValue.FromFunction(new FenFunction("clearMarks", (args, _) => FenValue.Undefined)));
            Set("clearMeasures", FenValue.FromFunction(new FenFunction("clearMeasures", (args, _) => FenValue.Undefined)));
        }
    }

}




