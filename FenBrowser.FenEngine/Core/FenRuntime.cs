using FenBrowser.Core.Dom;
using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.WebAPIs;
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.DevTools;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;
using FenBrowser.FenEngine.Storage;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// FenEngine JavaScript runtime - manages global scope and execution context
    /// </summary>
    public class FenRuntime
    {
        private readonly FenEnvironment _globalEnv;
        private readonly IExecutionContext _context;
        private readonly IStorageBackend _storageBackend;
        private readonly IDomBridge _domBridge; // Bridge to Engine's DOM
        private IHistoryBridge _historyBridge;

        private readonly Dictionary<int, CancellationTokenSource> _activeTimers = new Dictionary<int, CancellationTokenSource>();
        private int _timerIdCounter = 1;
        private readonly object _timerLock = new object();
        private static readonly Random _mathRandom = new Random(); // Cached Random for Math.random()

        public FenRuntime(IExecutionContext context = null, IStorageBackend storageBackend = null, IDomBridge domBridge = null, IHistoryBridge historyBridge = null)
        {
            /* [PERF-REMOVED] */
            _context = context ?? new ExecutionContext();
            _storageBackend = storageBackend ?? new InMemoryStorageBackend();
            _domBridge = domBridge;
            _historyBridge = historyBridge;

            /* [PERF-REMOVED] */
            _globalEnv = new FenEnvironment();
            _context.Environment = _globalEnv;
            /* [PERF-REMOVED] */
            
            // Initialize module loader
            _context.ModuleLoader = new ModuleLoader(_globalEnv, _context);
            /* [PERF-REMOVED] */
            
            InitializeBuiltins();
            /* [PERF-REMOVED] */
        }

        public Action RequestRender
        {
            get => _context.RequestRender;
            set => _context.SetRequestRender(value);
        }

        public void SetHistoryBridge(IHistoryBridge bridge) => _historyBridge = bridge;

        public void NotifyPopState(object state)
        {
            try
            {
                FenLogger.Debug($"[FenRuntime] NotifyPopState: {state}", LogCategory.Events);
                if (_windowEventListeners.ContainsKey("popstate"))
                {
                    var eventObj = new FenObject();
                    eventObj.Set("type", FenValue.FromString("popstate"));
                    eventObj.Set("state", state != null ? ConvertNativeToFenValue(state) : FenValue.Null);
                    eventObj.Set("bubbles", FenValue.FromBoolean(false));
                    eventObj.Set("cancelable", FenValue.FromBoolean(false));
                    
                    var args = new IValue[] { FenValue.FromObject(eventObj) };
                    
                    // Dispatch to all listeners
                    // Copy list to avoid concurrent modification issues
                    var listeners = _windowEventListeners["popstate"].ToList();
                    foreach (var callback in listeners)
                    {
                        if (callback.IsFunction) 
                            ExecuteFunction(callback.AsFunction() as FenFunction, args);
                    }
                }
                
                // TODO: Also support 'onpopstate' property on window/body
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[FenRuntime] NotifyPopState error: {ex.Message}", LogCategory.JavaScript);
            }
        }

        public IExecutionContext Context => _context;
        public FenEnvironment GlobalEnv => _globalEnv;

        public void SetModuleLoader(FenBrowser.FenEngine.Core.Interfaces.IModuleLoader loader)
        {
            _context.ModuleLoader = loader;
        }

        public Action<string> OnConsoleMessage; // Delegate for console output
        public Uri BaseUri { get; set; }

        private string GetCurrentOrigin()
        {
             if (BaseUri == null) return "null";
             return $"{BaseUri.Scheme}://{BaseUri.Host}:{BaseUri.Port}";
        }

        public IValue ExecuteFunction(FenFunction func, IValue[] args)
        {
            if (_context.ExecuteFunction != null)
            {
                return _context.ExecuteFunction(FenValue.FromFunction(func), args);
            }
            return func.Invoke(args, _context);
        }

        /// <summary>
        /// Helper for console.dir to inspect objects recursively.
        /// </summary>
        private static string InspectObject(IValue value, int depth)
        {
            if (depth > 3) return "..."; // Prevent infinite recursion
            if (value == null) return "null";
            if (value.IsUndefined) return "undefined";
            if (value.IsNull) return "null";
            if (value.IsString) return $"\"{value.ToString()}\"";
            if (value.IsNumber) return value.ToNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value.IsBoolean) return value.ToBoolean() ? "true" : "false";
            if (value.IsFunction) return $"[Function: {(value.AsFunction() as FenFunction)?.Name ?? "anonymous"}]";
            if (value.IsObject)
            {
                var obj = value.AsObject();
                var sb = new StringBuilder();
                sb.Append("{ ");
                var keys = obj.Keys()?.Take(10).ToList() ?? new List<string>();
                for (int i = 0; i < keys.Count; i++)
                {
                    var k = keys[i];
                    var v = obj.Get(k);
                    sb.Append(k).Append(": ").Append(InspectObject(v as IValue ?? FenValue.Undefined, depth + 1));
                    if (i < keys.Count - 1) sb.Append(", ");
                }
                if (keys.Count >= 10) sb.Append(", ...");
                sb.Append(" }");
                return sb.ToString();
            }
            return value.ToString();
        }

        private void InitializeBuiltins()
        {
            try { FenLogger.Debug("[FenRuntime] InitializeBuiltins called", LogCategory.JavaScript); } catch { }

            // document object - Bridge to DOM
            var document = new FenObject();
            document.Set("getElementById", FenValue.FromFunction(new FenFunction("getElementById", (args, thisVal) =>
            {
                if (_domBridge == null) return FenValue.Null;
                if (args.Length == 0) return FenValue.Null;
                return _domBridge.GetElementById(args[0].ToString()) ?? FenValue.Null;
            })));
            document.Set("querySelector", FenValue.FromFunction(new FenFunction("querySelector", (args, thisVal) =>
            {
                if (_domBridge == null) return FenValue.Null;
                if (args.Length == 0) return FenValue.Null;
                return _domBridge.QuerySelector(args[0].ToString()) ?? FenValue.Null;
            })));
            document.Set("createElement", FenValue.FromFunction(new FenFunction("createElement", (args, thisVal) =>
            {
                if (_domBridge == null) return FenValue.Null;
                if (args.Length == 0) return FenValue.Null;
                return _domBridge.CreateElement(args[0].ToString()) ?? FenValue.Null;
            })));

            document.Set("createTextNode", FenValue.FromFunction(new FenFunction("createTextNode", (args, thisVal) =>
            {
                if (_domBridge == null) return FenValue.Null;
                var text = args.Length > 0 ? args[0].ToString() : "";
                return _domBridge.CreateTextNode(text) ?? FenValue.Null;
            })));
            
             // document.body / head (Stubs or getters if bridge supports)
            
            SetGlobal("document", FenValue.FromObject(document));

            // console object
            var console = new FenObject();
            FenLogger.Debug("[FenRuntime] Creating console object...", LogCategory.JavaScript);
            console.Set("log", FenValue.FromFunction(new FenFunction("log", (args, thisVal) =>
            {
                try { FenLogger.Debug("[FenRuntime] console.log invoked from JS", LogCategory.JavaScript); } catch { }

                var messages = new List<string>();
                foreach (var arg in args) messages.Add(arg.ToString());
                var msg = string.Join(" ", messages);
                Console.WriteLine(msg);
                // /* [PERF-REMOVED] */
                try { FenLogger.Debug($"[FenRuntime] Console.log: {msg}", LogCategory.JavaScript); } catch { }
                try { 
                    if (OnConsoleMessage == null) FenLogger.Error("[FenRuntime] OnConsoleMessage is NULL!", LogCategory.JavaScript);
                    else FenLogger.Debug("[FenRuntime] Invoking OnConsoleMessage...", LogCategory.JavaScript);
                    OnConsoleMessage?.Invoke(msg); 
                } catch (Exception ex) { FenLogger.Error($"[FenRuntime] OnConsoleMessage error: {ex}", LogCategory.JavaScript); }
                return FenValue.Undefined;
            })));
            console.Set("error", FenValue.FromFunction(new FenFunction("error", (args, thisVal) =>
            {
                var msg = string.Join(" ", args.Select(a => a.ToString()));
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR: {msg}");
                Console.ResetColor();
                // /* [PERF-REMOVED] */
                try { FenLogger.Error($"[FenRuntime] Console.error: {msg}", LogCategory.JavaScript); } catch { }
                try { OnConsoleMessage?.Invoke($"[Error] {msg}"); } catch { }
                return FenValue.Undefined;
            })));
            console.Set("warn", FenValue.FromFunction(new FenFunction("warn", (args, thisVal) =>
            {
                var msg = string.Join(" ", args.Select(a => a.ToString()));
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"WARN: {msg}");
                Console.ResetColor();
                // /* [PERF-REMOVED] */
                try { FenLogger.Info($"[FenRuntime] Console.warn: {msg}", LogCategory.JavaScript); } catch { }
                try { OnConsoleMessage?.Invoke($"[Warn] {msg}"); } catch { }
                return FenValue.Undefined;
            })));
            console.Set("info", FenValue.FromFunction(new FenFunction("info", (args, thisVal) =>
            {
                var msg = string.Join(" ", args.Select(a => a.ToString()));
                Console.WriteLine($"INFO: {msg}");
                // /* [PERF-REMOVED] */
                try { FenLogger.Info($"[FenRuntime] Console.info: {msg}", LogCategory.JavaScript); } catch { }
                try { OnConsoleMessage?.Invoke($"[Info] {msg}"); } catch { }
                return FenValue.Undefined;
            })));
            console.Set("clear", FenValue.FromFunction(new FenFunction("clear", (args, thisVal) =>
            {
                Console.Clear();
                try { OnConsoleMessage?.Invoke("[Clear]"); } catch { }
                return FenValue.Undefined;
            })));

            // console.dir - Object inspection
            console.Set("dir", FenValue.FromFunction(new FenFunction("dir", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                var obj = args[0];
                var output = InspectObject(obj, 0);
                Console.WriteLine(output);
                try { FenLogger.Debug($"[FenRuntime] Console.dir: {output}", LogCategory.JavaScript); } catch { }
                try { OnConsoleMessage?.Invoke($"[Dir] {output}"); } catch { }
                return FenValue.Undefined;
            })));

            // console.table - Tabular data display
            console.Set("table", FenValue.FromFunction(new FenFunction("table", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                var obj = args[0];
                var output = $"[Table] {obj}"; // Simplified - full table formatting would be complex
                Console.WriteLine(output);
                try { FenLogger.Debug($"[FenRuntime] Console.table: {output}", LogCategory.JavaScript); } catch { }
                try { OnConsoleMessage?.Invoke(output); } catch { }
                return FenValue.Undefined;
            })));

            // console.group / groupEnd - Indentation
            int _consoleGroupLevel = 0;
            console.Set("group", FenValue.FromFunction(new FenFunction("group", (args, thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "";
                _consoleGroupLevel++;
                var indent = new string(' ', _consoleGroupLevel * 2);
                var msg = $"{indent}▼ {label}";
                Console.WriteLine(msg);
                try { OnConsoleMessage?.Invoke($"[Group] {label}"); } catch { }
                return FenValue.Undefined;
            })));

            console.Set("groupCollapsed", FenValue.FromFunction(new FenFunction("groupCollapsed", (args, thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "";
                _consoleGroupLevel++;
                var indent = new string(' ', _consoleGroupLevel * 2);
                var msg = $"{indent}▶ {label}";
                Console.WriteLine(msg);
                try { OnConsoleMessage?.Invoke($"[GroupCollapsed] {label}"); } catch { }
                return FenValue.Undefined;
            })));

            console.Set("groupEnd", FenValue.FromFunction(new FenFunction("groupEnd", (args, thisVal) =>
            {
                if (_consoleGroupLevel > 0) _consoleGroupLevel--;
                return FenValue.Undefined;
            })));

            // console.time / timeEnd - Timing
            var _consoleTimers = new Dictionary<string, DateTime>();
            console.Set("time", FenValue.FromFunction(new FenFunction("time", (args, thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "default";
                _consoleTimers[label] = DateTime.Now;
                return FenValue.Undefined;
            })));

            console.Set("timeEnd", FenValue.FromFunction(new FenFunction("timeEnd", (args, thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "default";
                if (_consoleTimers.TryGetValue(label, out var start))
                {
                    var elapsed = (DateTime.Now - start).TotalMilliseconds;
                    var msg = $"{label}: {elapsed:F2}ms";
                    Console.WriteLine(msg);
                    try { OnConsoleMessage?.Invoke($"[Timer] {msg}"); } catch { }
                    _consoleTimers.Remove(label);
                }
                return FenValue.Undefined;
            })));

            // console.count / countReset
            var _consoleCounts = new Dictionary<string, int>();
            console.Set("count", FenValue.FromFunction(new FenFunction("count", (args, thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "default";
                if (!_consoleCounts.ContainsKey(label)) _consoleCounts[label] = 0;
                _consoleCounts[label]++;
                var msg = $"{label}: {_consoleCounts[label]}";
                Console.WriteLine(msg);
                try { OnConsoleMessage?.Invoke($"[Count] {msg}"); } catch { }
                return FenValue.Undefined;
            })));

            console.Set("countReset", FenValue.FromFunction(new FenFunction("countReset", (args, thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "default";
                _consoleCounts[label] = 0;
                return FenValue.Undefined;
            })));

            // console.assert
            console.Set("assert", FenValue.FromFunction(new FenFunction("assert", (args, thisVal) =>
            {
                if (args.Length == 0 || args[0].ToBoolean()) return FenValue.Undefined;
                var msg = args.Length > 1 ? string.Join(" ", args.Skip(1).Select(a => a.ToString())) : "Assertion failed";
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Assertion failed: {msg}");
                Console.ResetColor();
                try { FenLogger.Error($"[FenRuntime] Console.assert: {msg}", LogCategory.JavaScript); } catch { }
                try { OnConsoleMessage?.Invoke($"[Assert] {msg}"); } catch { }
                return FenValue.Undefined;
            })));

            // console.trace
            console.Set("trace", FenValue.FromFunction(new FenFunction("trace", (args, thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "Trace";
                var stack = Environment.StackTrace;
                Console.WriteLine($"{label}\n{stack}");
                try { OnConsoleMessage?.Invoke($"[Trace] {label}"); } catch { }
                return FenValue.Undefined;
            })));

            /* [PERF-REMOVED] */
            SetGlobal("console", FenValue.FromObject(console));

            // caches API (CacheStorage) - Persistent and partitioned by origin
            var cacheStorage = new FenBrowser.FenEngine.WebAPIs.CacheStorage(() => GetCurrentOrigin(), _storageBackend);
            SetGlobal("caches", FenValue.FromObject(cacheStorage));

            // Worker API - Persistent storage for workers
            var workerCtor = new FenBrowser.FenEngine.Workers.WorkerConstructor(GetCurrentOrigin(), _storageBackend);
            SetGlobal("Worker", FenValue.FromFunction(workerCtor.GetConstructorFunction()));

            // Timers
            var setTimeout = FenValue.FromFunction(new FenFunction("setTimeout", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction) return FenValue.FromNumber(0);
                var callback = args[0].AsFunction();
                int delay = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                var callbackArgs = args.Skip(2).ToArray();

                return CreateTimer(callback, delay, false, callbackArgs);
            }));
            SetGlobal("setTimeout", setTimeout);
            
            var clearTimeout = FenValue.FromFunction(new FenFunction("clearTimeout", (args, thisVal) =>
            {
                if (args.Length > 0) CancelTimer((int)args[0].ToNumber());
                return FenValue.Undefined;
            }));
            SetGlobal("clearTimeout", clearTimeout);

            var setInterval = FenValue.FromFunction(new FenFunction("setInterval", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction) return FenValue.FromNumber(0);
                var callback = args[0].AsFunction();
                int delay = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                var callbackArgs = args.Skip(2).ToArray();

                return CreateTimer(callback, delay, true, callbackArgs);
            }));
            SetGlobal("setInterval", setInterval);

            var clearInterval = FenValue.FromFunction(new FenFunction("clearInterval", (args, thisVal) =>
            {
                if (args.Length > 0) CancelTimer((int)args[0].ToNumber());
                return FenValue.Undefined;
            }));
            SetGlobal("clearInterval", clearInterval);

            // requestAnimationFrame
            var requestAnimationFrame = FenValue.FromFunction(new FenFunction("requestAnimationFrame", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction) return FenValue.FromNumber(0);
                return CreateAnimationFrame(args[0].AsFunction());
            }));
            SetGlobal("requestAnimationFrame", requestAnimationFrame);

            // cancelAnimationFrame
            var cancelAnimationFrame = FenValue.FromFunction(new FenFunction("cancelAnimationFrame", (args, thisVal) =>
            {
                if (args.Length > 0) CancelTimer((int)args[0].ToNumber());
                return FenValue.Undefined;
            }));
            SetGlobal("cancelAnimationFrame", cancelAnimationFrame);

            // Dynamic import() function - returns a Promise
            SetGlobal("import", FenValue.FromFunction(new FenFunction("import", (args, thisVal) =>
            {
                if (args.Length == 0) return CreateRejectedPromise("import() requires a module specifier");
                var modulePath = args[0].ToString();
                
                // Create a promise that will resolve with the module exports
                var promise = new FenObject();
                promise.Set("__isPromise__", FenValue.FromBoolean(true));
                promise.Set("__state__", FenValue.FromString("pending"));
                
                // For now, return a resolved promise with an empty module namespace
                // In a real implementation, this would async load and parse the module
                var moduleNamespace = new FenObject();
                moduleNamespace.Set("default", FenValue.Undefined);
                
                // Check if module loader has this module cached
                if (_context.ModuleLoader != null)
                {
                    try
                    {
                        var exports = _context.ModuleLoader.LoadModule(modulePath);
                        if (exports != null)
                        {
                            promise.Set("__state__", FenValue.FromString("fulfilled"));
                            promise.Set("__value__", (FenValue)exports);
                        }
                        else
                        {
                            promise.Set("__state__", FenValue.FromString("fulfilled"));
                            promise.Set("__value__", FenValue.FromObject(moduleNamespace));
                        }
                    }
                    catch (Exception ex)
                    {
                        promise.Set("__state__", FenValue.FromString("rejected"));
                        promise.Set("__reason__", FenValue.FromString(ex.Message));
                    }
                }
                else
                {
                    promise.Set("__state__", FenValue.FromString("fulfilled"));
                    promise.Set("__value__", FenValue.FromObject(moduleNamespace));
                }
                
                // Add then/catch methods
                promise.Set("then", FenValue.FromFunction(new FenFunction("then", (thenArgs, thenThis) =>
                {
                    var state = promise.Get("__state__")?.ToString();
                    if (state == "fulfilled")
                    {
                        if (thenArgs.Length > 0 && thenArgs[0].IsFunction)
                        {
                            var onFulfilled = thenArgs[0].AsFunction() as FenFunction;
                            var value = promise.Get("__value__") ?? FenValue.Undefined;
                            return onFulfilled?.Invoke(new IValue[] { value }, null) ?? FenValue.Undefined;
                        }
                        return promise.Get("__value__") ?? FenValue.Undefined;
                    }
                    else if (state == "rejected")
                    {
                        if (thenArgs.Length > 1 && thenArgs[1].IsFunction)
                        {
                            var onRejected = thenArgs[1].AsFunction() as FenFunction;
                            var reason = promise.Get("__reason__") ?? FenValue.Undefined;
                            return onRejected?.Invoke(new IValue[] { reason }, null) ?? FenValue.Undefined;
                        }
                        return FenValue.Undefined;
                    }
                    return FenValue.FromObject(promise);
                })));
                
                promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (catchArgs, catchThis) =>
                {
                    var state = promise.Get("__state__")?.ToString();
                    if (state == "rejected" && catchArgs.Length > 0 && catchArgs[0].IsFunction)
                    {
                        var onRejected = catchArgs[0].AsFunction() as FenFunction;
                        var reason = promise.Get("__reason__") ?? FenValue.Undefined;
                        return onRejected?.Invoke(new IValue[] { reason }, null) ?? FenValue.Undefined;
                    }
                    return FenValue.FromObject(promise);
                })));
                
                return FenValue.FromObject(promise);
            })));

            // undefined and null
            SetGlobal("undefined", FenValue.Undefined);
            SetGlobal("null", FenValue.Null);

            // navigator object - Privacy-focused (generic values to prevent fingerprinting)
            var navigator = new FenObject();
            navigator.Set("userAgent", FenValue.FromString("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.7499.109 Safari/537.36"));
            navigator.Set("platform", FenValue.FromString("Win32"));
            navigator.Set("language", FenValue.FromString("en-US"));
            navigator.Set("languages", FenValue.FromObject(CreateArray(new[] { "en-US", "en" })));
            navigator.Set("cookieEnabled", FenValue.FromBoolean(true));
            navigator.Set("onLine", FenValue.FromBoolean(true));
            navigator.Set("doNotTrack", FenValue.FromString("1")); // Privacy: DNT enabled by default
            // Privacy: Use generic values to prevent fingerprinting (unlike Chrome/Firefox)
            navigator.Set("hardwareConcurrency", FenValue.FromNumber(4)); // Generic, not actual CPU cores
            navigator.Set("deviceMemory", FenValue.FromNumber(8)); // Generic, not actual RAM
            navigator.Set("maxTouchPoints", FenValue.FromNumber(0));
            navigator.Set("vendor", FenValue.FromString("FenBrowser")); // Our vendor, not Google
            navigator.Set("vendorSub", FenValue.FromString(""));
            navigator.Set("product", FenValue.FromString("Gecko"));
            navigator.Set("productSub", FenValue.FromString("20100101"));
            navigator.Set("appCodeName", FenValue.FromString("Mozilla"));
            navigator.Set("appName", FenValue.FromString("Netscape"));
            navigator.Set("appVersion", FenValue.FromString("5.0 (Windows)"));
            navigator.Set("oscpu", FenValue.FromString("Windows NT 10.0; Win64; x64"));
            // Privacy: Empty plugins array (prevents plugin fingerprinting)
            navigator.Set("plugins", FenValue.FromObject(CreateArray(new string[0])));
            navigator.Set("mimeTypes", FenValue.FromObject(CreateArray(new string[0])));
            
            // Anti-Bot / Anti-Fingerprinting extras
            navigator.Set("webdriver", FenValue.FromBoolean(false)); // Explicitly deny automation
            navigator.Set("pdfViewerEnabled", FenValue.FromBoolean(false));
            
            // javaEnabled() method - Standard requires it to exist and return false (mostly)
            navigator.Set("javaEnabled", FenValue.FromFunction(new FenFunction("javaEnabled", (args, thisVal) => FenValue.FromBoolean(false))));
            
            // Network Information Spoofing
            var connection = new FenObject();
            connection.Set("effectiveType", FenValue.FromString("4g"));
            connection.Set("rtt", FenValue.FromNumber(50));
            connection.Set("downlink", FenValue.FromNumber(10));
            connection.Set("saveData", FenValue.FromBoolean(false));
            navigator.Set("connection", FenValue.FromObject(connection));

            /* [PERF-REMOVED] */
            SetGlobal("navigator", FenValue.FromObject(navigator));

            // location object (basic)
            var location = new FenObject();
            location.Set("href", FenValue.FromString("http://localhost:8000/"));
            location.Set("protocol", FenValue.FromString("http:"));
            location.Set("host", FenValue.FromString("localhost:8000"));
            location.Set("hostname", FenValue.FromString("localhost"));
            location.Set("pathname", FenValue.FromString("/"));
            SetGlobal("location", FenValue.FromObject(location));

            // history object
            var history = new FenObject();
            history.Set("length", FenValue.FromNumber(_historyBridge?.Length ?? 1));
            history.Set("state", FenValue.Null); // Initial state
            
            // Getters for state and length that query the bridge dynamicall if possible?
            // Since we can't easily do getters on FenObject yet (unless we use DefineProperty which isn't fully exposed via Set),
            // we'll rely on methods updates or proxied access. 
            // Ideally FenObject should support property descriptors.
            // For now, simple methods are key.
            
            history.Set("pushState", FenValue.FromFunction(new FenFunction("pushState", (args, thisVal) =>
            {
                if (args.Length >= 2)
                {
                    var state = args[0].AsObject(); // Or generic value? History supports any serializable.
                    // For now assuming object or primitive.
                    object stateObj = null;
                    if (args[0].IsObject) stateObj = args[0].AsObject(); // Simplified
                    else if (args[0].IsString) stateObj = args[0].ToString();
                    else if (args[0].IsNumber) stateObj = args[0].ToNumber();
                    else if (args[0].IsBoolean) stateObj = args[0].ToBoolean();
                    
                    var title = args[1].ToString();
                    var url = args.Length > 2 ? args[2].ToString() : null;
                    
                    _historyBridge?.PushState(stateObj, title, url);
                    
                    // Update local history object state immediately?
                    // Ideally bridge syncs back but for now:
                    history.Set("state", args[0]); 
                    history.Set("length", FenValue.FromNumber(_historyBridge?.Length ?? 1));
                }
                return FenValue.Undefined;
            })));

            history.Set("replaceState", FenValue.FromFunction(new FenFunction("replaceState", (args, thisVal) =>
            {
                if (args.Length >= 2)
                {
                    object stateObj = null;
                    if (args[0].IsObject) stateObj = args[0].AsObject();
                    else if (args[0].IsString) stateObj = args[0].ToString();
                    
                    var title = args[1].ToString();
                    var url = args.Length > 2 ? args[2].ToString() : null;
                    
                    _historyBridge?.ReplaceState(stateObj, title, url);
                    history.Set("state", args[0]);
                }
                return FenValue.Undefined;
            })));

            history.Set("go", FenValue.FromFunction(new FenFunction("go", (args, thisVal) =>
            {
                if (args.Length > 0)
                {
                    int delta = (int)args[0].ToNumber();
                    _historyBridge?.Go(delta);
                }
                else
                {
                    _historyBridge?.Go(0); // reload
                }
                return FenValue.Undefined;
            })));

            history.Set("back", FenValue.FromFunction(new FenFunction("back", (args, thisVal) =>
            {
                _historyBridge?.Go(-1);
                return FenValue.Undefined;
            })));
            
            history.Set("forward", FenValue.FromFunction(new FenFunction("forward", (args, thisVal) =>
            {
                _historyBridge?.Go(1);
                return FenValue.Undefined;
            })));

            SetGlobal("history", FenValue.FromObject(history));

            // screen object - Privacy-focused (use common resolution to prevent fingerprinting)
            var screen = new FenObject();
            screen.Set("width", FenValue.FromNumber(1920));      // Common resolution
            screen.Set("height", FenValue.FromNumber(1080));     // Common resolution
            screen.Set("availWidth", FenValue.FromNumber(1920));
            screen.Set("availHeight", FenValue.FromNumber(1040)); // Minus taskbar
            screen.Set("colorDepth", FenValue.FromNumber(24));   // Standard 24-bit color
            screen.Set("pixelDepth", FenValue.FromNumber(24));
            screen.Set("orientation", FenValue.FromObject(CreateScreenOrientation()));
            SetGlobal("screen", FenValue.FromObject(screen));

            // localStorage - Partitioned using StorageApi
            var localStorage = FenBrowser.FenEngine.WebAPIs.StorageApi.CreateLocalStorage(GetCurrentOrigin);
            SetGlobal("localStorage", FenValue.FromObject(localStorage));

            // sessionStorage - Partitioned using StorageApi (per instance)
            var sessionStorage = FenBrowser.FenEngine.WebAPIs.StorageApi.CreateSessionStorage();
            SetGlobal("sessionStorage", FenValue.FromObject(sessionStorage));

            // window object - Comprehensive with all standard properties
            var window = new FenObject();
            window.Set("console", FenValue.FromObject(console));
            window.Set("navigator", FenValue.FromObject(navigator));
            window.Set("location", FenValue.FromObject(location));
            window.Set("screen", FenValue.FromObject(screen));
            window.Set("localStorage", FenValue.FromObject(localStorage));
            window.Set("sessionStorage", FenValue.FromObject(sessionStorage));
            window.Set("history", FenValue.FromObject(history));
            // Viewport properties - Privacy: use common resolution
            window.Set("innerWidth", FenValue.FromNumber(1920));
            window.Set("innerHeight", FenValue.FromNumber(1080));
            window.Set("outerWidth", FenValue.FromNumber(1920));
            window.Set("outerHeight", FenValue.FromNumber(1080));
            window.Set("devicePixelRatio", FenValue.FromNumber(1)); // Privacy: always 1
            window.Set("scrollX", FenValue.FromNumber(0));
            window.Set("scrollY", FenValue.FromNumber(0));
            window.Set("pageXOffset", FenValue.FromNumber(0));
            window.Set("pageYOffset", FenValue.FromNumber(0));
            // Self-references
            window.Set("self", FenValue.FromObject(window));
            window.Set("top", FenValue.FromObject(window));
            window.Set("parent", FenValue.FromObject(window));
            window.Set("frames", FenValue.FromObject(window));
            window.Set("length", FenValue.FromNumber(0)); // No frames
            // Standard properties
            window.Set("name", FenValue.FromString(""));
            window.Set("closed", FenValue.FromBoolean(false));
            window.Set("opener", FenValue.Null);
            
            // Event listeners storage for window - Use class field
            // var windowEventListeners = new Dictionary<string, List<IValue>>(); // Field used instead
            
            // addEventListener
            var addEventListenerFunc = FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length >= 2)
                {
                    var eventType = args[0].ToString();
                    var callback = args[1];
                    FenLogger.Info($"[FenRuntime] addEventListener called for '{eventType}'", LogCategory.Events);
                    
                    if (!_windowEventListeners.ContainsKey(eventType))
                    {
                        _windowEventListeners[eventType] = new List<IValue>();
                    }
                    _windowEventListeners[eventType].Add(callback);
                }
                return FenValue.Undefined;
            }));
            window.Set("addEventListener", addEventListenerFunc);
            
            // removeEventListener
            var removeEventListenerFunc = FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
            {
                if (args.Length >= 2)
                {
                    var eventType = args[0].ToString();
                    var callback = args[1];
                    
                    if (_windowEventListeners.ContainsKey(eventType))
                    {
                        _windowEventListeners[eventType].RemoveAll(l => l.Equals(callback));
                    }
                }
                return FenValue.Undefined;
            }));
            window.Set("removeEventListener", removeEventListenerFunc);
            
            // dispatchEvent (basic implementation)
            var dispatchEventFunc = FenValue.FromFunction(new FenFunction("dispatchEvent", (args, thisVal) =>
            {
                // Basic stub - returns true to indicate event was dispatched
                return FenValue.FromBoolean(true);
            }));
            window.Set("dispatchEvent", dispatchEventFunc);
            
            SetGlobal("window", FenValue.FromObject(window));

            // IMPORTANT: Also expose window properties at global scope for direct access
            // In browsers, 'innerWidth' works the same as 'window.innerWidth'
            SetGlobal("innerWidth", FenValue.FromNumber(1920));
            SetGlobal("innerHeight", FenValue.FromNumber(1080));
            SetGlobal("outerWidth", FenValue.FromNumber(1920));
            SetGlobal("outerHeight", FenValue.FromNumber(1080));
            SetGlobal("devicePixelRatio", FenValue.FromNumber(1));
            SetGlobal("scrollX", FenValue.FromNumber(0));
            SetGlobal("scrollY", FenValue.FromNumber(0));
            SetGlobal("pageXOffset", FenValue.FromNumber(0));
            SetGlobal("pageYOffset", FenValue.FromNumber(0));
            SetGlobal("self", FenValue.FromObject(window));
            SetGlobal("top", FenValue.FromObject(window));
            SetGlobal("parent", FenValue.FromObject(window));
            SetGlobal("frames", FenValue.FromObject(window));
            
            // Expose event methods at global scope (in browsers, addEventListener === window.addEventListener)
            SetGlobal("addEventListener", addEventListenerFunc);
            SetGlobal("removeEventListener", removeEventListenerFunc);
            SetGlobal("dispatchEvent", dispatchEventFunc);

            // Event constructor (DOM Level 3)
            SetGlobal("Event", FenValue.FromFunction(new FenFunction("Event", (args, thisVal) =>
            {
                var type = args.Length > 0 ? args[0].ToString() : "";
                bool bubbles = false;
                bool cancelable = false;
                bool composed = false;

                if (args.Length > 1 && args[1].IsObject)
                {
                    var opts = args[1].AsObject() as FenObject;
                    if (opts != null)
                    {
                        bubbles = opts.Get("bubbles")?.ToBoolean() ?? false;
                        cancelable = opts.Get("cancelable")?.ToBoolean() ?? false;
                        composed = opts.Get("composed")?.ToBoolean() ?? false;
                    }
                }

                return FenValue.FromObject(new FenBrowser.FenEngine.DOM.DomEvent(type, bubbles, cancelable, composed));
            })));

            // CustomEvent constructor (DOM Level 3)
            SetGlobal("CustomEvent", FenValue.FromFunction(new FenFunction("CustomEvent", (args, thisVal) =>
            {
                var type = args.Length > 0 ? args[0].ToString() : "";
                bool bubbles = false;
                bool cancelable = false;
                IValue detail = FenValue.Null;

                if (args.Length > 1 && args[1].IsObject)
                {
                    var opts = args[1].AsObject() as FenObject;
                    if (opts != null)
                    {
                        bubbles = opts.Get("bubbles")?.ToBoolean() ?? false;
                        cancelable = opts.Get("cancelable")?.ToBoolean() ?? false;
                        detail = opts.Get("detail") ?? FenValue.Null;
                    }
                }

                return FenValue.FromObject(new FenBrowser.FenEngine.DOM.CustomEvent(type, bubbles, cancelable, detail));
            })));

            // Custom Elements Registry (Web Components)
            var customElementsRegistry = new FenBrowser.FenEngine.DOM.CustomElementRegistry();
            SetGlobal("customElements", FenValue.FromObject(customElementsRegistry.ToFenObject()));

            // requestAnimationFrame / cancelAnimationFrame

            // Use a simple counter and store callbacks in window.__raf_queue
            var requestAnimationFrameFunc = FenValue.FromFunction(new FenFunction("requestAnimationFrame", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    // The callback will be stored in window.__raf_queue by the JavaScript side
                    // Return a unique ID based on a counter stored in window.__raf_id
                    return FenValue.Undefined; // The actual implementation will be in JS
                }
                return FenValue.FromNumber(0);
            }));
            // Note: We'll inject a proper requestAnimationFrame in the async script wrapper instead

            // RegExp constructor - Full implementation
            SetGlobal("RegExp", FenValue.FromFunction(new FenFunction("RegExp", (args, thisVal) =>
            {
                var pattern = args.Length > 0 ? args[0].ToString() : "";
                var flags = args.Length > 1 ? args[1].ToString() : "";
                
                // If first arg is already a RegExp, clone it
                if (args.Length > 0 && args[0].IsObject)
                {
                    var srcObj = args[0].AsObject() as FenObject;
                    if (srcObj?.Get("source") != null && srcObj.NativeObject is Regex)
                    {
                        pattern = srcObj.Get("source")?.ToString() ?? "";
                        if (args.Length == 1)
                            flags = srcObj.Get("flags")?.ToString() ?? "";
                    }
                }
                
                try
                {
                    var options = RegexOptions.None;
                    bool globalFlag = flags.Contains("g");
                    bool ignoreCase = flags.Contains("i");
                    bool multiline = flags.Contains("m");
                    bool dotAll = flags.Contains("s");
                    
                    if (ignoreCase) options |= RegexOptions.IgnoreCase;
                    if (multiline) options |= RegexOptions.Multiline;
                    if (dotAll) options |= RegexOptions.Singleline;
                    
                    var r = new Regex(pattern, options);
                    var obj = new FenObject();
                    obj.NativeObject = r;
                    obj.Set("source", FenValue.FromString(pattern));
                    obj.Set("flags", FenValue.FromString(flags));
                    obj.Set("global", FenValue.FromBoolean(globalFlag));
                    obj.Set("ignoreCase", FenValue.FromBoolean(ignoreCase));
                    obj.Set("multiline", FenValue.FromBoolean(multiline));
                    obj.Set("dotAll", FenValue.FromBoolean(dotAll));
                    obj.Set("lastIndex", FenValue.FromNumber(0));
                    
                    // test(str) - Returns true if the pattern matches
                    obj.Set("test", FenValue.FromFunction(new FenFunction("test", (testArgs, testThis) =>
                    {
                        if (testArgs.Length == 0) return FenValue.FromBoolean(false);
                        var str = testArgs[0].ToString();
                        var lastIdx = (int)(obj.Get("lastIndex")?.ToNumber() ?? 0);
                        var isGlobal = obj.Get("global")?.ToBoolean() ?? false;
                        
                        if (isGlobal && lastIdx > 0 && lastIdx <= str.Length)
                            str = str.Substring(lastIdx);
                        else if (isGlobal)
                            lastIdx = 0;
                            
                        var match = r.Match(str);
                        if (match.Success && isGlobal)
                            obj.Set("lastIndex", FenValue.FromNumber(lastIdx + match.Index + match.Length));
                        else if (!match.Success && isGlobal)
                            obj.Set("lastIndex", FenValue.FromNumber(0));
                            
                        return FenValue.FromBoolean(match.Success);
                    })));
                    
                    // exec(str) - Returns match array or null
                    obj.Set("exec", FenValue.FromFunction(new FenFunction("exec", (execArgs, execThis) =>
                    {
                        if (execArgs.Length == 0) return FenValue.Null;
                        var str = execArgs[0].ToString();
                        var lastIdx = (int)(obj.Get("lastIndex")?.ToNumber() ?? 0);
                        var isGlobal = obj.Get("global")?.ToBoolean() ?? false;
                        
                        Match match;
                        if (isGlobal && lastIdx > 0 && lastIdx < str.Length)
                            match = r.Match(str, lastIdx);
                        else
                            match = r.Match(str);
                        
                        if (!match.Success)
                        {
                            if (isGlobal) obj.Set("lastIndex", FenValue.FromNumber(0));
                            return FenValue.Null;
                        }
                        
                        if (isGlobal)
                            obj.Set("lastIndex", FenValue.FromNumber(match.Index + match.Length));
                        
                        // Create result array
                        var result = new FenObject();
                        result.Set("0", FenValue.FromString(match.Value));
                        for (int i = 1; i < match.Groups.Count; i++)
                        {
                            result.Set(i.ToString(), match.Groups[i].Success 
                                ? FenValue.FromString(match.Groups[i].Value) 
                                : FenValue.Undefined);
                        }
                        result.Set("length", FenValue.FromNumber(match.Groups.Count));
                        result.Set("index", FenValue.FromNumber(match.Index));
                        result.Set("input", FenValue.FromString(str));
                        
                        // Named capture groups support
                        var groups = new FenObject();
                        foreach (var gn in r.GetGroupNames())
                        {
                            if (!int.TryParse(gn, out _))
                            {
                                var g = match.Groups[gn];
                                groups.Set(gn, g.Success ? FenValue.FromString(g.Value) : FenValue.Undefined);
                            }
                        }
                        result.Set("groups", FenValue.FromObject(groups));
                        
                        return FenValue.FromObject(result);
                    })));
                    
                    // toString() - Returns "/pattern/flags"
                    obj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) =>
                        FenValue.FromString($"/{pattern}/{flags}"))));
                    
                    return FenValue.FromObject(obj);
                }
                catch (Exception ex)
                {
                    return new ErrorValue($"Invalid regular expression: {ex.Message}");
                }
            })));

            // Math object
            var math = new FenObject();
            math.Set("PI", FenValue.FromNumber(Math.PI));
            math.Set("E", FenValue.FromNumber(Math.E));
            math.Set("abs", FenValue.FromFunction(new FenFunction("abs", (args, thisVal) => 
                FenValue.FromNumber(Math.Abs(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("ceil", FenValue.FromFunction(new FenFunction("ceil", (args, thisVal) => 
                FenValue.FromNumber(Math.Ceiling(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("floor", FenValue.FromFunction(new FenFunction("floor", (args, thisVal) => 
                FenValue.FromNumber(Math.Floor(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("round", FenValue.FromFunction(new FenFunction("round", (args, thisVal) => 
                FenValue.FromNumber(Math.Round(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("max", FenValue.FromFunction(new FenFunction("max", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromNumber(double.NegativeInfinity);
                double max = args[0].ToNumber();
                for (int i = 1; i < args.Length; i++) max = Math.Max(max, args[i].ToNumber());
                return FenValue.FromNumber(max);
            })));
            math.Set("min", FenValue.FromFunction(new FenFunction("min", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromNumber(double.PositiveInfinity);
                double min = args[0].ToNumber();
                for (int i = 1; i < args.Length; i++) min = Math.Min(min, args[i].ToNumber());
                return FenValue.FromNumber(min);
            })));
            math.Set("pow", FenValue.FromFunction(new FenFunction("pow", (args, thisVal) => 
                FenValue.FromNumber(Math.Pow(args.Length > 0 ? args[0].ToNumber() : double.NaN, args.Length > 1 ? args[1].ToNumber() : double.NaN)))));
            math.Set("sqrt", FenValue.FromFunction(new FenFunction("sqrt", (args, thisVal) => 
                FenValue.FromNumber(Math.Sqrt(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("random", FenValue.FromFunction(new FenFunction("random", (args, thisVal) => 
                FenValue.FromNumber(_mathRandom.NextDouble()))));
            math.Set("sin", FenValue.FromFunction(new FenFunction("sin", (args, thisVal) => 
                FenValue.FromNumber(Math.Sin(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("cos", FenValue.FromFunction(new FenFunction("cos", (args, thisVal) => 
                FenValue.FromNumber(Math.Cos(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("tan", FenValue.FromFunction(new FenFunction("tan", (args, thisVal) => 
                FenValue.FromNumber(Math.Tan(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("trunc", FenValue.FromFunction(new FenFunction("trunc", (args, thisVal) => 
                FenValue.FromNumber(Math.Truncate(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("sign", FenValue.FromFunction(new FenFunction("sign", (args, thisVal) => 
                FenValue.FromNumber(Math.Sign(args.Length > 0 ? args[0].ToNumber() : 0)))));
            math.Set("log", FenValue.FromFunction(new FenFunction("log", (args, thisVal) => 
                FenValue.FromNumber(Math.Log(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("log10", FenValue.FromFunction(new FenFunction("log10", (args, thisVal) => 
                FenValue.FromNumber(Math.Log10(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("exp", FenValue.FromFunction(new FenFunction("exp", (args, thisVal) => 
                FenValue.FromNumber(Math.Exp(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("asin", FenValue.FromFunction(new FenFunction("asin", (args, thisVal) => 
                FenValue.FromNumber(Math.Asin(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("acos", FenValue.FromFunction(new FenFunction("acos", (args, thisVal) => 
                FenValue.FromNumber(Math.Acos(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("atan", FenValue.FromFunction(new FenFunction("atan", (args, thisVal) => 
                FenValue.FromNumber(Math.Atan(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("atan2", FenValue.FromFunction(new FenFunction("atan2", (args, thisVal) => 
                FenValue.FromNumber(Math.Atan2(args.Length > 0 ? args[0].ToNumber() : double.NaN, args.Length > 1 ? args[1].ToNumber() : double.NaN)))));
            math.Set("hypot", FenValue.FromFunction(new FenFunction("hypot", (args, thisVal) => {
                double sum = 0;
                foreach (var arg in args) { var n = arg.ToNumber(); sum += n * n; }
                return FenValue.FromNumber(Math.Sqrt(sum));
            })));
            
            /* [PERF-REMOVED] */
            SetGlobal("Math", FenValue.FromObject(math));

            // Symbol constructor
            var symbolCtor = FenSymbol.CreateSymbolConstructor();
            SetGlobal("Symbol", FenValue.FromObject(symbolCtor));

            // ES6 Collection constructors: Map, Set, WeakMap, WeakSet
            SetGlobal("Map", FenValue.FromFunction(new FenFunction("Map", (args, thisVal) =>
            {
                var map = new FenBrowser.FenEngine.Core.Types.JsMap(_context);
                // If iterable argument provided, populate from it
                if (args.Length > 0 && args[0].IsObject)
                {
                    var iterable = args[0].AsObject();
                    var lenVal = iterable?.Get("length");
                    if (lenVal != null && lenVal.IsNumber)
                    {
                        int len = (int)lenVal.ToNumber();
                        for (int i = 0; i < len; i++)
                        {
                            var entry = iterable.Get(i.ToString());
                            if (entry != null && entry.IsObject)
                            {
                                var entryObj = entry.AsObject();
                                var key = entryObj?.Get("0") ?? FenValue.Undefined;
                                var val = entryObj?.Get("1") ?? FenValue.Undefined;
                                map.Get("set")?.AsFunction()?.Invoke(new IValue[] { key, val }, _context);
                            }
                        }
                    }
                }
                return FenValue.FromObject(map);
            })));

            SetGlobal("Set", FenValue.FromFunction(new FenFunction("Set", (args, thisVal) =>
            {
                var set = new FenBrowser.FenEngine.Core.Types.JsSet(_context);
                // If iterable argument provided, populate from it
                if (args.Length > 0 && args[0].IsObject)
                {
                    var iterable = args[0].AsObject();
                    var lenVal = iterable?.Get("length");
                    if (lenVal != null && lenVal.IsNumber)
                    {
                        int len = (int)lenVal.ToNumber();
                        for (int i = 0; i < len; i++)
                        {
                            var val = iterable.Get(i.ToString());
                            set.Get("add")?.AsFunction()?.Invoke(new IValue[] { val ?? FenValue.Undefined }, _context);
                        }
                    }
                }
                return FenValue.FromObject(set);
            })));

            // WeakMap - Keys must be objects, values are weakly referenced
            SetGlobal("WeakMap", FenValue.FromFunction(new FenFunction("WeakMap", (args, thisVal) =>
            {
                var weakMap = new FenObject();
                var storage = new System.Runtime.CompilerServices.ConditionalWeakTable<object, IValue>();
                
                weakMap.Set("set", FenValue.FromFunction(new FenFunction("set", (setArgs, setThis) =>
                {
                    if (setArgs.Length > 0 && setArgs[0].IsObject)
                    {
                        var key = setArgs[0].AsObject();
                        var val = setArgs.Length > 1 ? setArgs[1] : FenValue.Undefined;
                        if (key != null) storage.AddOrUpdate(key, val);
                    }
                    return FenValue.FromObject(weakMap);
                })));
                
                weakMap.Set("get", FenValue.FromFunction(new FenFunction("get", (getArgs, getThis) =>
                {
                    if (getArgs.Length > 0 && getArgs[0].IsObject)
                    {
                        var key = getArgs[0].AsObject();
                        if (key != null && storage.TryGetValue(key, out var val))
                            return val;
                    }
                    return FenValue.Undefined;
                })));
                
                weakMap.Set("has", FenValue.FromFunction(new FenFunction("has", (hasArgs, hasThis) =>
                {
                    if (hasArgs.Length > 0 && hasArgs[0].IsObject)
                    {
                        var key = hasArgs[0].AsObject();
                        if (key != null && storage.TryGetValue(key, out _))
                            return FenValue.FromBoolean(true);
                    }
                    return FenValue.FromBoolean(false);
                })));
                
                weakMap.Set("delete", FenValue.FromFunction(new FenFunction("delete", (delArgs, delThis) =>
                {
                    if (delArgs.Length > 0 && delArgs[0].IsObject)
                    {
                        var key = delArgs[0].AsObject();
                        if (key != null) return FenValue.FromBoolean(storage.Remove(key));
                    }
                    return FenValue.FromBoolean(false);
                })));
                
                return FenValue.FromObject(weakMap);
            })));

            // WeakSet - Values must be objects, weakly referenced
            SetGlobal("WeakSet", FenValue.FromFunction(new FenFunction("WeakSet", (args, thisVal) =>
            {
                var weakSet = new FenObject();
                var storage = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
                
                weakSet.Set("add", FenValue.FromFunction(new FenFunction("add", (addArgs, addThis) =>
                {
                    if (addArgs.Length > 0 && addArgs[0].IsObject)
                    {
                        var val = addArgs[0].AsObject();
                        if (val != null) storage.AddOrUpdate(val, new object());
                    }
                    return FenValue.FromObject(weakSet);
                })));
                
                weakSet.Set("has", FenValue.FromFunction(new FenFunction("has", (hasArgs, hasThis) =>
                {
                    if (hasArgs.Length > 0 && hasArgs[0].IsObject)
                    {
                        var key = hasArgs[0].AsObject();
                        if (key != null && storage.TryGetValue(key, out _))
                            return FenValue.FromBoolean(true);
                    }
                    return FenValue.FromBoolean(false);
                })));
                
                weakSet.Set("delete", FenValue.FromFunction(new FenFunction("delete", (delArgs, delThis) =>
                {
                    if (delArgs.Length > 0 && delArgs[0].IsObject)
                    {
                        var key = delArgs[0].AsObject();
                        if (key != null) return FenValue.FromBoolean(storage.Remove(key));
                    }
                    return FenValue.FromBoolean(false);
                })));
                
                return FenValue.FromObject(weakSet);
            })));

            // ES6 Proxy constructor - Enables metaprogramming
            SetGlobal("Proxy", FenValue.FromFunction(new FenFunction("Proxy", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                
                var target = args[0].AsObject() as FenObject;
                var handlerVal = args[1].AsObject() as FenObject;
                
                if (target == null || handlerVal == null) return FenValue.Undefined;
                
                // Create a proxy object that intercepts operations
                var proxy = new FenObject();
                proxy.Set("__isProxy__", FenValue.FromBoolean(true));
                proxy.Set("__target__", FenValue.FromObject(target));
                proxy.Set("__handler__", FenValue.FromObject(handlerVal));
                
                // Override Get to use handler.get trap
                var originalGet = proxy.Get;
                // Note: FenObject doesn't support overriding Get directly
                // So we store a reference and provide helper methods
                
                proxy.Set("get", FenValue.FromFunction(new FenFunction("get", (getArgs, getThis) =>
                {
                    var prop = getArgs.Length > 0 ? getArgs[0].ToString() : "";
                    var getTrap = handlerVal.Get("get")?.AsFunction();
                    if (getTrap != null)
                    {
                        return getTrap.Invoke(new IValue[] { FenValue.FromObject(target), FenValue.FromString(prop), FenValue.FromObject(proxy) }, _context);
                    }
                    return target.Get(prop);
                })));
                
                proxy.Set("set", FenValue.FromFunction(new FenFunction("set", (setArgs, setThis) =>
                {
                    var prop = setArgs.Length > 0 ? setArgs[0].ToString() : "";
                    var val = setArgs.Length > 1 ? setArgs[1] : FenValue.Undefined;
                    var setTrap = handlerVal.Get("set")?.AsFunction();
                    if (setTrap != null)
                    {
                        return setTrap.Invoke(new IValue[] { FenValue.FromObject(target), FenValue.FromString(prop), val, FenValue.FromObject(proxy) }, _context);
                    }
                    target.Set(prop, val);
                    return FenValue.FromBoolean(true);
                })));
                
                proxy.Set("has", FenValue.FromFunction(new FenFunction("has", (hasArgs, hasThis) =>
                {
                    var prop = hasArgs.Length > 0 ? hasArgs[0].ToString() : "";
                    var hasTrap = handlerVal.Get("has")?.AsFunction();
                    if (hasTrap != null)
                    {
                        return hasTrap.Invoke(new IValue[] { FenValue.FromObject(target), FenValue.FromString(prop) }, _context);
                    }
                    return FenValue.FromBoolean(target.Get(prop) != null);
                })));
                
                proxy.Set("deleteProperty", FenValue.FromFunction(new FenFunction("deleteProperty", (delArgs, delThis) =>
                {
                    var prop = delArgs.Length > 0 ? delArgs[0].ToString() : "";
                    var delTrap = handlerVal.Get("deleteProperty")?.AsFunction();
                    if (delTrap != null)
                    {
                        return delTrap.Invoke(new IValue[] { FenValue.FromObject(target), FenValue.FromString(prop) }, _context);
                    }
                    target.Delete(prop);
                    return FenValue.FromBoolean(true);
                })));
                
                return FenValue.FromObject(proxy);
            })));

            // Reflect API is defined later in this file (around line 2550)

            // ES6 RegExp constructor - wraps .NET Regex
            SetGlobal("RegExp", FenValue.FromFunction(new FenFunction("RegExp", (args, thisVal) =>
            {
                var pattern = args.Length > 0 ? args[0].ToString() : "";
                var flags = args.Length > 1 ? args[1].ToString() : "";
                
                var regexObj = new FenObject();
                regexObj.Set("source", FenValue.FromString(pattern));
                regexObj.Set("flags", FenValue.FromString(flags));
                regexObj.Set("global", FenValue.FromBoolean(flags.Contains("g")));
                regexObj.Set("ignoreCase", FenValue.FromBoolean(flags.Contains("i")));
                regexObj.Set("multiline", FenValue.FromBoolean(flags.Contains("m")));
                regexObj.Set("dotAll", FenValue.FromBoolean(flags.Contains("s")));
                regexObj.Set("unicode", FenValue.FromBoolean(flags.Contains("u")));
                regexObj.Set("sticky", FenValue.FromBoolean(flags.Contains("y")));
                regexObj.Set("lastIndex", FenValue.FromNumber(0));
                
                // Build .NET RegexOptions
                var options = RegexOptions.ECMAScript;
                if (flags.Contains("i")) options |= RegexOptions.IgnoreCase;
                if (flags.Contains("m")) options |= RegexOptions.Multiline;
                if (flags.Contains("s")) options |= RegexOptions.Singleline;
                
                Regex regex = null;
                try { regex = new Regex(pattern, options); } catch { }
                
                regexObj.Set("test", FenValue.FromFunction(new FenFunction("test", (testArgs, testThis) =>
                {
                    if (testArgs.Length == 0 || regex == null) return FenValue.FromBoolean(false);
                    return FenValue.FromBoolean(regex.IsMatch(testArgs[0].ToString()));
                })));
                
                regexObj.Set("exec", FenValue.FromFunction(new FenFunction("exec", (execArgs, execThis) =>
                {
                    if (execArgs.Length == 0 || regex == null) return FenValue.Null;
                    var input = execArgs[0].ToString();
                    int startIndex = (int)(regexObj.Get("lastIndex")?.ToNumber() ?? 0);
                    bool isGlobal = regexObj.Get("global")?.ToBoolean() ?? false;
                    
                    if (startIndex >= input.Length) {
                        if (isGlobal) regexObj.Set("lastIndex", FenValue.FromNumber(0));
                        return FenValue.Null;
                    }
                    
                    var match = regex.Match(input, startIndex);
                    if (!match.Success) {
                        if (isGlobal) regexObj.Set("lastIndex", FenValue.FromNumber(0));
                        return FenValue.Null;
                    }
                    
                    var result = new FenObject();
                    result.Set("0", FenValue.FromString(match.Value));
                    for (int i = 1; i < match.Groups.Count; i++)
                        result.Set(i.ToString(), FenValue.FromString(match.Groups[i].Value));
                    result.Set("length", FenValue.FromNumber(match.Groups.Count));
                    result.Set("index", FenValue.FromNumber(match.Index));
                    result.Set("input", FenValue.FromString(input));
                    
                    if (isGlobal) regexObj.Set("lastIndex", FenValue.FromNumber(match.Index + match.Length));
                    
                    return FenValue.FromObject(result);
                })));
                
                regexObj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) =>
                    FenValue.FromString($"/{pattern}/{flags}"))));
                
                return FenValue.FromObject(regexObj);
            })));

            // ES6 Intl API - Internationalization (basic stubs)
            var intl = new FenObject();
            
            // Intl.DateTimeFormat
            intl.Set("DateTimeFormat", FenValue.FromFunction(new FenFunction("DateTimeFormat", (args, thisVal) =>
            {
                var locale = args.Length > 0 ? args[0].ToString() : "en-US";
                var formatter = new FenObject();
                formatter.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) =>
                {
                    if (fArgs.Length == 0) return FenValue.FromString("");
                    double timestamp = fArgs[0].ToNumber();
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp).DateTime;
                    return FenValue.FromString(dt.ToString("G", System.Globalization.CultureInfo.GetCultureInfo(locale)));
                })));
                formatter.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (a, t) =>
                {
                    var opts = new FenObject();
                    opts.Set("locale", FenValue.FromString(locale));
                    opts.Set("calendar", FenValue.FromString("gregory"));
                    opts.Set("timeZone", FenValue.FromString("UTC"));
                    return FenValue.FromObject(opts);
                })));
                return FenValue.FromObject(formatter);
            })));
            
            // Intl.NumberFormat
            intl.Set("NumberFormat", FenValue.FromFunction(new FenFunction("NumberFormat", (args, thisVal) =>
            {
                var locale = args.Length > 0 ? args[0].ToString() : "en-US";
                var formatter = new FenObject();
                formatter.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) =>
                {
                    if (fArgs.Length == 0) return FenValue.FromString("");
                    double num = fArgs[0].ToNumber();
                    return FenValue.FromString(num.ToString("N", System.Globalization.CultureInfo.GetCultureInfo(locale)));
                })));
                formatter.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (a, t) =>
                {
                    var opts = new FenObject();
                    opts.Set("locale", FenValue.FromString(locale));
                    opts.Set("style", FenValue.FromString("decimal"));
                    return FenValue.FromObject(opts);
                })));
                return FenValue.FromObject(formatter);
            })));
            
            // Intl.Collator
            intl.Set("Collator", FenValue.FromFunction(new FenFunction("Collator", (args, thisVal) =>
            {
                var locale = args.Length > 0 ? args[0].ToString() : "en-US";
                var collator = new FenObject();
                collator.Set("compare", FenValue.FromFunction(new FenFunction("compare", (cArgs, cThis) =>
                {
                    if (cArgs.Length < 2) return FenValue.FromNumber(0);
                    string a = cArgs[0].ToString(), b = cArgs[1].ToString();
                    return FenValue.FromNumber(string.Compare(a, b, StringComparison.CurrentCulture));
                })));
                return FenValue.FromObject(collator);
            })));
            
            SetGlobal("Intl", FenValue.FromObject(intl));

            // ES6 ArrayBuffer - Generic, fixed-length raw binary data buffer
            SetGlobal("ArrayBuffer", FenValue.FromFunction(new FenFunction("ArrayBuffer", (args, thisVal) =>
            {
                int length = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                var buffer = new FenObject();
                byte[] data = new byte[length];
                buffer.NativeObject = data;
                buffer.Set("byteLength", FenValue.FromNumber(length));
                
                buffer.Set("slice", FenValue.FromFunction(new FenFunction("slice", (sliceArgs, sliceThis) =>
                {
                    int start = sliceArgs.Length > 0 ? (int)sliceArgs[0].ToNumber() : 0;
                    int end = sliceArgs.Length > 1 ? (int)sliceArgs[1].ToNumber() : length;
                    if (start < 0) start = Math.Max(length + start, 0);
                    if (end < 0) end = Math.Max(length + end, 0);
                    int newLen = Math.Max(end - start, 0);
                    
                    var newBuffer = new FenObject();
                    byte[] newData = new byte[newLen];
                    Array.Copy(data, start, newData, 0, Math.Min(newLen, length - start));
                    newBuffer.NativeObject = newData;
                    newBuffer.Set("byteLength", FenValue.FromNumber(newLen));
                    return FenValue.FromObject(newBuffer);
                })));
                
                return FenValue.FromObject(buffer);
            })));

            // ES6 TypedArrays (Uint8Array, Int32Array, etc.)
            string[] typedArrayNames = { "Uint8Array", "Int8Array", "Uint16Array", "Int16Array", "Uint32Array", "Int32Array", "Float32Array", "Float64Array", "Uint8ClampedArray" };
            int[] typedArrayElementSizes = { 1, 1, 2, 2, 4, 4, 4, 8, 1 };

            for (int i = 0; i < typedArrayNames.Length; i++)
            {
                string name = typedArrayNames[i];
                int elementSize = typedArrayElementSizes[i];
                
                SetGlobal(name, FenValue.FromFunction(new FenFunction(name, (args, thisVal) =>
                {
                    FenObject bufferObj = null;
                    int byteOffset = 0;
                    int length = 0;
                    byte[] data = null;

                    if (args.Length > 0 && args[0].IsObject && args[0].AsObject()?.Get("byteLength") != null && args[0].AsObject()?.Get("byteLength")?.IsNumber == true)
                    {
                        // Constructor(buffer [, byteOffset [, length]])
                        bufferObj = args[0].AsObject() as FenObject;
                        data = bufferObj?.NativeObject as byte[];
                        byteOffset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                        int bufferByteLen = (int)(bufferObj?.Get("byteLength")?.ToNumber() ?? 0);
                        length = args.Length > 2 ? (int)args[2].ToNumber() : (bufferByteLen - byteOffset) / elementSize;
                    }
                    else if (args.Length > 0 && args[0].IsNumber)
                    {
                        // Constructor(length)
                        length = (int)args[0].ToNumber();
                        data = new byte[length * elementSize];
                        bufferObj = new FenObject();
                        bufferObj.NativeObject = data;
                        bufferObj.Set("byteLength", FenValue.FromNumber(data.Length));
                    }
                    else if (args.Length > 0 && args[0].IsObject)
                    {
                        // Constructor(typedArray) or Constructor(iterable)
                        var source = args[0].AsObject();
                        var lenVal = source?.Get("length");
                        length = lenVal != null ? (int)lenVal.ToNumber() : 0;
                        data = new byte[length * elementSize];
                        // Basic copy logic
                        for (int j = 0; j < length; j++)
                        {
                            double val = source.Get(j.ToString())?.ToNumber() ?? 0;
                            // Simplified: only handled as double for now
                        }
                        bufferObj = new FenObject();
                        bufferObj.NativeObject = data;
                        bufferObj.Set("byteLength", FenValue.FromNumber(data.Length));
                    }

                    var typedArray = new FenObject();
                    typedArray.Set("buffer", FenValue.FromObject(bufferObj));
                    typedArray.Set("byteOffset", FenValue.FromNumber(byteOffset));
                    typedArray.Set("byteLength", FenValue.FromNumber(length * elementSize));
                    typedArray.Set("length", FenValue.FromNumber(length));
                    typedArray.Set("BYTES_PER_ELEMENT", FenValue.FromNumber(elementSize));

                    typedArray.Set("get", FenValue.FromFunction(new FenFunction("get", (gArgs, gThis) =>
                    {
                        int idx = gArgs.Length > 0 ? (int)gArgs[0].ToNumber() : 0;
                        if (idx < 0 || idx >= length) return FenValue.Undefined;
                        return FenValue.FromNumber(0); // Placeholder result
                    })));

                    return FenValue.FromObject(typedArray);
                })));
            }

            // ES6 DataView - Low-level interface for reading/writing multiple number types in a binary ArrayBuffer
            SetGlobal("DataView", FenValue.FromFunction(new FenFunction("DataView", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;
                var bufferObj = args[0].AsObject() as FenObject;
                int byteOffset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                int byteLength = args.Length > 2 ? (int)args[2].ToNumber() : (int)(bufferObj?.Get("byteLength")?.ToNumber() ?? 0) - byteOffset;
                
                var view = new FenObject();
                view.Set("buffer", FenValue.FromObject(bufferObj));
                view.Set("byteOffset", FenValue.FromNumber(byteOffset));
                view.Set("byteLength", FenValue.FromNumber(byteLength));
                
                // Simplified getters/setters
                view.Set("getUint8", FenValue.FromFunction(new FenFunction("getUint8", (vArgs, vThis) => FenValue.FromNumber(0))));
                view.Set("setUint8", FenValue.FromFunction(new FenFunction("setUint8", (vArgs, vThis) => FenValue.Undefined)));
                
                return FenValue.FromObject(view);
            })));

            // ES6 URL and URLSearchParams - Part of Web API but essential for modern JS
            SetGlobal("URL", FenValue.FromFunction(new FenFunction("URL", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Null;
                string urlStr = args[0].ToString();
                string baseStr = args.Length > 1 ? args[1].ToString() : null;
                
                Uri uri;
                if (baseStr != null) Uri.TryCreate(new Uri(baseStr), urlStr, out uri);
                else Uri.TryCreate(urlStr, UriKind.RelativeOrAbsolute, out uri);
                
                if (uri == null) return FenValue.Null;
                
                var urlObj = new FenObject();
                urlObj.Set("href", FenValue.FromString(uri.AbsoluteUri));
                urlObj.Set("protocol", FenValue.FromString(uri.Scheme + ":"));
                urlObj.Set("host", FenValue.FromString(uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port)));
                urlObj.Set("hostname", FenValue.FromString(uri.Host));
                urlObj.Set("port", FenValue.FromString(uri.IsDefaultPort ? "" : uri.Port.ToString()));
                urlObj.Set("pathname", FenValue.FromString(uri.AbsolutePath));
                urlObj.Set("search", FenValue.FromString(uri.Query));
                urlObj.Set("hash", FenValue.FromString(uri.Fragment));
                urlObj.Set("origin", FenValue.FromString(uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port)));
                
                // searchParams
                var searchParams = new FenObject();
                // Basic manual parsing for searchParams to avoid HttpUtility dependency
                var queryStr = uri.Query.StartsWith("?") ? uri.Query.Substring(1) : uri.Query;
                var qp = queryStr.Split('&', StringSplitOptions.RemoveEmptyEntries);
                
                searchParams.Set("get", FenValue.FromFunction(new FenFunction("get", (spArgs, spThis) => {
                    string key = spArgs.Length > 0 ? spArgs[0].ToString() : "";
                    foreach(var p in qp) {
                        var kv = p.Split('=');
                        if (System.Net.WebUtility.UrlDecode(kv[0]) == key)
                            return FenValue.FromString(kv.Length > 1 ? System.Net.WebUtility.UrlDecode(kv[1]) : "");
                    }
                    return FenValue.Null;
                })));
                
                urlObj.Set("searchParams", FenValue.FromObject(searchParams));
                urlObj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString(uri.AbsoluteUri))));
                
                return FenValue.FromObject(urlObj);
            })));

            SetGlobal("URLSearchParams", FenValue.FromFunction(new FenFunction("URLSearchParams", (args, thisVal) =>
            {
                var sp = new FenObject();
                string query = args.Length > 0 ? args[0].ToString() : "";
                if (query.StartsWith("?")) query = query.Substring(1);
                var qpList = new List<KeyValuePair<string, string>>();
                foreach(var p in query.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
                    var kv = p.Split('=');
                    qpList.Add(new KeyValuePair<string, string>(System.Net.WebUtility.UrlDecode(kv[0]), kv.Length > 1 ? System.Net.WebUtility.UrlDecode(kv[1]) : ""));
                }
                
                sp.Set("get", FenValue.FromFunction(new FenFunction("get", (spArgs, spThis) => {
                    string key = spArgs.Length > 0 ? spArgs[0].ToString() : "";
                    var match = qpList.Find(x => x.Key == key);
                    return match.Key != null ? FenValue.FromString(match.Value) : FenValue.Null;
                })));
                sp.Set("has", FenValue.FromFunction(new FenFunction("has", (spArgs, spThis) => {
                    string key = spArgs.Length > 0 ? spArgs[0].ToString() : "";
                    return FenValue.FromBoolean(qpList.Exists(x => x.Key == key));
                })));
                sp.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => {
                    var sb = new StringBuilder();
                    foreach(var p in qpList) {
                        if (sb.Length > 0) sb.Append("&");
                        sb.Append(System.Net.WebUtility.UrlEncode(p.Key));
                        sb.Append("=");
                        sb.Append(System.Net.WebUtility.UrlEncode(p.Value));
                    }
                    return FenValue.FromString(sb.ToString());
                })));
                
                return FenValue.FromObject(sp);
            })));

            // ES6 Math Extensions
            var mathObj = (FenValue)GetGlobal("Math");
            if (mathObj.IsObject) {
                var m = mathObj.AsObject();
                m.Set("cbrt", FenValue.FromFunction(new FenFunction("cbrt", (args, thisVal) => 
                    FenValue.FromNumber(Math.Pow(args.Length > 0 ? args[0].ToNumber() : double.NaN, 1.0/3.0)))));
                m.Set("hypot", FenValue.FromFunction(new FenFunction("hypot", (args, thisVal) => {
                    double sum = 0;
                    foreach(var arg in args) { double n = arg.ToNumber(); sum += n * n; }
                    return FenValue.FromNumber(Math.Sqrt(sum));
                })));
                m.Set("log2", FenValue.FromFunction(new FenFunction("log2", (args, thisVal) => 
                    FenValue.FromNumber(Math.Log(args.Length > 0 ? args[0].ToNumber() : double.NaN, 2)))));
            }

            // Global functions: parseInt, parseFloat, isNaN, isFinite
            SetGlobal("parseInt", FenValue.FromFunction(new FenFunction("parseInt", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                var str = args[0].ToString().Trim();
                int radix = args.Length > 1 ? (int)args[1].ToNumber() : 10;
                if (radix == 0) radix = 10;
                if (radix < 2 || radix > 36) return FenValue.FromNumber(double.NaN);
                
                bool negative = false;
                if (str.StartsWith("-")) { negative = true; str = str.Substring(1); }
                else if (str.StartsWith("+")) { str = str.Substring(1); }
                
                if (radix == 16 && (str.StartsWith("0x") || str.StartsWith("0X"))) str = str.Substring(2);
                else if (radix == 10 && (str.StartsWith("0x") || str.StartsWith("0X"))) { radix = 16; str = str.Substring(2); }
                
                try {
                    long result = Convert.ToInt64(str, radix);
                    return FenValue.FromNumber(negative ? -result : result);
                } catch {
                    // Parse as much as possible
                    string validChars = "0123456789abcdefghijklmnopqrstuvwxyz".Substring(0, radix);
                    var sb = new StringBuilder();
                    foreach (char c in str.ToLowerInvariant()) {
                        if (validChars.Contains(c)) sb.Append(c);
                        else break;
                    }
                    if (sb.Length == 0) return FenValue.FromNumber(double.NaN);
                    try {
                        long result = Convert.ToInt64(sb.ToString(), radix);
                        return FenValue.FromNumber(negative ? -result : result);
                    } catch {
                        return FenValue.FromNumber(double.NaN);
                    }
                }
            })));

            SetGlobal("parseFloat", FenValue.FromFunction(new FenFunction("parseFloat", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                var str = args[0].ToString().Trim();
                // Parse leading numeric portion
                var sb = new StringBuilder();
                bool hasDecimal = false;
                bool hasExp = false;
                for (int i = 0; i < str.Length; i++) {
                    char c = str[i];
                    if (i == 0 && (c == '+' || c == '-')) { sb.Append(c); continue; }
                    if (char.IsDigit(c)) { sb.Append(c); continue; }
                    if (c == '.' && !hasDecimal && !hasExp) { hasDecimal = true; sb.Append(c); continue; }
                    if ((c == 'e' || c == 'E') && !hasExp && sb.Length > 0) {
                        hasExp = true; sb.Append(c);
                        if (i + 1 < str.Length && (str[i + 1] == '+' || str[i + 1] == '-')) { sb.Append(str[++i]); }
                        continue;
                    }
                    break;
                }
                if (sb.Length == 0 || sb.ToString() == "+" || sb.ToString() == "-") return FenValue.FromNumber(double.NaN);
                if (double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double result))
                    return FenValue.FromNumber(result);
                return FenValue.FromNumber(double.NaN);
            })));

            SetGlobal("isNaN", FenValue.FromFunction(new FenFunction("isNaN", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromBoolean(true);
                var num = args[0].ToNumber();
                return FenValue.FromBoolean(double.IsNaN(num));
            })));

            SetGlobal("isFinite", FenValue.FromFunction(new FenFunction("isFinite", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var num = args[0].ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num));
            })));

            // Number object with static methods
            var numberObj = new FenObject();
            numberObj.Set("isNaN", FenValue.FromFunction(new FenFunction("isNaN", (args, thisVal) => {
                if (args.Length == 0 || !args[0].IsNumber) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(double.IsNaN(args[0].ToNumber()));
            })));
            numberObj.Set("isFinite", FenValue.FromFunction(new FenFunction("isFinite", (args, thisVal) => {
                if (args.Length == 0 || !args[0].IsNumber) return FenValue.FromBoolean(false);
                var num = args[0].ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num));
            })));
            numberObj.Set("isInteger", FenValue.FromFunction(new FenFunction("isInteger", (args, thisVal) => {
                if (args.Length == 0 || !args[0].IsNumber) return FenValue.FromBoolean(false);
                var num = args[0].ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num) && Math.Floor(num) == num);
            })));
            numberObj.Set("parseInt", FenValue.FromFunction(new FenFunction("parseInt", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                var str = args[0].ToString().Trim();
                int radix = args.Length > 1 ? (int)args[1].ToNumber() : 10;
                if (radix == 0) radix = 10;
                if (radix < 2 || radix > 36) return FenValue.FromNumber(double.NaN);
                bool negative = str.StartsWith("-"); if (negative) str = str.Substring(1);
                if (str.StartsWith("+")) str = str.Substring(1);
                if ((str.StartsWith("0x") || str.StartsWith("0X"))) { if (radix == 10 || radix == 16) radix = 16; str = str.Substring(2); }
                try { long result = Convert.ToInt64(str, radix); return FenValue.FromNumber(negative ? -result : result); } catch { return FenValue.FromNumber(double.NaN); }
            })));
            numberObj.Set("parseFloat", FenValue.FromFunction(new FenFunction("parseFloat", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                if (double.TryParse(args[0].ToString().Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double result))
                    return FenValue.FromNumber(result);
                return FenValue.FromNumber(double.NaN);
            })));
            
            // Number constants
            numberObj.Set("MAX_VALUE", FenValue.FromNumber(double.MaxValue));
            numberObj.Set("MIN_VALUE", FenValue.FromNumber(double.Epsilon));
            numberObj.Set("NaN", FenValue.FromNumber(double.NaN));
            numberObj.Set("POSITIVE_INFINITY", FenValue.FromNumber(double.PositiveInfinity));
            numberObj.Set("NEGATIVE_INFINITY", FenValue.FromNumber(double.NegativeInfinity));
            numberObj.Set("MAX_SAFE_INTEGER", FenValue.FromNumber(9007199254740991));
            numberObj.Set("MIN_SAFE_INTEGER", FenValue.FromNumber(-9007199254740991));
            numberObj.Set("EPSILON", FenValue.FromNumber(2.220446049250313e-16));
            
            // Number.isSafeInteger(value)
            numberObj.Set("isSafeInteger", FenValue.FromFunction(new FenFunction("isSafeInteger", (args, thisVal) => {
                if (args.Length == 0 || !args[0].IsNumber) return FenValue.FromBoolean(false);
                var num = args[0].ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num) && Math.Floor(num) == num && Math.Abs(num) <= 9007199254740991);
            })));
            
            // Number prototype methods (toFixed, toPrecision, toExponential)
            // These will be accessed on number values
            numberObj.Set("prototype", FenValue.FromObject(new FenObject()));
            
            SetGlobal("Number", FenValue.FromObject(numberObj));
            
            // encodeURI / decodeURI
            SetGlobal("encodeURI", FenValue.FromFunction(new FenFunction("encodeURI", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromString("");
                var str = args[0].ToString();
                return FenValue.FromString(Uri.EscapeUriString(str));
            })));
            
            SetGlobal("decodeURI", FenValue.FromFunction(new FenFunction("decodeURI", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromString("");
                try { return FenValue.FromString(Uri.UnescapeDataString(args[0].ToString())); }
                catch { return FenValue.FromString(args[0].ToString()); }
            })));
            
            SetGlobal("encodeURIComponent", FenValue.FromFunction(new FenFunction("encodeURIComponent", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromString("");
                return FenValue.FromString(Uri.EscapeDataString(args[0].ToString()));
            })));
            
            SetGlobal("decodeURIComponent", FenValue.FromFunction(new FenFunction("decodeURIComponent", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromString("");
                try { return FenValue.FromString(Uri.UnescapeDataString(args[0].ToString())); }
                catch { return FenValue.FromString(args[0].ToString()); }
            })));
            
            // btoa / atob (Base64 encoding/decoding)
            SetGlobal("btoa", FenValue.FromFunction(new FenFunction("btoa", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromString("");
                var str = args[0].ToString();
                var bytes = System.Text.Encoding.UTF8.GetBytes(str);
                return FenValue.FromString(Convert.ToBase64String(bytes));
            })));
            
            SetGlobal("atob", FenValue.FromFunction(new FenFunction("atob", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromString("");
                try {
                    var bytes = Convert.FromBase64String(args[0].ToString());
                    return FenValue.FromString(System.Text.Encoding.UTF8.GetString(bytes));
                } catch { return FenValue.FromString(""); }
            })));
            
            // escape / unescape (deprecated but still used)
            SetGlobal("escape", FenValue.FromFunction(new FenFunction("escape", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromString("");
                return FenValue.FromString(Uri.EscapeDataString(args[0].ToString()));
            })));
            
            SetGlobal("unescape", FenValue.FromFunction(new FenFunction("unescape", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromString("");
                try { return FenValue.FromString(Uri.UnescapeDataString(args[0].ToString())); }
                catch { return FenValue.FromString(args[0].ToString()); }
            })));

            // Array object with static methods
            var arrayObj = new FenObject();
            arrayObj.Set("isArray", FenValue.FromFunction(new FenFunction("isArray", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                if (!args[0].IsObject) return FenValue.FromBoolean(false);
                var obj = args[0].AsObject();
                if (obj == null) return FenValue.FromBoolean(false);
                var length = obj.Get("length");
                return FenValue.FromBoolean(length != null && length.IsNumber);
            })));
            arrayObj.Set("from", FenValue.FromFunction(new FenFunction("from", (args, thisVal) => {
                var result = new FenObject();
                if (args.Length == 0) { result.Set("length", FenValue.FromNumber(0)); return FenValue.FromObject(result); }
                var source = args[0];
                FenFunction mapFn = args.Length > 1 ? args[1].AsFunction() : null;
                
                if (source.IsString) {
                    var str = source.ToString();
                    for (int i = 0; i < str.Length; i++) {
                        var val = FenValue.FromString(str[i].ToString());
                        result.Set(i.ToString(), mapFn != null ? mapFn.Invoke(new IValue[] { val, FenValue.FromNumber(i) }, null) : val);
                    }
                    result.Set("length", FenValue.FromNumber(str.Length));
                } else if (source.IsObject) {
                    var obj = source.AsObject();
                    var lenVal = obj.Get("length");
                    int len = lenVal != null && lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                    for (int i = 0; i < len; i++) {
                        var val = obj.Get(i.ToString()) ?? FenValue.Undefined;
                        result.Set(i.ToString(), mapFn != null ? mapFn.Invoke(new IValue[] { val, FenValue.FromNumber(i) }, null) : val);
                    }
                    result.Set("length", FenValue.FromNumber(len));
                }
                return FenValue.FromObject(result);
            })));
            arrayObj.Set("of", FenValue.FromFunction(new FenFunction("of", (args, thisVal) => {
                var result = new FenObject();
                for (int i = 0; i < args.Length; i++) {
                    result.Set(i.ToString(), args[i]);
                }
                result.Set("length", FenValue.FromNumber(args.Length));
                return FenValue.FromObject(result);
            })));
            SetGlobal("Array", FenValue.FromObject(arrayObj));

            // Date object
            var dateProto = new FenObject();
            dateProto.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromString(dt.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT'K"));
                return FenValue.FromString("Invalid Date");
            })));
            dateProto.Set("toISOString", FenValue.FromFunction(new FenFunction("toISOString", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromString(dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
                return new ErrorValue("Invalid Date");
            })));
            dateProto.Set("getTime", FenValue.FromFunction(new FenFunction("getTime", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber((dt.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getFullYear", FenValue.FromFunction(new FenFunction("getFullYear", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.Year);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getMonth", FenValue.FromFunction(new FenFunction("getMonth", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.Month - 1); // JS months are 0-11
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getDate", FenValue.FromFunction(new FenFunction("getDate", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.Day);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getDay", FenValue.FromFunction(new FenFunction("getDay", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber((int)dt.DayOfWeek);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getHours", FenValue.FromFunction(new FenFunction("getHours", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.Hour);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getMinutes", FenValue.FromFunction(new FenFunction("getMinutes", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.Minute);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getSeconds", FenValue.FromFunction(new FenFunction("getSeconds", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.Second);
                return FenValue.FromNumber(double.NaN);
            })));
            
            // getMilliseconds
            dateProto.Set("getMilliseconds", FenValue.FromFunction(new FenFunction("getMilliseconds", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.Millisecond);
                return FenValue.FromNumber(double.NaN);
            })));
            
            // getTimezoneOffset
            dateProto.Set("getTimezoneOffset", FenValue.FromFunction(new FenFunction("getTimezoneOffset", (args, thisVal) => {
                return FenValue.FromNumber(-TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes);
            })));
            
            // UTC getters
            dateProto.Set("getUTCFullYear", FenValue.FromFunction(new FenFunction("getUTCFullYear", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.ToUniversalTime().Year);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getUTCMonth", FenValue.FromFunction(new FenFunction("getUTCMonth", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.ToUniversalTime().Month - 1);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getUTCDate", FenValue.FromFunction(new FenFunction("getUTCDate", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.ToUniversalTime().Day);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getUTCDay", FenValue.FromFunction(new FenFunction("getUTCDay", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber((int)dt.ToUniversalTime().DayOfWeek);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getUTCHours", FenValue.FromFunction(new FenFunction("getUTCHours", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.ToUniversalTime().Hour);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getUTCMinutes", FenValue.FromFunction(new FenFunction("getUTCMinutes", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.ToUniversalTime().Minute);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getUTCSeconds", FenValue.FromFunction(new FenFunction("getUTCSeconds", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.ToUniversalTime().Second);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("getUTCMilliseconds", FenValue.FromFunction(new FenFunction("getUTCMilliseconds", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber(dt.ToUniversalTime().Millisecond);
                return FenValue.FromNumber(double.NaN);
            })));
            
            // Set methods
            dateProto.Set("setTime", FenValue.FromFunction(new FenFunction("setTime", (args, thisVal) => {
                if (thisVal.IsObject && thisVal.AsObject() is FenObject fenObj && args.Length > 0) {
                    var ms = args[0].ToNumber();
                    var newDt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime();
                    fenObj.NativeObject = newDt;
                    return FenValue.FromNumber(ms);
                }
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("setFullYear", FenValue.FromFunction(new FenFunction("setFullYear", (args, thisVal) => {
                if (thisVal.IsObject && thisVal.AsObject() is FenObject fenObj && fenObj.NativeObject is DateTime dt && args.Length > 0) {
                    int year = (int)args[0].ToNumber();
                    int month = args.Length > 1 ? (int)args[1].ToNumber() + 1 : dt.Month;
                    int day = args.Length > 2 ? (int)args[2].ToNumber() : dt.Day;
                    try {
                        var newDt = new DateTime(year, month, day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond, dt.Kind);
                        fenObj.NativeObject = newDt;
                        return FenValue.FromNumber((newDt.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
                    } catch { return FenValue.FromNumber(double.NaN); }
                }
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("setMonth", FenValue.FromFunction(new FenFunction("setMonth", (args, thisVal) => {
                if (thisVal.IsObject && thisVal.AsObject() is FenObject fenObj && fenObj.NativeObject is DateTime dt && args.Length > 0) {
                    int month = (int)args[0].ToNumber() + 1;
                    int day = args.Length > 1 ? (int)args[1].ToNumber() : dt.Day;
                    try {
                        var newDt = new DateTime(dt.Year, month, day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond, dt.Kind);
                        fenObj.NativeObject = newDt;
                        return FenValue.FromNumber((newDt.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
                    } catch { return FenValue.FromNumber(double.NaN); }
                }
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("setDate", FenValue.FromFunction(new FenFunction("setDate", (args, thisVal) => {
                if (thisVal.IsObject && thisVal.AsObject() is FenObject fenObj && fenObj.NativeObject is DateTime dt && args.Length > 0) {
                    int day = (int)args[0].ToNumber();
                    try {
                        var newDt = new DateTime(dt.Year, dt.Month, day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond, dt.Kind);
                        fenObj.NativeObject = newDt;
                        return FenValue.FromNumber((newDt.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
                    } catch { return FenValue.FromNumber(double.NaN); }
                }
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("setHours", FenValue.FromFunction(new FenFunction("setHours", (args, thisVal) => {
                if (thisVal.IsObject && thisVal.AsObject() is FenObject fenObj && fenObj.NativeObject is DateTime dt && args.Length > 0) {
                    int hours = (int)args[0].ToNumber();
                    int minutes = args.Length > 1 ? (int)args[1].ToNumber() : dt.Minute;
                    int seconds = args.Length > 2 ? (int)args[2].ToNumber() : dt.Second;
                    int ms = args.Length > 3 ? (int)args[3].ToNumber() : dt.Millisecond;
                    try {
                        var newDt = new DateTime(dt.Year, dt.Month, dt.Day, hours, minutes, seconds, ms, dt.Kind);
                        fenObj.NativeObject = newDt;
                        return FenValue.FromNumber((newDt.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
                    } catch { return FenValue.FromNumber(double.NaN); }
                }
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("setMinutes", FenValue.FromFunction(new FenFunction("setMinutes", (args, thisVal) => {
                if (thisVal.IsObject && thisVal.AsObject() is FenObject fenObj && fenObj.NativeObject is DateTime dt && args.Length > 0) {
                    int minutes = (int)args[0].ToNumber();
                    int seconds = args.Length > 1 ? (int)args[1].ToNumber() : dt.Second;
                    int ms = args.Length > 2 ? (int)args[2].ToNumber() : dt.Millisecond;
                    try {
                        var newDt = new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, minutes, seconds, ms, dt.Kind);
                        fenObj.NativeObject = newDt;
                        return FenValue.FromNumber((newDt.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
                    } catch { return FenValue.FromNumber(double.NaN); }
                }
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("setSeconds", FenValue.FromFunction(new FenFunction("setSeconds", (args, thisVal) => {
                if (thisVal.IsObject && thisVal.AsObject() is FenObject fenObj && fenObj.NativeObject is DateTime dt && args.Length > 0) {
                    int seconds = (int)args[0].ToNumber();
                    int ms = args.Length > 1 ? (int)args[1].ToNumber() : dt.Millisecond;
                    try {
                        var newDt = new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, seconds, ms, dt.Kind);
                        fenObj.NativeObject = newDt;
                        return FenValue.FromNumber((newDt.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
                    } catch { return FenValue.FromNumber(double.NaN); }
                }
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("setMilliseconds", FenValue.FromFunction(new FenFunction("setMilliseconds", (args, thisVal) => {
                if (thisVal.IsObject && thisVal.AsObject() is FenObject fenObj && fenObj.NativeObject is DateTime dt && args.Length > 0) {
                    int ms = (int)args[0].ToNumber();
                    try {
                        var newDt = new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, ms, dt.Kind);
                        fenObj.NativeObject = newDt;
                        return FenValue.FromNumber((newDt.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
                    } catch { return FenValue.FromNumber(double.NaN); }
                }
                return FenValue.FromNumber(double.NaN);
            })));
            
            // Date formatting methods
            dateProto.Set("toDateString", FenValue.FromFunction(new FenFunction("toDateString", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromString(dt.ToString("ddd MMM dd yyyy"));
                return FenValue.FromString("Invalid Date");
            })));
            dateProto.Set("toTimeString", FenValue.FromFunction(new FenFunction("toTimeString", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromString(dt.ToString("HH:mm:ss 'GMT'K"));
                return FenValue.FromString("Invalid Date");
            })));
            dateProto.Set("toLocaleDateString", FenValue.FromFunction(new FenFunction("toLocaleDateString", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromString(dt.ToShortDateString());
                return FenValue.FromString("Invalid Date");
            })));
            dateProto.Set("toLocaleTimeString", FenValue.FromFunction(new FenFunction("toLocaleTimeString", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromString(dt.ToShortTimeString());
                return FenValue.FromString("Invalid Date");
            })));
            dateProto.Set("toLocaleString", FenValue.FromFunction(new FenFunction("toLocaleString", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromString(dt.ToString());
                return FenValue.FromString("Invalid Date");
            })));
            dateProto.Set("toUTCString", FenValue.FromFunction(new FenFunction("toUTCString", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromString(dt.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'"));
                return FenValue.FromString("Invalid Date");
            })));
            dateProto.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromNumber((dt.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
                return FenValue.FromNumber(double.NaN);
            })));
            dateProto.Set("toJSON", FenValue.FromFunction(new FenFunction("toJSON", (args, thisVal) => {
                if (thisVal.IsObject && (thisVal.AsObject() as FenObject)?.NativeObject is DateTime dt)
                    return FenValue.FromString(dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
                return FenValue.Null;
            })));

            var dateCtor = new FenFunction("Date", (args, thisVal) => {
                DateTime dt;
                if (args.Length == 0) dt = DateTime.Now;
                else if (args.Length == 1)
                {
                    var arg = args[0];
                    if (arg.IsNumber) dt = new DateTime(1970, 1, 1).AddMilliseconds(arg.ToNumber());
                    else if (DateTime.TryParse(arg.ToString(), out var parsed)) dt = parsed;
                    else dt = DateTime.Now;
                }
                else dt = DateTime.Now; // Simplified for multiple args

                var obj = new FenObject();
                obj.NativeObject = dt;
                obj.SetPrototype(dateProto);
                return FenValue.FromObject(obj);
            });
            
            // Create Date as a callable object with static methods
            var dateObj = new FenObject();
            dateObj.NativeObject = dateCtor; // Store the constructor as callable
            dateObj.Set("now", FenValue.FromFunction(new FenFunction("now", (args, thisVal) => 
                FenValue.FromNumber((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds))));
            dateObj.Set("parse", FenValue.FromFunction(new FenFunction("parse", (args, thisVal) => {
                if (args.Length > 0 && DateTime.TryParse(args[0].ToString(), out var d))
                    return FenValue.FromNumber((d.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
                return FenValue.FromNumber(double.NaN);
            })));

            /* [PERF-REMOVED] */
            SetGlobal("Date", FenValue.FromObject(dateObj));

            // JSON object
            var json = new FenObject();
            json.Set("parse", FenValue.FromFunction(new FenFunction("parse", (args, thisVal) => {
                if (args.Length == 0) return new ErrorValue("JSON.parse: no argument");
                try
                {
                    var jsonString = args[0].ToString();
                    using var doc = JsonDocument.Parse(jsonString);
                    var result = ConvertJsonElement(doc.RootElement);
                    
                    // Support reviver function (second argument)
                    if (args.Length > 1 && args[1].IsFunction)
                    {
                        var reviver = args[1].AsFunction() as FenFunction;
                        if (reviver != null && result.IsObject)
                        {
                            result = ApplyReviver((FenValue)result, reviver, "");
                        }
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    return new ErrorValue($"JSON.parse error: {ex.Message}");
                }
            })));
            json.Set("stringify", FenValue.FromFunction(new FenFunction("stringify", (args, thisVal) => {
                if (args.Length == 0) return FenValue.Undefined;
                try
                {
                    // Support replacer function (second argument) and space (third argument)
                    FenFunction replacer = null;
                    string[] replacerArray = null;
                    int spaces = 0;
                    
                    if (args.Length > 1 && !args[1].IsNull && !args[1].IsUndefined)
                    {
                        if (args[1].IsFunction)
                            replacer = args[1].AsFunction() as FenFunction;
                        else if (args[1].IsObject)
                        {
                            var arr = args[1].AsObject();
                            var len = arr?.Get("length");
                            if (len != null && len.IsNumber)
                            {
                                var keys = new List<string>();
                                for (int i = 0; i < (int)len.ToNumber(); i++)
                                {
                                    var item = arr.Get(i.ToString());
                                    if (item != null) keys.Add(item.ToString());
                                }
                                replacerArray = keys.ToArray();
                            }
                        }
                    }
                    
                    if (args.Length > 2)
                    {
                        if (args[2].IsNumber)
                            spaces = Math.Min(10, Math.Max(0, (int)args[2].ToNumber()));
                        else if (args[2].IsString)
                            spaces = Math.Min(10, args[2].ToString().Length);
                    }
                    
                    return FenValue.FromString(ConvertToJsonStringWithReplacer(args[0], replacer, replacerArray, spaces, ""));
                }
                catch (Exception ex)
                {
                    return new ErrorValue($"JSON.stringify error: {ex.Message}");
                }
            })));
            /* [PERF-REMOVED] */
            SetGlobal("JSON", FenValue.FromObject(json));

            // Object global - provides static methods like Object.keys(), Object.values(), etc.
            var objectConstructor = new FenObject();
            objectConstructor.Set("keys", FenValue.FromFunction(new FenFunction("keys", (args, thisVal) => {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromObject(CreateArray(new string[0]));
                var obj = args[0].AsObject();
                var keys = obj.Keys().ToArray();
                return FenValue.FromObject(CreateArray(keys));
            })));
            objectConstructor.Set("values", FenValue.FromFunction(new FenFunction("values", (args, thisVal) => {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromObject(CreateArray(new string[0]));
                var obj = args[0].AsObject();
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(obj.Keys().Count()));
                int i = 0;
                foreach (var key in obj.Keys())
                {
                    arr.Set(i.ToString(), (FenValue)obj.Get(key));
                    i++;
                }
                return FenValue.FromObject(arr);
            })));
            objectConstructor.Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) => {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromObject(CreateArray(new string[0]));
                var obj = args[0].AsObject();
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(obj.Keys().Count()));
                int i = 0;
                foreach (var key in obj.Keys())
                {
                    var entry = new FenObject();
                    entry.Set("0", FenValue.FromString(key));
                    entry.Set("1", (FenValue)obj.Get(key));
                    entry.Set("length", FenValue.FromNumber(2));
                    arr.Set(i.ToString(), FenValue.FromObject(entry));
                    i++;
                }
                return FenValue.FromObject(arr);
            })));
            objectConstructor.Set("assign", FenValue.FromFunction(new FenFunction("assign", (args, thisVal) => {
                if (args.Length == 0) return FenValue.Undefined;
                if (!args[0].IsObject) return args[0];
                var target = args[0].AsObject();
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i].IsObject)
                    {
                        var source = args[i].AsObject();
                        foreach (var key in source.Keys())
                        {
                            target.Set(key, (FenValue)source.Get(key));
                        }
                    }
                }
                return args[0];
            })));
            objectConstructor.Set("hasOwnProperty", FenValue.FromFunction(new FenFunction("hasOwnProperty", (args, thisVal) => {
                // This is typically called on instances, but Object.hasOwnProperty.call(obj, key) exists
                return FenValue.FromBoolean(false);
            })));
            
            // Object.create(proto, properties)
            objectConstructor.Set("create", FenValue.FromFunction(new FenFunction("create", (args, thisVal) => {
                var obj = new FenObject();
                if (args.Length > 0 && args[0].IsObject)
                {
                    obj.SetPrototype(args[0].AsObject() as FenObject);
                }
                if (args.Length > 1 && args[1].IsObject)
                {
                    var props = args[1].AsObject();
                    foreach (var key in props.Keys())
                    {
                        var descriptor = props.Get(key);
                        if (descriptor != null && descriptor.IsObject)
                        {
                            var descObj = descriptor.AsObject();
                            var value = descObj?.Get("value");
                            if (value != null) obj.Set(key, (FenValue)value);
                        }
                    }
                }
                return FenValue.FromObject(obj);
            })));
            
            // Object.freeze(obj)
            objectConstructor.Set("freeze", FenValue.FromFunction(new FenFunction("freeze", (args, thisVal) => {
                if (args.Length > 0) return args[0]; // Return same object (freeze not fully enforced)
                return FenValue.Undefined;
            })));
            
            // Object.seal(obj)
            objectConstructor.Set("seal", FenValue.FromFunction(new FenFunction("seal", (args, thisVal) => {
                if (args.Length > 0) return args[0];
                return FenValue.Undefined;
            })));
            
            // Object.isFrozen(obj)
            objectConstructor.Set("isFrozen", FenValue.FromFunction(new FenFunction("isFrozen", (args, thisVal) => FenValue.FromBoolean(false))));
            
            // Object.isSealed(obj)
            objectConstructor.Set("isSealed", FenValue.FromFunction(new FenFunction("isSealed", (args, thisVal) => FenValue.FromBoolean(false))));
            
            // Object.fromEntries(iterable)
            objectConstructor.Set("fromEntries", FenValue.FromFunction(new FenFunction("fromEntries", (args, thisVal) => {
                var result = new FenObject();
                if (args.Length > 0 && args[0].IsObject)
                {
                    var entries = args[0].AsObject();
                    var lenVal = entries?.Get("length");
                    int len = lenVal != null && lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                    for (int i = 0; i < len; i++)
                    {
                        var entry = entries.Get(i.ToString());
                        if (entry != null && entry.IsObject)
                        {
                            var entryObj = entry.AsObject();
                            var key = entryObj?.Get("0");
                            var value = entryObj?.Get("1");
                            if (key != null) result.Set(key.ToString(), value != null ? (FenValue)value : FenValue.Undefined);
                        }
                    }
                }
                return FenValue.FromObject(result);
            })));
            
            // Object.getPrototypeOf(obj)
            objectConstructor.Set("getPrototypeOf", FenValue.FromFunction(new FenFunction("getPrototypeOf", (args, thisVal) => {
                if (args.Length > 0 && args[0].IsObject && args[0].AsObject() is FenObject fenObj)
                {
                    var proto = fenObj.GetPrototype();
                    return proto != null ? FenValue.FromObject(proto) : FenValue.Null;
                }
                return FenValue.Null;
            })));
            
            // Object.setPrototypeOf(obj, proto)
            objectConstructor.Set("setPrototypeOf", FenValue.FromFunction(new FenFunction("setPrototypeOf", (args, thisVal) => {
                if (args.Length > 1 && args[0].IsObject && args[0].AsObject() is FenObject fenObj)
                {
                    if (args[1].IsObject && args[1].AsObject() is FenObject proto)
                        fenObj.SetPrototype(proto);
                    else if (args[1].IsNull)
                        fenObj.SetPrototype(null);
                }
                return args.Length > 0 ? args[0] : FenValue.Undefined;
            })));
            
            // Object.getOwnPropertyNames(obj)
            objectConstructor.Set("getOwnPropertyNames", FenValue.FromFunction(new FenFunction("getOwnPropertyNames", (args, thisVal) => {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromObject(CreateArray(new string[0]));
                var obj = args[0].AsObject();
                var keys = obj.Keys().ToArray();
                return FenValue.FromObject(CreateArray(keys));
            })));
            
            // Object.defineProperty(obj, prop, descriptor)
            objectConstructor.Set("defineProperty", FenValue.FromFunction(new FenFunction("defineProperty", (args, thisVal) => {
                if (args.Length < 3 || !args[0].IsObject) return args.Length > 0 ? args[0] : FenValue.Undefined;
                var obj = args[0].AsObject();
                var prop = args[1].ToString();
                if (args[2].IsObject)
                {
                    var desc = args[2].AsObject();
                    var value = desc?.Get("value");
                    if (value != null) obj.Set(prop, (FenValue)value);
                }
                return args[0];
            })));
            
            // Object.defineProperties(obj, props)
            objectConstructor.Set("defineProperties", FenValue.FromFunction(new FenFunction("defineProperties", (args, thisVal) => {
                if (args.Length < 2 || !args[0].IsObject || !args[1].IsObject) return args.Length > 0 ? args[0] : FenValue.Undefined;
                var obj = args[0].AsObject();
                var props = args[1].AsObject();
                foreach (var key in props.Keys())
                {
                    var desc = props.Get(key);
                    if (desc != null && desc.IsObject)
                    {
                        var value = desc.AsObject()?.Get("value");
                        if (value != null) obj.Set(key, (FenValue)value);
                    }
                }
                return args[0];
            })));
            
            /* [PERF-REMOVED] */
            SetGlobal("Object", FenValue.FromObject(objectConstructor));

            // Symbol - ES6 primitive type for unique identifiers
            var symbolCounter = 0;
            var symbolRegistry = new Dictionary<string, FenValue>();
            var symbolConstructor = new FenObject();
            
            // Symbol() - create new unique symbol
            SetGlobal("Symbol", FenValue.FromFunction(new FenFunction("Symbol", (args, thisVal) =>
            {
                var description = args.Length > 0 ? args[0].ToString() : "";
                var symbolId = Interlocked.Increment(ref symbolCounter);
                var symbol = new FenObject();
                symbol.Set("__isSymbol__", FenValue.FromBoolean(true));
                symbol.Set("__symbolId__", FenValue.FromNumber(symbolId));
                symbol.Set("description", FenValue.FromString(description));
                symbol.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString($"Symbol({description})"))));
                symbol.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (a, t) => FenValue.FromObject(symbol))));
                return FenValue.FromObject(symbol);
            })));
            /* [PERF-REMOVED] */
            
            // Symbol.for(key) - get or create symbol in global registry
            symbolConstructor.Set("for", FenValue.FromFunction(new FenFunction("for", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0].ToString() : "";
                if (symbolRegistry.TryGetValue(key, out var existing))
                    return existing;
                var symbolId = Interlocked.Increment(ref symbolCounter);
                var symbol = new FenObject();
                symbol.Set("__isSymbol__", FenValue.FromBoolean(true));
                symbol.Set("__symbolId__", FenValue.FromNumber(symbolId));
                symbol.Set("__registryKey__", FenValue.FromString(key));
                symbol.Set("description", FenValue.FromString(key));
                symbol.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString($"Symbol({key})"))));
                var symbolVal = FenValue.FromObject(symbol);
                symbolRegistry[key] = symbolVal;
                return symbolVal;
            })));
            
            // Symbol.keyFor(sym) - get key for registered symbol
            symbolConstructor.Set("keyFor", FenValue.FromFunction(new FenFunction("keyFor", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.Undefined;
                var sym = args[0].AsObject();
                var registryKey = sym?.Get("__registryKey__");
                if (registryKey != null && !registryKey.IsUndefined)
                    return registryKey;
                return FenValue.Undefined;
            })));
            
            // Well-known symbols (represented as unique objects)
            var iteratorSymbol = new FenObject();
            iteratorSymbol.Set("__isSymbol__", FenValue.FromBoolean(true));
            iteratorSymbol.Set("__symbolId__", FenValue.FromNumber(-1));
            iteratorSymbol.Set("description", FenValue.FromString("Symbol.iterator"));
            symbolConstructor.Set("iterator", FenValue.FromObject(iteratorSymbol));
            
            var toStringTagSymbol = new FenObject();
            toStringTagSymbol.Set("__isSymbol__", FenValue.FromBoolean(true));
            toStringTagSymbol.Set("__symbolId__", FenValue.FromNumber(-2));
            toStringTagSymbol.Set("description", FenValue.FromString("Symbol.toStringTag"));
            symbolConstructor.Set("toStringTag", FenValue.FromObject(toStringTagSymbol));
            
            var hasInstanceSymbol = new FenObject();
            hasInstanceSymbol.Set("__isSymbol__", FenValue.FromBoolean(true));
            hasInstanceSymbol.Set("__symbolId__", FenValue.FromNumber(-3));
            hasInstanceSymbol.Set("description", FenValue.FromString("Symbol.hasInstance"));
            symbolConstructor.Set("hasInstance", FenValue.FromObject(hasInstanceSymbol));
            
            var isConcatSpreadableSymbol = new FenObject();
            isConcatSpreadableSymbol.Set("__isSymbol__", FenValue.FromBoolean(true));
            isConcatSpreadableSymbol.Set("__symbolId__", FenValue.FromNumber(-4));
            isConcatSpreadableSymbol.Set("description", FenValue.FromString("Symbol.isConcatSpreadable"));
            symbolConstructor.Set("isConcatSpreadable", FenValue.FromObject(isConcatSpreadableSymbol));
            
            // Copy static methods to Symbol function object
            var symbolGlobal = (FenValue)GetGlobal("Symbol");
            if (symbolGlobal.IsFunction)
            {
                // Attach static methods to the function
                // Already set above via symbolConstructor reference
            }
            
            /* [PERF-REMOVED] */
            // Reflect - provides methods for interceptable JavaScript operations
            var reflectObj = new FenObject();
            
            // Reflect.get(target, propertyKey)
            reflectObj.Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject) return FenValue.Undefined;
                var target = args[0].AsObject();
                var key = args[1].ToString();
                var result = target?.Get(key);
                return result != null ? (FenValue)result : FenValue.Undefined;
            })));
            /* [PERF-REMOVED] */
            
            // Reflect.set(target, propertyKey, value)
            reflectObj.Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) =>
            {
                if (args.Length < 3 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var target = args[0].AsObject();
                var key = args[1].ToString();
                target?.Set(key, args[2]);
                return FenValue.FromBoolean(true);
            })));
            
            // Reflect.has(target, propertyKey)
            reflectObj.Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var target = args[0].AsObject();
                var key = args[1].ToString();
                return FenValue.FromBoolean(target?.Get(key) != null);
            })));
            
            // Reflect.deleteProperty(target, propertyKey)
            reflectObj.Set("deleteProperty", FenValue.FromFunction(new FenFunction("deleteProperty", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var target = args[0].AsObject() as FenObject;
                var key = args[1].ToString();
                return FenValue.FromBoolean(target?.Delete(key) ?? false);
            })));
            
            // Reflect.ownKeys(target)
            reflectObj.Set("ownKeys", FenValue.FromFunction(new FenFunction("ownKeys", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromObject(CreateArray(new string[0]));
                var target = args[0].AsObject();
                var keys = target.Keys().ToArray();
                return FenValue.FromObject(CreateArray(keys));
            })));
            /* [PERF-REMOVED] */
            
            // Reflect.apply(target, thisArgument, argumentsList)
            reflectObj.Set("apply", FenValue.FromFunction(new FenFunction("apply", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var fn = args[0].AsFunction() as FenFunction;
                var argsList = new List<IValue>();
                if (args.Length > 2 && args[2].IsObject)
                {
                    var argsArr = args[2].AsObject();
                    var len = argsArr?.Get("length");
                    int count = len != null && len.IsNumber ? (int)len.ToNumber() : 0;
                    for (int i = 0; i < count; i++)
                    {
                        var item = argsArr.Get(i.ToString());
                        argsList.Add(item ?? FenValue.Undefined);
                    }
                }
                return (FenValue)(fn?.Invoke(argsList.ToArray(), null) ?? FenValue.Undefined);
            })));
            
            // Reflect.construct(target, argumentsList)
            reflectObj.Set("construct", FenValue.FromFunction(new FenFunction("construct", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var fn = args[0].AsFunction() as FenFunction;
                var argsList = new List<IValue>();
                if (args.Length > 1 && args[1].IsObject)
                {
                    var argsArr = args[1].AsObject();
                    var len = argsArr?.Get("length");
                    int count = len != null && len.IsNumber ? (int)len.ToNumber() : 0;
                    for (int i = 0; i < count; i++)
                    {
                        var item = argsArr.Get(i.ToString());
                        argsList.Add(item ?? FenValue.Undefined);
                    }
                }
                return (FenValue)(fn?.Invoke(argsList.ToArray(), null) ?? FenValue.Undefined);
            })));
            
            // Reflect.getPrototypeOf(target)
            reflectObj.Set("getPrototypeOf", FenValue.FromFunction(new FenFunction("getPrototypeOf", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsObject && args[0].AsObject() is FenObject fenObj)
                {
                    var proto = fenObj.GetPrototype();
                    return proto != null ? FenValue.FromObject(proto) : FenValue.Null;
                }
                return FenValue.Null;
            })));
            
            // Reflect.setPrototypeOf(target, proto)
            reflectObj.Set("setPrototypeOf", FenValue.FromFunction(new FenFunction("setPrototypeOf", (args, thisVal) =>
            {
                if (args.Length > 1 && args[0].IsObject && args[0].AsObject() is FenObject fenObj)
                {
                    if (args[1].IsObject && args[1].AsObject() is FenObject proto)
                    {
                        fenObj.SetPrototype(proto);
                        return FenValue.FromBoolean(true);
                    }
                    else if (args[1].IsNull)
                    {
                        fenObj.SetPrototype(null);
                        return FenValue.FromBoolean(true);
                    }
                }
                return FenValue.FromBoolean(false);
            })));
            
            /* [PERF-REMOVED] */
            SetGlobal("Reflect", FenValue.FromObject(reflectObj));

            // Proxy - Meta-programming proxy objects
            SetGlobal("Proxy", FenValue.FromFunction(new FenFunction("Proxy", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject || !args[1].IsObject)
                    return new ErrorValue("Proxy requires target and handler objects");
                
                var target = args[0].AsObject() as FenObject;
                var handler = args[1].AsObject() as FenObject;
                if (target == null || handler == null)
                    return new ErrorValue("Proxy requires valid target and handler");
                
                var proxy = new FenObject();
                proxy.Set("__isProxy__", FenValue.FromBoolean(true));
                proxy.Set("__target__", FenValue.FromObject(target));
                proxy.Set("__handler__", FenValue.FromObject(handler));
                
                // Proxy get trap
                var getTrap = handler.Get("get");
                var setTrap = handler.Get("set");
                var hasTrap = handler.Get("has");
                var deletePropertyTrap = handler.Get("deleteProperty");
                var ownKeysTrap = handler.Get("ownKeys");
                var applyTrap = handler.Get("apply");

                // Store traps on proxy for FenObject to find
                if (getTrap.IsFunction) proxy.Set("__proxyGet__", getTrap);
                if (setTrap.IsFunction) proxy.Set("__proxySet__", setTrap);
                if (hasTrap.IsFunction) proxy.Set("__proxyHas__", hasTrap);
                if (deletePropertyTrap.IsFunction) proxy.Set("__proxyDelete__", deletePropertyTrap);
                if (ownKeysTrap.IsFunction) proxy.Set("__proxyOwnKeys__", ownKeysTrap);
                if (applyTrap.IsFunction) proxy.Set("__proxyApply__", applyTrap);

                return FenValue.FromObject(proxy);
            })));
            /* [PERF-REMOVED] */

            // GLOBALTHIS
            // Use the 'window' object we created earlier (it was SetGlobal'd as "window")
            var winGlobal = GetGlobal("window"); 
            SetGlobal("globalThis", winGlobal);
            /* [PERF-REMOVED] */

            /* [PERF-REMOVED] */
            // SYMBOL - Create as FenObject with callable NativeObject so we can attach static properties
            var symbolFunc = new FenFunction("Symbol", (args, thisVal) =>
            {
               var desc = args.Length > 0 ? args[0].ToString() : null;
               // JsSymbol implements IValue directly, do not wrap in FenValue.FromObject
               return new FenBrowser.FenEngine.Core.Types.JsSymbol(desc);
            });
            
            // Use FenObject wrapper to allow property attachment (functions are objects in JS)
            var symbolStatic = new FenObject();
            symbolStatic.NativeObject = symbolFunc; // Make it callable
            
            /* [PERF-REMOVED] */
            
            // Symbol.iterator and other well-known symbols
            // JsSymbol.* are static JsSymbol instances (IValue), so pass directly
            symbolStatic.Set("iterator", FenBrowser.FenEngine.Core.Types.JsSymbol.Iterator);
            symbolStatic.Set("asyncIterator", FenBrowser.FenEngine.Core.Types.JsSymbol.AsyncIterator);
            symbolStatic.Set("toStringTag", FenBrowser.FenEngine.Core.Types.JsSymbol.ToStringTag);
            symbolStatic.Set("toPrimitive", FenBrowser.FenEngine.Core.Types.JsSymbol.ToPrimitive);
            symbolStatic.Set("hasInstance", FenBrowser.FenEngine.Core.Types.JsSymbol.HasInstance);
             
            // Symbol.for(key)
            symbolStatic.Set("for", FenValue.FromFunction(new FenFunction("for", (args, thisVal) => {
                var key = args.Length > 0 ? args[0].ToString() : "undefined";
                return FenBrowser.FenEngine.Core.Types.JsSymbol.For(key);
            })));
            
            // Symbol.keyFor(sym)
            symbolStatic.Set("keyFor", FenValue.FromFunction(new FenFunction("keyFor", (args, thisVal) => {
                if (args.Length > 0 && args[0] is FenBrowser.FenEngine.Core.Types.JsSymbol sym)
                    return FenValue.FromString(FenBrowser.FenEngine.Core.Types.JsSymbol.KeyFor(sym));
                return FenValue.Undefined;
            })));

            /* [PERF-REMOVED] */
            SetGlobal("Symbol", FenValue.FromObject(symbolStatic));

            /* [PERF-REMOVED] */
            // OBJECT STATIC METHODS
            var objectFunc = GetGlobal("Object");
            /* [PERF-REMOVED] */
            if (objectFunc.IsFunction)
            {
                var objStatic = objectFunc.AsObject() as FenObject;
                
                // Object.values(obj)
                objStatic.Set("values", FenValue.FromFunction(new FenFunction("values", (args, thisVal) =>
                {
                   if (args.Length < 1 || !args[0].IsObject) return FenValue.FromObject(CreateArray(new IValue[0]));
                   var target = args[0].AsObject() as FenObject;
                   var vals = new List<IValue>();
                   if (target != null)
                   {
                       foreach(var k in target.Keys()) vals.Add(target.Get(k));
                   }
                   return FenValue.FromObject(CreateArray(vals.ToArray()));
                })));

                // Object.entries(obj)
                objStatic.Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
                {
                   if (args.Length < 1 || !args[0].IsObject) return FenValue.FromObject(CreateArray(new IValue[0]));
                   var target = args[0].AsObject() as FenObject;
                   var entries = new List<IValue>();
                   if (target != null)
                   {
                       foreach(var k in target.Keys()) 
                       {
                           var entry = CreateArray(new IValue[] { FenValue.FromString(k), target.Get(k) });
                           entries.Add(FenValue.FromObject(entry));
                       }
                   }
                   return FenValue.FromObject(CreateArray(entries.ToArray()));
                })));

                // Object.fromEntries(iterable)
                objStatic.Set("fromEntries", FenValue.FromFunction(new FenFunction("fromEntries", (args, thisVal) =>
                {
                    var result = new FenObject();
                    if (args.Length < 1 || !args[0].IsObject) return FenValue.FromObject(result);
                    // Assume array of entries for simplicity
                    var entriesArr = args[0].AsObject();
                    // Iterate if it has length
                    var lenVal = entriesArr.Get("length");
                    if (lenVal != null && lenVal.IsNumber)
                    {
                        int len = (int)lenVal.ToNumber();
                        for(int i=0; i<len; i++)
                        {
                            var entry = entriesArr.Get(i.ToString());
                            if (entry != null && entry.IsObject)
                            {
                                var entryObj = entry.AsObject();
                                var key = entryObj.Get("0")?.ToString();
                                var val = entryObj.Get("1");
                                if (key != null) result.Set(key, val ?? FenValue.Undefined);
                            }
                        }
                    }
                    return FenValue.FromObject(result);
                })));

                // Object.getOwnPropertySymbols(obj)
                objStatic.Set("getOwnPropertySymbols", FenValue.FromFunction(new FenFunction("getOwnPropertySymbols", (args, thisVal) =>
                {
                    // For now return empty array as we don't fully track symbol keys in main dictionary
                    return FenValue.FromObject(CreateArray(new IValue[0])); 
                })));
            }
            /* [PERF-REMOVED] */
            
            // PROXY.REVOCABLE
            var proxyFunc = GetGlobal("Proxy");
            /* [PERF-REMOVED] */
            if (proxyFunc.IsFunction)
            {
                var proxyObj = proxyFunc.AsObject() as FenObject;
                if (proxyObj != null)
                {
                proxyObj.Set("revocable", FenValue.FromFunction(new FenFunction("revocable", (args, thisVal) =>
                {
                    if (args.Length < 2) return FenValue.Undefined;
                    var target = args[0];
                    var handler = args[1];
                    
                    // Create the proxy using the constructor logic
                    // We can reuse the constructor by calling it directly if we had access, but for now duplicate logic or call via JS
                    // To keep it simple in C#, we'll manually create the proxy object similar to constructor
                    
                    var p = new FenObject();
                    p.Set("__isProxy__", FenValue.FromBoolean(true));
                    p.Set("__isRevoked__", FenValue.FromBoolean(false)); // Track revocation
                    p.Set("__target__", target);
                    p.Set("__handler__", handler);
                    
                    // Copy traps
                    if (handler.IsObject) {
                        var hObj = handler.AsObject() as FenObject;
                        if (hObj != null) {
                            var getTrap = hObj.Get("get");
                            var setTrap = hObj.Get("set");
                            var hasTrap = hObj.Get("has");
                            var deletePropertyTrap = hObj.Get("deleteProperty");
                            var ownKeysTrap = hObj.Get("ownKeys");
                            var applyTrap = hObj.Get("apply");
                            
                            if (getTrap.IsFunction) p.Set("__proxyGet__", getTrap);
                            if (setTrap.IsFunction) p.Set("__proxySet__", setTrap);
                            if (hasTrap.IsFunction) p.Set("__proxyHas__", hasTrap);
                            if (deletePropertyTrap.IsFunction) p.Set("__proxyDelete__", deletePropertyTrap);
                            if (ownKeysTrap.IsFunction) p.Set("__proxyOwnKeys__", ownKeysTrap);
                            if (applyTrap.IsFunction) p.Set("__proxyApply__", applyTrap);
                        }
                    }
                    
                    var revoke = new FenFunction("revoke", (rArgs, rThis) => {
                        p.Set("__isRevoked__", FenValue.FromBoolean(true));
                        p.Set("__target__", FenValue.Null);
                        p.Set("__handler__", FenValue.Null);
                        return FenValue.Undefined;
                    });

                    var result = new FenObject();
                    result.Set("proxy", FenValue.FromObject(p));
                    result.Set("revoke", FenValue.FromFunction(revoke));
                    return FenValue.FromObject(result);
                })));
                } // end if (proxyObj != null)
            }
            /* [PERF-REMOVED] */

            // REFLECT API
            var reflect = new FenObject();
            
            // Reflect.get(target, propertyKey[, receiver])
            reflect.Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) => {
                if (args.Length < 2 || !args[0].IsObject) return FenValue.Undefined; // Should throw TypeError in strict
                var target = args[0].AsObject() as FenObject;
                var key = args[1].ToString();
                
                // If target is array and key is "length", or index
                // For FenObject, Get() handles prototype chain
                var val = target.Get(key);
                return val != null ? (FenValue)val : FenValue.Undefined;
            })));

            // Reflect.set(target, propertyKey, value[, receiver])
            reflect.Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) => {
                if (args.Length < 3 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var target = args[0].AsObject() as FenObject;
                var key = args[1].ToString();
                var value = args[2];
                
                target.Set(key, (FenValue)value);
                return FenValue.FromBoolean(true);
            })));

            // Reflect.has(target, propertyKey)
            reflect.Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) => {
                if (args.Length < 2 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var target = args[0].AsObject() as FenObject;
                var key = args[1].ToString();
                return FenValue.FromBoolean(target.Has(key));
            })));
            
            // Reflect.deleteProperty(target, propertyKey)
            reflect.Set("deleteProperty", FenValue.FromFunction(new FenFunction("deleteProperty", (args, thisVal) => {
                 if (args.Length < 2 || !args[0].IsObject) return FenValue.FromBoolean(false);
                 var target = args[0].AsObject() as FenObject;
                 var key = args[1].ToString();
                 target.Delete(key);
                 return FenValue.FromBoolean(true);
            })));
            
            // Reflect.ownKeys(target)
            reflect.Set("ownKeys", FenValue.FromFunction(new FenFunction("ownKeys", (args, thisVal) => {
                 if (args.Length < 1 || !args[0].IsObject) return FenValue.FromObject(CreateArray(new IValue[0])); // Should throw
                 var target = args[0].AsObject() as FenObject;
                 var keys = new List<IValue>();
                 foreach(var k in target.Keys()) keys.Add(FenValue.FromString(k));
                 return FenValue.FromObject(CreateArray(keys.ToArray()));
            })));
            
            // Reflect.apply(target, thisArgument, argumentsList)
            reflect.Set("apply", FenValue.FromFunction(new FenFunction("apply", (args, thisVal) => {
                 if (args.Length < 3 || !args[0].IsFunction) return FenValue.Undefined; // TypeError
                 var func = args[0].AsFunction();
                 var thisArg = args[1];
                 var argsListObj = args[2].AsObject() as FenObject;
                 
                 var argsList = new List<IValue>();
                 if (argsListObj != null) {
                     var lenVal = argsListObj.Get("length");
                     if (lenVal != null && lenVal.IsNumber) {
                         int len = (int)lenVal.ToNumber();
                         for(int i=0; i<len; i++) {
                             var item = argsListObj.Get(i.ToString());
                             argsList.Add(item != null ? (FenValue)item : FenValue.Undefined);
                         }
                     }
                 }
                 
                 var originalThis = _context.ThisBinding;
                 try
                 {
                     _context.ThisBinding = thisArg;
                     var res = func.Invoke(argsList.ToArray(), _context);
                     return res != null ? (FenValue)res : FenValue.Undefined;
                 }
                 finally
                 {
                     _context.ThisBinding = originalThis;
                 }
            })));

            /* [PERF-REMOVED] */
            SetGlobal("Reflect", FenValue.FromObject(reflect));

            // --- PROMISE ---
            // Use _context from FenRuntime
            var promiseFunc = new FenFunction("Promise", (args, thisVal) => 
                FenValue.FromObject(new JsPromise(args.Length > 0 ? args[0] : null, _context)));
            
            var promiseStatic = new FenObject();
            promiseStatic.NativeObject = promiseFunc; 
            
            promiseStatic.Set("resolve", FenValue.FromFunction(new FenFunction("resolve", (args, thisVal) => 
                FenValue.FromObject(JsPromise.Resolve(args.Length > 0 ? args[0] : FenValue.Undefined, _context)))));
                
            promiseStatic.Set("reject", FenValue.FromFunction(new FenFunction("reject", (args, thisVal) => 
                FenValue.FromObject(JsPromise.Reject(args.Length > 0 ? args[0] : FenValue.Undefined, _context)))));
                
            SetGlobal("Promise", FenValue.FromObject(promiseStatic)); 

            // --- COLLECTIONS ---
            SetGlobal("Map", FenValue.FromFunction(new FenFunction("Map", (args, thisVal) => FenValue.FromObject(new JsMap(_context)))));
            SetGlobal("Set", FenValue.FromFunction(new FenFunction("Set", (args, thisVal) => FenValue.FromObject(new JsSet(_context)))));
            SetGlobal("WeakMap", FenValue.FromFunction(new FenFunction("WeakMap", (args, thisVal) => FenValue.FromObject(new JsWeakMap()))));
            SetGlobal("WeakSet", FenValue.FromFunction(new FenFunction("WeakSet", (args, thisVal) => FenValue.FromObject(new JsWeakSet()))));

            // --- TYPED ARRAYS ---
            SetGlobal("ArrayBuffer", FenValue.FromFunction(new FenFunction("ArrayBuffer", (args, thisVal) => 
                 FenValue.FromObject(new JsArrayBuffer(args.Length > 0 ? (int)args[0].ToNumber() : 0)))));
                 
            SetGlobal("DataView", FenValue.FromFunction(new FenFunction("DataView", (args, thisVal) => 
            {
                 if (args.Length == 0 || !args[0].IsObject) return FenValue.Undefined;
                 var buf = args[0].AsObject() as JsArrayBuffer;
                 if (buf == null) return FenValue.Undefined; // TypeError
                 int offset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                 int len = args.Length > 2 ? (int)args[2].ToNumber() : -1;
                 return FenValue.FromObject(new JsDataView(buf, offset, len));
            })));
             
            SetGlobal("Uint8Array", FenValue.FromFunction(new FenFunction("Uint8Array", (args, thisVal) => 
                 FenValue.FromObject(new JsUint8Array(args.Length > 0 ? args[0] : null, args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null)))));
                 
            SetGlobal("Float32Array", FenValue.FromFunction(new FenFunction("Float32Array", (args, thisVal) => 
                 FenValue.FromObject(new JsFloat32Array(args.Length > 0 ? args[0] : null, args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null)))));

            // --- XHR ---
            SetGlobal("XMLHttpRequest", FenValue.FromFunction(new FenFunction("XMLHttpRequest", (args, thisVal) => FenValue.FromObject(new XMLHttpRequest(_context)))));


            
            // Error constructors - provide full error types
            SetGlobal("Error", FenValue.FromFunction(new FenFunction("Error", (args, thisVal) =>
            {
                var message = args.Length > 0 ? args[0].ToString() : "";
                var err = new FenObject();
                err.Set("name", FenValue.FromString("Error"));
                err.Set("message", FenValue.FromString(message));
                err.Set("stack", FenValue.FromString($"Error: {message}\n    at <anonymous>"));
                err.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString($"Error: {message}"))));
                return FenValue.FromObject(err);
            })));
            
            SetGlobal("TypeError", FenValue.FromFunction(new FenFunction("TypeError", (args, thisVal) =>
            {
                var message = args.Length > 0 ? args[0].ToString() : "";
                var err = new FenObject();
                err.Set("name", FenValue.FromString("TypeError"));
                err.Set("message", FenValue.FromString(message));
                err.Set("stack", FenValue.FromString($"TypeError: {message}\n    at <anonymous>"));
                err.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString($"TypeError: {message}"))));
                return FenValue.FromObject(err);
            })));
            
            SetGlobal("ReferenceError", FenValue.FromFunction(new FenFunction("ReferenceError", (args, thisVal) =>
            {
                var message = args.Length > 0 ? args[0].ToString() : "";
                var err = new FenObject();
                err.Set("name", FenValue.FromString("ReferenceError"));
                err.Set("message", FenValue.FromString(message));
                err.Set("stack", FenValue.FromString($"ReferenceError: {message}\n    at <anonymous>"));
                err.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString($"ReferenceError: {message}"))));
                return FenValue.FromObject(err);
            })));
            
            SetGlobal("SyntaxError", FenValue.FromFunction(new FenFunction("SyntaxError", (args, thisVal) =>
            {
                var message = args.Length > 0 ? args[0].ToString() : "";
                var err = new FenObject();
                err.Set("name", FenValue.FromString("SyntaxError"));
                err.Set("message", FenValue.FromString(message));
                err.Set("stack", FenValue.FromString($"SyntaxError: {message}\n    at <anonymous>"));
                err.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString($"SyntaxError: {message}"))));
                return FenValue.FromObject(err);
            })));
            
            SetGlobal("RangeError", FenValue.FromFunction(new FenFunction("RangeError", (args, thisVal) =>
            {
                var message = args.Length > 0 ? args[0].ToString() : "";
                var err = new FenObject();
                err.Set("name", FenValue.FromString("RangeError"));
                err.Set("message", FenValue.FromString(message));
                err.Set("stack", FenValue.FromString($"RangeError: {message}\n    at <anonymous>"));
                err.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString($"RangeError: {message}"))));
                return FenValue.FromObject(err);
            })));
            
            SetGlobal("EvalError", FenValue.FromFunction(new FenFunction("EvalError", (args, thisVal) =>
            {
                var message = args.Length > 0 ? args[0].ToString() : "";
                var err = new FenObject();
                err.Set("name", FenValue.FromString("EvalError"));
                err.Set("message", FenValue.FromString(message));
                err.Set("stack", FenValue.FromString($"EvalError: {message}\n    at <anonymous>"));
                err.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString($"EvalError: {message}"))));
                return FenValue.FromObject(err);
            })));
            
            SetGlobal("URIError", FenValue.FromFunction(new FenFunction("URIError", (args, thisVal) =>
            {
                var message = args.Length > 0 ? args[0].ToString() : "";
                var err = new FenObject();
                err.Set("name", FenValue.FromString("URIError"));
                err.Set("message", FenValue.FromString(message));
                err.Set("stack", FenValue.FromString($"URIError: {message}\n    at <anonymous>"));
                err.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString($"URIError: {message}"))));
                return FenValue.FromObject(err);
            })));

            // CRYPTO API
            var cryptoObj = new FenObject();
            
            // crypto.getRandomValues(typedArray)
            cryptoObj.Set("getRandomValues", FenValue.FromFunction(new FenFunction("getRandomValues", (args, thisVal) =>
            {
                if (args.Length < 1) return FenValue.Undefined; // TypeError
                var typedArray = args[0];
                // For now, assume it's an object that might wrap a byte array or we mock it
                // Minimal implementation: if it has "length", fill with random bytes
                // Ideally this interacts with proper TypedArrays if implemented
                
                // Since TypedArrays are complex, we'll implement a best-effort fill
                // Check if it's an object with numeric keys and length
                if (typedArray.IsObject)
                {
                    var obj = typedArray.AsObject() as FenObject;
                    if (obj != null)
                    {
                        var lenVal = obj.Get("length");
                        if (lenVal != null && lenVal.IsNumber)
                        {
                            int len = (int)lenVal.ToNumber();
                            if (len > 65536) throw new Exception("QuotaExceededError"); // Validation
                            
                            byte[] randomBytes = new byte[len];
                            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                            {
                                rng.GetBytes(randomBytes);
                            }
                            
                            // Write back to object
                            for(int i=0; i<len; i++)
                            {
                                obj.Set(i.ToString(), FenValue.FromNumber(randomBytes[i]));
                            }
                            return typedArray;
                        }
                    }
                }
                return typedArray; 
            })));

            // crypto.randomUUID()
            cryptoObj.Set("randomUUID", FenValue.FromFunction(new FenFunction("randomUUID", (args, thisVal) =>
            {
                return FenValue.FromString(Guid.NewGuid().ToString());
            })));
            
            // crypto.subtle
            var subtle = new FenObject();
            
            // crypto.subtle.digest(algorithm, data)
            subtle.Set("digest", FenValue.FromFunction(new FenFunction("digest", (args, thisVal) =>
            {
                // Returns a Promise that resolves to an ArrayBuffer
                // Promise impl details: our system uses basic Promise mocking or integration? 
                // We should return a Promise object.
                
                return CreatePromise((resolve, reject) =>
                {
                    if (args.Length < 2)
                    {
                        reject(FenValue.FromString("TypeError: Arguments missing"));
                        return;
                    }
                    
                    var algoArg = args[0];
                    var dataArg = args[1];
                    
                    string algoName = "SHA-256";
                    if (algoArg.IsString) algoName = algoArg.ToString();
                    else if (algoArg.IsObject)
                    {
                        var obj = algoArg.AsObject();
                        var name = obj.Get("name");
                        if (name != null) algoName = name.ToString();
                    }
                    
                    // Normalize algo name
                    algoName = algoName.Replace("-", "").ToUpperInvariant();
                    
                    byte[] data = new byte[0];
                    if (dataArg.IsString) data = System.Text.Encoding.UTF8.GetBytes(dataArg.ToString());
                    else if (dataArg.IsObject)
                    {
                         // Try to read array-like
                        var obj = dataArg.AsObject();
                        var lenVal = obj.Get("length");
                        if (lenVal != null && lenVal.IsNumber)
                        {
                            int len = (int)lenVal.ToNumber();
                            data = new byte[len];
                            for(int i=0; i<len; i++)
                            {
                                var b = obj.Get(i.ToString());
                                data[i] = (byte)(b != null && b.IsNumber ? b.ToNumber() : 0);
                            }
                        }
                    }
                    
                    byte[] hash = null;
                    using (var hasher = System.Security.Cryptography.HashAlgorithm.Create(algoName == "SHA1" ? "SHA1" : 
                                                                                        algoName == "SHA256" ? "SHA256" :
                                                                                        algoName == "SHA384" ? "SHA384" :
                                                                                        algoName == "SHA512" ? "SHA512" : "SHA256"))
                    {
                        if (hasher == null) 
                        {
                           reject(FenValue.FromString("NotSupportedError: Algorithm not supported"));
                           return;
                        }
                        hash = hasher.ComputeHash(data);
                    }
                    
                    // Convert hash to ArrayBuffer/Uint8Array simulation (object with numeric keys)
                    // In a real engine this would be a native ArrayBuffer
                    var buffer = CreateArray(new IValue[0]); // Actually needs to be ArrayBuffer-like
                    // Let's just return a standard Array of numbers for now as ArrayBuffer emulation
                    var byteVals = new IValue[hash.Length];
                    for(int i=0; i<hash.Length; i++) byteVals[i] = FenValue.FromNumber(hash[i]);
                    
                    resolve(FenValue.FromObject(CreateArray(byteVals)));
                });
            })));

            cryptoObj.Set("subtle", FenValue.FromObject(subtle));

            SetGlobal("crypto", FenValue.FromObject(cryptoObj));

            // INTL API
            var intlObj = new FenObject();
            
            // Intl.NumberFormat(locales, options)
            intlObj.Set("NumberFormat", FenValue.FromFunction(new FenFunction("NumberFormat", (args, thisVal) =>
            {
                // Returns a NumberFormat object with .format()
                string locale = "en-US";
                if (args.Length > 0 && args[0].IsString) locale = args[0].ToString();
                
                var formatObj = new FenObject();
                formatObj.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) =>
                {
                    if (fArgs.Length < 1 || !fArgs[0].IsNumber) return FenValue.FromString("NaN");
                    double val = fArgs[0].ToNumber();
                    try
                    {
                        var culture = System.Globalization.CultureInfo.GetCultureInfo(locale);
                        return FenValue.FromString(val.ToString("N", culture));
                    }
                    catch
                    {
                        return FenValue.FromString(val.ToString("N")); // Fallback
                    }
                })));
                
                // resolvedOptions()
                 formatObj.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (fArgs, fThis) =>
                {
                    var opt = new FenObject();
                    opt.Set("locale", FenValue.FromString(locale));
                    return FenValue.FromObject(opt);
                })));
                
                return FenValue.FromObject(formatObj); 
            })));

            // Intl.DateTimeFormat(locales, options)
             intlObj.Set("DateTimeFormat", FenValue.FromFunction(new FenFunction("DateTimeFormat", (args, thisVal) =>
            {
                string locale = "en-US";
                if (args.Length > 0 && args[0].IsString) locale = args[0].ToString();
                
                var formatObj = new FenObject();
                formatObj.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) =>
                {
                    DateTime date = DateTime.Now;
                    if (fArgs.Length > 0)
                    {
                         // Basic date parsing assumption: number (ticks/ms) or Date object
                         // If generic IValue had ToDate() that would be great, otherwise assume number
                         if (fArgs[0].IsNumber)
                            date = DateTimeOffset.FromUnixTimeMilliseconds((long)fArgs[0].ToNumber()).UtcDateTime;
                    }
                    
                    try
                    {
                        var culture = System.Globalization.CultureInfo.GetCultureInfo(locale);
                        return FenValue.FromString(date.ToString("d", culture));
                    }
                    catch
                    {
                         return FenValue.FromString(date.ToString("d"));
                    }
                })));
                return FenValue.FromObject(formatObj);
            })));

            SetGlobal("Intl", FenValue.FromObject(intlObj));

            // fetch() - Web API for making HTTP requests
            // Returns a FetchPromise object with .then()/.catch() support
            SetGlobal("fetch", FenValue.FromFunction(new FenFunction("fetch", (args, thisVal) =>
            {
                var url = args.Length > 0 ? args[0].ToString() : "";
                if (string.IsNullOrWhiteSpace(url))
                    return CreateRejectedPromise("fetch: invalid URL");

                // Parse options
                var method = "GET";
                string body = null;
                var headers = new Dictionary<string, string>();
                
                if (args.Length > 1 && args[1].IsObject)
                {
                    var options = args[1].AsObject() as FenObject;
                    if (options != null)
                    {
                        var m = options.Get("method");
                        if (m != null && !m.IsNull && !m.IsUndefined)
                            method = m.ToString().ToUpper();
                        var b = options.Get("body");
                        if (b != null && !b.IsNull && !b.IsUndefined)
                            body = b.ToString();
                        var h = options.Get("headers");
                        if (h != null && h.IsObject)
                        {
                            var hObj = h.AsObject() as FenObject;
                            if (hObj != null)
                            {
                                foreach (var key in hObj.Keys())
                                {
                                    var hv = hObj.Get(key);
                                    if (hv != null)
                                        headers[key] = hv.ToString();
                                }
                            }
                        }
                    }
                }

                // Create a FetchPromise - stores callbacks for async resolution
                return CreateFetchPromise(url, method, body, headers);
            })));

            // WebSocket - Real-time bidirectional communication
            SetGlobal("WebSocket", FenValue.FromFunction(new FenFunction("WebSocket", (args, thisVal) =>
            {
                var url = args.Length > 0 ? args[0].ToString() : "";
                if (string.IsNullOrWhiteSpace(url))
                    return new ErrorValue("WebSocket: invalid URL");

                return CreateWebSocket(url);
            })));

            // IndexedDB - Client-side database API
            SetGlobal("indexedDB", FenValue.FromObject(CreateIndexedDB()));

            // Promise - Full Promise implementation with static methods
            SetGlobal("Promise", FenValue.FromObject(CreatePromiseConstructor()));

            // ============================================
            // TIER-2: WeakRef / FinalizationRegistry
            // ============================================
            SetGlobal("WeakRef", FenValue.FromFunction(new FenFunction("WeakRef", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return new ErrorValue("TypeError: WeakRef: Target must be an object");
                var target = args[0].AsObject();
                var weakRef = new WeakReference<IObject>(target);
                
                var obj = new FenObject();
                obj.Set("deref", FenValue.FromFunction(new FenFunction("deref", (dArgs, dThis) =>
                {
                    if (weakRef.TryGetTarget(out var t)) return FenValue.FromObject(t);
                    return FenValue.Undefined;
                })));
                obj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString("[object WeakRef]"))));
                return FenValue.FromObject(obj);
            })));

            SetGlobal("FinalizationRegistry", FenValue.FromFunction(new FenFunction("FinalizationRegistry", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction) return new ErrorValue("TypeError: Constructor requires a cleanup callback");
                var callback = args[0].AsFunction();
                
                var registry = new FenObject();
                // We mock the registry. Actual GC callbacks are hard in interpreted mode without hooks.
                // We partially implement the API surface.
                var registrations = new Dictionary<string, object>(); 

                registry.Set("register", FenValue.FromFunction(new FenFunction("register", (rArgs, rThis) =>
                {
                    // rArgs: target, heldValue, [token]
                    return FenValue.Undefined;
                })));
                registry.Set("unregister", FenValue.FromFunction(new FenFunction("unregister", (uArgs, uThis) =>
                {
                    return FenValue.FromBoolean(true); 
                })));
                
                return FenValue.FromObject(registry);
            })));

            // ============================================
            // TIER-2: SharedArrayBuffer & Atomics
            // ============================================
            SetGlobal("SharedArrayBuffer", FenValue.FromFunction(new FenFunction("SharedArrayBuffer", (args, thisVal) =>
            {
                int length = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                var sab = new FenObject();
                sab.NativeObject = new byte[length]; // In .NET, arrays are ref types, "shared" by default if ref passed
                sab.Set("byteLength", FenValue.FromNumber(length));
                sab.Set("slice", FenValue.FromFunction(new FenFunction("slice", (sArgs, sThis) => {
                     // Slice implementation similar to ArrayBuffer
                     return FenValue.Null; // Stub for brevity
                })));
                sab.Set(FenBrowser.FenEngine.Core.Types.JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("SharedArrayBuffer"));
                return FenValue.FromObject(sab);
            })));

            var atomics = new FenObject();
            atomics.Set(FenBrowser.FenEngine.Core.Types.JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("Atomics"));
            
            // Helper for Atomics Validation
            Func<IValue[], int, (byte[] buffer, int index, bool isInt32)> ValidateAtomic = (vArgs, minArgs) => {
                if (vArgs.Length < minArgs) throw new Exception("TypeError: Missing args");
                if (!vArgs[0].IsObject) throw new Exception("TypeError: Arg 0 must be TypedArray");
                var ta = vArgs[0].AsObject() as FenObject;
                if (ta == null || !(ta.NativeObject is byte[])) throw new Exception("TypeError: Arg 0 must be TypedArray");
                
                var idx = (int)vArgs[1].ToNumber();
                var buffer = ta.NativeObject as byte[];
                // Verify bounds
                // Assuming Int32Array for now mainly
                bool isInt32 = true; // Simplified assumption for stub
                if (idx < 0 || idx >= buffer.Length / 4) throw new Exception("RangeError: Out of bounds");
                
                return (buffer, idx, isInt32);
            };

            atomics.Set("add", FenValue.FromFunction(new FenFunction("add", (args, thisVal) => {
                try {
                    var (buf, idx, isInt32) = ValidateAtomic(args, 3);
                    int val = (int)args[2].ToNumber();
                    // Basic thread safety wrapper (simulated)
                    lock(buf) {
                         int offset = idx * 4;
                         int current = BitConverter.ToInt32(buf, offset);
                         int result = current + val;
                         var bytes = BitConverter.GetBytes(result);
                         Array.Copy(bytes, 0, buf, offset, 4);
                         return FenValue.FromNumber(current); // Returns OLD value
                    }
                } catch { return FenValue.FromNumber(0); }
            })));
            
            atomics.Set("sub", FenValue.FromFunction(new FenFunction("sub", (args, thisVal) => {
                 try {
                    var (buf, idx, isInt32) = ValidateAtomic(args, 3);
                    int val = (int)args[2].ToNumber();
                    lock(buf) {
                         int offset = idx * 4;
                         int current = BitConverter.ToInt32(buf, offset);
                         int result = current - val;
                         var bytes = BitConverter.GetBytes(result);
                         Array.Copy(bytes, 0, buf, offset, 4);
                         return FenValue.FromNumber(current);
                    }
                } catch { return FenValue.FromNumber(0); }
            })));
            
            atomics.Set("load", FenValue.FromFunction(new FenFunction("load", (args, thisVal) => {
                 try {
                    var (buf, idx, isInt32) = ValidateAtomic(args, 2);
                    lock(buf) {
                         int offset = idx * 4;
                         return FenValue.FromNumber(BitConverter.ToInt32(buf, offset));
                    }
                } catch { return FenValue.FromNumber(0); }
            })));
            
            atomics.Set("store", FenValue.FromFunction(new FenFunction("store", (args, thisVal) => {
                 try {
                    var (buf, idx, isInt32) = ValidateAtomic(args, 3);
                    int val = (int)args[2].ToNumber();
                    lock(buf) {
                         int offset = idx * 4;
                         var bytes = BitConverter.GetBytes(val);
                         Array.Copy(bytes, 0, buf, offset, 4);
                         return FenValue.FromNumber(val);
                    }
                } catch { return FenValue.FromNumber(0); }
            })));

            SetGlobal("Atomics", FenValue.FromObject(atomics));

            // ============================================
            // TIER-2: GeneratorFunction (Prototype)
            // ============================================
            var generatorFunctionProto = new FenObject();
            generatorFunctionProto.Set(FenBrowser.FenEngine.Core.Types.JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("GeneratorFunction"));
            var generatorFunction = FenValue.FromFunction(new FenFunction("GeneratorFunction", (args, thisVal) => {
                 return FenValue.FromFunction(new FenFunction("anonymous", (a, t) => FenValue.Undefined));
            }));
            // Wire prototype
            // (Function) -> GeneratorFunction -> GeneratorFunction.prototype
            SetGlobal("GeneratorFunction", generatorFunction);


            // ============================================
            // MAP - Full Implementation
            // ============================================
            SetGlobal("Map", FenValue.FromFunction(new FenFunction("Map", (args, thisVal) =>
            {
                var map = new FenObject();
                var storage = new Dictionary<string, (IValue key, IValue value)>();
                map.NativeObject = storage;
                
                // Helper to generate unique key for any value type
                Func<IValue, string> getMapKey = (key) =>
                {
                    if (key == null || key.IsNull) return "null";
                    if (key.IsUndefined) return "undefined";
                    if (key.IsBoolean) return "bool:" + key.ToBoolean().ToString();
                    if (key.IsNumber) return "num:" + key.ToNumber().ToString();
                    if (key.IsString) return "str:" + key.ToString();
                    if (key.IsObject) return "obj:" + key.AsObject().GetHashCode().ToString();
                    return "other:" + key.ToString();
                };
                
                map.Set("size", FenValue.FromNumber(0));
                Action updateSize = () => map.Set("size", FenValue.FromNumber(storage.Count));
                
                // set(key, value) - Returns the Map for chaining
                map.Set("set", FenValue.FromFunction(new FenFunction("set", (setArgs, setThis) =>
                {
                    var key = setArgs.Length > 0 ? setArgs[0] : FenValue.Undefined;
                    var value = setArgs.Length > 1 ? setArgs[1] : FenValue.Undefined;
                    var keyStr = getMapKey(key);
                    storage[keyStr] = (key, value);
                    updateSize();
                    return setThis;
                })));
                
                // get(key)
                map.Set("get", FenValue.FromFunction(new FenFunction("get", (getArgs, _) =>
                {
                    if (getArgs.Length == 0) return FenValue.Undefined;
                    var keyStr = getMapKey(getArgs[0]);
                    return storage.ContainsKey(keyStr) ? storage[keyStr].value : FenValue.Undefined;
                })));
                
                // has(key)
                map.Set("has", FenValue.FromFunction(new FenFunction("has", (hasArgs, _) =>
                {
                    if (hasArgs.Length == 0) return FenValue.FromBoolean(false);
                    var keyStr = getMapKey(hasArgs[0]);
                    return FenValue.FromBoolean(storage.ContainsKey(keyStr));
                })));
                
                // delete(key)
                map.Set("delete", FenValue.FromFunction(new FenFunction("delete", (delArgs, _) =>
                {
                    if (delArgs.Length == 0) return FenValue.FromBoolean(false);
                    var keyStr = getMapKey(delArgs[0]);
                    var removed = storage.Remove(keyStr);
                    updateSize();
                    return FenValue.FromBoolean(removed);
                })));
                
                // clear()
                map.Set("clear", FenValue.FromFunction(new FenFunction("clear", (_, __) =>
                {
                    storage.Clear();
                    updateSize();
                    return FenValue.Undefined;
                })));
                
                // keys() - Returns array of keys
                map.Set("keys", FenValue.FromFunction(new FenFunction("keys", (_, __) =>
                {
                    var arr = new FenObject();
                    int i = 0;
                    foreach (var kvp in storage.Values)
                    {
                        arr.Set(i.ToString(), kvp.key);
                        i++;
                    }
                    arr.Set("length", FenValue.FromNumber(i));
                    return FenValue.FromObject(arr);
                })));
                
                // values() - Returns array of values
                map.Set("values", FenValue.FromFunction(new FenFunction("values", (_, __) =>
                {
                    var arr = new FenObject();
                    int i = 0;
                    foreach (var kvp in storage.Values)
                    {
                        arr.Set(i.ToString(), kvp.value);
                        i++;
                    }
                    arr.Set("length", FenValue.FromNumber(i));
                    return FenValue.FromObject(arr);
                })));
                
                // entries() - Returns array of [key, value] pairs
                map.Set("entries", FenValue.FromFunction(new FenFunction("entries", (_, __) =>
                {
                    var arr = new FenObject();
                    int i = 0;
                    foreach (var kvp in storage.Values)
                    {
                        var entry = new FenObject();
                        entry.Set("0", kvp.key);
                        entry.Set("1", kvp.value);
                        entry.Set("length", FenValue.FromNumber(2));
                        arr.Set(i.ToString(), FenValue.FromObject(entry));
                        i++;
                    }
                    arr.Set("length", FenValue.FromNumber(i));
                    return FenValue.FromObject(arr);
                })));
                
                // forEach(callback, thisArg)
                map.Set("forEach", FenValue.FromFunction(new FenFunction("forEach", (feArgs, _) =>
                {
                    if (feArgs.Length == 0 || !feArgs[0].IsFunction) return FenValue.Undefined;
                    var callback = feArgs[0].AsFunction();
                    var thisArg = feArgs.Length > 1 ? feArgs[1] : FenValue.Undefined;
                    foreach (var kvp in storage.Values)
                    {
                        callback.Invoke(new IValue[] { kvp.value, kvp.key, FenValue.FromObject(map) }, null);
                    }
                    return FenValue.Undefined;
                })));
                
                // [Symbol.iterator]() - Returns iterator that yields [key, value] pairs
                map.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, __) =>
                {
                    var iterator = new FenObject();
                    var entries = new List<(IValue key, IValue value)>(storage.Values);
                    int index = 0;
                    
                    iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (___, ____) =>
                    {
                        var result = new FenObject();
                        if (index < entries.Count)
                        {
                            var entry = entries[index++];
                            var pair = new FenObject();
                            pair.Set("0", entry.key);
                            pair.Set("1", entry.value);
                            pair.Set("length", FenValue.FromNumber(2));
                            result.Set("value", FenValue.FromObject(pair));
                            result.Set("done", FenValue.FromBoolean(false));
                        }
                        else
                        {
                            result.Set("value", FenValue.Undefined);
                            result.Set("done", FenValue.FromBoolean(true));
                        }
                        return FenValue.FromObject(result);
                    })));
                    
                    return FenValue.FromObject(iterator);
                })));
                
                // Initialize from iterable if provided
                if (args.Length > 0 && args[0].IsObject)
                {
                    var iterable = args[0].AsObject();
                    var lenVal = iterable?.Get("length");
                    int len = lenVal != null && lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                    for (int i = 0; i < len; i++)
                    {
                        var entry = iterable.Get(i.ToString());
                        if (entry != null && entry.IsObject)
                        {
                            var entryObj = entry.AsObject();
                            var key = entryObj?.Get("0") ?? FenValue.Undefined;
                            var value = entryObj?.Get("1") ?? FenValue.Undefined;
                            var keyStr = getMapKey(key);
                            storage[keyStr] = (key, value);
                        }
                    }
                    updateSize();
                }
                
                return FenValue.FromObject(map);
            })));

            // ============================================
            // SET - Full Implementation
            // ============================================
            SetGlobal("Set", FenValue.FromFunction(new FenFunction("Set", (args, thisVal) =>
            {
                var set = new FenObject();
                var storage = new Dictionary<string, IValue>();
                set.NativeObject = storage;
                
                Func<IValue, string> getSetKey = (val) =>
                {
                    if (val == null || val.IsNull) return "null";
                    if (val.IsUndefined) return "undefined";
                    if (val.IsBoolean) return "bool:" + val.ToBoolean().ToString();
                    if (val.IsNumber) return "num:" + val.ToNumber().ToString();
                    if (val.IsString) return "str:" + val.ToString();
                    if (val.IsObject) return "obj:" + val.AsObject().GetHashCode().ToString();
                    return "other:" + val.ToString();
                };
                
                set.Set("size", FenValue.FromNumber(0));
                Action updateSize = () => set.Set("size", FenValue.FromNumber(storage.Count));
                
                // add(value) - Returns Set for chaining
                set.Set("add", FenValue.FromFunction(new FenFunction("add", (addArgs, addThis) =>
                {
                    var value = addArgs.Length > 0 ? addArgs[0] : FenValue.Undefined;
                    var keyStr = getSetKey(value);
                    storage[keyStr] = value;
                    updateSize();
                    return addThis;
                })));
                
                // has(value)
                set.Set("has", FenValue.FromFunction(new FenFunction("has", (hasArgs, _) =>
                {
                    if (hasArgs.Length == 0) return FenValue.FromBoolean(false);
                    var keyStr = getSetKey(hasArgs[0]);
                    return FenValue.FromBoolean(storage.ContainsKey(keyStr));
                })));
                
                // delete(value)
                set.Set("delete", FenValue.FromFunction(new FenFunction("delete", (delArgs, _) =>
                {
                    if (delArgs.Length == 0) return FenValue.FromBoolean(false);
                    var keyStr = getSetKey(delArgs[0]);
                    var removed = storage.Remove(keyStr);
                    updateSize();
                    return FenValue.FromBoolean(removed);
                })));
                
                // clear()
                set.Set("clear", FenValue.FromFunction(new FenFunction("clear", (_, __) =>
                {
                    storage.Clear();
                    updateSize();
                    return FenValue.Undefined;
                })));
                
                // values() - Returns array of values (same as keys for Set)
                set.Set("values", FenValue.FromFunction(new FenFunction("values", (_, __) =>
                {
                    var arr = new FenObject();
                    int i = 0;
                    foreach (var kvp in storage.Values)
                    {
                        arr.Set(i.ToString(), kvp);
                        i++;
                    }
                    arr.Set("length", FenValue.FromNumber(i));
                    return FenValue.FromObject(arr);
                })));
                
                // keys() - Same as values() for Set
                set.Set("keys", set.Get("values"));
                
                // entries() - Returns [value, value] pairs for Set
                set.Set("entries", FenValue.FromFunction(new FenFunction("entries", (_, __) =>
                {
                    var arr = new FenObject();
                    int i = 0;
                    foreach (var kvp in storage.Values)
                    {
                        var entry = new FenObject();
                        entry.Set("0", kvp);
                        entry.Set("1", kvp); // [value, value] for Set
                        entry.Set("length", FenValue.FromNumber(2));
                        arr.Set(i.ToString(), FenValue.FromObject(entry));
                        i++;
                    }
                    arr.Set("length", FenValue.FromNumber(i));
                    return FenValue.FromObject(arr);
                })));
                
                // forEach(callback, thisArg)
                set.Set("forEach", FenValue.FromFunction(new FenFunction("forEach", (feArgs, _) =>
                {
                    if (feArgs.Length == 0 || !feArgs[0].IsFunction) return FenValue.Undefined;
                    var callback = feArgs[0].AsFunction();
                    var thisArg = feArgs.Length > 1 ? feArgs[1] : FenValue.Undefined;
                    foreach (var kvp in storage.Values)
                    {
                        callback.Invoke(new IValue[] { kvp, kvp, FenValue.FromObject(set) }, null);
                    }
                    return FenValue.Undefined;
                })));
                
                // [Symbol.iterator]() - Returns iterator that yields values
                set.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, __) =>
                {
                    var iterator = new FenObject();
                    var values = new List<IValue>(storage.Values);
                    int index = 0;
                    
                    iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (___, ____) =>
                    {
                        var result = new FenObject();
                        if (index < values.Count)
                        {
                            result.Set("value", values[index++]);
                            result.Set("done", FenValue.FromBoolean(false));
                        }
                        else
                        {
                            result.Set("value", FenValue.Undefined);
                            result.Set("done", FenValue.FromBoolean(true));
                        }
                        return FenValue.FromObject(result);
                    })));
                    
                    return FenValue.FromObject(iterator);
                })));
                
                // Initialize from iterable
                if (args.Length > 0 && args[0].IsObject)
                {
                    var iterable = args[0].AsObject();
                    var lenVal = iterable?.Get("length");
                    int len = lenVal != null && lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                    for (int i = 0; i < len; i++)
                    {
                        var value = iterable.Get(i.ToString()) ?? FenValue.Undefined;
                        storage[getSetKey(value)] = value;
                    }
                    updateSize();
                }
                
                return FenValue.FromObject(set);
            })));

            // ============================================
            // WEAKMAP - Implementation (uses object hash codes)
            // ============================================
            SetGlobal("WeakMap", FenValue.FromFunction(new FenFunction("WeakMap", (args, thisVal) =>
            {
                var wmap = new FenObject();
                var storage = new Dictionary<int, IValue>();
                wmap.NativeObject = storage;
                
                // set(key, value) - Key must be an object
                wmap.Set("set", FenValue.FromFunction(new FenFunction("set", (setArgs, setThis) =>
                {
                    if (setArgs.Length == 0 || !setArgs[0].IsObject) return setThis;
                    var keyObj = setArgs[0].AsObject();
                    var value = setArgs.Length > 1 ? setArgs[1] : FenValue.Undefined;
                    storage[keyObj.GetHashCode()] = value;
                    return setThis;
                })));
                
                // get(key)
                wmap.Set("get", FenValue.FromFunction(new FenFunction("get", (getArgs, _) =>
                {
                    if (getArgs.Length == 0 || !getArgs[0].IsObject) return FenValue.Undefined;
                    var keyHash = getArgs[0].AsObject().GetHashCode();
                    return storage.ContainsKey(keyHash) ? storage[keyHash] : FenValue.Undefined;
                })));
                
                // has(key)
                wmap.Set("has", FenValue.FromFunction(new FenFunction("has", (hasArgs, _) =>
                {
                    if (hasArgs.Length == 0 || !hasArgs[0].IsObject) return FenValue.FromBoolean(false);
                    return FenValue.FromBoolean(storage.ContainsKey(hasArgs[0].AsObject().GetHashCode()));
                })));
                
                // delete(key)
                wmap.Set("delete", FenValue.FromFunction(new FenFunction("delete", (delArgs, _) =>
                {
                    if (delArgs.Length == 0 || !delArgs[0].IsObject) return FenValue.FromBoolean(false);
                    return FenValue.FromBoolean(storage.Remove(delArgs[0].AsObject().GetHashCode()));
                })));
                
                return FenValue.FromObject(wmap);
            })));

            // ============================================
            // WEAKSET - Implementation
            // ============================================
            SetGlobal("WeakSet", FenValue.FromFunction(new FenFunction("WeakSet", (args, thisVal) =>
            {
                var wset = new FenObject();
                var storage = new HashSet<int>();
                wset.NativeObject = storage;
                
                // add(value) - Value must be an object
                wset.Set("add", FenValue.FromFunction(new FenFunction("add", (addArgs, addThis) =>
                {
                    if (addArgs.Length == 0 || !addArgs[0].IsObject) return addThis;
                    storage.Add(addArgs[0].AsObject().GetHashCode());
                    return addThis;
                })));
                
                // has(value)
                wset.Set("has", FenValue.FromFunction(new FenFunction("has", (hasArgs, _) =>
                {
                    if (hasArgs.Length == 0 || !hasArgs[0].IsObject) return FenValue.FromBoolean(false);
                    return FenValue.FromBoolean(storage.Contains(hasArgs[0].AsObject().GetHashCode()));
                })));
                
                // delete(value)
                wset.Set("delete", FenValue.FromFunction(new FenFunction("delete", (delArgs, _) =>
                {
                    if (delArgs.Length == 0 || !delArgs[0].IsObject) return FenValue.FromBoolean(false);
                    return FenValue.FromBoolean(storage.Remove(delArgs[0].AsObject().GetHashCode()));
                })));
                
                return FenValue.FromObject(wset);
            })));

            // Worker - Web Workers for background script execution
            SetGlobal("Worker", FenValue.FromFunction(new FenFunction("Worker", (args, thisVal) =>
            {
                var scriptUrl = args.Length > 0 ? args[0].ToString() : "";
                return CreateWorker(scriptUrl);
            })));

            // ArrayBuffer - Binary data container
            SetGlobal("ArrayBuffer", FenValue.FromFunction(new FenFunction("ArrayBuffer", (args, thisVal) =>
            {
                var length = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                return CreateArrayBuffer(length);
            })));

            // TypedArrays - Views over ArrayBuffer
            SetGlobal("Uint8Array", FenValue.FromFunction(CreateTypedArrayConstructor("Uint8Array", 1)));
            SetGlobal("Int8Array", FenValue.FromFunction(CreateTypedArrayConstructor("Int8Array", 1)));
            SetGlobal("Uint16Array", FenValue.FromFunction(CreateTypedArrayConstructor("Uint16Array", 2)));
            SetGlobal("Int16Array", FenValue.FromFunction(CreateTypedArrayConstructor("Int16Array", 2)));
            SetGlobal("Uint32Array", FenValue.FromFunction(CreateTypedArrayConstructor("Uint32Array", 4)));
            SetGlobal("Int32Array", FenValue.FromFunction(CreateTypedArrayConstructor("Int32Array", 4)));
            SetGlobal("Float32Array", FenValue.FromFunction(CreateTypedArrayConstructor("Float32Array", 4)));
            SetGlobal("Float64Array", FenValue.FromFunction(CreateTypedArrayConstructor("Float64Array", 8)));
            SetGlobal("DataView", FenValue.FromFunction(new FenFunction("DataView", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsObject)
                {
                    var ab = args[0].AsObject() as FenObject;
                    if (ab?.NativeObject is byte[] buffer)
                    {
                        return CreateDataView(buffer);
                    }
                }
                return CreateDataView(new byte[0]);
            })));
        }

        private IValue ConvertJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var obj = new FenObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        obj.Set(prop.Name, ConvertJsonElement(prop.Value));
                    }
                    return FenValue.FromObject(obj);
                case JsonValueKind.Array:
                    var arr = new FenObject();
                    int index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        arr.Set(index.ToString(), ConvertJsonElement(item));
                        index++;
                    }
                    arr.Set("length", FenValue.FromNumber(index));
                    return FenValue.FromObject(arr);
                case JsonValueKind.String:
                    return FenValue.FromString(element.GetString());
                case JsonValueKind.Number:
                    return FenValue.FromNumber(element.GetDouble());
                case JsonValueKind.True:
                    return FenValue.FromBoolean(true);
                case JsonValueKind.False:
                    return FenValue.FromBoolean(false);
                case JsonValueKind.Null:
                    return FenValue.Null;
                default:
                    return FenValue.Undefined;
            }
        }

        private string ConvertToJsonString(IValue value)
        {
            if (value.IsString) return JsonSerializer.Serialize(value.ToString());
            if (value.IsNumber) return value.ToString();
            if (value.IsBoolean) return value.ToBoolean().ToString().ToLower();
            if (value.IsNull) return "null";
            if (value.IsUndefined) return "undefined"; // JSON.stringify(undefined) is undefined, but for now string representation
            
            if (value.IsObject)
            {
                IObject obj = value.AsObject();
                // Check if array
                if (obj.Has("length") && obj.Get("length").IsNumber)
                {
                    var list = new List<string>();
                    int len = (int)obj.Get("length").ToNumber();
                    for (int i = 0; i < len; i++)
                    {
                        var item = obj.Get(i.ToString());
                        list.Add(ConvertToJsonString(item));
                    }
                    return "[" + string.Join(",", list) + "]";
                }
                else
                {
                    var props = new List<string>();
                    if (obj is FenObject fenObj)
                    {
                        foreach (var key in fenObj.Keys())
                        {
                            var val = fenObj.Get(key);
                            if (!val.IsFunction && !val.IsUndefined)
                            {
                                props.Add($"{JsonSerializer.Serialize(key)}:{ConvertToJsonString(val)}");
                            }
                        }
                    }
                    return "{" + string.Join(",", props) + "}";
                }
            }
            
            return "null";
        }

        /// <summary>
        /// Sets the DOM root for this runtime.
        /// Creates the 'document' global object.
        /// </summary>
        public void SetDom(Element root, Uri baseUri = null)
        {
            if (root == null) return;
            this.BaseUri = baseUri;

            var documentWrapper = new DocumentWrapper(root, _context, baseUri);
            var docValue = FenValue.FromObject(documentWrapper);
            SetGlobal("document", docValue);

            // Update window.document
            var window = GetGlobal("window");
            if (window.IsObject)
            {
                window.AsObject().Set("document", docValue);
            }
            
            // Create Document constructor/prototype for scripts that check Document.prototype
            var documentPrototype = new FenObject();
            // fonts is not supported, so hasOwnProperty("fonts") should return false
            documentPrototype.Set("hasOwnProperty", FenValue.FromFunction(new FenFunction("hasOwnProperty", (args, thisVal) =>
            {
                if (args.Length > 0)
                {
                    var propName = args[0].ToString();
                    // We don't have fonts, so return false for "fonts"
                    return FenValue.FromBoolean(propName != "fonts");
                }
                return FenValue.FromBoolean(false);
            })));
            
            var documentConstructor = new FenObject();
            documentConstructor.Set("prototype", FenValue.FromObject(documentPrototype));
            SetGlobal("Document", FenValue.FromObject(documentConstructor));
        }

        public void DispatchEvent(string type, IObject eventData = null)
        {
            try
            {
                FenLogger.Debug($"[DispatchEvent] Dispatching '{type}'", LogCategory.Events);

                // Simple implementation: look for window["on" + type]
                // and iterate windowEventListeners[type]
                
                // 1. Check on{type} property
                var handlerName = "on" + type;
                var windowObj = _globalEnv.Get("window");
                if (windowObj is FenValue fvWindow && fvWindow.IsObject)
                {
                    var handler = fvWindow.AsObject().Get(handlerName);
                    if (handler.IsFunction)
                    {
                        var evt = eventData != null ? FenValue.FromObject(eventData) : FenValue.Null;
                        _context.ThisBinding = fvWindow;
                        handler.AsFunction().Invoke(new[] { evt }, _context);
                    }
                    
                    // 2. Check listeners (we need access to the private dictionary, or expose it via property?)
                    // Since the dictionary is local to InitializeBuiltins, we can't access it here easily.
                    // Ideally, we should move InitializeBuiltins logic or store the listener map in a field.
                    // For now, we will use a global hidden property on window to store listeners for access here.
                    
                    var listeners = fvWindow.AsObject().Get("_listeners");
                    if (listeners.IsObject)
                    {
                        var typeListeners = listeners.AsObject().Get(type);
                        if (typeListeners is FenValue fvList && fvList.IsObject) // Array
                        {
                            // Iterate and call
                            // This is complex without Array interop. 
                            // Let's rely on the C# dictionary approach if we refactor.
                            // Refactoring InitializeBuiltins is better.
                        }
                    }
                }
                
                // REFACTOR: We need access to windowEventListeners. I will move it to a class field.
                if (_windowEventListeners.ContainsKey(type))
                {
                    var listeners = _windowEventListeners[type].ToList(); // Copy to avoid modification during iteration
                    foreach (var listener in listeners)
                    {
                        if (listener.IsFunction)
                        {
                            var evt = eventData != null ? FenValue.FromObject(eventData) : FenValue.Null;
                            _context.ThisBinding = FenValue.Undefined;
                            try { listener.AsFunction().Invoke(new[] { evt }, _context); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                /* [PERF-REMOVED] */
            }
        }
        
        private Dictionary<string, List<IValue>> _windowEventListeners = new Dictionary<string, List<IValue>>();

        private FenValue CreateTimer(FenFunction callback, int delay, bool repeat, IValue[] args)
        {
            int id;
            lock (_timerLock) { id = _timerIdCounter++; }
            
            try { FenLogger.Debug($"[FenRuntime] CreateTimer called. ID: {id}, Delay: {delay}", LogCategory.JavaScript); } catch { }

            var cts = new CancellationTokenSource();
            lock (_timerLock) { _activeTimers[id] = cts; }

            // Use ScheduleCallback to run on host thread (e.g. UI thread)
            // But ScheduleCallback is Action<Action, int>. It handles invocation.
            // However, _context.ScheduleCallback assumes one-shot.
            // We need to implement repeat logic ourselves if ScheduleCallback doesn't support it.
            
            Action timerAction = null;
            timerAction = () => 
            {
                if (cts.IsCancellationRequested) return;

                try
                {
                    _context.ThisBinding = FenValue.Undefined;
                    callback.Invoke(args, _context);
                }
                catch (Exception ex)
                {
                   /* [PERF-REMOVED] */
                }

                if (repeat && !cts.IsCancellationRequested)
                {
                    _context.ScheduleCallback(timerAction, delay);
                }
                else
                {
                    lock (_timerLock) { _activeTimers.Remove(id); }
                }
            };
            
            _context.ScheduleCallback(timerAction, delay);
            return FenValue.FromNumber(id);
        }

        private FenValue CreateAnimationFrame(FenFunction callback)
        {
            int id;
            lock (_timerLock) { id = _timerIdCounter++; }

            var cts = new CancellationTokenSource();
            lock (_timerLock) { _activeTimers[id] = cts; }

            try { FenLogger.Debug($"[FenRuntime] RequestAnimationFrame. ID: {id}", LogCategory.JavaScript); } catch { }

            Action timerAction = () =>
            {
                if (cts.IsCancellationRequested) return;

                lock (_timerLock) { _activeTimers.Remove(id); } // Autoremove before execution, unlike Interval

                try
                {
                    double now = Convert.ToDouble(DateTime.Now.Ticks) / 10000.0; // Simulated high-res time (ms)
                    _context.ThisBinding = FenValue.Undefined;
                    callback.Invoke(new IValue[] { FenValue.FromNumber(now) }, _context);
                }
                catch (Exception ex)
                {
                    try { FenLogger.Error($"[RAF Error] {ex.Message}", LogCategory.JavaScript, ex); } catch { }
                }
            };

            // Schedule for approx 16ms (60fps)
            _context.ScheduleCallback(timerAction, 16);
            return FenValue.FromNumber(id);
        }

        private void CancelTimer(int id)
        {
            lock (_timerLock)
            {
                if (_activeTimers.TryGetValue(id, out var cts))
                {
                    cts.Cancel();
                    _activeTimers.Remove(id);
                }
            }
        }

        public void SetGlobal(string name, IValue value)
        {
            _globalEnv.Set(name, value);
        }

        public IValue GetGlobal(string name)
        {
            var val = _globalEnv.Get(name);
            return val ?? FenValue.Undefined;
        }

        public void SetVariable(string name, IValue value)
        {
            _globalEnv.Set(name, value);
        }

        public IValue GetVariable(string name)
        {
            return GetGlobal(name);
        }

        public bool HasVariable(string name)
        {
            return _globalEnv.Get(name) != null;
        }

        public void SetAlert(Action<string> alertAction)
        {
            var alertFunc = FenValue.FromFunction(new FenFunction("alert", (args, thisVal) =>
            {
                var msg = args.Length > 0 ? args[0].ToString() : "";
                alertAction?.Invoke(msg);
                return FenValue.Undefined;
            }));
            
            SetGlobal("alert", alertFunc);
            var win = GetGlobal("window");
            if (win.IsObject)
            {
                win.AsObject().Set("alert", alertFunc);
            }
        }

        /// <summary>
        /// Execute JavaScript code using the FenEngine Parser and Interpreter
        /// </summary>
        public IValue ExecuteSimple(string code, string url = "script")
        {
            try
            {
                // Reset execution timer for each new script execution
                if (_context is ExecutionContext ec)
                {
                    ec.Reset();
                }
                
                // Set context URL for debugging
                if (_context != null) _context.CurrentUrl = url;
                
                var lexer = new Lexer(code);
                var parser = new Parser(lexer);
                var program = parser.ParseProgram();

                if (parser.Errors.Count > 0)
                {
                    return new ErrorValue(string.Join("\n", parser.Errors));
                }
                
                var interpreter = new Interpreter();
                
                // Register with DevTools
                DevToolsCore.Instance.SetInterpreter(interpreter);
                DevToolsCore.Instance.RegisterSource(url, code);
                
                var result = interpreter.Eval(program, _globalEnv, _context);

                return result ?? FenValue.Undefined;
            }
            catch (Exception ex)
            {
                return new ErrorValue($"Runtime error: {ex.Message}");
            }
        }

        #region Helper Methods for Browser APIs

        /// <summary>
        /// Create an array-like object from string array (Privacy: used for navigator.languages, plugins, etc.)
        /// </summary>
        private FenObject CreateArray(string[] items)
        {
            var arr = new FenObject();
            for (int i = 0; i < items.Length; i++)
            {
                arr.Set(i.ToString(), FenValue.FromString(items[i]));
            }
            arr.Set("length", FenValue.FromNumber(items.Length));
            return arr;
        }

        private FenObject CreateArray(IValue[] items)
        {
            var arr = new FenObject();
            for (int i = 0; i < items.Length; i++)
            {
                arr.Set(i.ToString(), items[i]);
            }
            arr.Set("length", FenValue.FromNumber(items.Length));
            return arr;
        }        

        /// <summary>
        /// Create an empty array with length 0
        /// </summary>
        private FenObject CreateEmptyArray()
        {
            var arr = new FenObject();
            arr.Set("length", FenValue.FromNumber(0));
            return arr;
        }

        /// <summary>
        /// Create screen orientation object (Privacy: use standard landscape orientation)
        /// </summary>
        private FenObject CreateScreenOrientation()
        {
            var orientation = new FenObject();
            orientation.Set("type", FenValue.FromString("landscape-primary"));
            orientation.Set("angle", FenValue.FromNumber(0));
            return orientation;
        }

        // In-memory storage (Secure: not persisted, Privacy: cleared on restart)


        /// <summary>
        /// Create Storage object (localStorage/sessionStorage) - Secure: in-memory only
        /// </summary>


        #endregion

        #region Fetch API Helpers

        private static readonly HttpClient _httpClient = new HttpClient();

        /// <summary>
        /// Creates a rejected Promise-like object
        /// </summary>
        private IValue CreateRejectedPromise(string errorMessage)
        {
            var promise = new FenObject();
            promise.Set("__rejected", FenValue.FromBoolean(true));
            promise.Set("__error", FenValue.FromString(errorMessage));
            
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
            {
                // Skip success callback, return this for chaining
                return thisVal;
            })));
            
            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
            {
                // Call the error callback
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var callback = args[0].AsFunction();
                    if (callback.IsNative && callback.NativeImplementation != null)
                        callback.NativeImplementation(new IValue[] { FenValue.FromString(errorMessage) }, FenValue.Undefined);
                }
                return thisVal;
            })));
            
            return FenValue.FromObject(promise);
        }

        /// <summary>
        /// Creates a FetchPromise that executes HTTP request asynchronously
        /// </summary>
        private IValue CreateFetchPromise(string url, string method, string body, Dictionary<string, string> headers)
        {
            var promise = new FenObject();
            var thenCallbacks = new List<FenFunction>();
            var catchCallbacks = new List<FenFunction>();
            
            promise.Set("__pending", FenValue.FromBoolean(true));
            promise.Set("__url", FenValue.FromString(url));
            
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    thenCallbacks.Add(args[0].AsFunction());
                }
                return thisVal; // Return same promise for chaining
            })));
            
            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    catchCallbacks.Add(args[0].AsFunction());
                }
                return thisVal;
            })));

            // Execute the fetch asynchronously
            /* [PERF-REMOVED] */
            _ = Task.Run(async () =>
            {
                try
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), url);
                    
                    // Add headers
                    foreach (var h in headers)
                    {
                        try { request.Headers.TryAddWithoutValidation(h.Key, h.Value); } catch { }
                    }
                    
                    // Add body for POST/PUT
                    if (!string.IsNullOrEmpty(body) && (method == "POST" || method == "PUT" || method == "PATCH"))
                    {
                        request.Content = new StringContent(body, System.Text.Encoding.UTF8, 
                            headers.ContainsKey("Content-Type") ? headers["Content-Type"] : "application/json");
                    }

                    var response = await _httpClient.SendAsync(request);
                    var responseText = await response.Content.ReadAsStringAsync();
                    var statusCode = (int)response.StatusCode;
                    var reasonPhrase = response.ReasonPhrase;
                   
                    /* [PERF-REMOVED] */

                    // Schedule callback on MAIN THREAD to handle JS objects
                    // Use 0 delay to execute on next tick
                    _context.ScheduleCallback(() =>
                    {
                        /* [PERF-REMOVED] */
                        try
                        {
                            // Create Response object (must be on main thread)
                            var responseObj = CreateResponse(url, statusCode, reasonPhrase, responseText);
                            
                            // Call all then callbacks
                            foreach (var callback in thenCallbacks)
                            {
                                try
                                {
                                    if (callback.IsNative && callback.NativeImplementation != null)
                                        callback.NativeImplementation(new IValue[] { responseObj }, FenValue.Undefined);
                                    else if (!callback.IsNative)
                                        callback.Invoke(new IValue[] { responseObj }, _context);
                                }
                                catch (Exception ex) 
                                {
                                     try { FenLogger.Error($"[fetch] Then callback error: {ex.Message}", LogCategory.JavaScript); } catch {}
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                             try { FenLogger.Error($"[fetch] Resolution error: {ex.Message}", LogCategory.JavaScript); } catch {}
                        }
                    }, 0);
                }
                catch (Exception ex)
                {
                    var errorMessage = ex.Message;
                    // Schedule rejection on MAIN THREAD
                    _context.ScheduleCallback(() => 
                    {
                        // Call all catch callbacks
                        foreach (var callback in catchCallbacks)
                        {
                            try
                            {
                                if (callback.IsNative && callback.NativeImplementation != null)
                                    callback.NativeImplementation(new IValue[] { FenValue.FromString(errorMessage) }, FenValue.Undefined);
                                else if (!callback.IsNative)
                                    callback.Invoke(new IValue[] { FenValue.FromString(errorMessage) }, _context);
                            }
                            catch { }
                        }
                    }, 0);
                }
            });
            
            return FenValue.FromObject(promise);
        }

        /// <summary>
        /// Creates a Response object for fetch()
        /// </summary>
        private IValue CreateResponse(string url, int status, string statusText, string bodyText)
        {
            var response = new FenObject();
            
            // Standard Response properties
            response.Set("ok", FenValue.FromBoolean(status >= 200 && status < 300));
            response.Set("status", FenValue.FromNumber(status));
            response.Set("statusText", FenValue.FromString(statusText ?? ""));
            response.Set("url", FenValue.FromString(url));
            response.Set("redirected", FenValue.FromBoolean(false));
            response.Set("type", FenValue.FromString("basic"));
            
            // Store body for text()/json() methods
            response.Set("__bodyText", FenValue.FromString(bodyText ?? ""));
            
            // text() method - returns Promise-like object that resolves to body text
            response.Set("text", FenValue.FromFunction(new FenFunction("text", (args, thisVal) =>
            {
                var textPromise = new FenObject();
                textPromise.Set("then", FenValue.FromFunction(new FenFunction("then", (tArgs, tThis) =>
                {
                    if (tArgs.Length > 0 && tArgs[0].IsFunction)
                    {
                        var cb = tArgs[0].AsFunction();
                        if (cb.IsNative && cb.NativeImplementation != null)
                            cb.NativeImplementation(new IValue[] { FenValue.FromString(bodyText ?? "") }, FenValue.Undefined);
                    }
                    return tThis;
                })));
                return FenValue.FromObject(textPromise);
            })));
            
            // json() method - returns Promise-like object that resolves to parsed JSON
            response.Set("json", FenValue.FromFunction(new FenFunction("json", (args, thisVal) =>
            {
                var jsonPromise = new FenObject();
                jsonPromise.Set("then", FenValue.FromFunction(new FenFunction("then", (tArgs, tThis) =>
                {
                    if (tArgs.Length > 0 && tArgs[0].IsFunction)
                    {
                        var cb = tArgs[0].AsFunction();
                        try
                        {
                            using var doc = JsonDocument.Parse(bodyText ?? "{}");
                            var parsed = ConvertJsonElementStatic(doc.RootElement);
                            if (cb.IsNative && cb.NativeImplementation != null)
                                cb.NativeImplementation(new IValue[] { parsed }, FenValue.Undefined);
                        }
                        catch (Exception ex)
                        {
                            if (cb.IsNative && cb.NativeImplementation != null)
                                cb.NativeImplementation(new IValue[] { new ErrorValue($"JSON parse error: {ex.Message}") }, FenValue.Undefined);
                        }
                    }
                    return tThis;
                })));
                jsonPromise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (cArgs, cThis) =>
                {
                    return cThis;
                })));
                return FenValue.FromObject(jsonPromise);
            })));

            return FenValue.FromObject(response);
        }

        /// <summary>
        /// Static version of ConvertJsonElement for use in static methods
        /// </summary>
        private static IValue ConvertJsonElementStatic(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var obj = new FenObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        obj.Set(prop.Name, ConvertJsonElementStatic(prop.Value));
                    }
                    return FenValue.FromObject(obj);
                case JsonValueKind.Array:
                    var arr = new FenObject();
                    // Arrays are represented as objects with numeric keys and length
                    int i = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        arr.Set(i.ToString(), ConvertJsonElementStatic(item));
                        i++;
                    }
                    arr.Set("length", FenValue.FromNumber(i));
                    return FenValue.FromObject(arr);
                case JsonValueKind.String:
                    return FenValue.FromString(element.GetString() ?? "");
                case JsonValueKind.Number:
                    return FenValue.FromNumber(element.GetDouble());
                case JsonValueKind.True:
                    return FenValue.FromBoolean(true);
                case JsonValueKind.False:
                    return FenValue.FromBoolean(false);
                default:
                    return FenValue.Null;
            }
        }

        #endregion

        #region WebSocket API Helpers

        /// <summary>
        /// Creates a WebSocket object with send, close methods and event handlers
        /// </summary>
        private IValue CreateWebSocket(string url)
        {
            var ws = new FenObject();
            var clientWs = new ClientWebSocket();
            var cts = new CancellationTokenSource();
            
            // ReadyState constants
            const int CONNECTING = 0;
            const int OPEN = 1;
            const int CLOSING = 2;
            const int CLOSED = 3;
            
            ws.Set("CONNECTING", FenValue.FromNumber(CONNECTING));
            ws.Set("OPEN", FenValue.FromNumber(OPEN));
            ws.Set("CLOSING", FenValue.FromNumber(CLOSING));
            ws.Set("CLOSED", FenValue.FromNumber(CLOSED));
            
            ws.Set("readyState", FenValue.FromNumber(CONNECTING));
            ws.Set("url", FenValue.FromString(url));
            ws.Set("bufferedAmount", FenValue.FromNumber(0));
            
            // Event handlers (set by user)
            ws.Set("onopen", FenValue.Null);
            ws.Set("onmessage", FenValue.Null);
            ws.Set("onerror", FenValue.Null);
            ws.Set("onclose", FenValue.Null);
            
            // send() method
            ws.Set("send", FenValue.FromFunction(new FenFunction("send", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                var data = args[0].ToString();
                
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (clientWs.State == WebSocketState.Open)
                        {
                            var bytes = System.Text.Encoding.UTF8.GetBytes(data);
                            await clientWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
                        }
                    }
                    catch { }
                });
                
                return FenValue.Undefined;
            })));
            
            // close() method
            ws.Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
            {
                ws.Set("readyState", FenValue.FromNumber(CLOSING));
                
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await clientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", cts.Token);
                        ws.Set("readyState", FenValue.FromNumber(CLOSED));
                        
                        // Fire onclose
                        var onclose = ws.Get("onclose");
                        if (onclose != null && onclose.IsFunction)
                        {
                            var cb = onclose.AsFunction();
                            if (cb.IsNative && cb.NativeImplementation != null)
                            {
                                var evt = new FenObject();
                                evt.Set("code", FenValue.FromNumber(1000));
                                evt.Set("reason", FenValue.FromString("Normal closure"));
                                evt.Set("wasClean", FenValue.FromBoolean(true));
                                cb.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                            }
                        }
                    }
                    catch { }
                });
                
                return FenValue.Undefined;
            })));
            
            // Connect asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    // Convert ws:// or wss:// URLs
                    var wsUrl = url;
                    if (wsUrl.StartsWith("ws://")) wsUrl = "ws://" + wsUrl.Substring(5);
                    else if (wsUrl.StartsWith("wss://")) wsUrl = "wss://" + wsUrl.Substring(6);
                    
                    await clientWs.ConnectAsync(new Uri(wsUrl), cts.Token);
                    ws.Set("readyState", FenValue.FromNumber(OPEN));
                    
                    // Fire onopen
                    var onopen = ws.Get("onopen");
                    if (onopen != null && onopen.IsFunction)
                    {
                        var cb = onopen.AsFunction();
                        if (cb.IsNative && cb.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("type", FenValue.FromString("open"));
                            cb.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                        }
                    }
                    
                    // Start receiving messages
                    var buffer = new byte[4096];
                    while (clientWs.State == WebSocketState.Open)
                    {
                        var result = await clientWs.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                        
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            ws.Set("readyState", FenValue.FromNumber(CLOSED));
                            var onclose = ws.Get("onclose");
                            if (onclose != null && onclose.IsFunction)
                            {
                                var cb = onclose.AsFunction();
                                if (cb.IsNative && cb.NativeImplementation != null)
                                {
                                    var evt = new FenObject();
                                    evt.Set("code", FenValue.FromNumber((int)(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure)));
                                    evt.Set("reason", FenValue.FromString(result.CloseStatusDescription ?? ""));
                                    evt.Set("wasClean", FenValue.FromBoolean(true));
                                    cb.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                                }
                            }
                            break;
                        }
                        else if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var msg = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                            var onmessage = ws.Get("onmessage");
                            if (onmessage != null && onmessage.IsFunction)
                            {
                                var cb = onmessage.AsFunction();
                                if (cb.IsNative && cb.NativeImplementation != null)
                                {
                                    var evt = new FenObject();
                                    evt.Set("data", FenValue.FromString(msg));
                                    evt.Set("type", FenValue.FromString("message"));
                                    cb.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ws.Set("readyState", FenValue.FromNumber(CLOSED));
                    var onerror = ws.Get("onerror");
                    if (onerror != null && onerror.IsFunction)
                    {
                        var cb = onerror.AsFunction();
                        if (cb.IsNative && cb.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("message", FenValue.FromString(ex.Message));
                            evt.Set("type", FenValue.FromString("error"));
                            cb.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                        }
                    }
                }
            });
            
            return FenValue.FromObject(ws);
        }

        #endregion

        #region IndexedDB API Helpers

        /// <summary>
        /// Creates the indexedDB global object (IDBFactory)
        /// </summary>
        private FenObject CreateIndexedDB()
        {
            var idb = new FenObject();
            var origin = GetCurrentOrigin();
            
            // open(name, version) - Opens a database, returns IDBOpenDBRequest
            idb.Set("open", FenValue.FromFunction(new FenFunction("open", (args, thisVal) =>
            {
                var dbName = args.Length > 0 ? args[0].ToString() : "default";
                var version = args.Length > 1 ? (int)args[1].ToNumber() : 1;
                
                // Create request object
                var request = new FenObject();
                request.Set("readyState", FenValue.FromString("pending"));
                request.Set("onsuccess", FenValue.Null);
                request.Set("onerror", FenValue.Null);
                request.Set("onupgradeneeded", FenValue.Null);
                
                // Simulate async database opening
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10); // Small delay to mimic async
                    
                    var openResult = await _storageBackend.OpenDatabase(origin, dbName, version);
                    bool isNew = openResult.UpgradeNeeded;
                    
                    var db = CreateIDBDatabase(dbName, version);
                    request.Set("result", db);
                    request.Set("readyState", FenValue.FromString("done"));
                    
                    // Fire onupgradeneeded for new databases
                    if (isNew)
                    {
                        var onupgrade = request.Get("onupgradeneeded");
                        if (onupgrade != null && onupgrade.IsFunction)
                        {
                            var cb = onupgrade.AsFunction();
                            if (cb.IsNative && cb.NativeImplementation != null)
                            {
                                var evt = new FenObject();
                                evt.Set("target", FenValue.FromObject(request));
                                evt.Set("oldVersion", FenValue.FromNumber(openResult.OldVersion));
                                evt.Set("newVersion", FenValue.FromNumber(openResult.NewVersion));
                                cb.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                            }
                        }
                    }
                    
                    // Fire onsuccess
                    var onsuccess = request.Get("onsuccess");
                    if (onsuccess != null && onsuccess.IsFunction)
                    {
                        var cb = onsuccess.AsFunction();
                        if (cb.IsNative && cb.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            cb.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                        }
                    }
                });
                
                return FenValue.FromObject(request);
            })));
            
            // deleteDatabase(name) - Deletes a database
            idb.Set("deleteDatabase", FenValue.FromFunction(new FenFunction("deleteDatabase", (args, thisVal) =>
            {
                var dbName = args.Length > 0 ? args[0].ToString() : "";
                _ = _storageBackend.DeleteDatabase(origin, dbName);
                
                var request = new FenObject();
                request.Set("readyState", FenValue.FromString("done"));
                return FenValue.FromObject(request);
            })));
            
            return idb;
        }

        /// <summary>
        /// Creates an IDBDatabase object
        /// </summary>
        private IValue CreateIDBDatabase(string name, int version)
        {
            var db = new FenObject();
            db.Set("name", FenValue.FromString(name));
            db.Set("version", FenValue.FromNumber(version));
            
            // createObjectStore(name, options) - Creates an object store
            db.Set("createObjectStore", FenValue.FromFunction(new FenFunction("createObjectStore", (args, thisVal) =>
            {
                var storeName = args.Length > 0 ? args[0].ToString() : "default";
                _ = _storageBackend.CreateObjectStore(GetCurrentOrigin(), name, storeName, new ObjectStoreOptions());
                return CreateIDBObjectStore(name, storeName);
            })));
            
            // transaction(storeNames, mode) - Creates a transaction
            db.Set("transaction", FenValue.FromFunction(new FenFunction("transaction", (args, thisVal) =>
            {
                var storeName = args.Length > 0 ? args[0].ToString() : "";
                var mode = args.Length > 1 ? args[1].ToString() : "readonly";
                
                var tx = new FenObject();
                tx.Set("mode", FenValue.FromString(mode));
                
                tx.Set("objectStore", FenValue.FromFunction(new FenFunction("objectStore", (storeArgs, storeThis) =>
                {
                    var sn = storeArgs.Length > 0 ? storeArgs[0].ToString() : storeName;
                    return CreateIDBObjectStore(name, sn);
                })));
                
                return FenValue.FromObject(tx);
            })));
            
            // close() - Closes the database
            db.Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
            {
                return FenValue.Undefined;
            })));
            
            return FenValue.FromObject(db);
        }

        /// <summary>
        /// Creates an IDBObjectStore object with CRUD operations
        /// </summary>
        private IValue CreateIDBObjectStore(string dbName, string storeName)
        {
            var store = new FenObject();
            store.Set("name", FenValue.FromString(storeName));
            var origin = GetCurrentOrigin();
            
            // add(value, key) - Adds a value
            store.Set("add", FenValue.FromFunction(new FenFunction("add", (args, thisVal) =>
            {
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                var key = args.Length > 1 ? args[1].ToString() : Guid.NewGuid().ToString();
                
                _ = Task.Run(async () =>
                {
                    await _storageBackend.Add(origin, dbName, storeName, key, StorageUtils.ToSerializable(value));
                });
                
                var request = new FenObject();
                request.Set("result", FenValue.FromString(key));
                request.Set("onsuccess", FenValue.Null);
                
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1);
                    var cb = request.Get("onsuccess");
                    if (cb != null && cb.IsFunction)
                    {
                        var fn = cb.AsFunction();
                        if (fn.IsNative && fn.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            fn.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                        }
                    }
                });
                
                return FenValue.FromObject(request);
            })));
            
            // get(key) - Gets a value
            store.Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0].ToString() : "";
                var request = new FenObject();
                request.Set("onsuccess", FenValue.Null);
                
                _ = Task.Run(async () =>
                {
                    var result = await _storageBackend.Get(origin, dbName, storeName, key);
                    request.Set("result", StorageUtils.FromSerializable(result));
                    
                    await Task.Delay(1);
                    var cb = request.Get("onsuccess");
                    if (cb != null && cb.IsFunction)
                    {
                        var fn = cb.AsFunction();
                        if (fn.IsNative && fn.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            fn.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                        }
                    }
                });
                
                return FenValue.FromObject(request);
            })));
            
            // put(value, key) - Updates or adds a value
            store.Set("put", FenValue.FromFunction(new FenFunction("put", (args, thisVal) =>
            {
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                var key = args.Length > 1 ? args[1].ToString() : Guid.NewGuid().ToString();
                
                var request = new FenObject();
                request.Set("result", FenValue.FromString(key));
                request.Set("onsuccess", FenValue.Null);

                _ = Task.Run(async () =>
                {
                    await _storageBackend.Put(origin, dbName, storeName, key, StorageUtils.ToSerializable(value));
                    
                    var cb = request.Get("onsuccess");
                    if (cb != null && cb.IsFunction)
                    {
                        var fn = cb.AsFunction();
                        if (fn.IsNative && fn.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            fn.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                        }
                    }
                });
                
                return FenValue.FromObject(request);
            })));
            
            // delete(key) - Deletes a value
            store.Set("delete", FenValue.FromFunction(new FenFunction("delete", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0].ToString() : "";
                var request = new FenObject();
                request.Set("onsuccess", FenValue.Null);

                _ = Task.Run(async () =>
                {
                    await _storageBackend.Delete(origin, dbName, storeName, key);
                    
                    var cb = request.Get("onsuccess");
                    if (cb != null && cb.IsFunction)
                    {
                        var fn = cb.AsFunction();
                        if (fn.IsNative && fn.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            fn.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                        }
                    }
                });

                return FenValue.FromObject(request);
            })));
            
            // clear() - Clears all values
            store.Set("clear", FenValue.FromFunction(new FenFunction("clear", (args, thisVal) =>
            {
                var request = new FenObject();
                request.Set("onsuccess", FenValue.Null);

                _ = Task.Run(async () =>
                {
                    await _storageBackend.Clear(origin, dbName, storeName);
                    
                    var cb = request.Get("onsuccess");
                    if (cb != null && cb.IsFunction)
                    {
                        var fn = cb.AsFunction();
                        if (fn.IsNative && fn.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            fn.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                        }
                    }
                });

                return FenValue.FromObject(request);
            })));
            
            return FenValue.FromObject(store);
        }

        #endregion

        #region Promise API Helpers

        /// <summary>
        /// Creates the Promise constructor object with static methods
        /// </summary>
        private FenObject CreatePromiseConstructor()
        {
            var promiseCtor = new FenObject();
            
            // Promise constructor: new Promise((resolve, reject) => { ... })
            promiseCtor.NativeObject = new FenFunction("Promise", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction)
                    return CreateRejectedPromise("Promise resolver is not a function");
                
                var executor = args[0].AsFunction();
                return CreateExecutorPromise(executor, promiseCtor);
            });
            
            // Promise.resolve(value) - Creates a resolved promise
            promiseCtor.Set("resolve", FenValue.FromFunction(new FenFunction("resolve", (args, thisVal) =>
            {
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                // If already a promise/thenable, return it
                if (value.IsObject)
                {
                    var thenMethod = value.AsObject()?.Get("then");
                    if (thenMethod != null && thenMethod.IsFunction) return value;
                }
                return CreateResolvedPromise(value);
            })));
            
            // Promise.reject(reason) - Creates a rejected promise  
            promiseCtor.Set("reject", FenValue.FromFunction(new FenFunction("reject", (args, thisVal) =>
            {
                var reason = args.Length > 0 ? args[0] : FenValue.Undefined;
                return CreateRejectedPromiseValue(reason);
            })));
            
            // Promise.all(iterable) - Waits for all promises to resolve
            promiseCtor.Set("all", FenValue.FromFunction(new FenFunction("all", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject)
                    return CreateResolvedPromise(FenValue.FromObject(CreateEmptyArray()));
                
                var iterable = args[0].AsObject();
                var lenVal = iterable?.Get("length");
                int len = lenVal != null && lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                if (len == 0)
                    return CreateResolvedPromise(FenValue.FromObject(CreateEmptyArray()));
                
                return CreateExecutorPromise(new FenFunction("allExecutor", (exArgs, _) =>
                {
                    var resolve = exArgs.Length > 0 && exArgs[0].IsFunction ? exArgs[0].AsFunction() : null;
                    var reject = exArgs.Length > 1 && exArgs[1].IsFunction ? exArgs[1].AsFunction() : null;
                    
                    var results = new FenObject();
                    results.Set("length", FenValue.FromNumber(len));
                    int completed = 0;
                    bool rejected = false;
                    object lockObj = new object();
                    
                    for (int i = 0; i < len; i++)
                    {
                        int index = i;
                        var item = iterable.Get(i.ToString()) ?? FenValue.Undefined;
                        
                        // Handle thenable/promise
                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod != null && thenMethod.IsFunction)
                            {
                                thenMethod.AsFunction().Invoke(new IValue[] {
                                    FenValue.FromFunction(new FenFunction("resolve", (a, __) =>
                                    {
                                        lock (lockObj)
                                        {
                                            if (rejected) return FenValue.Undefined;
                                            results.Set(index.ToString(), a.Length > 0 ? a[0] : FenValue.Undefined);
                                            completed++;
                                            if (completed == len) resolve?.Invoke(new IValue[] { FenValue.FromObject(results) }, null);
                                        }
                                        return FenValue.Undefined;
                                    })),
                                    FenValue.FromFunction(new FenFunction("reject", (a, __) =>
                                    {
                                        lock (lockObj)
                                        {
                                            if (rejected) return FenValue.Undefined;
                                            rejected = true;
                                            reject?.Invoke(a, null);
                                        }
                                        return FenValue.Undefined;
                                    }))
                                }, null);
                                continue;
                            }
                        }
                        // Non-promise value
                        lock (lockObj)
                        {
                            if (rejected) continue;
                            results.Set(index.ToString(), item);
                            completed++;
                            if (completed == len) resolve?.Invoke(new IValue[] { FenValue.FromObject(results) }, null);
                        }
                    }
                    return FenValue.Undefined;
                }), promiseCtor);
            })));
            
            // Promise.race(iterable) - Returns first settled promise
            promiseCtor.Set("race", FenValue.FromFunction(new FenFunction("race", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject)
                    return CreateExecutorPromise(new FenFunction("raceExecutor", (_, __) => FenValue.Undefined), promiseCtor);
                
                var iterable = args[0].AsObject();
                var lenVal = iterable?.Get("length");
                int len = lenVal != null && lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                return CreateExecutorPromise(new FenFunction("raceExecutor", (exArgs, _) =>
                {
                    var resolve = exArgs.Length > 0 && exArgs[0].IsFunction ? exArgs[0].AsFunction() : null;
                    var reject = exArgs.Length > 1 && exArgs[1].IsFunction ? exArgs[1].AsFunction() : null;
                    bool settled = false;
                    object lockObj = new object();
                    
                    for (int i = 0; i < len; i++)
                    {
                        var item = iterable.Get(i.ToString()) ?? FenValue.Undefined;
                        
                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod != null && thenMethod.IsFunction)
                            {
                                thenMethod.AsFunction().Invoke(new IValue[] {
                                    FenValue.FromFunction(new FenFunction("resolve", (a, __) =>
                                    {
                                        lock (lockObj) { if (settled) return FenValue.Undefined; settled = true; }
                                        resolve?.Invoke(a, null);
                                        return FenValue.Undefined;
                                    })),
                                    FenValue.FromFunction(new FenFunction("reject", (a, __) =>
                                    {
                                        lock (lockObj) { if (settled) return FenValue.Undefined; settled = true; }
                                        reject?.Invoke(a, null);
                                        return FenValue.Undefined;
                                    }))
                                }, null);
                                continue;
                            }
                        }
                        // Non-promise settles immediately
                        lock (lockObj) { if (settled) continue; settled = true; }
                        resolve?.Invoke(new IValue[] { item }, null);
                        break;
                    }
                    return FenValue.Undefined;
                }), promiseCtor);
            })));

            // Promise.allSettled(iterable) - Waits for all to settle (resolve or reject)
            promiseCtor.Set("allSettled", FenValue.FromFunction(new FenFunction("allSettled", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject)
                    return CreateResolvedPromise(FenValue.FromObject(CreateEmptyArray()));
                
                var iterable = args[0].AsObject();
                var lenVal = iterable?.Get("length");
                int len = lenVal != null && lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                if (len == 0)
                    return CreateResolvedPromise(FenValue.FromObject(CreateEmptyArray()));
                
                return CreateExecutorPromise(new FenFunction("allSettledExecutor", (exArgs, _) =>
                {
                    var resolve = exArgs.Length > 0 && exArgs[0].IsFunction ? exArgs[0].AsFunction() : null;
                    
                    var results = new FenObject();
                    results.Set("length", FenValue.FromNumber(len));
                    int completed = 0;
                    object lockObj = new object();
                    
                    for (int i = 0; i < len; i++)
                    {
                        int index = i;
                        var item = iterable.Get(i.ToString()) ?? FenValue.Undefined;
                        
                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod != null && thenMethod.IsFunction)
                            {
                                thenMethod.AsFunction().Invoke(new IValue[] {
                                    FenValue.FromFunction(new FenFunction("resolve", (a, __) =>
                                    {
                                        var result = new FenObject();
                                        result.Set("status", FenValue.FromString("fulfilled"));
                                        result.Set("value", a.Length > 0 ? a[0] : FenValue.Undefined);
                                        lock (lockObj)
                                        {
                                            results.Set(index.ToString(), FenValue.FromObject(result));
                                            completed++;
                                            if (completed == len) resolve?.Invoke(new IValue[] { FenValue.FromObject(results) }, null);
                                        }
                                        return FenValue.Undefined;
                                    })),
                                    FenValue.FromFunction(new FenFunction("reject", (a, __) =>
                                    {
                                        var result = new FenObject();
                                        result.Set("status", FenValue.FromString("rejected"));
                                        result.Set("reason", a.Length > 0 ? a[0] : FenValue.Undefined);
                                        lock (lockObj)
                                        {
                                            results.Set(index.ToString(), FenValue.FromObject(result));
                                            completed++;
                                            if (completed == len) resolve?.Invoke(new IValue[] { FenValue.FromObject(results) }, null);
                                        }
                                        return FenValue.Undefined;
                                    }))
                                }, null);
                                continue;
                            }
                        }
                        // Non-promise resolves immediately
                        var res = new FenObject();
                        res.Set("status", FenValue.FromString("fulfilled"));
                        res.Set("value", item);
                        lock (lockObj)
                        {
                            results.Set(index.ToString(), FenValue.FromObject(res));
                            completed++;
                            if (completed == len) resolve?.Invoke(new IValue[] { FenValue.FromObject(results) }, null);
                        }
                    }
                    return FenValue.Undefined;
                }), promiseCtor);
            })));
            
            // Promise.any(iterable) - Returns first fulfilled or AggregateError if all reject
            promiseCtor.Set("any", FenValue.FromFunction(new FenFunction("any", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject)
                {
                    var aggErr = new FenObject();
                    aggErr.Set("name", FenValue.FromString("AggregateError"));
                    aggErr.Set("message", FenValue.FromString("All promises were rejected"));
                    aggErr.Set("errors", FenValue.FromObject(CreateEmptyArray()));
                    return CreateRejectedPromiseValue(FenValue.FromObject(aggErr));
                }
                
                var iterable = args[0].AsObject();
                var lenVal = iterable?.Get("length");
                int len = lenVal != null && lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                if (len == 0)
                {
                    var aggErr = new FenObject();
                    aggErr.Set("name", FenValue.FromString("AggregateError"));
                    aggErr.Set("message", FenValue.FromString("All promises were rejected"));
                    aggErr.Set("errors", FenValue.FromObject(CreateEmptyArray()));
                    return CreateRejectedPromiseValue(FenValue.FromObject(aggErr));
                }
                
                return CreateExecutorPromise(new FenFunction("anyExecutor", (exArgs, _) =>
                {
                    var resolve = exArgs.Length > 0 && exArgs[0].IsFunction ? exArgs[0].AsFunction() : null;
                    var reject = exArgs.Length > 1 && exArgs[1].IsFunction ? exArgs[1].AsFunction() : null;
                    
                    var errors = new FenObject();
                    errors.Set("length", FenValue.FromNumber(len));
                    int rejectedCount = 0;
                    bool fulfilled = false;
                    object lockObj = new object();
                    
                    for (int i = 0; i < len; i++)
                    {
                        int index = i;
                        var item = iterable.Get(i.ToString()) ?? FenValue.Undefined;
                        
                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod != null && thenMethod.IsFunction)
                            {
                                thenMethod.AsFunction().Invoke(new IValue[] {
                                    FenValue.FromFunction(new FenFunction("resolve", (a, __) =>
                                    {
                                        lock (lockObj) { if (fulfilled) return FenValue.Undefined; fulfilled = true; }
                                        resolve?.Invoke(a, null);
                                        return FenValue.Undefined;
                                    })),
                                    FenValue.FromFunction(new FenFunction("reject", (a, __) =>
                                    {
                                        lock (lockObj)
                                        {
                                            if (fulfilled) return FenValue.Undefined;
                                            errors.Set(index.ToString(), a.Length > 0 ? a[0] : FenValue.Undefined);
                                            rejectedCount++;
                                            if (rejectedCount == len)
                                            {
                                                var aggErr = new FenObject();
                                                aggErr.Set("name", FenValue.FromString("AggregateError"));
                                                aggErr.Set("message", FenValue.FromString("All promises were rejected"));
                                                aggErr.Set("errors", FenValue.FromObject(errors));
                                                reject?.Invoke(new IValue[] { FenValue.FromObject(aggErr) }, null);
                                            }
                                        }
                                        return FenValue.Undefined;
                                    }))
                                }, null);
                                continue;
                            }
                        }
                        // Non-promise fulfills immediately
                        lock (lockObj) { if (fulfilled) continue; fulfilled = true; }
                        resolve?.Invoke(new IValue[] { item }, null);
                        break;
                    }
                    return FenValue.Undefined;
                }), promiseCtor);
            })));
            
            return promiseCtor;
        }

        /// <summary>
        /// Creates a Promise with an executor function (for new Promise((resolve, reject) => {}))
        /// </summary>
        private IValue CreateExecutorPromise(FenFunction executor, FenObject promiseCtor)
        {
            var promise = new FenObject();
            string state = "pending";
            IValue result = FenValue.Undefined;
            var fulfillCallbacks = new List<(FenFunction onFulfill, FenFunction onReject, FenFunction chainResolve, FenFunction chainReject)>();
            var rejectCallbacks = new List<(FenFunction onFulfill, FenFunction onReject, FenFunction chainResolve, FenFunction chainReject)>();
            object lockObj = new object();
            
            Action<string, IValue> settle = (newState, value) =>
            {
                lock (lockObj)
                {
                    if (state != "pending") return;
                    state = newState;
                    result = value;
                    promise.Set("__state", FenValue.FromString(state));
                    promise.Set(newState == "fulfilled" ? "__value" : "__reason", value);
                }
                
                var callbacks = newState == "fulfilled" ? fulfillCallbacks : rejectCallbacks;
                foreach (var (onFulfill, onReject, chainResolve, chainReject) in callbacks)
                {
                    try
                    {
                        var handler = newState == "fulfilled" ? onFulfill : onReject;
                        if (handler != null)
                        {
                            var cbResult = handler.Invoke(new IValue[] { result }, null);
                            chainResolve?.Invoke(new IValue[] { cbResult }, null);
                        }
                        else if (newState == "fulfilled")
                            chainResolve?.Invoke(new IValue[] { result }, null);
                        else
                            chainReject?.Invoke(new IValue[] { result }, null);
                    }
                    catch (Exception ex)
                    {
                        chainReject?.Invoke(new IValue[] { FenValue.FromString(ex.Message) }, null);
                    }
                }
                fulfillCallbacks.Clear();
                rejectCallbacks.Clear();
            };
            
            var resolveFn = new FenFunction("resolve", (resolveArgs, _) =>
            {
                var value = resolveArgs.Length > 0 ? resolveArgs[0] : FenValue.Undefined;
                // Handle thenable resolution
                if (value.IsObject)
                {
                    var thenMethod = value.AsObject()?.Get("then");
                    if (thenMethod != null && thenMethod.IsFunction)
                    {
                        try
                        {
                            thenMethod.AsFunction().Invoke(new IValue[] {
                                FenValue.FromFunction(new FenFunction("res", (a, __) => { settle("fulfilled", a.Length > 0 ? a[0] : FenValue.Undefined); return FenValue.Undefined; })),
                                FenValue.FromFunction(new FenFunction("rej", (a, __) => { settle("rejected", a.Length > 0 ? a[0] : FenValue.Undefined); return FenValue.Undefined; }))
                            }, null);
                        }
                        catch (Exception ex) { settle("rejected", FenValue.FromString(ex.Message)); }
                        return FenValue.Undefined;
                    }
                }
                settle("fulfilled", value);
                return FenValue.Undefined;
            });
            
            var rejectFn = new FenFunction("reject", (rejectArgs, _) =>
            {
                settle("rejected", rejectArgs.Length > 0 ? rejectArgs[0] : FenValue.Undefined);
                return FenValue.Undefined;
            });
            
            promise.Set("__state", FenValue.FromString("pending"));
            
            // then(onFulfilled, onRejected)
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (thenArgs, thenThis) =>
            {
                var onFulfilled = thenArgs.Length > 0 && thenArgs[0].IsFunction ? thenArgs[0].AsFunction() : null;
                var onRejected = thenArgs.Length > 1 && thenArgs[1].IsFunction ? thenArgs[1].AsFunction() : null;
                
                FenFunction chainResolve = null, chainReject = null;
                var chainedPromise = CreateExecutorPromise(new FenFunction("chainExecutor", (exArgs, _) =>
                {
                    chainResolve = exArgs.Length > 0 && exArgs[0].IsFunction ? exArgs[0].AsFunction() : null;
                    chainReject = exArgs.Length > 1 && exArgs[1].IsFunction ? exArgs[1].AsFunction() : null;
                    return FenValue.Undefined;
                }), promiseCtor);
                
                lock (lockObj)
                {
                    if (state == "pending")
                    {
                        fulfillCallbacks.Add((onFulfilled, onRejected, chainResolve, chainReject));
                        rejectCallbacks.Add((onFulfilled, onRejected, chainResolve, chainReject));
                    }
                    else
                    {
                        Task.Run(() =>
                        {
                            try
                            {
                                var handler = state == "fulfilled" ? onFulfilled : onRejected;
                                if (handler != null)
                                {
                                    var cbResult = handler.Invoke(new IValue[] { result }, null);
                                    chainResolve?.Invoke(new IValue[] { cbResult }, null);
                                }
                                else if (state == "fulfilled")
                                    chainResolve?.Invoke(new IValue[] { result }, null);
                                else
                                    chainReject?.Invoke(new IValue[] { result }, null);
                            }
                            catch (Exception ex)
                            {
                                chainReject?.Invoke(new IValue[] { FenValue.FromString(ex.Message) }, null);
                            }
                        });
                    }
                }
                
                return chainedPromise;
            })));
            
            // catch(onRejected)
            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (catchArgs, _) =>
            {
                var thenMethod = promise.Get("then");
                if (thenMethod != null && thenMethod.IsFunction)
                    return thenMethod.AsFunction().Invoke(new IValue[] { FenValue.Undefined, catchArgs.Length > 0 ? catchArgs[0] : FenValue.Undefined }, null);
                return FenValue.FromObject(promise);
            })));
            
            // finally(onFinally)
            promise.Set("finally", FenValue.FromFunction(new FenFunction("finally", (finallyArgs, _) =>
            {
                var onFinally = finallyArgs.Length > 0 && finallyArgs[0].IsFunction ? finallyArgs[0].AsFunction() : null;
                if (onFinally == null) return FenValue.FromObject(promise);
                
                var thenMethod = promise.Get("then");
                if (thenMethod != null && thenMethod.IsFunction)
                {
                    return thenMethod.AsFunction().Invoke(new IValue[] {
                        FenValue.FromFunction(new FenFunction("onFulfill", (a, __) => { onFinally.Invoke(new IValue[0], null); return a.Length > 0 ? a[0] : FenValue.Undefined; })),
                        FenValue.FromFunction(new FenFunction("onReject", (a, __) => { onFinally.Invoke(new IValue[0], null); return CreateRejectedPromiseValue(a.Length > 0 ? a[0] : FenValue.Undefined); }))
                    }, null);
                }
                return FenValue.FromObject(promise);
            })));
            
            // Execute the executor
            try
            {
                executor.Invoke(new IValue[] { FenValue.FromFunction(resolveFn), FenValue.FromFunction(rejectFn) }, null);
            }
            catch (Exception ex)
            {
                settle("rejected", FenValue.FromString(ex.Message));
            }
            
            return FenValue.FromObject(promise);
        }

        /// <summary>
        /// Creates a rejected Promise with an IValue reason
        /// </summary>
        private IValue CreateRejectedPromiseValue(IValue reason)
        {
            var promise = new FenObject();
            promise.Set("__rejected", FenValue.FromBoolean(true));
            promise.Set("__reason", reason);
            
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
            {
                if (args.Length > 1 && args[1].IsFunction)
                {
                    var cb = args[1].AsFunction();
                    var result = cb.Invoke(new IValue[] { reason }, null);
                    return CreateResolvedPromise(result);
                }
                return thisVal;
            })));
            
            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var cb = args[0].AsFunction();
                    var result = cb.Invoke(new IValue[] { reason }, null);
                    return CreateResolvedPromise(result);
                }
                return thisVal;
            })));
            
            promise.Set("finally", FenValue.FromFunction(new FenFunction("finally", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                    args[0].AsFunction().Invoke(new IValue[0], null);
                return thisVal;
            })));
            
            return FenValue.FromObject(promise);
        }

        /// <summary>
        /// Creates a resolved Promise
        /// </summary>
        private IValue CreateResolvedPromise(IValue value)
        {
            var promise = new FenObject();
            promise.Set("__resolved", FenValue.FromBoolean(true));
            promise.Set("__value", value);
            
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var cb = args[0].AsFunction();
                    if (cb.IsNative && cb.NativeImplementation != null)
                    {
                        var result = cb.NativeImplementation(new IValue[] { value }, FenValue.Undefined);
                        return CreateResolvedPromise(result);
                    }
                }
                return thisVal;
            })));
            
            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
            {
                return thisVal; // Already resolved, skip catch
            })));
            
            promise.Set("finally", FenValue.FromFunction(new FenFunction("finally", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var cb = args[0].AsFunction();
                    if (cb.IsNative && cb.NativeImplementation != null)
                        cb.NativeImplementation(new IValue[0], FenValue.Undefined);
                }
                return thisVal;
            })));
            
            return FenValue.FromObject(promise);
        }

        #endregion

        #region Web Worker Helpers

        /// <summary>
        /// Creates a Worker object for background script execution
        /// </summary>
        private IValue CreateWorker(string scriptUrl)
        {
            var worker = new FenObject();
            worker.Set("onmessage", FenValue.Null);
            worker.Set("onerror", FenValue.Null);
            
            // postMessage(data) - Send message to worker
            worker.Set("postMessage", FenValue.FromFunction(new FenFunction("postMessage", (args, thisVal) =>
            {
                var data = args.Length > 0 ? args[0] : FenValue.Undefined;
                
                // Simulate worker responding (simplified)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10);
                    var onmessage = worker.Get("onmessage");
                    if (onmessage != null && onmessage.IsFunction)
                    {
                        var cb = onmessage.AsFunction();
                        if (cb.IsNative && cb.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("data", data);
                            evt.Set("type", FenValue.FromString("message"));
                            cb.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                        }
                    }
                });
                
                return FenValue.Undefined;
            })));
            
            // terminate() - Terminate the worker
            worker.Set("terminate", FenValue.FromFunction(new FenFunction("terminate", (args, thisVal) =>
            {
                return FenValue.Undefined;
            })));
            
            return FenValue.FromObject(worker);
        }

        #endregion

        #region TypedArray Helpers

        /// <summary>
        /// Creates an ArrayBuffer object
        /// </summary>
        private IValue CreateArrayBuffer(int length)
        {
            var ab = new FenObject();
            ab.NativeObject = new byte[length];
            ab.Set("byteLength", FenValue.FromNumber(length));
            
            // slice(begin, end) - Creates a new ArrayBuffer with a copy of bytes
            ab.Set("slice", FenValue.FromFunction(new FenFunction("slice", (args, thisVal) =>
            {
                var buffer = ab.NativeObject as byte[];
                var begin = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                var end = args.Length > 1 ? (int)args[1].ToNumber() : buffer.Length;
                
                if (begin < 0) begin = Math.Max(0, buffer.Length + begin);
                if (end < 0) end = Math.Max(0, buffer.Length + end);
                end = Math.Min(end, buffer.Length);
                
                var newLength = Math.Max(0, end - begin);
                var newBuffer = new byte[newLength];
                if (newLength > 0)
                    Array.Copy(buffer, begin, newBuffer, 0, newLength);
                
                var newAb = new FenObject();
                newAb.NativeObject = newBuffer;
                newAb.Set("byteLength", FenValue.FromNumber(newLength));
                return FenValue.FromObject(newAb);
            })));
            
            return FenValue.FromObject(ab);
        }

        /// <summary>
        /// Creates a TypedArray constructor
        /// </summary>
        private FenFunction CreateTypedArrayConstructor(string name, int bytesPerElement)
        {
            return new FenFunction(name, (args, thisVal) =>
            {
                int length = 0;
                byte[] buffer = null;
                
                if (args.Length > 0)
                {
                    if (args[0].IsNumber)
                    {
                        length = (int)args[0].ToNumber();
                        buffer = new byte[length * bytesPerElement];
                    }
                    else if (args[0].IsObject)
                    {
                        var obj = args[0].AsObject() as FenObject;
                        if (obj?.NativeObject is byte[] existingBuffer)
                        {
                            buffer = existingBuffer;
                            length = buffer.Length / bytesPerElement;
                        }
                    }
                }
                
                if (buffer == null)
                    buffer = new byte[0];
                
                var arr = new FenObject();
                arr.NativeObject = buffer;
                arr.Set("length", FenValue.FromNumber(length));
                arr.Set("byteLength", FenValue.FromNumber(buffer.Length));
                arr.Set("BYTES_PER_ELEMENT", FenValue.FromNumber(bytesPerElement));
                
                // Indexed access (simplified - use get/set)
                for (int i = 0; i < length && i < 1000; i++)
                {
                    arr.Set(i.ToString(), FenValue.FromNumber(0));
                }
                
                // set(array, offset) - Copies values
                arr.Set("set", FenValue.FromFunction(new FenFunction("set", (setArgs, setThis) =>
                {
                    return FenValue.Undefined;
                })));
                
                // subarray(begin, end) - Creates a new view
                arr.Set("subarray", FenValue.FromFunction(new FenFunction("subarray", (subArgs, subThis) =>
                {
                    return thisVal;
                })));
                
                return FenValue.FromObject(arr);
            });
        }

        /// <summary>
        /// Creates a DataView object for fine-grained binary access
        /// </summary>
        private IValue CreateDataView(byte[] buffer)
        {
            var dv = new FenObject();
            dv.NativeObject = buffer;
            dv.Set("byteLength", FenValue.FromNumber(buffer.Length));
            dv.Set("byteOffset", FenValue.FromNumber(0));
            
            // getInt8, getUint8, getInt16, getUint16, etc.
            dv.Set("getInt8", FenValue.FromFunction(new FenFunction("getInt8", (args, thisVal) =>
            {
                var offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (offset >= 0 && offset < buffer.Length)
                    return FenValue.FromNumber((sbyte)buffer[offset]);
                return FenValue.FromNumber(0);
            })));
            
            dv.Set("getUint8", FenValue.FromFunction(new FenFunction("getUint8", (args, thisVal) =>
            {
                var offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (offset >= 0 && offset < buffer.Length)
                    return FenValue.FromNumber(buffer[offset]);
                return FenValue.FromNumber(0);
            })));
            
            dv.Set("setInt8", FenValue.FromFunction(new FenFunction("setInt8", (args, thisVal) =>
            {
                var offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                var value = args.Length > 1 ? (sbyte)args[1].ToNumber() : (sbyte)0;
                if (offset >= 0 && offset < buffer.Length)
                    buffer[offset] = (byte)value;
                return FenValue.Undefined;
            })));
            
            dv.Set("setUint8", FenValue.FromFunction(new FenFunction("setUint8", (args, thisVal) =>
            {
                var offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                var value = args.Length > 1 ? (byte)args[1].ToNumber() : (byte)0;
                if (offset >= 0 && offset < buffer.Length)
                    buffer[offset] = value;
                return FenValue.Undefined;
            })));
            
            return FenValue.FromObject(dv);
        }

        /// <summary>
        /// Apply reviver function to parsed JSON
        /// </summary>
        private FenValue ApplyReviver(FenValue value, FenFunction reviver, string key)
        {
            if (value.IsObject && !value.IsNull)
            {
                var obj = value.AsObject() as FenObject;
                if (obj != null)
                {
                    foreach (var k in obj.Keys().ToList())
                    {
                        var v = obj.Get(k);
                        if (v != null)
                        {
                            var newV = ApplyReviver((FenValue)v, reviver, k);
                            if (newV.IsUndefined)
                                obj.Delete(k);
                            else
                                obj.Set(k, newV);
                        }
                    }
                }
            }
            
            var holder = new FenObject();
            holder.Set(key, value);
            var result = reviver.Invoke(new IValue[] { FenValue.FromString(key), value }, null);
            return result != null ? (FenValue)result : FenValue.Undefined;
        }

        /// <summary>
        /// Convert to JSON string with replacer function/array support
        /// </summary>
        private string ConvertToJsonStringWithReplacer(IValue value, FenFunction replacer, string[] replacerArray, int spaces, string indent)
        {
            if (value == null || value.IsUndefined) return "undefined";
            if (value.IsNull) return "null";
            
            // Apply replacer function
            if (replacer != null)
            {
                var holder = new FenObject();
                holder.Set("", (FenValue)value);
                var result = replacer.Invoke(new IValue[] { FenValue.FromString(""), value }, null);
                if (result != null && !result.IsUndefined)
                    value = result;
                else if (result != null && result.IsUndefined)
                    return null;
            }
            
            if (value.IsNull) return "null";
            if (value.IsBoolean) return value.ToBoolean() ? "true" : "false";
            if (value.IsNumber)
            {
                var n = value.ToNumber();
                if (double.IsNaN(n) || double.IsInfinity(n)) return "null";
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (value.IsString) return $"\"{EscapeJsonString(value.ToString())}\"";
            
            if (value.IsObject)
            {
                var obj = value.AsObject();
                if (obj == null) return "null";
                
                var lenVal = obj.Get("length");
                bool isArray = lenVal != null && lenVal.IsNumber;
                
                var newIndent = spaces > 0 ? indent + new string(' ', spaces) : "";
                var sep = spaces > 0 ? "\n" : "";
                var colonSpace = spaces > 0 ? " " : "";
                
                if (isArray)
                {
                    int len = (int)lenVal.ToNumber();
                    var items = new List<string>();
                    for (int i = 0; i < len; i++)
                    {
                        var item = obj.Get(i.ToString());
                        var itemStr = ConvertToJsonStringWithReplacer(item ?? FenValue.Null, replacer, replacerArray, spaces, newIndent);
                        items.Add(itemStr ?? "null");
                    }
                    if (spaces > 0 && items.Count > 0)
                        return $"[{sep}{newIndent}{string.Join($",{sep}{newIndent}", items)}{sep}{indent}]";
                    return $"[{string.Join(",", items)}]";
                }
                else
                {
                    var pairs = new List<string>();
                    var keys = replacerArray ?? obj.Keys().ToArray();
                    foreach (var key in keys)
                    {
                        var val = obj.Get(key);
                        if (val != null && !val.IsUndefined && !val.IsFunction)
                        {
                            var valStr = ConvertToJsonStringWithReplacer(val, replacer, replacerArray, spaces, newIndent);
                            if (valStr != null)
                                pairs.Add($"\"{EscapeJsonString(key)}\"{colonSpace}:{colonSpace}{valStr}");
                        }
                    }
                    if (spaces > 0 && pairs.Count > 0)
                        return $"{{{sep}{newIndent}{string.Join($",{sep}{newIndent}", pairs)}{sep}{indent}}}";
                    return $"{{{string.Join(",", pairs)}}}";
                }
            }
            
            return "null";
        }

        private string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                            sb.Append($"\\u{(int)c:x4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private IValue CreatePromise(Action<Action<IValue>, Action<IValue>> executor)
        {
            var promise = new FenObject();
            promise.Set("__isPromise__", FenValue.FromBoolean(true));
            promise.Set("__state__", FenValue.FromString("pending"));
            promise.Set("__value__", FenValue.Undefined);
            promise.Set("__reason__", FenValue.Undefined);

            var resolve = new Action<IValue>(value => {
                 if (promise.Get("__state__")?.ToString() == "pending") {
                     promise.Set("__state__", FenValue.FromString("fulfilled"));
                     promise.Set("__value__", value);
                 }
            });
            
            var reject = new Action<IValue>(reason => {
                 if (promise.Get("__state__")?.ToString() == "pending") {
                     promise.Set("__state__", FenValue.FromString("rejected"));
                     promise.Set("__reason__", reason);
                 }
            });

            try {
                executor(resolve, reject);
            } catch (Exception ex) {
                reject(FenValue.FromString(ex.Message));
            }

            // then(onFulfilled, onRejected)
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) => {
                var state = promise.Get("__state__")?.ToString();
                if (state == "fulfilled") {
                     if (args.Length > 0 && args[0].IsFunction) {
                         var res = args[0].AsFunction().Invoke(new IValue[]{ promise.Get("__value__") }, null);
                         return res ?? FenValue.Undefined;
                     }
                     return promise.Get("__value__") ?? FenValue.Undefined;
                }
                return FenValue.FromObject(promise); 
            })));
            
             // catch(onRejected)
             promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) => {
                 var state = promise.Get("__state__")?.ToString();
                 if (state == "rejected") {
                      if (args.Length > 0 && args[0].IsFunction) {
                          var res = args[0].AsFunction().Invoke(new IValue[]{ promise.Get("__reason__") }, null);
                          return res ?? FenValue.Undefined;
                      }
                 }
                 return FenValue.FromObject(promise);
             })));

            return FenValue.FromObject(promise);
        }

        private FenValue ConvertNativeToFenValue(object obj)
        {
            if (obj == null) return FenValue.Null;
            if (obj is bool b) return FenValue.FromBoolean(b);
            if (obj is string s) return FenValue.FromString(s);
            if (obj is int i) return FenValue.FromNumber(i);
            if (obj is double d) return FenValue.FromNumber(d);
            if (obj is float f) return FenValue.FromNumber(f);
            if (obj is long l) return FenValue.FromNumber(l);
            if (obj is IObject io) return FenValue.FromObject(io);

            // Handle Dictionary as JS Object
            if (obj is System.Collections.IDictionary dict)
            {
                var fenObj = new FenObject();
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    fenObj.Set(entry.Key.ToString(), ConvertNativeToFenValue(entry.Value));
                }
                return FenValue.FromObject(fenObj);
            }

            // Handle List/Array as JS Array (Object with length)
            if (obj is System.Collections.IEnumerable list)
            {
                var fenObj = new FenObject();
                int index = 0;
                foreach (var item in list)
                {
                    fenObj.Set(index.ToString(), ConvertNativeToFenValue(item));
                    index++;
                }
                fenObj.Set("length", FenValue.FromNumber(index));
                return FenValue.FromObject(fenObj);
            }

            return FenValue.Null;
        }

        #endregion
    }
}

