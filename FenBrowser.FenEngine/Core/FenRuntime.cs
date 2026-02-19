using FenBrowser.Core.Dom.V2;
using System;
using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.WebAPIs;
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.DevTools;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;
using FenBrowser.FenEngine.Storage;
using FenBrowser.Core.Network.Handlers;

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
            // Reset the default prototype so objectPrototype created in this runtime doesn't
            // accidentally inherit from a prototype created by a previous FenRuntime instance.
            FenObject.DefaultPrototype = null;

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

        /// <summary>
        /// Centralized network delegate for all runtime HTTP fetches (fetch/XHR/worker scripts).
        /// BrowserHost/JavaScriptEngine must set this to enforce shared policy (CSP/CORS/cookies/TLS).
        /// </summary>
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> NetworkFetchHandler { get; set; }

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
                    
                    var args = new FenValue[] { FenValue.FromObject(eventObj) };
                    
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

        /// <summary>
        /// Executes JavaScript code to apply prototype pollution defense by freezing built-in prototypes.
        /// Call this method after initialization to prevent malicious code from modifying
        /// Object.prototype, Array.prototype, etc.
        ///
        /// WARNING: This is irreversible and may break code that relies on prototype modification.
        /// Only use in security-critical environments with untrusted code.
        ///
        /// This method executes the following JavaScript:
        /// Object.freeze(Object.prototype);
        /// Object.freeze(Array.prototype);
        /// Object.freeze(Function.prototype);
        /// Object.freeze(String.prototype);
        /// Object.freeze(Number.prototype);
        /// Object.freeze(Boolean.prototype);
        /// </summary>
        public void ApplyPrototypeHardening()
        {
            try
            {
                string hardeningScript = @"
                    Object.freeze(Object.prototype);
                    Object.freeze(Array.prototype);
                    Object.freeze(Function.prototype);
                    Object.freeze(String.prototype);
                    Object.freeze(Number.prototype);
                    Object.freeze(Boolean.prototype);
                ";

                var interpreter = new Interpreter();
                var lexer = new Lexer(hardeningScript);
                var parser = new Parser(lexer);
                var program = parser.ParseProgram();

                if (parser.Errors.Count == 0)
                {
                    interpreter.Eval(program, _globalEnv, _context);
                    FenLogger.Info("[FenRuntime] Prototype hardening applied successfully", LogCategory.JavaScript);
                }
                else
                {
                    FenLogger.Error($"[FenRuntime] Prototype hardening parse error: {string.Join(", ", parser.Errors)}", LogCategory.JavaScript);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[FenRuntime] Failed to apply prototype hardening: {ex.Message}", LogCategory.JavaScript);
            }
        }

        public Action<string> OnConsoleMessage; // Delegate for console output
        public Uri BaseUri { get; set; }

        private string GetCurrentOrigin()
        {
             if (BaseUri  == null) return "null";
             return $"{BaseUri.Scheme}://{BaseUri.Host}:{BaseUri.Port}";
        }

        private async Task<HttpResponseMessage> SendNetworkRequestAsync(HttpRequestMessage request)
        {
            var handler = NetworkFetchHandler;
            if (handler == null)
                throw new InvalidOperationException("Network fetch handler not configured on runtime");

            return await handler(request).ConfigureAwait(false);
        }

        private async Task<string> FetchWorkerScriptAsync(Uri scriptUri)
        {
            if (scriptUri == null)
                throw new ArgumentNullException(nameof(scriptUri));

            using var request = new HttpRequestMessage(HttpMethod.Get, scriptUri);
            var response = await SendNetworkRequestAsync(request).ConfigureAwait(false);
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

            // Dedicated worker scripts are restricted to same-origin in the current policy profile.
            if (BaseUri != null && !CorsHandler.IsSameOrigin(scriptUri, BaseUri))
                return false;

            return true;
        }

        public IValue ExecuteFunction(FenFunction func, FenValue[] args)
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
        private static string InspectObject(FenValue value, int depth)
        {
            if (depth > 3) return "..."; // Prevent infinite recursion
            if (value  == null) return "null";
            if (value.IsUndefined) return "undefined";
            if (value == null) return "null";
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
                    sb.Append(k).Append(": ").Append(InspectObject(v, depth + 1));
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

            // ============================================
            // CORE ERROR TYPES (Initialized first)
            // ============================================
            
            // 1. Error.prototype
            var errorProto = new FenObject();
            
            // ============================================
            // CORE CONSTRUCTORS (Refactored to FenFunction)
            // ============================================
            var window = new FenObject();
            var objectProto = new FenObject();
            var arrayProto = new FenObject();
            var stringProto = new FenObject();
            var numberProto = new FenObject();
            var booleanProto = new FenObject();

            // Object
            var objectCtor = new FenFunction("Object", (args, thisVal) => {
                if (args.Length > 0 && !args[0].IsNull && !args[0].IsUndefined)
                    return args[0].IsObject ? args[0] : FenValue.FromObject(new FenObject());
                return FenValue.FromObject(new FenObject());
            });
            objectCtor.Prototype = objectProto;
            objectCtor.Set("prototype", FenValue.FromObject(objectProto));
            objectProto.Set("constructor", FenValue.FromFunction(objectCtor));
            SetGlobal("Object", FenValue.FromFunction(objectCtor));
            window.Set("Object", FenValue.FromFunction(objectCtor));
            
            // Object static methods
            objectCtor.Set("keys", FenValue.FromFunction(new FenFunction("keys", (args, thisVal) => {
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                    return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                var obj = args[0].AsObject();
                if (obj == null) return FenValue.FromObject(FenObject.CreateArray());
                var keys = obj.Keys()?.ToList() ?? new List<string>();
                var arr = FenObject.CreateArray();
                for (int i = 0; i < keys.Count; i++)
                    arr.Set(i.ToString(), FenValue.FromString(keys[i]));
                arr.Set("length", FenValue.FromNumber(keys.Count));
                return FenValue.FromObject(arr);
            })));
            
            objectCtor.Set("values", FenValue.FromFunction(new FenFunction("values", (args, thisVal) => {
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                    return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                var obj = args[0].AsObject();
                if (obj == null) return FenValue.FromObject(FenObject.CreateArray());
                var keys = obj.Keys()?.ToList() ?? new List<string>();
                var arr = FenObject.CreateArray();
                for (int i = 0; i < keys.Count; i++)
                    arr.Set(i.ToString(), obj.Get(keys[i]));
                arr.Set("length", FenValue.FromNumber(keys.Count));
                return FenValue.FromObject(arr);
            })));
            
            objectCtor.Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) => {
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                    return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                var obj = args[0].AsObject();
                if (obj == null) return FenValue.FromObject(FenObject.CreateArray());
                var keys = obj.Keys()?.ToList() ?? new List<string>();
                var arr = FenObject.CreateArray();
                for (int i = 0; i < keys.Count; i++)
                {
                    var entry = FenObject.CreateArray();
                    entry.Set("0", FenValue.FromString(keys[i]));
                    entry.Set("1", obj.Get(keys[i]));
                    entry.Set("length", FenValue.FromNumber(2));
                    arr.Set(i.ToString(), FenValue.FromObject(entry));
                }
                arr.Set("length", FenValue.FromNumber(keys.Count));
                return FenValue.FromObject(arr);
            })));
            
            objectCtor.Set("assign", FenValue.FromFunction(new FenFunction("assign", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                var target = args[0].AsObject();
                if (target == null) return args[0];
                for (int i = 1; i < args.Length; i++)
                {
                    var source = args[i].AsObject();
                    if (source == null) continue;
                    var keys = source.Keys()?.ToList() ?? new List<string>();
                    foreach (var key in keys)
                        target.Set(key, source.Get(key));
                }
                return FenValue.FromObject(target);
            })));
            
            // Object.create is defined later (line ~377) with full propertiesObject support.

            objectCtor.Set("defineProperty", FenValue.FromFunction(new FenFunction("defineProperty", (args, thisVal) => {
                if (args.Length < 3) return FenValue.FromError("TypeError: Object.defineProperty requires 3 arguments");
                var obj = args[0].AsObject();
                if (obj == null) return FenValue.FromError("TypeError: Object.defineProperty called on non-object");
                var prop = args[1].AsString(_context); // Ensure string conversion happens correctly
                
                try
                {
                    var desc = ToPropertyDescriptor(args[2], _context);
                    if (!obj.DefineOwnProperty(prop, desc))
                    {
                        return FenValue.FromError($"TypeError: Cannot redefine property: {prop}");
                    }
                }
                catch (InvalidOperationException ex)
                {
                     return FenValue.FromError($"TypeError: {ex.Message}");
                }
                
                return args[0];
            })));

            objectCtor.Set("defineProperties", FenValue.FromFunction(new FenFunction("defineProperties", (args, thisVal) => {
                if (args.Length < 2) return FenValue.FromError("TypeError: Object.defineProperties requires 2 arguments");
                var obj = args[0].AsObject();
                if (obj == null) return FenValue.FromError("TypeError: Object.defineProperties called on non-object");
                var props = args[1].AsObject();
                if (props == null) return FenValue.FromError("TypeError: Properties argument must be an object");

                // Get all keys first to simulate atomic-ish behavior (though ES spec says sequential)
                // Actually spec says: 1. Get all descriptors. 2. Define them.
                var descriptors = new Dictionary<string, PropertyDescriptor>();
                var keys = props.Keys(_context) ?? Enumerable.Empty<string>();
                
                foreach (var key in keys)
                {
                     var propDescObj = props.Get(key, _context);
                     try
                     {
                         descriptors[key] = ToPropertyDescriptor(propDescObj, _context);
                     }
                     catch (InvalidOperationException ex)
                     {
                         return FenValue.FromError($"TypeError: {ex.Message}");
                     }
                }

                foreach (var kvp in descriptors)
                {
                    if (!obj.DefineOwnProperty(kvp.Key, kvp.Value))
                    {
                         return FenValue.FromError($"TypeError: Cannot redefine property: {kvp.Key}");
                    }
                }
                
                return args[0];
            })));

            objectCtor.Set("create", FenValue.FromFunction(new FenFunction("create", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromError("TypeError: Object.create requires at least 1 argument");
                
                IObject proto = null;
                if (args[0].IsNull) proto = null;
                else if (args[0].IsObject) proto = args[0].AsObject();
                else return FenValue.FromError("TypeError: Object prototype may only be an Object or null");

                var obj = new FenObject();
                obj.SetPrototype(proto);
                
                if (args.Length > 1 && !args[1].IsUndefined)
                {
                    var props = args[1].AsObject();
                     if (props == null) return FenValue.FromError("TypeError: Properties argument must be an object");

                    // Re-use logic from defineProperties? Or just copy-paste for safety
                    var keys = props.Keys(_context) ?? Enumerable.Empty<string>();
                    foreach (var key in keys)
                    {
                        var propDescObj = props.Get(key, _context);
                        try
                        {
                            var desc = ToPropertyDescriptor(propDescObj, _context);
                            if (!obj.DefineOwnProperty(key, desc))
                            {
                                return FenValue.FromError($"TypeError: Cannot define property: {key}");
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                             return FenValue.FromError($"TypeError: {ex.Message}");
                        }
                    }
                }
                
                return FenValue.FromObject(obj);
            })));

            objectCtor.Set("getOwnPropertyDescriptor", FenValue.FromFunction(new FenFunction("getOwnPropertyDescriptor", (args, thisVal) => {
                if (args.Length < 2) return FenValue.FromError("TypeError: Object.getOwnPropertyDescriptor requires 2 arguments");
                var obj = args[0].AsObject();
                // Coerce to object if primitive? ES6 says yes, ES5 says throw. 
                // Test262 usually assumes ES6+ but strict mode might vary. 
                // FenEngine usually follows loose ES6.
                if (obj == null && !args[0].IsNull && !args[0].IsUndefined)
                {
                     // Attempt auto-boxing
                     // For now, return undefined if not object to match old behavior or throw?
                     // Let's throw for now to catch issues.
                     return FenValue.FromError("TypeError: Object.getOwnPropertyDescriptor called on non-object");
                }
                
                var prop = args[1].AsString(_context);
                var desc = obj.GetOwnPropertyDescriptor(prop);
                
                if (desc.HasValue)
                {
                    return FromPropertyDescriptor(desc.Value);
                }
                return FenValue.Undefined;
            })));

            objectCtor.Set("getPrototypeOf", FenValue.FromFunction(new FenFunction("getPrototypeOf", (args, thisVal) => {
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                    return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                var obj = args[0].AsObject();
                // ES6: ToObject(args[0])
                if (obj == null) return FenValue.FromObject(new FenObject()); // Fallback? Or should we box?
                
                var proto = obj.GetPrototype();
                return proto != null ? FenValue.FromObject(proto) : FenValue.Null;
            })));
            
            objectCtor.Set("setPrototypeOf", FenValue.FromFunction(new FenFunction("setPrototypeOf", (args, thisVal) => {
                if (args.Length < 2) return FenValue.FromError("TypeError: Object.setPrototypeOf requires 2 arguments");
                var obj = args[0].AsObject();
                if (obj == null) return FenValue.FromError("TypeError: Object.setPrototypeOf called on non-object");
                
                if (args[1].IsNull)
                {
                    obj.SetPrototype(null);
                }
                else if (args[1].IsObject)
                {
                    obj.SetPrototype(args[1].AsObject());
                }
                else
                {
                    return FenValue.FromError("TypeError: Object prototype may only be an Object or null");
                }
                return args[0];
            })));
            
            objectCtor.Set("freeze", FenValue.FromFunction(new FenFunction("freeze", (args, thisVal) => {
                if (args.Length == 0) return FenValue.Undefined;
                var obj = args[0].AsObject();
                if (obj != null) obj.Freeze();
                return args[0];
            })));
            
            objectCtor.Set("seal", FenValue.FromFunction(new FenFunction("seal", (args, thisVal) => {
                if (args.Length == 0) return FenValue.Undefined;
                var obj = args[0].AsObject();
                if (obj != null) obj.Seal();
                return args[0];
            })));
            
            objectCtor.Set("preventExtensions", FenValue.FromFunction(new FenFunction("preventExtensions", (args, thisVal) => {
                 if (args.Length == 0) return FenValue.Undefined;
                 var obj = args[0].AsObject();
                 if (obj != null) obj.PreventExtensions();
                 return args[0];
            })));

            objectCtor.Set("isExtensible", FenValue.FromFunction(new FenFunction("isExtensible", (args, thisVal) => {
                 if (args.Length == 0) return FenValue.FromBoolean(false);
                 var obj = args[0].AsObject();
                 return FenValue.FromBoolean(obj != null && obj.IsExtensible);
            })));
            
            objectCtor.Set("isSealed", FenValue.FromFunction(new FenFunction("isSealed", (args, thisVal) => {
                 if (args.Length == 0) return FenValue.FromBoolean(true);
                 var obj = args[0].AsObject();
                 return FenValue.FromBoolean(obj != null && obj.IsSealed());
            })));
            
            objectCtor.Set("isFrozen", FenValue.FromFunction(new FenFunction("isFrozen", (args, thisVal) => {
                 if (args.Length == 0) return FenValue.FromBoolean(true);
                 var obj = args[0].AsObject();
                 return FenValue.FromBoolean(obj != null && obj.IsFrozen());
            })));

            objectCtor.Set("is", FenValue.FromFunction(new FenFunction("is", (args, thisVal) => {
                if (args.Length < 2) return FenValue.FromBoolean(false);
                var a = args[0];
                var b = args[1];
                // SameValue algorithm (ES6)
                if (a.Type != b.Type) return FenValue.FromBoolean(false);
                if (a.IsNumber && b.IsNumber)
                {
                    var an = a.ToNumber();
                    var bn = b.ToNumber();
                    if (double.IsNaN(an) && double.IsNaN(bn)) return FenValue.FromBoolean(true);
                    if (an == 0 && bn == 0)
                    {
                        // Distinguish +0 and -0
                        return FenValue.FromBoolean(double.IsPositiveInfinity(1.0 / an) == double.IsPositiveInfinity(1.0 / bn));
                    }
                    return FenValue.FromBoolean(an == bn);
                }
                return FenValue.FromBoolean(a.Equals(b));
            })));

            // Object.prototype.toString
            objectProto.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) => {
                // Debug logging
                // Console.WriteLine($"[DEBUG] Object.prototype.toString called on {thisVal.Type}");
                
                if (thisVal.IsUndefined) return FenValue.FromString("[object Undefined]");
                if (thisVal.IsNull) return FenValue.FromString("[object Null]");
                
                var obj = thisVal.AsObject(); 
                if (obj == null && thisVal.IsBoolean) return FenValue.FromString("[object Boolean]");
                if (obj == null && thisVal.IsNumber) return FenValue.FromString("[object Number]");
                if (obj == null && thisVal.IsString) return FenValue.FromString("[object String]");
                
                try {
                    var ctorVal = obj != null ? obj.Get("constructor") : FenValue.Undefined;
                    var ctor = ctorVal.AsFunction();
                    var type = ctor?.Name ?? "Object";
                    return FenValue.FromString($"[object {type}]");
                } catch (Exception ex) {
                    return FenValue.FromString($"[object Error: {ex.GetType().Name} - {ex.Message}]");
                }
            })));

            // Array
            var arrayCtor = new FenFunction("Array", (args, thisVal) => {
                var arr = FenObject.CreateArray();
                if (args.Length == 1 && args[0].IsNumber) {
                    arr.Set("length", args[0]);
                } else {
                    for(int i=0; i<args.Length; i++) arr.Set(i.ToString(), args[i]);
                    arr.Set("length", FenValue.FromNumber(args.Length));
                }
                return FenValue.FromObject(arr);
            });
            arrayCtor.Prototype = arrayProto;
            arrayCtor.Set("prototype", FenValue.FromObject(arrayProto));
            arrayProto.Set("constructor", FenValue.FromFunction(arrayCtor));
            SetGlobal("Array", FenValue.FromFunction(arrayCtor));
            window.Set("Array", FenValue.FromFunction(arrayCtor));
            
            // Array static methods
            arrayCtor.Set("isArray", FenValue.FromFunction(new FenFunction("isArray", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var obj = args[0].AsObject();
                if (obj == null) return FenValue.FromBoolean(false);
                // Check if object has Array constructor
                var ctor = obj.Get("constructor").AsFunction();
                return FenValue.FromBoolean(ctor != null && ctor.Name == "Array");
            })));
            
            arrayCtor.Set("from", FenValue.FromFunction(new FenFunction("from", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromObject(FenObject.CreateArray());
                var source = args[0].AsObject();
                if (source == null) return FenValue.FromObject(FenObject.CreateArray());
                
                var arr = FenObject.CreateArray();
                var lengthVal = source.Get("length");
                var length = lengthVal.IsNumber ? (int)lengthVal.ToNumber() : 0;
                
                for (int i = 0; i < length; i++)
                {
                    arr.Set(i.ToString(), source.Get(i.ToString()));
                }
                arr.Set("length", FenValue.FromNumber(length));
                return FenValue.FromObject(arr);
            })));
            
            arrayCtor.Set("of", FenValue.FromFunction(new FenFunction("of", (args, thisVal) => {
                var arr = FenObject.CreateArray();
                for (int i = 0; i < args.Length; i++)
                {
                    arr.Set(i.ToString(), args[i]);
                }
                arr.Set("length", FenValue.FromNumber(args.Length));
                return FenValue.FromObject(arr);
            })));
            
            // Array.prototype methods
            arrayProto.Set("find", FenValue.FromFunction(new FenFunction("find", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) return FenValue.FromError("TypeError: callback is not a function");
                var thisArg = args.Length > 1 ? args[1] : FenValue.Undefined;
                var len = arr.Get("length").ToNumber();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var result = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) }, _context);
                    if (result.ToBoolean()) return elem;
                }
                return FenValue.Undefined;
            })));
            
            arrayProto.Set("findIndex", FenValue.FromFunction(new FenFunction("findIndex", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromNumber(-1);
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) return FenValue.FromError("TypeError: callback is not a function");
                var thisArg = args.Length > 1 ? args[1] : FenValue.Undefined;
                var len = arr.Get("length").ToNumber();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var result = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) }, _context);
                    if (result.ToBoolean()) return FenValue.FromNumber(i);
                }
                return FenValue.FromNumber(-1);
            })));
            
            arrayProto.Set("fill", FenValue.FromFunction(new FenFunction("fill", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return thisVal;
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                var len = (int)arr.Get("length").ToNumber();
                var start = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                var end = args.Length > 2 ? (int)args[2].ToNumber() : len;
                if (start < 0) start = Math.Max(0, len + start);
                if (end < 0) end = Math.Max(0, len + end);
                for (int i = start; i < Math.Min(end, len); i++)
                {
                    arr.Set(i.ToString(), value);
                }
                return thisVal;
            })));
            
            arrayProto.Set("includes", FenValue.FromFunction(new FenFunction("includes", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromBoolean(false);
                var searchElement = args.Length > 0 ? args[0] : FenValue.Undefined;
                var len = (int)arr.Get("length").ToNumber();
                var fromIndex = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (fromIndex < 0) fromIndex = Math.Max(0, len + fromIndex);
                for (int i = fromIndex; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    // Use SameValueZero comparison (NaN === NaN, +0 === -0)
                    if (elem.Type == searchElement.Type && elem.LooseEquals(searchElement)) return FenValue.FromBoolean(true);
                    if (elem.IsNumber && searchElement.IsNumber && double.IsNaN(elem.ToNumber()) && double.IsNaN(searchElement.ToNumber())) return FenValue.FromBoolean(true);
                }
                return FenValue.FromBoolean(false);
            })));
            
            arrayProto.Set("map", FenValue.FromFunction(new FenFunction("map", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromObject(FenObject.CreateArray());
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) return FenValue.FromError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                var result = FenObject.CreateArray();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var mapped = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) }, _context);
                    result.Set(i.ToString(), mapped);
                }
                result.Set("length", FenValue.FromNumber(len));
                return FenValue.FromObject(result);
            })));
            
            arrayProto.Set("filter", FenValue.FromFunction(new FenFunction("filter", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromObject(FenObject.CreateArray());
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) return FenValue.FromError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                var result = FenObject.CreateArray();
                int resultIdx = 0;
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var keep = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) }, _context);
                    if (keep.ToBoolean())
                    {
                        result.Set(resultIdx.ToString(), elem);
                        resultIdx++;
                    }
                }
                result.Set("length", FenValue.FromNumber(resultIdx));
                return FenValue.FromObject(result);
            })));
            
            arrayProto.Set("reduce", FenValue.FromFunction(new FenFunction("reduce", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) return FenValue.FromError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                if (len == 0 && args.Length < 2) return FenValue.FromError("TypeError: Reduce of empty array with no initial value");
                var accumulator = args.Length > 1 ? args[1] : arr.Get("0");
                int startIdx = args.Length > 1 ? 0 : 1;
                for (int i = startIdx; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    accumulator = callback.Invoke(new[] { accumulator, elem, FenValue.FromNumber(i), FenValue.FromObject(arr) }, _context);
                }
                return accumulator;
            })));
            
            arrayProto.Set("forEach", FenValue.FromFunction(new FenFunction("forEach", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) return FenValue.FromError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) }, _context);
                }
                return FenValue.Undefined;
            })));
            
            arrayProto.Set("some", FenValue.FromFunction(new FenFunction("some", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromBoolean(false);
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) return FenValue.FromError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var result = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) }, _context);
                    if (result.ToBoolean()) return FenValue.FromBoolean(true);
                }
                return FenValue.FromBoolean(false);
            })));
            
            arrayProto.Set("every", FenValue.FromFunction(new FenFunction("every", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromBoolean(true);
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) return FenValue.FromError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var result = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) }, _context);
                    if (!result.ToBoolean()) return FenValue.FromBoolean(false);
                }
                return FenValue.FromBoolean(true);
            })));
            
            arrayProto.Set("slice", FenValue.FromFunction(new FenFunction("slice", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromObject(FenObject.CreateArray());
                var len = (int)arr.Get("length").ToNumber();
                var start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                var end = args.Length > 1 ? (int)args[1].ToNumber() : len;
                if (start < 0) start = Math.Max(0, len + start);
                if (end < 0) end = Math.Max(0, len + end);
                var result = FenObject.CreateArray();
                int resultIdx = 0;
                for (int i = start; i < Math.Min(end, len); i++)
                {
                    result.Set(resultIdx.ToString(), arr.Get(i.ToString()));
                    resultIdx++;
                }
                result.Set("length", FenValue.FromNumber(resultIdx));
                return FenValue.FromObject(result);
            })));
            
            arrayProto.Set("indexOf", FenValue.FromFunction(new FenFunction("indexOf", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromNumber(-1);
                var searchElement = args.Length > 0 ? args[0] : FenValue.Undefined;
                var len = (int)arr.Get("length").ToNumber();
                var fromIndex = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (fromIndex < 0) fromIndex = Math.Max(0, len + fromIndex);
                for (int i = fromIndex; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    if (elem.Type == searchElement.Type && elem.LooseEquals(searchElement))
                        return FenValue.FromNumber(i);
                }
                return FenValue.FromNumber(-1);
            })));
            
            arrayProto.Set("lastIndexOf", FenValue.FromFunction(new FenFunction("lastIndexOf", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromNumber(-1);
                var searchElement = args.Length > 0 ? args[0] : FenValue.Undefined;
                var len = (int)arr.Get("length").ToNumber();
                var fromIndex = args.Length > 1 ? (int)args[1].ToNumber() : len - 1;
                if (fromIndex >= len) fromIndex = len - 1;
                if (fromIndex < 0) fromIndex = len + fromIndex;
                for (int i = fromIndex; i >= 0; i--)
                {
                    var elem = arr.Get(i.ToString());
                    if (elem.Type == searchElement.Type && elem.LooseEquals(searchElement))
                        return FenValue.FromNumber(i);
                }
                return FenValue.FromNumber(-1);
            })));
            
            arrayProto.Set("join", FenValue.FromFunction(new FenFunction("join", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromString("");
                var separator = args.Length > 0 && !args[0].IsUndefined ? args[0].AsString(_context) : ",";
                var len = (int)arr.Get("length").ToNumber();
                var sb = new StringBuilder();
                for (int i = 0; i < len; i++)
                {
                    if (i > 0) sb.Append(separator);
                    var elem = arr.Get(i.ToString());
                    if (!elem.IsNull && !elem.IsUndefined)
                        sb.Append(elem.AsString(_context));
                }
                return FenValue.FromString(sb.ToString());
            })));
            
            arrayProto.Set("concat", FenValue.FromFunction(new FenFunction("concat", (args, thisVal) => {
                var arr = thisVal.AsObject();
                var result = FenObject.CreateArray();
                int resultIdx = 0;
                // Add this array elements
                if (arr != null)
                {
                    var len = (int)arr.Get("length").ToNumber();
                    for (int i = 0; i < len; i++)
                    {
                        result.Set(resultIdx.ToString(), arr.Get(i.ToString()));
                        resultIdx++;
                    }
                }
                // Add argument elements
                for (int a = 0; a < args.Length; a++)
                {
                    var arg = args[a];
                    if (arg.IsObject)
                    {
                        var argObj = arg.AsObject();
                        var argLen = argObj.Get("length");
                        if (argLen.IsNumber)
                        {
                            // It's array-like, concat elements
                            var len = (int)argLen.ToNumber();
                            for (int i = 0; i < len; i++)
                            {
                                result.Set(resultIdx.ToString(), argObj.Get(i.ToString()));
                                resultIdx++;
                            }
                            continue;
                        }
                    }
                    // Not array-like, add as single element
                    result.Set(resultIdx.ToString(), arg);
                    resultIdx++;
                }
                result.Set("length", FenValue.FromNumber(resultIdx));
                return FenValue.FromObject(result);
            })));
            
            arrayProto.Set("reverse", FenValue.FromFunction(new FenFunction("reverse", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return thisVal;
                var len = (int)arr.Get("length").ToNumber();
                for (int i = 0; i < len / 2; i++)
                {
                    var temp = arr.Get(i.ToString());
                    arr.Set(i.ToString(), arr.Get((len - 1 - i).ToString()));
                    arr.Set((len - 1 - i).ToString(), temp);
                }
                return thisVal;
            })));
            
            arrayProto.Set("sort", FenValue.FromFunction(new FenFunction("sort", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return thisVal;
                var compareFn = args.Length > 0 && args[0].IsFunction ? args[0].AsFunction() : null;
                var len = (int)arr.Get("length").ToNumber();
                // Simple bubble sort for now (TODO: use better algorithm)
                for (int i = 0; i < len - 1; i++)
                {
                    for (int j = 0; j < len - i - 1; j++)
                    {
                        var a = arr.Get(j.ToString());
                        var b = arr.Get((j + 1).ToString());
                        bool shouldSwap = false;
                        if (compareFn != null)
                        {
                            var result = compareFn.Invoke(new[] { a, b }, _context);
                            shouldSwap = result.ToNumber() > 0;
                        }
                        else
                        {
                            shouldSwap = string.Compare(a.AsString(_context), b.AsString(_context), StringComparison.Ordinal) > 0;
                        }
                        if (shouldSwap)
                        {
                            arr.Set(j.ToString(), b);
                            arr.Set((j + 1).ToString(), a);
                        }
                    }
                }
                return thisVal;
            })));

            
            arrayProto.Set("push", FenValue.FromFunction(new FenFunction("push", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromNumber(0);
                var len = (int)arr.Get("length").ToNumber();
                for (int i = 0; i < args.Length; i++)
                    arr.Set((len + i).ToString(), args[i]);
                var newLen = len + args.Length;
                arr.Set("length", FenValue.FromNumber(newLen));
                return FenValue.FromNumber(newLen);
            })));
            
            arrayProto.Set("pop", FenValue.FromFunction(new FenFunction("pop", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var len = (int)arr.Get("length").ToNumber();
                if (len == 0) return FenValue.Undefined;
                var result = arr.Get((len - 1).ToString());
                arr.Set("length", FenValue.FromNumber(len - 1));
                return result;
            })));
            
            arrayProto.Set("shift", FenValue.FromFunction(new FenFunction("shift", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var len = (int)arr.Get("length").ToNumber();
                if (len == 0) return FenValue.Undefined;
                var first = arr.Get("0");
                for (int i = 1; i < len; i++)
                    arr.Set((i - 1).ToString(), arr.Get(i.ToString()));
                arr.Set("length", FenValue.FromNumber(len - 1));
                return first;
            })));
            
            arrayProto.Set("unshift", FenValue.FromFunction(new FenFunction("unshift", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromNumber(0);
                var len = (int)arr.Get("length").ToNumber();
                for (int i = len - 1; i >= 0; i--)
                    arr.Set((i + args.Length).ToString(), arr.Get(i.ToString()));
                for (int i = 0; i < args.Length; i++)
                    arr.Set(i.ToString(), args[i]);
                var newLen = len + args.Length;
                arr.Set("length", FenValue.FromNumber(newLen));
                return FenValue.FromNumber(newLen);
            })));
            // Boolean
            var booleanCtor = new FenFunction("Boolean", (args, thisVal) => {
                var val = args.Length > 0 ? args[0].ToBoolean() : false;
                if (!thisVal.IsUndefined && thisVal.AsObject()?.GetPrototype() == booleanProto) {
                    thisVal.AsObject().Set("__value__", FenValue.FromBoolean(val));
                    return thisVal;
                }
                return FenValue.FromBoolean(val);
            });
            booleanCtor.Prototype = booleanProto;
            booleanCtor.Set("prototype", FenValue.FromObject(booleanProto));
            booleanProto.Set("constructor", FenValue.FromFunction(booleanCtor));
            SetGlobal("Boolean", FenValue.FromFunction(booleanCtor));
            window.Set("Boolean", FenValue.FromFunction(booleanCtor));

            // Boolean.prototype.toString
            booleanProto.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) => {
                bool b = false;
                if (thisVal.IsBoolean) b = thisVal.ToBoolean();
                else if (thisVal.IsObject && thisVal.AsObject().Has("__value__")) {
                    var inner = thisVal.AsObject().Get("__value__");
                    if (inner.IsBoolean) b = inner.ToBoolean();
                    else throw new InvalidOperationException("TypeError: Boolean.prototype.toString called on incompatible object");
                }
                else throw new InvalidOperationException("TypeError: Boolean.prototype.toString called on incompatible object");
                
                return FenValue.FromString(b ? "true" : "false");
            })));

            // Boolean.prototype.valueOf
            booleanProto.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) => {
                if (thisVal.IsBoolean) return thisVal;
                if (thisVal.IsObject && thisVal.AsObject().Has("__value__")) {
                    var inner = thisVal.AsObject().Get("__value__");
                    if (inner.IsBoolean) return inner;
                }
                throw new InvalidOperationException("TypeError: Boolean.prototype.valueOf called on incompatible object");
            })));

            // String
            var stringCtor = new FenFunction("String", (args, thisVal) => {
                var val = args.Length > 0 ? args[0].ToString() : "";
                if (!thisVal.IsUndefined && thisVal.AsObject()?.GetPrototype() == stringProto) {
                    thisVal.AsObject().Set("__value__", FenValue.FromString(val));
                    return thisVal;
                }
                return FenValue.FromString(val);
            });
            stringCtor.Prototype = stringProto;
            stringCtor.Set("prototype", FenValue.FromObject(stringProto));
            stringProto.Set("constructor", FenValue.FromFunction(stringCtor));
            SetGlobal("String", FenValue.FromFunction(stringCtor));
            window.Set("String", FenValue.FromFunction(stringCtor));
            
            // String.prototype methods
            stringProto.Set("repeat", FenValue.FromFunction(new FenFunction("repeat", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var count = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (count < 0 || double.IsInfinity(count)) return FenValue.FromError("RangeError: Invalid count value");
                if (count == 0) return FenValue.FromString("");
                var sb = new StringBuilder();
                for (int i = 0; i < count; i++) sb.Append(str);
                return FenValue.FromString(sb.ToString());
            })));
            
            stringProto.Set("includes", FenValue.FromFunction(new FenFunction("includes", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var searchString = args.Length > 0 ? args[0].AsString(_context) : "";
                var position = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (position < 0) position = 0;
                return FenValue.FromBoolean(str.IndexOf(searchString, position, StringComparison.Ordinal) >= 0);
            })));
            
            stringProto.Set("padStart", FenValue.FromFunction(new FenFunction("padStart", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var targetLength = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (targetLength <= str.Length) return FenValue.FromString(str);
                var fillString = args.Length > 1 ? args[1].AsString(_context) : " ";
                if (fillString.Length == 0) return FenValue.FromString(str);
                var padLength = targetLength - str.Length;
                var sb = new StringBuilder();
                while (sb.Length < padLength) sb.Append(fillString);
                sb.Length = padLength; // Truncate to exact length
                sb.Append(str);
                return FenValue.FromString(sb.ToString());
            })));
            
            stringProto.Set("padEnd", FenValue.FromFunction(new FenFunction("padEnd", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var targetLength = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (targetLength <= str.Length) return FenValue.FromString(str);
                var fillString = args.Length > 1 ? args[1].AsString(_context) : " ";
                if (fillString.Length == 0) return FenValue.FromString(str);
                var padLength = targetLength - str.Length;
                var sb = new StringBuilder(str);
                while (sb.Length < targetLength) sb.Append(fillString);
                sb.Length = targetLength; // Truncate to exact length
                return FenValue.FromString(sb.ToString());
            })));
            
            stringProto.Set("trim", FenValue.FromFunction(new FenFunction("trim", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                return FenValue.FromString(str.Trim());
            })));
            
            stringProto.Set("trimStart", FenValue.FromFunction(new FenFunction("trimStart", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                return FenValue.FromString(str.TrimStart());
            })));
            
            stringProto.Set("trimLeft", FenValue.FromFunction(new FenFunction("trimLeft", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                return FenValue.FromString(str.TrimStart());
            })));
            
            stringProto.Set("trimEnd", FenValue.FromFunction(new FenFunction("trimEnd", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                return FenValue.FromString(str.TrimEnd());
            })));
            
            stringProto.Set("trimRight", FenValue.FromFunction(new FenFunction("trimRight", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                return FenValue.FromString(str.TrimEnd());
            })));
            
            stringProto.Set("startsWith", FenValue.FromFunction(new FenFunction("startsWith", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var searchString = args.Length > 0 ? args[0].AsString(_context) : "";
                var position = args.Length > 1 ? Math.Max(0, (int)args[1].ToNumber()) : 0;
                if (position >= str.Length) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(str.Substring(position).StartsWith(searchString));
            })));
            
            stringProto.Set("endsWith", FenValue.FromFunction(new FenFunction("endsWith", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var searchString = args.Length > 0 ? args[0].AsString(_context) : "";
                var endPosition = args.Length > 1 ? (int)args[1].ToNumber() : str.Length;
                if (endPosition < 0) endPosition = 0;
                if (endPosition > str.Length) endPosition = str.Length;
                var substr = str.Substring(0, endPosition);
                return FenValue.FromBoolean(substr.EndsWith(searchString));
            })));
            
            stringProto.Set("slice", FenValue.FromFunction(new FenFunction("slice", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var len = str.Length;
                var start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                var end = args.Length > 1 ? (int)args[1].ToNumber() : len;
                if (start < 0) start = Math.Max(0, len + start);
                if (end < 0) end = Math.Max(0, len + end);
                if (start >= len) return FenValue.FromString("");
                return FenValue.FromString(str.Substring(start, Math.Max(0, Math.Min(end, len) - start)));
            })));
            
            stringProto.Set("substring", FenValue.FromFunction(new FenFunction("substring", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var len = str.Length;
                var start = args.Length > 0 ? Math.Max(0, (int)args[0].ToNumber()) : 0;
                var end = args.Length > 1 ? Math.Max(0, (int)args[1].ToNumber()) : len;
                if (start > end) { var temp = start; start = end; end = temp; }
                if (start >= len) return FenValue.FromString("");
                return FenValue.FromString(str.Substring(start, Math.Min(end - start, len - start)));
            })));
            
            stringProto.Set("charAt", FenValue.FromFunction(new FenFunction("charAt", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (index < 0 || index >= str.Length) return FenValue.FromString("");
                return FenValue.FromString(str[index].ToString());
            })));
            
            stringProto.Set("charCodeAt", FenValue.FromFunction(new FenFunction("charCodeAt", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (index < 0 || index >= str.Length) return FenValue.FromNumber(double.NaN);
                return FenValue.FromNumber((int)str[index]);
            })));
            
            stringProto.Set("indexOf", FenValue.FromFunction(new FenFunction("indexOf", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var searchString = args.Length > 0 ? args[0].AsString(_context) : "";
                var position = args.Length > 1 ? Math.Max(0, (int)args[1].ToNumber()) : 0;
                var result = str.IndexOf(searchString, position, StringComparison.Ordinal);
                return FenValue.FromNumber(result);
            })));
            
            stringProto.Set("lastIndexOf", FenValue.FromFunction(new FenFunction("lastIndexOf", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var searchString = args.Length > 0 ? args[0].AsString(_context) : "";
                var position = args.Length > 1 ? (int)args[1].ToNumber() : int.MaxValue;
                if (position < 0) return FenValue.FromNumber(-1);
                var searchStart = Math.Min(position, str.Length - searchString.Length);
                if (searchStart < 0) searchStart = 0;
                var result = str.LastIndexOf(searchString, searchStart, StringComparison.Ordinal);
                return FenValue.FromNumber(result);
            })));
            
            stringProto.Set("split", FenValue.FromFunction(new FenFunction("split", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                var separator = args.Length > 0 ? args[0] : FenValue.Undefined;
                var limit = args.Length > 1 ? (int)args[1].ToNumber() : int.MaxValue;
                var result = FenObject.CreateArray();
                
                if (separator.IsUndefined)
                {
                    result.Set("0", FenValue.FromString(str));
                    result.Set("length", FenValue.FromNumber(1));
                    return FenValue.FromObject(result);
                }
                
                var sepStr = separator.AsString(_context);
                if (sepStr == "")
                {
                    // Split into individual characters
                    int count = Math.Min(str.Length, limit);
                    for (int i = 0; i < count; i++)
                    {
                        result.Set(i.ToString(), FenValue.FromString(str[i].ToString()));
                    }
                    result.Set("length", FenValue.FromNumber(count));
                    return FenValue.FromObject(result);
                }
                
                var parts = str.Split(new[] { sepStr }, limit, StringSplitOptions.None);
                for (int i = 0; i < parts.Length; i++)
                {
                    result.Set(i.ToString(), FenValue.FromString(parts[i]));
                }
                result.Set("length", FenValue.FromNumber(parts.Length));
                return FenValue.FromObject(result);
            })));

            
            stringProto.Set("toLowerCase", FenValue.FromFunction(new FenFunction("toLowerCase", (args, thisVal) => {
                return FenValue.FromString(thisVal.AsString(_context).ToLowerInvariant());
            })));
            
            stringProto.Set("toUpperCase", FenValue.FromFunction(new FenFunction("toUpperCase", (args, thisVal) => {
                return FenValue.FromString(thisVal.AsString(_context).ToUpperInvariant());
            })));
            
            stringProto.Set("replace", FenValue.FromFunction(new FenFunction("replace", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                if (args.Length < 2) return FenValue.FromString(str);
                var search = args[0].AsString(_context);
                var replace = args[1].AsString(_context);
                var index = str.IndexOf(search, StringComparison.Ordinal);
                if (index >= 0)
                    return FenValue.FromString(str.Substring(0, index) + replace + str.Substring(index + search.Length));
                return FenValue.FromString(str);
            })));            
            stringProto.Set("match", FenValue.FromFunction(new FenFunction("match", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                if (args.Length == 0) return FenValue.Null;
                var regexArg = args[0];
                System.Text.RegularExpressions.Regex regex = null;
                
                if (regexArg.IsObject && (regexArg.AsObject() as FenObject)?.NativeObject is System.Text.RegularExpressions.Regex r)
                    regex = r;
                else
                {
                    try { regex = new System.Text.RegularExpressions.Regex(regexArg.AsString(_context)); }
                    catch { return FenValue.Null; }
                }
                
                var matches = regex.Matches(str);
                if (matches.Count == 0) return FenValue.Null;
                
                var result = FenObject.CreateArray();
                for (int i = 0; i < matches.Count; i++)
                    result.Set(i.ToString(), FenValue.FromString(matches[i].Value));
                result.Set("length", FenValue.FromNumber(matches.Count));
                return FenValue.FromObject(result);
            })));
            
            stringProto.Set("search", FenValue.FromFunction(new FenFunction("search", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                if (args.Length == 0) return FenValue.FromNumber(-1);
                var regexArg = args[0];
                System.Text.RegularExpressions.Regex regex = null;
                
                if (regexArg.IsObject && (regexArg.AsObject() as FenObject)?.NativeObject is System.Text.RegularExpressions.Regex r)
                    regex = r;
                else
                {
                    try { regex = new System.Text.RegularExpressions.Regex(regexArg.AsString(_context)); }
                    catch { return FenValue.FromNumber(-1); }
                }
                
                var match = regex.Match(str);
                return FenValue.FromNumber(match.Success ? match.Index : -1);
            })));

            // Number
            var numberCtor = new FenFunction("Number", (args, thisVal) => {
                var val = args.Length > 0 ? args[0].ToNumber() : 0.0;
                if (!thisVal.IsUndefined && thisVal.AsObject()?.GetPrototype() == numberProto) {
                    thisVal.AsObject().Set("__value__", FenValue.FromNumber(val));
                    return thisVal;
                }
                return FenValue.FromNumber(val);
            });
            numberCtor.Prototype = numberProto;
            
            numberProto.Set("toFixed", FenValue.FromFunction(new FenFunction("toFixed", (args, thisVal) => {
                var num = thisVal.IsNumber ? thisVal.ToNumber() : (thisVal.AsObject()?.Get("__value__").ToNumber() ?? double.NaN);
                var digits = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (double.IsNaN(num) || double.IsInfinity(num)) return FenValue.FromString(num.ToString());
                return FenValue.FromString(num.ToString("F" + Math.Max(0, Math.Min(20, digits))));
            })));
            
            numberProto.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) => {
                var num = thisVal.IsNumber ? thisVal.ToNumber() : (thisVal.AsObject()?.Get("__value__").ToNumber() ?? double.NaN);
                var radix = args.Length > 0 ? (int)args[0].ToNumber() : 10;
                if (radix < 2 || radix > 36) return FenValue.FromError("RangeError: radix must be between 2 and 36");
                if (radix == 10 || double.IsNaN(num) || double.IsInfinity(num)) return FenValue.FromString(num.ToString());
                try { return FenValue.FromString(Convert.ToString((long)num, radix)); }
                catch { return FenValue.FromString(num.ToString()); }
            })));
            numberCtor.Set("prototype", FenValue.FromObject(numberProto));
            numberProto.Set("constructor", FenValue.FromFunction(numberCtor));
            
            // Number static methods
            numberCtor.Set("isNaN", FenValue.FromFunction(new FenFunction("isNaN", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var val = args[0];
                return FenValue.FromBoolean(val.IsNumber && double.IsNaN(val.ToNumber()));
            })));
            
            numberCtor.Set("isFinite", FenValue.FromFunction(new FenFunction("isFinite", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var val = args[0];
                if (!val.IsNumber) return FenValue.FromBoolean(false);
                var num = val.ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num));
            })));
            
            numberCtor.Set("isInteger", FenValue.FromFunction(new FenFunction("isInteger", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var val = args[0];
                if (!val.IsNumber) return FenValue.FromBoolean(false);
                var num = val.ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num) && Math.Floor(num) == num);
            })));
            
            numberCtor.Set("isSafeInteger", FenValue.FromFunction(new FenFunction("isSafeInteger", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var val = args[0];
                if (!val.IsNumber) return FenValue.FromBoolean(false);
                var num = val.ToNumber();
                const double MAX_SAFE_INTEGER = 9007199254740991.0; // 2^53 - 1
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num) && Math.Floor(num) == num && Math.Abs(num) <= MAX_SAFE_INTEGER);
            })));
            
            numberCtor.Set("parseFloat", FenValue.FromFunction(new FenFunction("parseFloat", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                var str = args[0].ToString().Trim();
                if (double.TryParse(str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double result))
                {
                    return FenValue.FromNumber(result);
                }
                return FenValue.FromNumber(double.NaN);
            })));
            
            numberCtor.Set("parseInt", FenValue.FromFunction(new FenFunction("parseInt", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                var str = args[0].ToString().Trim();
                int radix = args.Length > 1 ? (int)args[1].ToNumber() : 10;
                if (radix < 2 || radix > 36) return FenValue.FromNumber(double.NaN);
                
                try
                {
                    var result = Convert.ToInt64(str, radix);
                    return FenValue.FromNumber(result);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));
            
            // Number constants
            numberCtor.Set("EPSILON", FenValue.FromNumber(double.Epsilon));
            numberCtor.Set("MAX_VALUE", FenValue.FromNumber(double.MaxValue));
            numberCtor.Set("MIN_VALUE", FenValue.FromNumber(double.Epsilon)); // Smallest positive value
            numberCtor.Set("MAX_SAFE_INTEGER", FenValue.FromNumber(9007199254740991.0)); // 2^53 - 1
            numberCtor.Set("MIN_SAFE_INTEGER", FenValue.FromNumber(-9007199254740991.0)); // -(2^53 - 1)
            numberCtor.Set("POSITIVE_INFINITY", FenValue.FromNumber(double.PositiveInfinity));
            numberCtor.Set("NEGATIVE_INFINITY", FenValue.FromNumber(double.NegativeInfinity));
            numberCtor.Set("NaN", FenValue.FromNumber(double.NaN));
            
            SetGlobal("Number", FenValue.FromFunction(numberCtor));
            window.Set("Number", FenValue.FromFunction(numberCtor));
            




            // Function.prototype methods
            var functionProto = new FenObject();
            
            // Function.prototype.call(thisArg, ...args)
            functionProto.Set("call", FenValue.FromFunction(new FenFunction("call", (args, thisVal) => {
                if (!thisVal.IsFunction) return FenValue.FromError("TypeError: call called on non-function");
                var func = thisVal.AsFunction() as FenFunction;
                if (func == null) return FenValue.FromError("TypeError: call called on non-function");
                var newThis = args.Length > 0 ? args[0] : FenValue.Undefined;
                var funcArgs = args.Length > 1 ? args.Skip(1).ToArray() : new FenValue[0];
                if (func.IsNative && func.NativeImplementation != null)
                    return func.NativeImplementation(funcArgs, newThis);
                return func.Invoke(funcArgs, _context);
            })));
            
            // Function.prototype.apply(thisArg, argsArray)
            functionProto.Set("apply", FenValue.FromFunction(new FenFunction("apply", (args, thisVal) => {
                if (!thisVal.IsFunction) return FenValue.FromError("TypeError: apply called on non-function");
                var func = thisVal.AsFunction() as FenFunction;
                if (func == null) return FenValue.FromError("TypeError: apply called on non-function");
                var newThis = args.Length > 0 ? args[0] : FenValue.Undefined;
                FenValue[] funcArgs;
                var argsArray = args.Length > 1 ? args[1] : FenValue.Undefined;
                if (argsArray.IsUndefined || argsArray.IsNull)
                    funcArgs = new FenValue[0];
                else if (argsArray.IsObject)
                {
                    var arrObj = argsArray.AsObject();
                    var len = (int)arrObj.Get("length").ToNumber();
                    funcArgs = new FenValue[len];
                    for (int i = 0; i < len; i++)
                        funcArgs[i] = arrObj.Get(i.ToString());
                }
                else
                    return FenValue.FromError("TypeError: apply arguments not iterable");
                if (func.IsNative && func.NativeImplementation != null)
                    return func.NativeImplementation(funcArgs, newThis);
                return func.Invoke(funcArgs, _context);
            })));
            
            // Function.prototype.bind(thisArg, ...args)
            functionProto.Set("bind", FenValue.FromFunction(new FenFunction("bind", (args, thisVal) => {
                if (!thisVal.IsFunction) return FenValue.FromError("TypeError: bind called on non-function");
                var originalFunc = thisVal.AsFunction() as FenFunction;
                if (originalFunc == null) return FenValue.FromError("TypeError: bind called on non-function");
                var boundThis = args.Length > 0 ? args[0] : FenValue.Undefined;
                var boundArgs = args.Length > 1 ? args.Skip(1).ToArray() : new FenValue[0];
                var boundFunc = new FenFunction("bound " + (originalFunc.Name ?? "anonymous"), (callArgs, _) => {
                    var finalArgs = boundArgs.Concat(callArgs).ToArray();
                    if (originalFunc.IsNative && originalFunc.NativeImplementation != null)
                        return originalFunc.NativeImplementation(finalArgs, boundThis);
                    return originalFunc.Invoke(finalArgs, _context);
                });
                return FenValue.FromFunction(boundFunc);
            })));
            
            // Function.prototype.toString()
            functionProto.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) => {
                if (!thisVal.IsFunction) return FenValue.FromString("function () { [native code] }");
                var func = thisVal.AsFunction() as FenFunction;
                if (func != null && func.Source != null)
                    return FenValue.FromString(func.Source);
                var name = func?.Name ?? "anonymous";
                return FenValue.FromString("function " + name + "() { [native code] }");
            })));
            
            // Function.prototype.length (default 0)
            functionProto.Set("length", FenValue.FromNumber(0));
            functionProto.Set("name", FenValue.FromString(""));
            
            

            // Symbol
            long symbolIdCounter = 0;
            var symbolRegistry = new System.Collections.Generic.Dictionary<string, FenValue>();
            var symbolProto = new FenObject();
            var symbolCtor = new FenFunction("Symbol", (args, thisVal) => {
                // Symbol cannot be used with 'new'
                if (!thisVal.IsUndefined && thisVal.IsObject)
                    return FenValue.FromError("TypeError: Symbol is not a constructor");
                
                var description = args.Length > 0 && !args[0].IsUndefined ? args[0].AsString(_context) : "";
                var symbolObj = new FenObject();
                symbolObj.SetPrototype(symbolProto);
                symbolObj.Set("__description__", FenValue.FromString(description));
                symbolObj.Set("__id__", FenValue.FromNumber(++symbolIdCounter));
                return FenValue.FromObject(symbolObj);
            });
            
            symbolCtor.Set("prototype", FenValue.FromObject(symbolProto));
            symbolProto.Set("constructor", FenValue.FromFunction(symbolCtor));
            
            // Symbol.for(key) - global symbol registry
            symbolCtor.Set("for", FenValue.FromFunction(new FenFunction("for", (args, thisVal) => {
                var key = args.Length > 0 ? args[0].AsString(_context) : "";
                if (symbolRegistry.TryGetValue(key, out var existing))
                    return existing;
                var symbolObj = new FenObject();
                symbolObj.SetPrototype(symbolProto);
                symbolObj.Set("__description__", FenValue.FromString(key));
                symbolObj.Set("__key__", FenValue.FromString(key));
                symbolObj.Set("__id__", FenValue.FromNumber(++symbolIdCounter));
                var symbol = FenValue.FromObject(symbolObj);
                symbolRegistry[key] = symbol;
                return symbol;
            })));
            
            // Symbol.keyFor(symbol)
            symbolCtor.Set("keyFor", FenValue.FromFunction(new FenFunction("keyFor", (args, thisVal) => {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.Undefined;
                var symbolObj = args[0].AsObject() as FenObject;
                if (symbolObj?.GetPrototype() != symbolProto) return FenValue.Undefined;
                var key = symbolObj.Get("__key__");
                return key.IsUndefined ? FenValue.Undefined : key;
            })));
            
            // Well-known symbols
            FenValue CreateWellKnownSymbol(string name)
            {
                var symbolObj = new FenObject();
                symbolObj.SetPrototype(symbolProto);
                symbolObj.Set("__description__", FenValue.FromString("Symbol." + name));
                symbolObj.Set("__wellknown__", FenValue.FromString(name));
                symbolObj.Set("__id__", FenValue.FromNumber(++symbolIdCounter));
                return FenValue.FromObject(symbolObj);
            }
            
            var symbolIterator = CreateWellKnownSymbol("iterator");
            var symbolToStringTag = CreateWellKnownSymbol("toStringTag");
            var symbolHasInstance = CreateWellKnownSymbol("hasInstance");
            var symbolToPrimitive = CreateWellKnownSymbol("toPrimitive");
            
            symbolCtor.Set("iterator", symbolIterator);
            symbolCtor.Set("toStringTag", symbolToStringTag);
            symbolCtor.Set("hasInstance", symbolHasInstance);
            symbolCtor.Set("toPrimitive", symbolToPrimitive);
            
            // Symbol.prototype.toString()
            symbolProto.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) => {
                if (!thisVal.IsObject) return FenValue.FromString("Symbol()");
                var obj = thisVal.AsObject() as FenObject;
                if (obj?.GetPrototype() != symbolProto) return FenValue.FromString("Symbol()");
                var desc = obj.Get("__description__");
                return FenValue.FromString("Symbol(" + (desc.IsUndefined || desc.AsString(_context) == "" ? "" : desc.AsString(_context)) + ")");
            })));
            
            // Symbol.prototype.valueOf()
            symbolProto.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) => {
                return thisVal;
            })));
            
            SetGlobal("Symbol", FenValue.FromFunction(symbolCtor));

            // Array Iterator
            arrayProto.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("@@iterator", (args, thisVal) => {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                
                var index = 0;
                var iterator = new FenObject();
                
                // Iterator next() method
                iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (nextArgs, nextThis) => {
                    var result = new FenObject();
                    
                    if (index < (int)arr.Get("length").ToNumber()) {
                        result.Set("value", arr.Get(index.ToString()));
                        result.Set("done", FenValue.FromBoolean(false));
                        index++;
                    } else {
                        result.Set("value", FenValue.Undefined);
                        result.Set("done", FenValue.FromBoolean(true));
                    }
                    
                    return FenValue.FromObject(result);
                })));
                
                return FenValue.FromObject(iterator);
            })));
            
            // String Iterator
            stringProto.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("@@iterator", (args, thisVal) => {
                var str = thisVal.AsString(_context);
                
                var index = 0;
                var iterator = new FenObject();
                
                // Iterator next() method
                iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (nextArgs, nextThis) => {
                    var result = new FenObject();
                    
                    if (index < str.Length) {
                        result.Set("value", FenValue.FromString(str[index].ToString()));
                        result.Set("done", FenValue.FromBoolean(false));
                        index++;
                    } else {
                        result.Set("value", FenValue.Undefined);
                        result.Set("done", FenValue.FromBoolean(true));
                    }
                    
                    return FenValue.FromObject(result);
                })));
                
                return FenValue.FromObject(iterator);
            })));
            window.Set("Symbol", FenValue.FromFunction(symbolCtor));
            // RegExp
            var regexpProto = new FenObject();
            var regexpCtor = new FenFunction("RegExp", (args, thisVal) => {
                var pattern = args.Length > 0 ? args[0].AsString(_context) : "";
                var flags = args.Length > 1 ? args[1].AsString(_context) : "";
                
                var obj = new FenObject();
                obj.SetPrototype(regexpProto);
                
                try
                {
                    var options = System.Text.RegularExpressions.RegexOptions.None;
                    if (flags.Contains("i")) options |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                    if (flags.Contains("m")) options |= System.Text.RegularExpressions.RegexOptions.Multiline;
                    if (flags.Contains("s")) options |= System.Text.RegularExpressions.RegexOptions.Singleline;
                    
                    var regex = new System.Text.RegularExpressions.Regex(pattern, options);
                    obj.NativeObject = regex;
                    obj.Set("source", FenValue.FromString(pattern));
                    obj.Set("flags", FenValue.FromString(flags));
                    obj.Set("global", FenValue.FromBoolean(flags.Contains("g")));
                    obj.Set("ignoreCase", FenValue.FromBoolean(flags.Contains("i")));
                    obj.Set("multiline", FenValue.FromBoolean(flags.Contains("m")));
                    obj.Set("dotAll", FenValue.FromBoolean(flags.Contains("s")));
                    obj.Set("lastIndex", FenValue.FromNumber(0));
                }
                catch
                {
                    // Invalid regex - still create object but mark as invalid
                    obj.Set("source", FenValue.FromString(pattern));
                    obj.Set("flags", FenValue.FromString(flags));
                }
                
                return FenValue.FromObject(obj);
            });
            
            regexpCtor.Set("prototype", FenValue.FromObject(regexpProto));
            regexpProto.Set("constructor", FenValue.FromFunction(regexpCtor));
            
            // RegExp.prototype.test(str)
            regexpProto.Set("test", FenValue.FromFunction(new FenFunction("test", (args, thisVal) => {
                if (!thisVal.IsObject) return FenValue.FromBoolean(false);
                var obj = thisVal.AsObject();
                var regex = (obj as FenObject)?.NativeObject as System.Text.RegularExpressions.Regex;
                if (regex == null) return FenValue.FromBoolean(false);
                var str = args.Length > 0 ? args[0].AsString(_context) : "";
                return FenValue.FromBoolean(regex.IsMatch(str));
            })));
            
            // RegExp.prototype.exec(str)
            regexpProto.Set("exec", FenValue.FromFunction(new FenFunction("exec", (args, thisVal) => {
                if (!thisVal.IsObject) return FenValue.Null;
                var obj = thisVal.AsObject();
                var regex = (obj as FenObject)?.NativeObject as System.Text.RegularExpressions.Regex;
                if (regex == null) return FenValue.Null;
                var str = args.Length > 0 ? args[0].AsString(_context) : "";
                var match = regex.Match(str);
                if (!match.Success) return FenValue.Null;
                
                var result = FenObject.CreateArray();
                result.Set("0", FenValue.FromString(match.Value));
                for (int i = 0; i < match.Groups.Count; i++)
                    result.Set(i.ToString(), FenValue.FromString(match.Groups[i].Value));
                result.Set("length", FenValue.FromNumber(match.Groups.Count));
                result.Set("index", FenValue.FromNumber(match.Index));
                result.Set("input", FenValue.FromString(str));
                return FenValue.FromObject(result);
            })));
            
            SetGlobal("RegExp", FenValue.FromFunction(regexpCtor));
            window.Set("RegExp", FenValue.FromFunction(regexpCtor));
            // Date
            var dateProto = new FenObject();
            var dateCtor = new FenFunction("Date", (args, thisVal) => {
                // When called as constructor: new Date(...)
                var now = DateTime.UtcNow;
                DateTime dt;
                
                if (args.Length == 0)
                {
                    dt = now;
                }
                else if (args.Length == 1)
                {
                    var arg = args[0];
                    if (arg.IsString)
                    {
                        if (!DateTime.TryParse(arg.AsString(_context), out dt))
                            dt = DateTime.MinValue; // Invalid date
                    }
                    else
                    {
                        // Milliseconds since epoch
                        var ms = arg.ToNumber();
                        if (double.IsNaN(ms) || double.IsInfinity(ms))
                            dt = DateTime.MinValue;
                        else
                            dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
                    }
                }
                else
                {
                    // new Date(year, month, day, hour, minute, second, millisecond)
                    int year = (int)args[0].ToNumber();
                    int month = args.Length > 1 ? (int)args[1].ToNumber() + 1 : 1; // JS months are 0-indexed
                    int day = args.Length > 2 ? (int)args[2].ToNumber() : 1;
                    int hour = args.Length > 3 ? (int)args[3].ToNumber() : 0;
                    int minute = args.Length > 4 ? (int)args[4].ToNumber() : 0;
                    int second = args.Length > 5 ? (int)args[5].ToNumber() : 0;
                    int ms = args.Length > 6 ? (int)args[6].ToNumber() : 0;
                    
                    try
                    {
                        dt = new DateTime(year, month, day, hour, minute, second, ms, DateTimeKind.Utc);
                    }
                    catch
                    {
                        dt = DateTime.MinValue; // Invalid date
                    }
                }
                
                if (!thisVal.IsUndefined && thisVal.AsObject()?.GetPrototype() == dateProto)
                {
                    thisVal.AsObject().Set("__date__", FenValue.FromNumber((dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds));
                    return thisVal;
                }
                // Called as function: Date() returns string representation
                return FenValue.FromString(now.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT'K"));
            });
            
            dateCtor.Set("prototype", FenValue.FromObject(dateProto));
            dateProto.Set("constructor", FenValue.FromFunction(dateCtor));
            
            // Helper to get DateTime from Date object
            DateTime GetDate(FenValue thisVal)
            {
                if (!thisVal.IsObject) throw new InvalidOperationException("TypeError: this is not a Date object");
                var obj = thisVal.AsObject();
                if (obj.GetPrototype() != dateProto && !obj.Has("__date__"))
                    throw new InvalidOperationException("TypeError: this is not a Date object");
                var ms = obj.Get("__date__").ToNumber();
                if (double.IsNaN(ms)) return DateTime.MinValue;
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
            }
            
            void SetDate(FenValue thisVal, DateTime dt)
            {
                if (!thisVal.IsObject) throw new InvalidOperationException("TypeError: this is not a Date object");
                var obj = thisVal.AsObject();
                obj.Set("__date__", FenValue.FromNumber((dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds));
            }
            
            // Date.prototype.getTime()
            dateProto.Set("getTime", FenValue.FromFunction(new FenFunction("getTime", (args, thisVal) => {
                try { return FenValue.FromNumber((GetDate(thisVal) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getFullYear()
            dateProto.Set("getFullYear", FenValue.FromFunction(new FenFunction("getFullYear", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Year); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getUTCFullYear()
            dateProto.Set("getUTCFullYear", FenValue.FromFunction(new FenFunction("getUTCFullYear", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Year); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getMonth()
            dateProto.Set("getMonth", FenValue.FromFunction(new FenFunction("getMonth", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Month - 1); } // JS months are 0-indexed
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getUTCMonth()
            dateProto.Set("getUTCMonth", FenValue.FromFunction(new FenFunction("getUTCMonth", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Month - 1); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getDate()
            dateProto.Set("getDate", FenValue.FromFunction(new FenFunction("getDate", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Day); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getUTCDate()
            dateProto.Set("getUTCDate", FenValue.FromFunction(new FenFunction("getUTCDate", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Day); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getDay()
            dateProto.Set("getDay", FenValue.FromFunction(new FenFunction("getDay", (args, thisVal) => {
                try { return FenValue.FromNumber((int)GetDate(thisVal).DayOfWeek); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getUTCDay()
            dateProto.Set("getUTCDay", FenValue.FromFunction(new FenFunction("getUTCDay", (args, thisVal) => {
                try { return FenValue.FromNumber((int)GetDate(thisVal).DayOfWeek); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getHours()
            dateProto.Set("getHours", FenValue.FromFunction(new FenFunction("getHours", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Hour); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getUTCHours()
            dateProto.Set("getUTCHours", FenValue.FromFunction(new FenFunction("getUTCHours", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Hour); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getMinutes()
            dateProto.Set("getMinutes", FenValue.FromFunction(new FenFunction("getMinutes", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Minute); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getUTCMinutes()
            dateProto.Set("getUTCMinutes", FenValue.FromFunction(new FenFunction("getUTCMinutes", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Minute); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getSeconds()
            dateProto.Set("getSeconds", FenValue.FromFunction(new FenFunction("getSeconds", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Second); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getUTCSeconds()
            dateProto.Set("getUTCSeconds", FenValue.FromFunction(new FenFunction("getUTCSeconds", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Second); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getMilliseconds()
            dateProto.Set("getMilliseconds", FenValue.FromFunction(new FenFunction("getMilliseconds", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Millisecond); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.getUTCMilliseconds()
            dateProto.Set("getUTCMilliseconds", FenValue.FromFunction(new FenFunction("getUTCMilliseconds", (args, thisVal) => {
                try { return FenValue.FromNumber(GetDate(thisVal).Millisecond); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.setTime()
            dateProto.Set("setTime", FenValue.FromFunction(new FenFunction("setTime", (args, thisVal) => {
                try {
                    var ms = args.Length > 0 ? args[0].ToNumber() : double.NaN;
                    if (double.IsNaN(ms) || double.IsInfinity(ms))
                    {
                        SetDate(thisVal, DateTime.MinValue);
                        return FenValue.FromNumber(double.NaN);
                    }
                    var dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
                    SetDate(thisVal, dt);
                    return FenValue.FromNumber(ms);
                }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.setFullYear()
            dateProto.Set("setFullYear", FenValue.FromFunction(new FenFunction("setFullYear", (args, thisVal) => {
                try {
                    var dt = GetDate(thisVal);
                    var year = args.Length > 0 ? (int)args[0].ToNumber() : dt.Year;
                    var month = args.Length > 1 ? (int)args[1].ToNumber() + 1 : dt.Month;
                    var day = args.Length > 2 ? (int)args[2].ToNumber() : dt.Day;
                    dt = new DateTime(year, month, day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond, DateTimeKind.Utc);
                    SetDate(thisVal, dt);
                    return FenValue.FromNumber((dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
                }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.setMonth()
            dateProto.Set("setMonth", FenValue.FromFunction(new FenFunction("setMonth", (args, thisVal) => {
                try {
                    var dt = GetDate(thisVal);
                    var month = args.Length > 0 ? (int)args[0].ToNumber() + 1 : dt.Month;
                    var day = args.Length > 1 ? (int)args[1].ToNumber() : dt.Day;
                    dt = new DateTime(dt.Year, month, day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond, DateTimeKind.Utc);
                    SetDate(thisVal, dt);
                    return FenValue.FromNumber((dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
                }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.setDate()
            dateProto.Set("setDate", FenValue.FromFunction(new FenFunction("setDate", (args, thisVal) => {
                try {
                    var dt = GetDate(thisVal);
                    var day = args.Length > 0 ? (int)args[0].ToNumber() : dt.Day;
                    dt = new DateTime(dt.Year, dt.Month, day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond, DateTimeKind.Utc);
                    SetDate(thisVal, dt);
                    return FenValue.FromNumber((dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
                }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.valueOf()
            dateProto.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) => {
                try { return FenValue.FromNumber((GetDate(thisVal) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds); }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            // Date.prototype.toString()
            dateProto.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) => {
                try { 
                    var dt = GetDate(thisVal);
                    if (dt == DateTime.MinValue) return FenValue.FromString("Invalid Date");
                    return FenValue.FromString(dt.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT'K"));
                }
                catch { return FenValue.FromString("Invalid Date"); }
            })));
            
            // Date.prototype.toISOString()
            dateProto.Set("toISOString", FenValue.FromFunction(new FenFunction("toISOString", (args, thisVal) => {
                try {
                    var dt = GetDate(thisVal);
                    if (dt == DateTime.MinValue) return FenValue.FromError("RangeError: Invalid time value");
                    return FenValue.FromString(dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                }
                catch { return FenValue.FromError("RangeError: Invalid time value"); }
            })));
            
            // Date static methods
            dateCtor.Set("now", FenValue.FromFunction(new FenFunction("now", (args, thisVal) => {
                return FenValue.FromNumber((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
            })));
            
            dateCtor.Set("parse", FenValue.FromFunction(new FenFunction("parse", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                var str = args[0].AsString(_context);
                if (DateTime.TryParse(str, out var dt))
                    return FenValue.FromNumber((dt.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
                return FenValue.FromNumber(double.NaN);
            })));
            
            dateCtor.Set("UTC", FenValue.FromFunction(new FenFunction("UTC", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                int year = (int)args[0].ToNumber();
                int month = args.Length > 1 ? (int)args[1].ToNumber() + 1 : 1;
                int day = args.Length > 2 ? (int)args[2].ToNumber() : 1;
                int hour = args.Length > 3 ? (int)args[3].ToNumber() : 0;
                int minute = args.Length > 4 ? (int)args[4].ToNumber() : 0;
                int second = args.Length > 5 ? (int)args[5].ToNumber() : 0;
                int ms = args.Length > 6 ? (int)args[6].ToNumber() : 0;
                try
                {
                    var dt = new DateTime(year, month, day, hour, minute, second, ms, DateTimeKind.Utc);
                    return FenValue.FromNumber((dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
                }
                catch { return FenValue.FromNumber(double.NaN); }
            })));
            
            SetGlobal("Date", FenValue.FromFunction(dateCtor));
            window.Set("Date", FenValue.FromFunction(dateCtor));
            
            errorProto.Set("name", FenValue.FromString("Error"));
            errorProto.Set("message", FenValue.FromString(""));
            errorProto.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.FromString("Error");
                var name = obj.Get("name").ToString();
                var msg = obj.Get("message").ToString();
                if (string.IsNullOrEmpty(name)) name = "Error";
                if (string.IsNullOrEmpty(msg)) return FenValue.FromString(name);
                return FenValue.FromString($"{name}: {msg}");
            })));

            // Helper to create error instances with correct prototype
            FenObject MakeError(string name, string message, FenValue[] args, FenObject proto)
            {
                var err = new FenObject();
                err.SetPrototype(proto);
                err.Set("name", FenValue.FromString(name));
                err.Set("message", FenValue.FromString(message));
                err.Set("stack", FenValue.FromString($"{name}: {message}\n    at <anonymous>"));
                
                // ES2022: Error.cause
                if (args.Length > 1 && args[1].IsObject)
                {
                    var opts = args[1].AsObject() as FenObject;
                    err.Set("cause", opts.Get("cause"));
                }
                return err;
            }

            // --- REFACTOR: Unified Constructor Registration ---
            // This ensures built-in constructors are typeof 'function' and have correct prototype linkage.
            void RegisterConstructor(string name, FenObject prototype, Func<FenValue[], FenValue, FenValue> ctorLogic, FenObject staticMembers = null)
            {
                var ctor = new FenFunction(name, (args, thisVal) => {
                    // Constructor call logic (handle 'new' vs function call)
                    return ctorLogic(args, thisVal);
                });
                
                // Link prototype
                if (prototype != null)
                {
                    ctor.Set("prototype", FenValue.FromObject(prototype));
                    prototype.Set("constructor", FenValue.FromFunction(ctor));
                }

                // Add static members if any
                if (staticMembers != null)
                {
                    foreach (var key in staticMembers.Keys())
                        ctor.Set(key, staticMembers.Get(key));
                }

                SetGlobal(name, FenValue.FromFunction(ctor));
                window.Set(name, FenValue.FromFunction(ctor));
            }

            // 2. Error Constructor
            var errorCtor = new FenFunction("Error", (args, thisVal) =>
            {
                var message = args.Length > 0 ? args[0].ToString() : "";
                return FenValue.FromObject(MakeError("Error", message, args, errorProto));
            });
            errorCtor.Prototype = errorProto;
            errorCtor.Set("prototype", FenValue.FromObject(errorProto));
            SetGlobal("Error", FenValue.FromFunction(errorCtor));

            // 3. Subtypes Definitions
            void DefineErrorType(string name, FenObject parentProto)
            {
                var proto = new FenObject();
                proto.SetPrototype(parentProto);
                proto.Set("name", FenValue.FromString(name));
                
                var ctor = new FenFunction(name, (args, thisVal) =>
                {
                    var message = args.Length > 0 ? args[0].ToString() : "";
                    return FenValue.FromObject(MakeError(name, message, args, proto));
                });
                ctor.Prototype = proto;
                ctor.Set("prototype", FenValue.FromObject(proto));
                SetGlobal(name, FenValue.FromFunction(ctor));
            }

            DefineErrorType("TypeError", errorProto);
            DefineErrorType("SyntaxError", errorProto);
            DefineErrorType("ReferenceError", errorProto);
            DefineErrorType("RangeError", errorProto);
            DefineErrorType("URIError", errorProto);
            DefineErrorType("EvalError", errorProto);

            // AggregateError is special (has 'errors' property)
            var aggProto = new FenObject();
            aggProto.SetPrototype(errorProto);
            aggProto.Set("name", FenValue.FromString("AggregateError"));
            var aggCtor = new FenFunction("AggregateError", (args, thisVal) =>
            {
                var message = args.Length > 1 ? args[1].ToString() : "";
                var err = MakeError("AggregateError", message, args.Length > 1 ? new[] { args[1] } : Array.Empty<FenValue>(), aggProto);
                var errors = args.Length > 0 ? args[0] : FenValue.Undefined;
                err.Set("errors", errors);
                return FenValue.FromObject(err);
            });
            aggCtor.Prototype = aggProto;
            aggCtor.Set("prototype", FenValue.FromObject(aggProto));
            SetGlobal("AggregateError", FenValue.FromFunction(aggCtor));


            // document object - Bridge to DOM
            var document = new FenObject();
            document.Set("getElementById", FenValue.FromFunction(new FenFunction("getElementById", (FenValue[] args, FenValue thisVal) =>
            {
                if (_domBridge  == null) return FenValue.Null;
                if (args.Length == 0) return FenValue.Null;
                return _domBridge.GetElementById(args[0].ToString()) ;
            })));
            document.Set("querySelector", FenValue.FromFunction(new FenFunction("querySelector", (FenValue[] args, FenValue thisVal) =>
            {
                if (_domBridge  == null) return FenValue.Null;
                if (args.Length == 0) return FenValue.Null;
                return _domBridge.QuerySelector(args[0].ToString()) ;
            })));
            document.Set("createElement", FenValue.FromFunction(new FenFunction("createElement", (FenValue[] args, FenValue thisVal) =>
            {
                if (_domBridge  == null) return FenValue.Null;
                if (args.Length == 0) return FenValue.Null;
                return _domBridge.CreateElement(args[0].ToString()) ;
            })));

            document.Set("createTextNode", FenValue.FromFunction(new FenFunction("createTextNode", (FenValue[] args, FenValue thisVal) =>
            {
                if (_domBridge  == null) return FenValue.Null;
                var text = args.Length > 0 ? args[0].ToString() : "";
                return _domBridge.CreateTextNode(text) ;
            })));
            
             // document.body / head (Stubs or getters if bridge supports)
            
            SetGlobal("document", FenValue.FromObject(document));

            // console object
            var console = new FenObject();
            FenLogger.Debug("[FenRuntime] Creating console object...", LogCategory.JavaScript);
            console.Set("log", FenValue.FromFunction(new FenFunction("log", (FenValue[] args, FenValue thisVal) =>
            {
                try { FenLogger.Debug("[FenRuntime] console.log invoked from JS", LogCategory.JavaScript); } catch { }

                var messages = new List<string>();
                foreach (var arg in args) messages.Add(arg.ToString());
                var msg = string.Join(" ", messages);
                Console.WriteLine(msg);
                // /* [PERF-REMOVED] */
                try { FenLogger.Debug($"[FenRuntime] Console.log: {msg}", LogCategory.JavaScript); } catch { }
                try { 
                    if (OnConsoleMessage  == null) FenLogger.Error("[FenRuntime] OnConsoleMessage is NULL!", LogCategory.JavaScript);
                    else FenLogger.Debug("[FenRuntime] Invoking OnConsoleMessage...", LogCategory.JavaScript);
                    OnConsoleMessage?.Invoke(msg); 
                } catch (Exception ex) { FenLogger.Error($"[FenRuntime] OnConsoleMessage error: {ex}", LogCategory.JavaScript); }
                return FenValue.Undefined;
            })));
            console.Set("error", FenValue.FromFunction(new FenFunction("error", (FenValue[] args, FenValue thisVal) =>
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
            console.Set("warn", FenValue.FromFunction(new FenFunction("warn", (FenValue[] args, FenValue thisVal) =>
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
            console.Set("info", FenValue.FromFunction(new FenFunction("info", (FenValue[] args, FenValue thisVal) =>
            {
                var msg = string.Join(" ", args.Select(a => a.ToString()));
                Console.WriteLine($"INFO: {msg}");
                // /* [PERF-REMOVED] */
                try { FenLogger.Info($"[FenRuntime] Console.info: {msg}", LogCategory.JavaScript); } catch { }
                try { OnConsoleMessage?.Invoke($"[Info] {msg}"); } catch { }
                return FenValue.Undefined;
            })));
            console.Set("clear", FenValue.FromFunction(new FenFunction("clear", (FenValue[] args, FenValue thisVal) =>
            {
                Console.Clear();
                try { OnConsoleMessage?.Invoke("[Clear]"); } catch { }
                return FenValue.Undefined;
            })));

            // console.dir - Object inspection
            console.Set("dir", FenValue.FromFunction(new FenFunction("dir", (FenValue[] args, FenValue thisVal) =>
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
            console.Set("table", FenValue.FromFunction(new FenFunction("table", (FenValue[] args, FenValue thisVal) =>
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
            console.Set("group", FenValue.FromFunction(new FenFunction("group", (FenValue[] args, FenValue thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "";
                _consoleGroupLevel++;
                var indent = new string(' ', _consoleGroupLevel * 2);
                var msg = $"{indent}▼ {label}";
                Console.WriteLine(msg);
                try { OnConsoleMessage?.Invoke($"[Group] {label}"); } catch { }
                return FenValue.Undefined;
            })));

            console.Set("groupCollapsed", FenValue.FromFunction(new FenFunction("groupCollapsed", (FenValue[] args, FenValue thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "";
                _consoleGroupLevel++;
                var indent = new string(' ', _consoleGroupLevel * 2);
                var msg = $"{indent}▶ {label}";
                Console.WriteLine(msg);
                try { OnConsoleMessage?.Invoke($"[GroupCollapsed] {label}"); } catch { }
                return FenValue.Undefined;
            })));

            console.Set("groupEnd", FenValue.FromFunction(new FenFunction("groupEnd", (FenValue[] args, FenValue thisVal) =>
            {
                if (_consoleGroupLevel > 0) _consoleGroupLevel--;
                return FenValue.Undefined;
            })));

            // console.time / timeEnd - Timing
            var _consoleTimers = new Dictionary<string, DateTime>();
            console.Set("time", FenValue.FromFunction(new FenFunction("time", (FenValue[] args, FenValue thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "default";
                _consoleTimers[label] = DateTime.Now;
                return FenValue.Undefined;
            })));

            console.Set("timeEnd", FenValue.FromFunction(new FenFunction("timeEnd", (FenValue[] args, FenValue thisVal) =>
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
            console.Set("count", FenValue.FromFunction(new FenFunction("count", (FenValue[] args, FenValue thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "default";
                if (!_consoleCounts.ContainsKey(label)) _consoleCounts[label] = 0;
                _consoleCounts[label]++;
                var msg = $"{label}: {_consoleCounts[label]}";
                Console.WriteLine(msg);
                try { OnConsoleMessage?.Invoke($"[Count] {msg}"); } catch { }
                return FenValue.Undefined;
            })));

            console.Set("countReset", FenValue.FromFunction(new FenFunction("countReset", (FenValue[] args, FenValue thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "default";
                _consoleCounts[label] = 0;
                return FenValue.Undefined;
            })));

            // console.assert
            console.Set("assert", FenValue.FromFunction(new FenFunction("assert", (FenValue[] args, FenValue thisVal) =>
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
            console.Set("trace", FenValue.FromFunction(new FenFunction("trace", (FenValue[] args, FenValue thisVal) =>
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
            var workerCtor = new FenBrowser.FenEngine.Workers.WorkerConstructor(
                GetCurrentOrigin(),
                _storageBackend,
                BaseUri,
                FetchWorkerScriptAsync,
                IsWorkerScriptUriAllowed);
            SetGlobal("Worker", FenValue.FromFunction(workerCtor.GetConstructorFunction()));

            // Timers
            var setTimeout = FenValue.FromFunction(new FenFunction("setTimeout", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction) return FenValue.FromNumber(0);
                var callback = args[0].AsFunction();
                int delay = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                var callbackArgs = args.Skip(2).ToArray();

                return CreateTimer(callback, delay, false, callbackArgs);
            }));
            SetGlobal("setTimeout", setTimeout);
            
            var clearTimeout = FenValue.FromFunction(new FenFunction("clearTimeout", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length > 0) CancelTimer((int)args[0].ToNumber());
                return FenValue.Undefined;
            }));
            SetGlobal("clearTimeout", clearTimeout);

            var setInterval = FenValue.FromFunction(new FenFunction("setInterval", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction) return FenValue.FromNumber(0);
                var callback = args[0].AsFunction();
                int delay = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                var callbackArgs = args.Skip(2).ToArray();

                return CreateTimer(callback, delay, true, callbackArgs);
            }));
            SetGlobal("setInterval", setInterval);

            var clearInterval = FenValue.FromFunction(new FenFunction("clearInterval", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length > 0) CancelTimer((int)args[0].ToNumber());
                return FenValue.Undefined;
            }));
            SetGlobal("clearInterval", clearInterval);

            // requestAnimationFrame
            var requestAnimationFrame = FenValue.FromFunction(new FenFunction("requestAnimationFrame", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction) return FenValue.FromNumber(0);
                return CreateAnimationFrame(args[0].AsFunction());
            }));
            SetGlobal("requestAnimationFrame", requestAnimationFrame);

            // cancelAnimationFrame
            var cancelAnimationFrame = FenValue.FromFunction(new FenFunction("cancelAnimationFrame", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length > 0) CancelTimer((int)args[0].ToNumber());
                return FenValue.Undefined;
            }));
            SetGlobal("cancelAnimationFrame", cancelAnimationFrame);

            // queueMicrotask
            var queueMicrotask = FenValue.FromFunction(new FenFunction("queueMicrotask", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction) 
                    throw new Exception("queueMicrotask requires a function argument.");
                
                var callback = args[0].AsFunction();
                FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance.ScheduleMicrotask(() => 
                {
                    try 
                    {
                        callback.Invoke(new FenValue[0], _context);
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Error($"[FenRuntime] Microtask Exception: {ex.Message}", LogCategory.JavaScript);
                    }
                });
                return FenValue.Undefined;
            }));
            SetGlobal("queueMicrotask", queueMicrotask);

            // Intl API
            try 
            {
                SetGlobal("Intl", FenValue.FromObject(JsIntl.CreateIntlObject(_context)));
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[FenRuntime] Failed to initialize Intl: {ex.Message}", LogCategory.JavaScript);
            }

            // Symbol API (Implemented as Object to support static properties like iterator)
            // Note: Symbol() constructor calls are not supported in this pattern, but Symbol.iterator is critical.
            var symbolObj = new FenObject();
            
            // Symbol.for(key)
            symbolObj.Set("for", FenValue.FromFunction(new FenFunction("for", (FenValue[] args, FenValue thisVal) =>
            {
                var key = args.Length > 0 ? args[0].ToString() : "undefined";
                return FenValue.FromSymbol(JsSymbol.For(key));
            })));
            
            // Symbol.keyFor(sym)
            symbolObj.Set("keyFor", FenValue.FromFunction(new FenFunction("keyFor", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length > 0 && args[0].IsSymbol && args[0].AsSymbol() is JsSymbol sym)
                {
                    var key = JsSymbol.KeyFor(sym);
                    return key != null ? FenValue.FromString(key) : FenValue.Undefined;
                }
                // Allow undefined return if not found/invalid to prevent crashes
                return FenValue.Undefined;
            })));
            
            // Well-known Symbols
            symbolObj.Set("iterator", FenValue.FromSymbol(JsSymbol.Iterator));
            symbolObj.Set("toStringTag", FenValue.FromSymbol(JsSymbol.ToStringTag));
            symbolObj.Set("toPrimitive", FenValue.FromSymbol(JsSymbol.ToPrimitive));
            symbolObj.Set("hasInstance", FenValue.FromSymbol(JsSymbol.HasInstance));
            symbolObj.Set("isConcatSpreadable", FenValue.FromSymbol(JsSymbol.IsConcatSpreadable));
            symbolObj.Set("species", FenValue.FromSymbol(JsSymbol.Species));
            symbolObj.Set("match", FenValue.FromSymbol(JsSymbol.Match));
            symbolObj.Set("replace", FenValue.FromSymbol(JsSymbol.Replace));
            symbolObj.Set("search", FenValue.FromSymbol(JsSymbol.Search));
            symbolObj.Set("split", FenValue.FromSymbol(JsSymbol.Split));
            symbolObj.Set("asyncIterator", FenValue.FromSymbol(JsSymbol.AsyncIterator));

            SetGlobal("Symbol", FenValue.FromObject(symbolObj));

            // ES2025: Iterator global with helpers
            // Iterator.from(iterable) creates a lazy iterator wrapper with map/filter/take/drop etc.
            var iteratorCtor = new FenFunction("Iterator", (args, thisVal) => FenValue.Undefined);

            // Helper: build an iterator object with the standard helper methods attached
            Func<IEnumerable<FenValue>, FenObject> MakeIteratorObject = null;
            MakeIteratorObject = (source) =>
            {
                var iter = new FenObject();
                var enumerator = source.GetEnumerator();
                bool done = false;

                iter.Set("next", FenValue.FromFunction(new FenFunction("next", (_, __) =>
                {
                    var res = new FenObject();
                    if (!done && enumerator.MoveNext())
                    {
                        res.Set("value", enumerator.Current);
                        res.Set("done", FenValue.FromBoolean(false));
                    }
                    else
                    {
                        done = true;
                        res.Set("value", FenValue.Undefined);
                        res.Set("done", FenValue.FromBoolean(true));
                    }
                    return FenValue.FromObject(res);
                })));
                iter.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, __) => FenValue.FromObject(iter))));

                // Lazy helpers — each returns a new iterator
                iter.Set("map", FenValue.FromFunction(new FenFunction("map", (a, _) =>
                {
                    var fn = a.Length > 0 ? a[0].AsFunction() : null;
                    if (fn == null) return FenValue.Undefined;
                    IEnumerable<FenValue> Mapped()
                    {
                        var e2 = source.GetEnumerator();
                        int i = 0;
                        while (e2.MoveNext()) yield return fn.Invoke(new FenValue[] { e2.Current, FenValue.FromNumber(i++) }, null);
                    }
                    return FenValue.FromObject(MakeIteratorObject(Mapped()));
                })));
                iter.Set("filter", FenValue.FromFunction(new FenFunction("filter", (a, _) =>
                {
                    var fn = a.Length > 0 ? a[0].AsFunction() : null;
                    if (fn == null) return FenValue.Undefined;
                    IEnumerable<FenValue> Filtered()
                    {
                        var e2 = source.GetEnumerator();
                        while (e2.MoveNext())
                            if (fn.Invoke(new FenValue[] { e2.Current }, null).ToBoolean())
                                yield return e2.Current;
                    }
                    return FenValue.FromObject(MakeIteratorObject(Filtered()));
                })));
                iter.Set("take", FenValue.FromFunction(new FenFunction("take", (a, _) =>
                {
                    int n = a.Length > 0 ? (int)a[0].ToNumber() : 0;
                    IEnumerable<FenValue> Taken()
                    {
                        var e2 = source.GetEnumerator();
                        int count = 0;
                        while (count < n && e2.MoveNext()) { yield return e2.Current; count++; }
                    }
                    return FenValue.FromObject(MakeIteratorObject(Taken()));
                })));
                iter.Set("drop", FenValue.FromFunction(new FenFunction("drop", (a, _) =>
                {
                    int n = a.Length > 0 ? (int)a[0].ToNumber() : 0;
                    IEnumerable<FenValue> Dropped()
                    {
                        var e2 = source.GetEnumerator();
                        int skipped = 0;
                        while (e2.MoveNext()) { if (skipped++ >= n) yield return e2.Current; }
                    }
                    return FenValue.FromObject(MakeIteratorObject(Dropped()));
                })));
                iter.Set("flatMap", FenValue.FromFunction(new FenFunction("flatMap", (a, _) =>
                {
                    var fn = a.Length > 0 ? a[0].AsFunction() : null;
                    if (fn == null) return FenValue.Undefined;
                    IEnumerable<FenValue> FlatMapped()
                    {
                        var e2 = source.GetEnumerator();
                        while (e2.MoveNext())
                        {
                            var mapped = fn.Invoke(new FenValue[] { e2.Current }, null);
                            var innerObj = mapped.IsObject ? mapped.AsObject() as FenObject : null;
                            if (innerObj != null)
                            {
                                var nv = innerObj.Get("next", null);
                                if (nv.IsFunction)
                                {
                                    var nfn = nv.AsFunction();
                                    while (true)
                                    {
                                        var r = nfn.Invoke(Array.Empty<FenValue>(), null);
                                        var ro = r.IsObject ? r.AsObject() as FenObject : null;
                                        if (ro == null || ro.Get("done", null).ToBoolean()) break;
                                        yield return ro.Get("value", null);
                                    }
                                    continue;
                                }
                                var lv = innerObj.Get("length", null);
                                if (lv.IsNumber)
                                {
                                    int l = (int)lv.ToNumber();
                                    for (int j = 0; j < l; j++) yield return innerObj.Get(j.ToString(), null);
                                    continue;
                                }
                            }
                            yield return mapped;
                        }
                    }
                    return FenValue.FromObject(MakeIteratorObject(FlatMapped()));
                })));

                // Terminal methods
                iter.Set("toArray", FenValue.FromFunction(new FenFunction("toArray", (a, _) =>
                {
                    var arr = new FenObject();
                    int i = 0;
                    var e2 = source.GetEnumerator();
                    while (e2.MoveNext()) arr.Set((i++).ToString(), e2.Current);
                    arr.Set("length", FenValue.FromNumber(i));
                    return FenValue.FromObject(arr);
                })));
                iter.Set("forEach", FenValue.FromFunction(new FenFunction("forEach", (a, _) =>
                {
                    var fn = a.Length > 0 ? a[0].AsFunction() : null;
                    if (fn == null) return FenValue.Undefined;
                    var e2 = source.GetEnumerator();
                    int i = 0;
                    while (e2.MoveNext()) fn.Invoke(new FenValue[] { e2.Current, FenValue.FromNumber(i++) }, null);
                    return FenValue.Undefined;
                })));
                iter.Set("reduce", FenValue.FromFunction(new FenFunction("reduce", (a, _) =>
                {
                    var fn = a.Length > 0 ? a[0].AsFunction() : null;
                    if (fn == null) return FenValue.Undefined;
                    var e2 = source.GetEnumerator();
                    FenValue acc = a.Length > 1 ? a[1] : FenValue.Undefined;
                    bool hasInit = a.Length > 1;
                    while (e2.MoveNext())
                    {
                        if (!hasInit) { acc = e2.Current; hasInit = true; continue; }
                        acc = fn.Invoke(new FenValue[] { acc, e2.Current }, null);
                    }
                    return acc;
                })));
                iter.Set("some", FenValue.FromFunction(new FenFunction("some", (a, _) =>
                {
                    var fn = a.Length > 0 ? a[0].AsFunction() : null;
                    if (fn == null) return FenValue.FromBoolean(false);
                    var e2 = source.GetEnumerator();
                    while (e2.MoveNext()) if (fn.Invoke(new FenValue[] { e2.Current }, null).ToBoolean()) return FenValue.FromBoolean(true);
                    return FenValue.FromBoolean(false);
                })));
                iter.Set("every", FenValue.FromFunction(new FenFunction("every", (a, _) =>
                {
                    var fn = a.Length > 0 ? a[0].AsFunction() : null;
                    if (fn == null) return FenValue.FromBoolean(true);
                    var e2 = source.GetEnumerator();
                    while (e2.MoveNext()) if (!fn.Invoke(new FenValue[] { e2.Current }, null).ToBoolean()) return FenValue.FromBoolean(false);
                    return FenValue.FromBoolean(true);
                })));
                iter.Set("find", FenValue.FromFunction(new FenFunction("find", (a, _) =>
                {
                    var fn = a.Length > 0 ? a[0].AsFunction() : null;
                    if (fn == null) return FenValue.Undefined;
                    var e2 = source.GetEnumerator();
                    while (e2.MoveNext()) if (fn.Invoke(new FenValue[] { e2.Current }, null).ToBoolean()) return e2.Current;
                    return FenValue.Undefined;
                })));

                return iter;
            };

            iteratorCtor.Set("from", FenValue.FromFunction(new FenFunction("from", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                var iterable = args[0];

                // If it's already an iterator (has .next), wrap it
                if (iterable.IsObject)
                {
                    var obj = iterable.AsObject() as FenObject;
                    if (obj == null) return FenValue.Undefined;
                    var nextVal = obj.Get("next", null);
                    var nextFn = nextVal.IsFunction ? nextVal.AsFunction() : null;
                    if (nextFn != null)
                    {
                        IEnumerable<FenValue> FromIterator()
                        {
                            while (true)
                            {
                                var r = nextFn.Invoke(Array.Empty<FenValue>(), null);
                                var rObj = r.AsObject() as FenObject;
                                if (rObj == null || rObj.Get("done", null).ToBoolean()) yield break;
                                yield return rObj.Get("value", null);
                            }
                        }
                        return FenValue.FromObject(MakeIteratorObject(FromIterator()));
                    }
                    // Array-like
                    var lenVal = obj.Get("length", null);
                    if (lenVal.IsNumber)
                    {
                        int len = (int)lenVal.ToNumber();
                        IEnumerable<FenValue> FromArray()
                        {
                            for (int i = 0; i < len; i++) yield return obj.Get(i.ToString(), null);
                        }
                        return FenValue.FromObject(MakeIteratorObject(FromArray()));
                    }
                }
                return FenValue.Undefined;
            })));

            SetGlobal("Iterator", FenValue.FromFunction(iteratorCtor));

            // Dynamic import() function - returns a Promise
            SetGlobal("import", FenValue.FromFunction(new FenFunction("import", (FenValue[] args, FenValue thisVal) =>
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
                            promise.Set("__value__", (FenValue)(object)exports);
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
                    var stateVal = promise.Get("__state__");
                    var state = stateVal.IsUndefined ? null : stateVal.ToString();
                    if (state == "fulfilled")
                    {
                        if (thenArgs.Length > 0 && thenArgs[0].IsFunction)
                        {
                            var onFulfilled = thenArgs[0].AsFunction() as FenFunction;
                            var value = promise.Get("__value__");
                            return (FenValue)(onFulfilled?.Invoke(new FenValue[] { value }, null) ?? FenValue.Undefined);
                        }
                        return promise.Get("__value__");
                    }
                    else if (state == "rejected")
                    {
                        if (thenArgs.Length > 1 && thenArgs[1].IsFunction)
                        {
                            var onRejected = thenArgs[1].AsFunction() as FenFunction;
                            var reason = promise.Get("__reason__") ;
                            return (FenValue)(onRejected?.Invoke(new FenValue[] { reason }, null) ?? FenValue.Undefined);
                        }
                        return FenValue.Undefined;
                    }
                    return FenValue.FromObject(promise);
                })));
                
                promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (catchArgs, catchThis) =>
                {
                    var stateVal = promise.Get("__state__");
                    var state = stateVal.IsUndefined ? null : stateVal.ToString();
                    if (state == "rejected" && catchArgs.Length > 0 && catchArgs[0].IsFunction)
                    {
                        var onRejected = catchArgs[0].AsFunction() as FenFunction;
                        var reason = promise.Get("__reason__");
                        return (FenValue)(onRejected?.Invoke(new FenValue[] { reason }, null) ?? FenValue.Undefined);
                    }
                    return FenValue.FromObject(promise);
                })));
                
                return FenValue.FromObject(promise);
            })));

            // undefined and null
            SetGlobal("undefined", FenValue.Undefined);
            SetGlobal("null", FenValue.Null);

            // Object constructor static methods
            var objectConstructor = objectCtor; 

            FenObject CoerceObjectLike(FenValue value, bool throwOnNullish, out FenValue error)
            {
                error = FenValue.Undefined;

                if (value.IsObject || value.IsFunction)
                {
                    return value.AsObject() as FenObject;
                }

                if (value.IsNull || value.IsUndefined)
                {
                    if (throwOnNullish)
                    {
                        error = FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                    }
                    return null;
                }

                if (value.IsString)
                {
                    var wrapper = new FenObject();
                    wrapper.SetPrototype(stringProto);
                    wrapper.Set("__value__", FenValue.FromString(value.ToString()));
                    var str = value.ToString();
                    for (int i = 0; i < str.Length; i++)
                    {
                        wrapper.Set(i.ToString(), FenValue.FromString(str[i].ToString()));
                    }
                    wrapper.DefineOwnProperty("length", new PropertyDescriptor
                    {
                        Value = FenValue.FromNumber(str.Length),
                        Writable = false,
                        Enumerable = false,
                        Configurable = false,
                    });
                    return wrapper;
                }

                if (value.IsNumber)
                {
                    var wrapper = new FenObject();
                    wrapper.SetPrototype(numberProto);
                    wrapper.Set("__value__", FenValue.FromNumber(value.ToNumber()));
                    return wrapper;
                }

                if (value.IsBoolean)
                {
                    var wrapper = new FenObject();
                    wrapper.SetPrototype(booleanProto);
                    wrapper.Set("__value__", FenValue.FromBoolean(value.ToBoolean()));
                    return wrapper;
                }

                if (value.IsBigInt || value.IsSymbol)
                {
                    var wrapper = new FenObject();
                    wrapper.SetPrototype(objectProto);
                    wrapper.Set("__value__", value);
                    return wrapper;
                }

                return null;
            }

            bool TrySetObjectPropertyForAssign(FenObject target, string key, FenValue propertyValue, out FenValue error)
            {
                error = FenValue.Undefined;
                if (target == null)
                {
                    error = FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                    return false;
                }

                var ownDescriptor = target.GetOwnPropertyDescriptor(key);
                if (ownDescriptor.HasValue)
                {
                    var desc = ownDescriptor.Value;
                    if (desc.IsAccessor)
                    {
                        if (desc.Setter == null)
                        {
                            error = FenValue.FromError($"TypeError: Cannot assign to read only property '{key}'");
                            return false;
                        }

                        var setterResult = desc.Setter.Invoke(
                            new[] { FenValue.FromObject(target), propertyValue },
                            _context);
                        if (setterResult.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error ||
                            setterResult.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Throw)
                        {
                            error = setterResult;
                            return false;
                        }

                        return true;
                    }

                    if (!(desc.Writable ?? false))
                    {
                        error = FenValue.FromError($"TypeError: Cannot assign to read only property '{key}'");
                        return false;
                    }
                }
                else if (!target.IsExtensible)
                {
                    error = FenValue.FromError($"TypeError: Cannot add property '{key}', object is not extensible");
                    return false;
                }

                target.Set(key, propertyValue, _context);
                return true;
            }
            
            // Object.keys(obj) - ES5
            objectConstructor.Set("keys", FenValue.FromFunction(new FenFunction("keys", (args, thisVal) =>
            {
                var result = FenObject.CreateArray();
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                {
                    return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                }

                var obj = CoerceObjectLike(args[0], throwOnNullish: true, out var coercionError);
                if (!coercionError.IsUndefined)
                {
                    return coercionError;
                }

                var keys = obj.Keys()?.ToArray() ?? new string[0];
                for (int i = 0; i < keys.Length; i++) result.Set(i.ToString(), FenValue.FromString(keys[i]), null);
                result.Set("length", FenValue.FromNumber(keys.Length), null);
                return FenValue.FromObject(result);
            })));

            // Object.values(obj) - ES2017
            objectConstructor.Set("values", FenValue.FromFunction(new FenFunction("values", (args, thisVal) =>
            {
                var result = FenObject.CreateArray();
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                {
                    return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                }

                var obj = CoerceObjectLike(args[0], throwOnNullish: true, out var coercionError);
                if (!coercionError.IsUndefined)
                {
                    return coercionError;
                }

                var keys = obj.Keys()?.ToArray() ?? new string[0];
                for (int i = 0; i < keys.Length; i++) result.Set(i.ToString(), obj.Get(keys[i], null), null);
                result.Set("length", FenValue.FromNumber(keys.Length), null);
                return FenValue.FromObject(result);
            })));

            // Object.entries(obj) - ES2017
            objectConstructor.Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
            {
                var result = FenObject.CreateArray();
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                {
                    return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                }

                var obj = CoerceObjectLike(args[0], throwOnNullish: true, out var coercionError);
                if (!coercionError.IsUndefined)
                {
                    return coercionError;
                }

                var keys = obj.Keys()?.ToArray() ?? new string[0];
                for (int i = 0; i < keys.Length; i++)
                {
                    var pair = FenObject.CreateArray();
                    pair.Set("0", FenValue.FromString(keys[i]), null);
                    pair.Set("1", obj.Get(keys[i], null), null);
                    pair.Set("length", FenValue.FromNumber(2), null);
                    result.Set(i.ToString(), FenValue.FromObject(pair), null);
                }
                result.Set("length", FenValue.FromNumber(keys.Length), null);
                return FenValue.FromObject(result);
            })));
            
            // Object.fromEntries(iterable) - ES2019
            objectConstructor.Set("fromEntries", FenValue.FromFunction(new FenFunction("fromEntries", (args, thisVal) =>
            {
                var result = new FenObject();
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromObject(result);
                var iterable = args[0].AsObject();
                var lenVal = iterable.Get("length", null);
                int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                for (int i = 0; i < len; i++)
                {
                    var entry = iterable.Get(i.ToString(), null);
                    if (entry.IsObject)
                    {
                        var entryObj = entry.AsObject();
                        var key = entryObj.Get("0", null).ToString();
                        var val = entryObj.Get("1", null);
                        result.Set(key, val, null);
                    }
                }
                return FenValue.FromObject(result);
            })));
            
            // Object.assign(target, ...sources) - ES2015
            objectConstructor.Set("assign", FenValue.FromFunction(new FenFunction("assign", (args, thisVal) =>
            {
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                {
                    return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                }

                var target = CoerceObjectLike(args[0], throwOnNullish: true, out var targetError);
                if (!targetError.IsUndefined)
                {
                    return targetError;
                }

                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i].IsNull || args[i].IsUndefined)
                    {
                        continue;
                    }

                    var source = CoerceObjectLike(args[i], throwOnNullish: false, out _);
                    if (source == null)
                    {
                        continue;
                    }

                    foreach (var key in source.Keys() ?? Enumerable.Empty<string>())
                    {
                        var propertyValue = source.Get(key, null);
                        if (!TrySetObjectPropertyForAssign(target, key, propertyValue, out var setError))
                        {
                            return setError;
                        }
                    }
                }
                return FenValue.FromObject(target);
            })));
            
            // Object.hasOwn(obj, prop) - ES2022
            objectConstructor.Set("hasOwn", FenValue.FromFunction(new FenFunction("hasOwn", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var obj = args[0].AsObject();
                var prop = args[1].ToString();
                return FenValue.FromBoolean(obj.Keys()?.Contains(prop) ?? false);
            })));
            
            // Object.groupBy(items, callback) - ES2024
            objectConstructor.Set("groupBy", FenValue.FromFunction(new FenFunction("groupBy", (args, thisVal) =>
            {
                var result = new FenObject();
                if (args.Length < 2 || !args[0].IsObject || !args[1].IsFunction) return FenValue.FromObject(result);
                var items = args[0].AsObject();
                var callback = args[1].AsFunction();
                var lenVal = items.Get("length", null);
                int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                var groups = new Dictionary<string, List<FenValue>>();
                for (int i = 0; i < len; i++)
                {
                    var item = items.Get(i.ToString(), null);
                    var keyResult = callback.Invoke(new FenValue[] { item, FenValue.FromNumber(i) }, null);
                    var groupKey = keyResult.ToString();
                    if (!groups.ContainsKey(groupKey)) groups[groupKey] = new List<FenValue>();
                    groups[groupKey].Add(item);
                }
                
                foreach (var kvp in groups)
                {
                    var groupArr = FenObject.CreateArray();
                    for (int i = 0; i < kvp.Value.Count; i++)
                    {
                        groupArr.Set(i.ToString(), kvp.Value[i], null);
                    }
                    groupArr.Set("length", FenValue.FromNumber(kvp.Value.Count), null);
                    result.Set(kvp.Key, FenValue.FromObject(groupArr), null);
                }
                return FenValue.FromObject(result);
            })));
            
            // Object.freeze(obj) - ES5
            objectConstructor.Set("freeze", FenValue.FromFunction(new FenFunction("freeze", (args, thisVal) =>
            {
                // Simplified: just return the object (full freeze would prevent modifications)
                return args.Length > 0 ? args[0] : FenValue.Undefined;
            })));
            
            // Object.seal(obj) - ES5
            objectConstructor.Set("seal", FenValue.FromFunction(new FenFunction("seal", (args, thisVal) =>
            {
                return args.Length > 0 ? args[0] : FenValue.Undefined;
            })));
            
            // Object.create is already defined earlier (line ~377) with full propertiesObject support.
            // Do NOT re-define it here — the earlier definition handles both args correctly.

            // Object.getPrototypeOf(obj) - ES5
            objectConstructor.Set("getPrototypeOf", FenValue.FromFunction(new FenFunction("getPrototypeOf", (args, thisVal) =>
            {
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                {
                    return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                }

                var obj = CoerceObjectLike(args[0], throwOnNullish: true, out var coercionError);
                if (!coercionError.IsUndefined)
                {
                    return coercionError;
                }

                var proto = obj?.GetPrototype();
                return proto != null ? FenValue.FromObject(proto) : FenValue.Null;
            })));
            
            // Object.setPrototypeOf(obj, proto) - ES2015
            objectConstructor.Set("setPrototypeOf", FenValue.FromFunction(new FenFunction("setPrototypeOf", (args, thisVal) =>
            {
                if (args.Length < 2)
                {
                    return FenValue.FromError("TypeError: Object.setPrototypeOf requires 2 arguments");
                }

                if (!args[0].IsObject)
                {
                    return FenValue.FromError("TypeError: Object.setPrototypeOf called on non-object");
                }

                if (!args[1].IsObject && !args[1].IsNull)
                {
                    return FenValue.FromError("TypeError: Object prototype may only be an Object or null");
                }

                var obj = args[0].AsObject();
                obj?.SetPrototype(args[1].IsObject ? args[1].AsObject() : null);
                return args[0];
            })));
            
            // Object.is(value1, value2) - ES2015
            objectConstructor.Set("is", FenValue.FromFunction(new FenFunction("is", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.FromBoolean(args.Length == 0 || args[0].IsUndefined);
                var v1 = args[0];
                var v2 = args[1];
                // SameValue algorithm
                if (v1.IsNumber && v2.IsNumber)
                {
                    var n1 = v1.ToNumber();
                    var n2 = v2.ToNumber();
                    if (double.IsNaN(n1) && double.IsNaN(n2)) return FenValue.FromBoolean(true);
                    if (n1 == 0 && n2 == 0)
                        return FenValue.FromBoolean(1.0 / n1 == 1.0 / n2); // Distinguish +0 and -0
                }
                return FenValue.FromBoolean(v1.StrictEquals(v2));
            })));
            
            // ES5.1: Object.defineProperty(obj, prop, descriptor)
            objectConstructor.Set("defineProperty", FenValue.FromFunction(new FenFunction("defineProperty", (args, thisVal) =>
            {
                if (args.Length < 3)
                {
                    return FenValue.FromError("TypeError: Object.defineProperty requires 3 arguments");
                }

                if (!args[0].IsObject)
                {
                    return FenValue.FromError("TypeError: Object.defineProperty called on non-object");
                }

                var obj = args[0].AsObject() as FenObject;
                if (obj == null)
                {
                    return FenValue.FromError("TypeError: Object.defineProperty called on non-object");
                }

                var prop = args[1].ToString();

                if (!args[2].IsObject)
                {
                    return FenValue.FromError("TypeError: Property description must be an object");
                }

                var descObj = args[2].AsObject() as FenObject;
                if (descObj == null)
                {
                    return FenValue.FromError("TypeError: Property description must be an object");
                }

                var desc = new PropertyDescriptor
                {
                    Enumerable = descObj.Get("enumerable", null).ToBoolean(),
                    Configurable = descObj.Get("configurable", null).ToBoolean(),
                };
                var getVal = descObj.Get("get", null);
                var setVal = descObj.Get("set", null);
                if (!getVal.IsUndefined || !setVal.IsUndefined)
                {
                    var hasDataFields = !descObj.Get("value", null).IsUndefined || !descObj.Get("writable", null).IsUndefined;
                    if (hasDataFields)
                    {
                        return FenValue.FromError("TypeError: Invalid property descriptor. Cannot both specify accessors and a value or writable attribute");
                    }
                    desc.Getter = getVal.IsFunction ? getVal.AsFunction() : null;
                    desc.Setter = setVal.IsFunction ? setVal.AsFunction() : null;
                }
                else
                {
                    desc.Value = descObj.Get("value", null);
                    desc.Writable = descObj.Get("writable", null).ToBoolean();
                }

                if (!obj.DefineOwnProperty(prop, desc))
                {
                    return FenValue.FromError($"TypeError: Cannot define property '{prop}'");
                }

                return args[0];
            })));

            // ES5.1: Object.defineProperties(obj, props)
            objectConstructor.Set("defineProperties", FenValue.FromFunction(new FenFunction("defineProperties", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject) return args.Length > 0 ? args[0] : FenValue.Undefined;
                var propsObj = args[1].AsObject() as FenObject;
                if (propsObj == null) return args[0];
                var defineProperty = objectConstructor.Get("defineProperty", null).AsFunction();
                foreach (var key in propsObj.Keys(null))
                    defineProperty?.Invoke(new FenValue[] { args[0], FenValue.FromString(key), propsObj.Get(key, null) }, null);
                return args[0];
            })));

            // ES5.1: Object.getOwnPropertyDescriptor(obj, prop)
            objectConstructor.Set("getOwnPropertyDescriptor", FenValue.FromFunction(new FenFunction("getOwnPropertyDescriptor", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject) return FenValue.Undefined;
                var obj = args[0].AsObject() as FenObject;
                var prop = args[1].ToString();
                if (prop == "create") 
                {
                    Console.WriteLine($"[DEBUG-RUNTIME] getOwnPropertyDescriptor called for 'create'");
                    Console.WriteLine($"[DEBUG-RUNTIME] Arg0 Type: {args[0].Type}");
                    Console.WriteLine($"[DEBUG-RUNTIME] Arg0 AsObject: {args[0].AsObject()?.GetType().Name ?? "null"}");
                    Console.WriteLine($"[DEBUG-RUNTIME] Obj is FenObject? {obj is FenObject}");
                    if (obj != null) Console.WriteLine($"[DEBUG-RUNTIME] Obj Hash: {obj.GetHashCode()}");
                }
                var desc = obj?.GetOwnPropertyDescriptor(prop);
                if (desc == null) return FenValue.Undefined;
                var result = new FenObject();
                if (desc.Value.IsAccessor)
                {
                    result.Set("get", desc.Value.Getter != null ? FenValue.FromFunction(desc.Value.Getter) : FenValue.Undefined);
                    result.Set("set", desc.Value.Setter != null ? FenValue.FromFunction(desc.Value.Setter) : FenValue.Undefined);
                }
                else
                {
                    result.Set("value", desc.Value.Value ?? FenValue.Undefined);
                    result.Set("writable", FenValue.FromBoolean(desc.Value.Writable ?? false));
                }
                result.Set("enumerable", FenValue.FromBoolean(desc.Value.Enumerable ?? false));
                result.Set("configurable", FenValue.FromBoolean(desc.Value.Configurable ?? false));
                return FenValue.FromObject(result);
            })));

            // ES2017: Object.getOwnPropertyDescriptors(obj)
            objectConstructor.Set("getOwnPropertyDescriptors", FenValue.FromFunction(new FenFunction("getOwnPropertyDescriptors", (args, thisVal) =>
            {
                var result = new FenObject();
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromObject(result);
                var obj = args[0].AsObject() as FenObject;
                if (obj == null) return FenValue.FromObject(result);
                var getDescFn = objectConstructor.Get("getOwnPropertyDescriptor", null).AsFunction();
                foreach (var key in obj.GetOwnPropertyNames())
                    result.Set(key, getDescFn?.Invoke(new FenValue[] { args[0], FenValue.FromString(key) }, null) ?? FenValue.Undefined);
                return FenValue.FromObject(result);
            })));

            // ES5.1: Object.getOwnPropertyNames(obj)
            objectConstructor.Set("getOwnPropertyNames", FenValue.FromFunction(new FenFunction("getOwnPropertyNames", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromObject(CreateEmptyArray());
                var obj = args[0].AsObject() as FenObject;
                if (obj == null) return FenValue.FromObject(CreateEmptyArray());
                var names = obj.GetOwnPropertyNames().ToList();
                var result = FenObject.CreateArray();
                for (int i = 0; i < names.Count; i++) result.Set(i.ToString(), FenValue.FromString(names[i]));
                result.Set("length", FenValue.FromNumber(names.Count));
                return FenValue.FromObject(result);
            })));

            // ES6: Object.getOwnPropertySymbols(obj) — returns array of own symbol keys
            objectConstructor.Set("getOwnPropertySymbols", FenValue.FromFunction(new FenFunction("getOwnPropertySymbols", (args, thisVal) =>
            {
                // Symbols stored as @@{id} keys — return empty for now (spec compliant skeleton)
                return FenValue.FromObject(CreateEmptyArray());
            })));

            // ES5.1: Object.preventExtensions(obj)
            objectConstructor.Set("preventExtensions", FenValue.FromFunction(new FenFunction("preventExtensions", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsObject)
                    (args[0].AsObject() as FenObject)?.PreventExtensions();
                return args.Length > 0 ? args[0] : FenValue.Undefined;
            })));

            // ES5.1: Object.isExtensible(obj)
            objectConstructor.Set("isExtensible", FenValue.FromFunction(new FenFunction("isExtensible", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var obj = args[0].AsObject() as FenObject;
                return FenValue.FromBoolean(obj?.IsExtensible ?? false);
            })));

            // ES5.1: Object.isFrozen(obj)
            objectConstructor.Set("isFrozen", FenValue.FromFunction(new FenFunction("isFrozen", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromBoolean(true);
                var obj = args[0].AsObject() as FenObject;
                return FenValue.FromBoolean(obj?.IsFrozen() ?? true);
            })));

            // ES5.1: Object.isSealed(obj)
            objectConstructor.Set("isSealed", FenValue.FromFunction(new FenFunction("isSealed", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromBoolean(true);
                var obj = args[0].AsObject() as FenObject;
                return FenValue.FromBoolean(obj?.IsSealed() ?? true);
            })));

            // Fix Object.freeze and Object.seal to actually work (FenObject.Freeze/Seal already implemented)
            objectConstructor.Set("freeze", FenValue.FromFunction(new FenFunction("freeze", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsObject) (args[0].AsObject() as FenObject)?.Freeze();
                return args.Length > 0 ? args[0] : FenValue.Undefined;
            })));
            objectConstructor.Set("seal", FenValue.FromFunction(new FenFunction("seal", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsObject) (args[0].AsObject() as FenObject)?.Seal();
                return args.Length > 0 ? args[0] : FenValue.Undefined;
            })));

            // Object.prototype methods — attached to a shared prototype all objects inherit from
            objectProto.Set("hasOwnProperty", FenValue.FromFunction(new FenFunction("hasOwnProperty", (args, thisVal) =>
            {
                var prop = args.Length > 0 ? args[0].ToString() : "";
                var obj = thisVal.AsObject() as FenObject;
                if (obj == null) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(obj.GetOwnPropertyDescriptor(prop) != null);
            })));
            objectProto.Set("isPrototypeOf", FenValue.FromFunction(new FenFunction("isPrototypeOf", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var target = args[0].AsObject();
                var proto = thisVal.AsObject();
                var cur = target?.GetPrototype();
                while (cur != null)
                {
                    if (ReferenceEquals(cur, proto)) return FenValue.FromBoolean(true);
                    cur = cur.GetPrototype();
                }
                return FenValue.FromBoolean(false);
            })));
            objectProto.Set("propertyIsEnumerable", FenValue.FromFunction(new FenFunction("propertyIsEnumerable", (args, thisVal) =>
            {
                var prop = args.Length > 0 ? args[0].ToString() : "";
                var obj = thisVal.AsObject() as FenObject;
                var desc = obj?.GetOwnPropertyDescriptor(prop);
                return FenValue.FromBoolean(desc?.Enumerable ?? false);
            })));
            objectProto.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                if (thisVal.IsUndefined) return FenValue.FromString("[object Undefined]");
                if (thisVal.IsNull) return FenValue.FromString("[object Null]");
                var cls = (thisVal.AsObject() as FenObject)?.InternalClass ?? "Object";
                return FenValue.FromString($"[object {cls}]");
            })));
            objectProto.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) => thisVal)));
            objectProto.Set("toLocaleString", FenValue.FromFunction(new FenFunction("toLocaleString", (args, thisVal) =>
                objectProto.Get("toString", null).AsFunction()?.Invoke(args, null) ?? FenValue.FromString("[object Object]"))));
            objectConstructor.Set("prototype", FenValue.FromObject(objectProto));



            // navigator object - Privacy-focused (generic values to prevent fingerprinting)
            var navigator = new FenObject();
            var navigatorUa = BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent);
            navigator.Set("userAgent", FenValue.FromString(navigatorUa));
            navigator.Set("platform", FenValue.FromString("Win32"));
            navigator.Set("language", FenValue.FromString("en-US"));
            navigator.Set("languages", FenValue.FromObject(CreateArray(new[] { "en-US", "en" })));
            navigator.Set("cookieEnabled", FenValue.FromBoolean(true));
            navigator.Set("onLine", FenValue.FromBoolean(true));
            navigator.Set("doNotTrack", FenValue.FromString(BrowserSettings.Instance.SendDoNotTrack ? "1" : "0"));
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
            navigator.Set("javaEnabled", FenValue.FromFunction(new FenFunction("javaEnabled", (FenValue[] args, FenValue thisVal) => FenValue.FromBoolean(false))));
            
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
            
            history.Set("pushState", FenValue.FromFunction(new FenFunction("pushState", (FenValue[] args, FenValue thisVal) =>
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

            history.Set("replaceState", FenValue.FromFunction(new FenFunction("replaceState", (FenValue[] args, FenValue thisVal) =>
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

            history.Set("go", FenValue.FromFunction(new FenFunction("go", (FenValue[] args, FenValue thisVal) =>
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

            history.Set("back", FenValue.FromFunction(new FenFunction("back", (FenValue[] args, FenValue thisVal) =>
            {
                _historyBridge?.Go(-1);
                return FenValue.Undefined;
            })));
            
            history.Set("forward", FenValue.FromFunction(new FenFunction("forward", (FenValue[] args, FenValue thisVal) =>
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

            // sessionStorage - Partitioned per runtime instance and origin
            var sessionStorage = FenBrowser.FenEngine.WebAPIs.StorageApi.CreateSessionStorage(GetCurrentOrigin);
            SetGlobal("sessionStorage", FenValue.FromObject(sessionStorage));

            // window object - Comprehensive with all standard properties
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
            // var windowEventListeners = new Dictionary<string, List<FenValue>>(); // Field used instead
            
            // addEventListener
            var addEventListenerFunc = FenValue.FromFunction(new FenFunction("addEventListener", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length >= 2)
                {
                    var eventType = args[0].ToString();
                    var callback = args[1];
                    FenLogger.Info($"[FenRuntime] addEventListener called for '{eventType}'", LogCategory.Events);
                    
                    if (!_windowEventListeners.ContainsKey(eventType))
                    {
                        _windowEventListeners[eventType] = new List<FenValue>();
                    }
                    _windowEventListeners[eventType].Add(callback);
                }
                return FenValue.Undefined;
            }));
            window.Set("addEventListener", addEventListenerFunc);
            
            // removeEventListener
            var removeEventListenerFunc = FenValue.FromFunction(new FenFunction("removeEventListener", (FenValue[] args, FenValue thisVal) =>
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
            var dispatchEventFunc = FenValue.FromFunction(new FenFunction("dispatchEvent", (FenValue[] args, FenValue thisVal) =>
            {
                // Basic stub - returns true to indicate event was dispatched
                return FenValue.FromBoolean(true);
            }));
            window.Set("dispatchEvent", dispatchEventFunc);

            // window.open (popup gate + same-window fallback)
            var windowOpenFunc = FenValue.FromFunction(new FenFunction("open", (FenValue[] args, FenValue thisVal) =>
            {
                if (BrowserSettings.Instance.BlockPopups)
                {
                    FenLogger.Warn("[FenRuntime] window.open blocked by BlockPopups policy", LogCategory.General);
                    return FenValue.Null;
                }

                var requestedUrl = args.Length > 0 ? args[0].ToString() : string.Empty;
                if (string.IsNullOrWhiteSpace(requestedUrl))
                {
                    return FenValue.Null;
                }

                try
                {
                    if (_historyBridge != null)
                    {
                        _historyBridge.PushState(null, string.Empty, requestedUrl);
                    }
                }
                catch { }

                try
                {
                    if (Uri.TryCreate(requestedUrl, UriKind.Absolute, out var resolved))
                    {
                        location.Set("href", FenValue.FromString(resolved.AbsoluteUri));
                        location.Set("protocol", FenValue.FromString(resolved.Scheme + ":"));
                        location.Set("host", FenValue.FromString(resolved.Authority));
                        location.Set("hostname", FenValue.FromString(resolved.Host));
                        location.Set("pathname", FenValue.FromString(string.IsNullOrEmpty(resolved.AbsolutePath) ? "/" : resolved.AbsolutePath));
                    }
                    else
                    {
                        location.Set("href", FenValue.FromString(requestedUrl));
                    }
                }
                catch { }

                // Same-window fallback for now until full popup/tab orchestration is wired.
                return FenValue.FromObject(window);
            }));
            window.Set("open", windowOpenFunc);
            
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
            SetGlobal("open", windowOpenFunc);

            // globalThis - ES2020 (reference to global object)
            SetGlobal("globalThis", FenValue.FromObject(window));

            // Array constructor with static methods - ES2015+
            var arrayConstructor = arrayCtor;
            
            // Array.isArray(value) - ES5
            arrayConstructor.Set("isArray", FenValue.FromFunction(new FenFunction("isArray", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var val = args[0];
                if (!val.IsObject && !val.IsFunction) return FenValue.FromBoolean(false);
                var obj = val.AsObject();
                return FenValue.FromBoolean(obj is FenObject fo && fo.InternalClass == "Array");
            })));
            
            // Array.from(arrayLike, mapFn, thisArg) - ES2015
            arrayConstructor.Set("from", FenValue.FromFunction(new FenFunction("from", (args, thisVal) =>
            {
                var result = FenObject.CreateArray();
                if (args.Length == 0)
                {
                    result.Set("length", FenValue.FromNumber(0), null);
                    return FenValue.FromObject(result);
                }
                
                var source = args[0];
                FenFunction mapFn = args.Length > 1 && args[1].IsFunction ? args[1].AsFunction() as FenFunction : null;
                int idx = 0;
                
                if (source.IsString)
                {
                    var str = source.ToString();
                    for (int i = 0; i < str.Length; i++)
                    {
                        var item = FenValue.FromString(str[i].ToString());
                        var mapped = mapFn != null ? mapFn.Invoke(new FenValue[] { item, FenValue.FromNumber(i) }, null) : item;
                        result.Set(idx.ToString(), mapped, null);
                        idx++;
                    }
                }
                else if (source.IsObject)
                {
                    var obj = source.AsObject();
                    var lenVal = obj.Get("length", null);
                    int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                    for (int i = 0; i < len; i++)
                    {
                        var item = obj.Get(i.ToString(), null);
                        var mapped = mapFn != null ? mapFn.Invoke(new FenValue[] { item, FenValue.FromNumber(i) }, null) : item;
                        result.Set(idx.ToString(), mapped, null);
                        idx++;
                    }
                }
                result.Set("length", FenValue.FromNumber(idx), null);
                return FenValue.FromObject(result);
            })));
            
            // Array.of(...elements) - ES2015
            arrayConstructor.Set("of", FenValue.FromFunction(new FenFunction("of", (args, thisVal) =>
            {
                var result = FenObject.CreateArray();
                for (int i = 0; i < args.Length; i++)
                {
                    result.Set(i.ToString(), args[i], null);
                }
                result.Set("length", FenValue.FromNumber(args.Length), null);
                return FenValue.FromObject(result);
            })));
            

            // Event constructor (DOM Level 3)
            SetGlobal("Event", FenValue.FromFunction(new FenFunction("Event", (FenValue[] args, FenValue thisVal) =>
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
                        bubbles = opts.Get("bubbles").ToBoolean();
                        cancelable = opts.Get("cancelable").ToBoolean();
                        composed = opts.Get("composed").ToBoolean();
                    }
                }

                return FenValue.FromObject(new FenBrowser.FenEngine.DOM.DomEvent(type, bubbles, cancelable, composed));
            })));

            // CustomEvent constructor (DOM Level 3)
            SetGlobal("CustomEvent", FenValue.FromFunction(new FenFunction("CustomEvent", (FenValue[] args, FenValue thisVal) =>
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
                        var bubblesVal = opts.Get("bubbles");
                        bubbles = bubblesVal.IsUndefined ? false : bubblesVal.ToBoolean();
                        var cancelableVal = opts.Get("cancelable");
                        cancelable = cancelableVal.IsUndefined ? false : cancelableVal.ToBoolean();
                        detail = opts.Get("detail");
                    }
                }

                return FenValue.FromObject(new FenBrowser.FenEngine.DOM.CustomEvent(type, bubbles, cancelable, detail));
            })));

            // ─── performance object ───
            var perfStartTime = System.Diagnostics.Stopwatch.GetTimestamp();
            var perfFreq = (double)System.Diagnostics.Stopwatch.Frequency;
            var performanceObj = new FenObject();
            performanceObj.Set("now", FenValue.FromFunction(new FenFunction("now", (args, thisVal) => {
                double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - perfStartTime) / perfFreq * 1000.0;
                return FenValue.FromNumber(Math.Round(elapsed, 2));
            })));
            performanceObj.Set("timeOrigin", FenValue.FromNumber(
                (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds));
            performanceObj.Set("getEntries", FenValue.FromFunction(new FenFunction("getEntries", (args, thisVal) => {
                return FenValue.FromObject(FenObject.CreateArray());
            })));
            performanceObj.Set("getEntriesByType", FenValue.FromFunction(new FenFunction("getEntriesByType", (args, thisVal) => {
                return FenValue.FromObject(FenObject.CreateArray());
            })));
            performanceObj.Set("getEntriesByName", FenValue.FromFunction(new FenFunction("getEntriesByName", (args, thisVal) => {
                return FenValue.FromObject(FenObject.CreateArray());
            })));
            performanceObj.Set("mark", FenValue.FromFunction(new FenFunction("mark", (args, thisVal) => FenValue.Undefined)));
            performanceObj.Set("measure", FenValue.FromFunction(new FenFunction("measure", (args, thisVal) => FenValue.Undefined)));
            performanceObj.Set("clearMarks", FenValue.FromFunction(new FenFunction("clearMarks", (args, thisVal) => FenValue.Undefined)));
            performanceObj.Set("clearMeasures", FenValue.FromFunction(new FenFunction("clearMeasures", (args, thisVal) => FenValue.Undefined)));
            SetGlobal("performance", FenValue.FromObject(performanceObj));
            window.Set("performance", FenValue.FromObject(performanceObj));

            // ─── TextEncoder ───
            SetGlobal("TextEncoder", FenValue.FromFunction(new FenFunction("TextEncoder", (args, thisVal) => {
                var encoder = new FenObject();
                encoder.Set("encoding", FenValue.FromString("utf-8"));
                encoder.Set("encode", FenValue.FromFunction(new FenFunction("encode", (encArgs, encThis) => {
                    var str = encArgs.Length > 0 ? encArgs[0].ToString() : "";
                    var bytes = Encoding.UTF8.GetBytes(str);
                    var arr = FenObject.CreateArray();
                    for (int bi = 0; bi < bytes.Length; bi++)
                        arr.Set(bi.ToString(), FenValue.FromNumber(bytes[bi]));
                    arr.Set("length", FenValue.FromNumber(bytes.Length));
                    arr.Set("byteLength", FenValue.FromNumber(bytes.Length));
                    arr.NativeObject = bytes; // Store raw bytes for interop
                    return FenValue.FromObject(arr);
                })));
                encoder.Set("encodeInto", FenValue.FromFunction(new FenFunction("encodeInto", (encArgs, encThis) => {
                    var result = new FenObject();
                    result.Set("read", FenValue.FromNumber(0));
                    result.Set("written", FenValue.FromNumber(0));
                    return FenValue.FromObject(result);
                })));
                return FenValue.FromObject(encoder);
            })));

            // ─── TextDecoder ───
            SetGlobal("TextDecoder", FenValue.FromFunction(new FenFunction("TextDecoder", (args, thisVal) => {
                var label = args.Length > 0 ? args[0].ToString() : "utf-8";
                var decoder = new FenObject();
                decoder.Set("encoding", FenValue.FromString(label));
                decoder.Set("fatal", FenValue.FromBoolean(false));
                decoder.Set("ignoreBOM", FenValue.FromBoolean(false));
                decoder.Set("decode", FenValue.FromFunction(new FenFunction("decode", (decArgs, decThis) => {
                    if (decArgs.Length == 0) return FenValue.FromString("");
                    var bufVal = decArgs[0];
                    if (bufVal.IsObject)
                    {
                        var obj = bufVal.AsObject() as FenObject;
                        if (obj?.NativeObject is byte[] rawBytes)
                            return FenValue.FromString(Encoding.UTF8.GetString(rawBytes));
                        // Array-like: read indexed properties
                        var lenVal = obj?.Get("length") ?? obj?.Get("byteLength");
                        if (lenVal.HasValue && lenVal.Value.IsNumber)
                        {
                            int len = (int)lenVal.Value.ToNumber();
                            var bytes = new byte[len];
                            for (int bi = 0; bi < len; bi++)
                                bytes[bi] = (byte)obj.Get(bi.ToString()).ToNumber();
                            return FenValue.FromString(Encoding.UTF8.GetString(bytes));
                        }
                    }
                    return FenValue.FromString(bufVal.ToString());
                })));
                return FenValue.FromObject(decoder);
            })));

            // ─── AbortController / AbortSignal ───
            SetGlobal("AbortController", FenValue.FromFunction(new FenFunction("AbortController", (args, thisVal) => {
                var controller = new FenObject();
                var signal = new FenObject();
                signal.Set("aborted", FenValue.FromBoolean(false));
                signal.Set("reason", FenValue.Undefined);
                var signalListeners = new List<FenValue>();
                signal.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (sigArgs, sigThis) => {
                    if (sigArgs.Length >= 2) signalListeners.Add(sigArgs[1]);
                    return FenValue.Undefined;
                })));
                signal.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (sigArgs, sigThis) => {
                    return FenValue.Undefined;
                })));
                signal.Set("throwIfAborted", FenValue.FromFunction(new FenFunction("throwIfAborted", (sigArgs, sigThis) => {
                    if (signal.Get("aborted").ToBoolean())
                        return FenValue.FromError("AbortError: signal is aborted");
                    return FenValue.Undefined;
                })));

                controller.Set("signal", FenValue.FromObject(signal));
                controller.Set("abort", FenValue.FromFunction(new FenFunction("abort", (abortArgs, abortThis) => {
                    signal.Set("aborted", FenValue.FromBoolean(true));
                    var reason = abortArgs.Length > 0 ? abortArgs[0] : FenValue.FromString("AbortError");
                    signal.Set("reason", reason);
                    foreach (var listener in signalListeners)
                    {
                        if (listener.IsFunction)
                            listener.AsFunction()?.Invoke(new FenValue[] { reason }, _context);
                    }
                    return FenValue.Undefined;
                })));

                return FenValue.FromObject(controller);
            })));

            // ─── WebSocket stub ───
            SetGlobal("WebSocket", FenValue.FromFunction(new FenFunction("WebSocket", (args, thisVal) => {
                var url = args.Length > 0 ? args[0].ToString() : "";
                var ws = new FenObject();
                ws.Set("url", FenValue.FromString(url));
                ws.Set("readyState", FenValue.FromNumber(0)); // CONNECTING
                ws.Set("CONNECTING", FenValue.FromNumber(0));
                ws.Set("OPEN", FenValue.FromNumber(1));
                ws.Set("CLOSING", FenValue.FromNumber(2));
                ws.Set("CLOSED", FenValue.FromNumber(3));
                ws.Set("bufferedAmount", FenValue.FromNumber(0));
                ws.Set("extensions", FenValue.FromString(""));
                ws.Set("protocol", FenValue.FromString(""));
                ws.Set("binaryType", FenValue.FromString("blob"));
                ws.Set("onopen", FenValue.Null);
                ws.Set("onclose", FenValue.Null);
                ws.Set("onerror", FenValue.Null);
                ws.Set("onmessage", FenValue.Null);
                ws.Set("send", FenValue.FromFunction(new FenFunction("send", (sendArgs, sendThis) => FenValue.Undefined)));
                ws.Set("close", FenValue.FromFunction(new FenFunction("close", (closeArgs, closeThis) => {
                    ws.Set("readyState", FenValue.FromNumber(3));
                    return FenValue.Undefined;
                })));
                ws.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (eArgs, eThis) => FenValue.Undefined)));
                ws.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (eArgs, eThis) => FenValue.Undefined)));
                return FenValue.FromObject(ws);
            })));

            // ─── structuredClone ───
            SetGlobal("structuredClone", FenValue.FromFunction(new FenFunction("structuredClone", (args, thisVal) => {
                if (args.Length == 0) return FenValue.Undefined;
                // Shallow clone for objects, pass-through for primitives
                var val = args[0];
                if (val.IsObject && val.AsObject() is FenObject srcObj)
                {
                    var clone = new FenObject();
                    foreach (var key in srcObj.Keys())
                        clone.Set(key, srcObj.Get(key));
                    return FenValue.FromObject(clone);
                }
                return val;
            })));

            // crypto and Intl are registered later in InitializeBuiltins (fuller implementations)

            // ─── getComputedStyle ───
            var getComputedStyleFn = FenValue.FromFunction(new FenFunction("getComputedStyle", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromObject(new FenObject());
                if (args[0].IsObject)
                {
                    var obj = args[0].AsObject();
                    // Get the native Element from either ElementWrapper or FenObject
                    FenBrowser.Core.Dom.V2.Element nativeEl = null;
                    if (obj is FenBrowser.FenEngine.DOM.ElementWrapper ew)
                        nativeEl = ew.Element;
                    else if (obj is FenObject fenObj)
                        nativeEl = fenObj.NativeObject as FenBrowser.Core.Dom.V2.Element;

                    if (nativeEl == null)
                    {
                        // Fallback: try .style property
                        var styleVal = obj?.Get("style");
                        if (styleVal.HasValue && styleVal.Value.IsObject)
                            return styleVal.Value;
                    }
                    else
                    {
                        var computedStyle = FenBrowser.Core.Css.NodeStyleExtensions.GetComputedStyle(nativeEl);
                        if (computedStyle != null)
                        {
                            var csObj = new FenObject();
                            // Expose as a proxy-like object with getPropertyValue
                            csObj.Set("getPropertyValue", FenValue.FromFunction(new FenFunction("getPropertyValue", (gpArgs, gpThis) => {
                                if (gpArgs.Length == 0) return FenValue.FromString("");
                                var prop = gpArgs[0].ToString();
                                var val = computedStyle.Map?.ContainsKey(prop) == true ? computedStyle.Map[prop] : "";
                                return FenValue.FromString(val ?? "");
                            })));
                            // Common properties
                            if (computedStyle.Map != null)
                            {
                                foreach (var kvp in computedStyle.Map)
                                    csObj.Set(kvp.Key, FenValue.FromString(kvp.Value ?? ""));
                            }
                            csObj.Set("display", FenValue.FromString(computedStyle.Display ?? "block"));
                            csObj.Set("visibility", FenValue.FromString(computedStyle.Visibility ?? "visible"));
                            csObj.Set("position", FenValue.FromString(computedStyle.Position ?? "static"));
                            return FenValue.FromObject(csObj);
                        }
                    }
                }
                return FenValue.FromObject(new FenObject());
            }));
            SetGlobal("getComputedStyle", getComputedStyleFn);
            window.Set("getComputedStyle", getComputedStyleFn);

            // ─── matchMedia ───
            var matchMediaFn = FenValue.FromFunction(new FenFunction("matchMedia", (args, thisVal) => {
                var query = args.Length > 0 ? args[0].ToString() : "";
                var mql = new FenObject();
                // Evaluate common media queries
                bool matches = false;
                if (query.Contains("prefers-color-scheme: dark")) matches = false; // light mode
                else if (query.Contains("prefers-color-scheme: light")) matches = true;
                else if (query.Contains("prefers-reduced-motion")) matches = false;
                else if (query.Contains("min-width"))
                {
                    // Parse min-width value and compare against 1920
                    var match = System.Text.RegularExpressions.Regex.Match(query, @"min-width:\s*(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int minW))
                        matches = 1920 >= minW;
                }
                else if (query.Contains("max-width"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(query, @"max-width:\s*(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int maxW))
                        matches = 1920 <= maxW;
                }
                else if (query.Contains("(pointer: fine)")) matches = true;
                else if (query.Contains("(hover: hover)")) matches = true;
                else if (query.Contains("screen")) matches = true;

                mql.Set("matches", FenValue.FromBoolean(matches));
                mql.Set("media", FenValue.FromString(query));
                var mqlListeners = new List<FenValue>();
                mql.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (eArgs, eThis) => {
                    if (eArgs.Length >= 2) mqlListeners.Add(eArgs[1]);
                    return FenValue.Undefined;
                })));
                mql.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (eArgs, eThis) => FenValue.Undefined)));
                mql.Set("addListener", FenValue.FromFunction(new FenFunction("addListener", (eArgs, eThis) => {
                    if (eArgs.Length >= 1 && eArgs[0].IsFunction) mqlListeners.Add(eArgs[0]);
                    return FenValue.Undefined;
                })));
                mql.Set("removeListener", FenValue.FromFunction(new FenFunction("removeListener", (eArgs, eThis) => FenValue.Undefined)));
                return FenValue.FromObject(mql);
            }));
            SetGlobal("matchMedia", matchMediaFn);
            window.Set("matchMedia", matchMediaFn);

            // ─── requestIdleCallback / cancelIdleCallback ───
            SetGlobal("requestIdleCallback", FenValue.FromFunction(new FenFunction("requestIdleCallback", (args, thisVal) => {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var cb = args[0].AsFunction();
                    // Execute via event loop task (idle = next available slot)
                    EventLoop.EventLoopCoordinator.Instance.ScheduleTask(() => {
                        var deadline = new FenObject();
                        deadline.Set("didTimeout", FenValue.FromBoolean(false));
                        deadline.Set("timeRemaining", FenValue.FromFunction(new FenFunction("timeRemaining", (a, t) => FenValue.FromNumber(50))));
                        cb?.Invoke(new FenValue[] { FenValue.FromObject(deadline) }, _context);
                    }, EventLoop.TaskSource.Other, "requestIdleCallback");
                    return FenValue.FromNumber(1);
                }
                return FenValue.FromNumber(0);
            })));
            SetGlobal("cancelIdleCallback", FenValue.FromFunction(new FenFunction("cancelIdleCallback", (args, thisVal) => FenValue.Undefined)));
            window.Set("requestIdleCallback", (FenValue)GetGlobal("requestIdleCallback"));
            window.Set("cancelIdleCallback", (FenValue)GetGlobal("cancelIdleCallback"));

            // ─── queueMicrotask at global scope ───
            SetGlobal("queueMicrotask", FenValue.FromFunction(new FenFunction("queueMicrotask", (args, thisVal) => {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var cb = args[0].AsFunction();
                    EventLoop.EventLoopCoordinator.Instance.ScheduleMicrotask(() => {
                        cb?.Invoke(Array.Empty<FenValue>(), _context);
                    });
                }
                return FenValue.Undefined;
            })));

            // ─── btoa / atob ───
            SetGlobal("btoa", FenValue.FromFunction(new FenFunction("btoa", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromString("");
                try {
                    var bytes = Encoding.Latin1.GetBytes(args[0].ToString());
                    return FenValue.FromString(Convert.ToBase64String(bytes));
                } catch { return FenValue.FromString(""); }
            })));
            SetGlobal("atob", FenValue.FromFunction(new FenFunction("atob", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromString("");
                try {
                    var bytes = Convert.FromBase64String(args[0].ToString());
                    return FenValue.FromString(Encoding.Latin1.GetString(bytes));
                } catch { return FenValue.FromString(""); }
            })));
            window.Set("btoa", (FenValue)GetGlobal("btoa"));
            window.Set("atob", (FenValue)GetGlobal("atob"));

            // Custom Elements Registry (Web Components)
            var customElementsRegistry = new FenBrowser.FenEngine.DOM.CustomElementRegistry();
            SetGlobal("customElements", FenValue.FromObject(customElementsRegistry.ToFenObject()));

            // requestAnimationFrame / cancelAnimationFrame

            // Use a simple counter and store callbacks in window.__raf_queue
            var requestAnimationFrameFunc = FenValue.FromFunction(new FenFunction("requestAnimationFrame", (FenValue[] args, FenValue thisVal) =>
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
            SetGlobal("RegExp", FenValue.FromFunction(new FenFunction("RegExp", (FenValue[] args, FenValue thisVal) =>
            {
                var pattern = args.Length > 0 ? args[0].ToString() : "";
                var flags = args.Length > 1 ? args[1].ToString() : "";
                
                // If first arg is already a RegExp, clone it
                if (args.Length > 0 && args[0].IsObject)
                {
                    var srcObj = args[0].AsObject() as FenObject;
                    var sourceVal = srcObj?.Get("source");
                    if (sourceVal != null && !sourceVal.Value.IsUndefined && srcObj.NativeObject is Regex)
                    {
                        pattern = sourceVal.Value.ToString();
                        if (args.Length == 1)
                        {
                            var flagsVal = srcObj.Get("flags");
                            flags = flagsVal.IsUndefined ? "" : flagsVal.ToString();
                        }
                    }
                }
                
                try
                {
                    var options = RegexOptions.None;
                    bool globalFlag = flags.Contains("g");
                    bool ignoreCase = flags.Contains("i");
                    bool multiline = flags.Contains("m");
                    bool dotAll = flags.Contains("s");
                    bool hasIndices = flags.Contains("d"); // ES2022

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
                    obj.Set("hasIndices", FenValue.FromBoolean(hasIndices)); // ES2022
                    obj.Set("lastIndex", FenValue.FromNumber(0));
                    
                    // test(str) - Returns true if the pattern matches
                    obj.Set("test", FenValue.FromFunction(new FenFunction("test", (testArgs, testThis) =>
                    {
                        if (testArgs.Length == 0) return FenValue.FromBoolean(false);
                        var str = testArgs[0].ToString();
                        var lastIdx = (int)(obj.Get("lastIndex").ToNumber());
                        var isGlobal = obj.Get("global").ToBoolean();
                        
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
                        var lastIdx = (int)(obj.Get("lastIndex").ToNumber());
                        var isGlobal = obj.Get("global").ToBoolean();
                        
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
                        var result = FenObject.CreateArray();
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
                        bool hasNamedGroups = false;
                        foreach (var gn in r.GetGroupNames())
                        {
                            if (!int.TryParse(gn, out _))
                            {
                                hasNamedGroups = true;
                                var g = match.Groups[gn];
                                groups.Set(gn, g.Success ? FenValue.FromString(g.Value) : FenValue.Undefined);
                            }
                        }
                        result.Set("groups", hasNamedGroups ? FenValue.FromObject(groups) : FenValue.Undefined);

                        // ES2022: hasIndices ('d' flag) — populate .indices array
                        if (hasIndices)
                        {
                            var indicesArr = new FenObject();
                            for (int i = 0; i < match.Groups.Count; i++)
                            {
                                var grp = match.Groups[i];
                                if (grp.Success)
                                {
                                    var pair = new FenObject();
                                    pair.Set("0", FenValue.FromNumber(grp.Index));
                                    pair.Set("1", FenValue.FromNumber(grp.Index + grp.Length));
                                    pair.Set("length", FenValue.FromNumber(2));
                                    indicesArr.Set(i.ToString(), FenValue.FromObject(pair));
                                }
                                else
                                {
                                    indicesArr.Set(i.ToString(), FenValue.Undefined);
                                }
                            }
                            indicesArr.Set("length", FenValue.FromNumber(match.Groups.Count));
                            // Named groups indices
                            if (hasNamedGroups)
                            {
                                var namedIndices = new FenObject();
                                foreach (var gn in r.GetGroupNames())
                                {
                                    if (!int.TryParse(gn, out _))
                                    {
                                        var g = match.Groups[gn];
                                        if (g.Success)
                                        {
                                            var pair = new FenObject();
                                            pair.Set("0", FenValue.FromNumber(g.Index));
                                            pair.Set("1", FenValue.FromNumber(g.Index + g.Length));
                                            pair.Set("length", FenValue.FromNumber(2));
                                            namedIndices.Set(gn, FenValue.FromObject(pair));
                                        }
                                        else
                                        {
                                            namedIndices.Set(gn, FenValue.Undefined);
                                        }
                                    }
                                }
                                indicesArr.Set("groups", FenValue.FromObject(namedIndices));
                            }
                            result.Set("indices", FenValue.FromObject(indicesArr));
                        }

                        return FenValue.FromObject(result);
                    })));
                    
                    // toString() - Returns "/pattern/flags"
                    obj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) =>
                        FenValue.FromString($"/{pattern}/{flags}"))));
                    
                    return FenValue.FromObject(obj);
                }
                catch (Exception ex)
                {
                    return FenValue.FromError($"Invalid regular expression: {ex.Message}");
                }
            })));

            // Math object
            var math = new FenObject();
            math.Set("PI", FenValue.FromNumber(Math.PI));
            math.Set("E", FenValue.FromNumber(Math.E));
            math.Set("abs", FenValue.FromFunction(new FenFunction("abs", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Abs(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("ceil", FenValue.FromFunction(new FenFunction("ceil", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Ceiling(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("floor", FenValue.FromFunction(new FenFunction("floor", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Floor(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("round", FenValue.FromFunction(new FenFunction("round", (FenValue[] args, FenValue thisVal) => 
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
            math.Set("pow", FenValue.FromFunction(new FenFunction("pow", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Pow(args.Length > 0 ? args[0].ToNumber() : double.NaN, args.Length > 1 ? args[1].ToNumber() : double.NaN)))));
            math.Set("sqrt", FenValue.FromFunction(new FenFunction("sqrt", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Sqrt(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("random", FenValue.FromFunction(new FenFunction("random", (FenValue[] args, FenValue thisVal) => 
            {
                lock (_mathRandom)
                {
                    return FenValue.FromNumber(_mathRandom.NextDouble());
                }
            })));
            math.Set("sin", FenValue.FromFunction(new FenFunction("sin", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Sin(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("cos", FenValue.FromFunction(new FenFunction("cos", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Cos(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("tan", FenValue.FromFunction(new FenFunction("tan", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Tan(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("trunc", FenValue.FromFunction(new FenFunction("trunc", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Truncate(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("sign", FenValue.FromFunction(new FenFunction("sign", (FenValue[] args, FenValue thisVal) =>
            {
                var x = args.Length > 0 ? args[0].ToNumber() : double.NaN;
                if (double.IsNaN(x)) return FenValue.FromNumber(double.NaN);
                if (x == 0)
                {
                    // Preserve the sign of zero per ECMAScript.
                    return FenValue.FromNumber(double.IsNegativeInfinity(1.0 / x) ? -0.0 : 0.0);
                }
                return FenValue.FromNumber(x > 0 ? 1 : -1);
            })));
            math.Set("log", FenValue.FromFunction(new FenFunction("log", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Log(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("log10", FenValue.FromFunction(new FenFunction("log10", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Log10(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("exp", FenValue.FromFunction(new FenFunction("exp", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Exp(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("asin", FenValue.FromFunction(new FenFunction("asin", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Asin(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("acos", FenValue.FromFunction(new FenFunction("acos", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Acos(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("atan", FenValue.FromFunction(new FenFunction("atan", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Atan(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("atan2", FenValue.FromFunction(new FenFunction("atan2", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromNumber(Math.Atan2(args.Length > 0 ? args[0].ToNumber() : double.NaN, args.Length > 1 ? args[1].ToNumber() : double.NaN)))));
            math.Set("hypot", FenValue.FromFunction(new FenFunction("hypot", (args, thisVal) => {
                double sum = 0;
                foreach (var arg in args) { var n = arg.ToNumber(); sum += n * n; }
                return FenValue.FromNumber(Math.Sqrt(sum));
            })));
            // ES2015+ Math methods
            math.Set("cbrt", FenValue.FromFunction(new FenFunction("cbrt", (args, t) => FenValue.FromNumber(Math.Cbrt(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("log2", FenValue.FromFunction(new FenFunction("log2", (args, t) => FenValue.FromNumber(Math.Log2(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("log1p", FenValue.FromFunction(new FenFunction("log1p", (args, t) => { var x = args.Length > 0 ? args[0].ToNumber() : double.NaN; return FenValue.FromNumber(Math.Log(1 + x)); })));
            math.Set("expm1", FenValue.FromFunction(new FenFunction("expm1", (args, t) => { var x = args.Length > 0 ? args[0].ToNumber() : double.NaN; return FenValue.FromNumber(Math.Exp(x) - 1); })));
            math.Set("sinh", FenValue.FromFunction(new FenFunction("sinh", (args, t) => FenValue.FromNumber(Math.Sinh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("cosh", FenValue.FromFunction(new FenFunction("cosh", (args, t) => FenValue.FromNumber(Math.Cosh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("tanh", FenValue.FromFunction(new FenFunction("tanh", (args, t) => FenValue.FromNumber(Math.Tanh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("asinh", FenValue.FromFunction(new FenFunction("asinh", (args, t) => FenValue.FromNumber(Math.Asinh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("acosh", FenValue.FromFunction(new FenFunction("acosh", (args, t) => FenValue.FromNumber(Math.Acosh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("atanh", FenValue.FromFunction(new FenFunction("atanh", (args, t) => FenValue.FromNumber(Math.Atanh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("clz32", FenValue.FromFunction(new FenFunction("clz32", (args, t) => {
                var n = (uint)(int)(args.Length > 0 ? args[0].ToNumber() : 0);
                if (n == 0) return FenValue.FromNumber(32);
                int clz = 0; while ((n & 0x80000000) == 0) { clz++; n <<= 1; }
                return FenValue.FromNumber(clz);
            })));
            math.Set("fround", FenValue.FromFunction(new FenFunction("fround", (args, t) => FenValue.FromNumber((double)(float)(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("imul", FenValue.FromFunction(new FenFunction("imul", (args, t) => {
                int a = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                int b = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                return FenValue.FromNumber((double)(a * b));
            })));
            // Math constants
            math.Set("LN2", FenValue.FromNumber(Math.Log(2)));
            math.Set("LN10", FenValue.FromNumber(Math.Log(10)));
            math.Set("LOG2E", FenValue.FromNumber(Math.Log2(Math.E)));
            math.Set("LOG10E", FenValue.FromNumber(Math.Log10(Math.E)));
            math.Set("SQRT2", FenValue.FromNumber(Math.Sqrt(2)));
            math.Set("SQRT1_2", FenValue.FromNumber(Math.Sqrt(0.5)));

            /* [PERF-REMOVED] */
            SetGlobal("Math", FenValue.FromObject(math));

            // BigInt constructor (ES2020)
            var bigIntCtor = new FenFunction("BigInt", (args, thisVal) =>
            {
                if (args.Length == 0)
                    return FenValue.FromBigInt(Types.JsBigInt.Zero);

                var arg = args[0];
                
                // BigInt cannot convert non-integer numbers
                if (arg.IsNumber)
                {
                    double num = arg.ToNumber();
                    if (double.IsNaN(num) || double.IsInfinity(num) || Math.Floor(num) != num)
                    {
                        return FenValue.FromError("RangeError: Cannot convert non-integer to BigInt");
                    }
                    return FenValue.FromBigInt(new Types.JsBigInt((long)num));
                }
                
                // Convert from string
                string str = arg.ToString();
                try
                {
                    return FenValue.FromBigInt(new Types.JsBigInt(str));
                }
                catch
                {
                    return FenValue.FromError($"SyntaxError: Cannot convert {str} to a BigInt");
                }
            });
            
            // BigInt static methods
            bigIntCtor.Set("asIntN", FenValue.FromFunction(new FenFunction("asIntN", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                // Simplified: just return the BigInt for now
                return args[1];
            })));
            
            bigIntCtor.Set("asUintN", FenValue.FromFunction(new FenFunction("asUintN", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                // Simplified: just return the BigInt for now
                return args[1];
            })));
            
            SetGlobal("BigInt", FenValue.FromFunction(bigIntCtor));

            // ES6 Collection constructors: Map, Set, WeakMap, WeakSet
            var mapCtorFn = new FenFunction("Map", (FenValue[] args, FenValue thisVal) =>
            {
                var map = new FenBrowser.FenEngine.Core.Types.JsMap(_context);
                if (args.Length > 0 && args[0].IsObject)
                {
                    var iterable = args[0].AsObject();
                    var lenVal = iterable?.Get("length");
                    if (lenVal != null && lenVal.Value.IsNumber)
                    {
                        int len = (int)lenVal.Value.ToNumber();
                        for (int i = 0; i < len; i++)
                        {
                            var entry = iterable.Get(i.ToString());
                            if (entry.IsObject)
                            {
                                var entryObj = entry.AsObject();
                                var key = entryObj?.Get("0") ?? FenValue.Undefined;
                                var val = entryObj?.Get("1") ?? FenValue.Undefined;
                                map.Get("set").AsFunction()?.Invoke(new FenValue[] { key, val }, _context);
                            }
                        }
                    }
                }
                return FenValue.FromObject(map);
            });
            // ES2024: Map.groupBy(items, keyFn)
            mapCtorFn.Set("groupBy", FenValue.FromFunction(new FenFunction("groupBy", (args, thisVal) =>
            {
                var result = new FenBrowser.FenEngine.Core.Types.JsMap(_context);
                if (args.Length < 2 || !args[0].IsObject || !args[1].IsFunction) return FenValue.FromObject(result);
                var items = args[0].AsObject();
                var callback = args[1].AsFunction();
                var lenVal = items.Get("length", null);
                int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                var mapGet = result.Get("get").AsFunction();
                var mapSet = result.Get("set").AsFunction();
                for (int i = 0; i < len; i++)
                {
                    var item = items.Get(i.ToString(), null);
                    var groupKey = callback.Invoke(new FenValue[] { item, FenValue.FromNumber(i) }, null);
                    var existing = mapGet.Invoke(new FenValue[] { groupKey }, null);
                    FenObject groupArr;
                    if (existing.IsUndefined)
                    {
                        groupArr = new FenObject();
                        groupArr.Set("length", FenValue.FromNumber(0), null);
                        mapSet.Invoke(new FenValue[] { groupKey, FenValue.FromObject(groupArr) }, null);
                    }
                    else
                    {
                        groupArr = existing.AsObject() as FenObject;
                    }
                    if (groupArr != null)
                    {
                        var idx = (int)groupArr.Get("length", null).ToNumber();
                        groupArr.Set(idx.ToString(), item, null);
                        groupArr.Set("length", FenValue.FromNumber(idx + 1), null);
                    }
                }
                return FenValue.FromObject(result);
            })));
            SetGlobal("Map", FenValue.FromFunction(mapCtorFn));

            SetGlobal("Set", FenValue.FromFunction(new FenFunction("Set", (FenValue[] args, FenValue thisVal) =>
            {
                var set = new FenBrowser.FenEngine.Core.Types.JsSet(_context);
                // If iterable argument provided, populate from it
                if (args.Length > 0 && args[0].IsObject)
                {
                    var iterable = args[0].AsObject();
                    var lenVal = iterable?.Get("length");
                    if (lenVal != null && lenVal.Value.IsNumber)
                    {
                        int len = (int)lenVal.Value.ToNumber();
                        for (int i = 0; i < len; i++)
                        {
                            var val = iterable.Get(i.ToString());
                            set.Get("add").AsFunction()?.Invoke(new FenValue[] { val  }, _context);
                        }
                    }
                }
                return FenValue.FromObject(set);
            })));

            // WeakMap - Keys must be objects, values are weakly referenced
            SetGlobal("WeakMap", FenValue.FromFunction(new FenFunction("WeakMap", (FenValue[] args, FenValue thisVal) =>
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
                            return (FenValue)val;
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
            SetGlobal("WeakSet", FenValue.FromFunction(new FenFunction("WeakSet", (FenValue[] args, FenValue thisVal) =>
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
            SetGlobal("Proxy", FenValue.FromFunction(new FenFunction("Proxy", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                
                var target = args[0].AsObject() as FenObject;
                var handlerVal = args[1].AsObject() as FenObject;
                
                if (target  == null || handlerVal  == null) return FenValue.Undefined;
                
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
                    var getVal = handlerVal.Get("get");
                    var getTrap = getVal.AsFunction();
                    if (getTrap != null)
                    {
                        return getTrap.Invoke(new FenValue[] { FenValue.FromObject(target), FenValue.FromString(prop), FenValue.FromObject(proxy) }, _context);
                    }
                    return target.Get(prop);
                })));
                
                proxy.Set("set", FenValue.FromFunction(new FenFunction("set", (setArgs, setThis) =>
                {
                    var prop = setArgs.Length > 0 ? setArgs[0].ToString() : "";
                    var val = setArgs.Length > 1 ? setArgs[1] : FenValue.Undefined;
                    var setTrap = handlerVal.Get("set").AsFunction();
                    if (setTrap != null)
                    {
                        return setTrap.Invoke(new FenValue[] { FenValue.FromObject(target), FenValue.FromString(prop), val, FenValue.FromObject(proxy) }, _context);
                    }
                    target.Set(prop, val);
                    return FenValue.FromBoolean(true);
                })));
                
                proxy.Set("has", FenValue.FromFunction(new FenFunction("has", (hasArgs, hasThis) =>
                {
                    var prop = hasArgs.Length > 0 ? hasArgs[0].ToString() : "";
                    var hasVal = handlerVal.Get("has");
                    var hasTrap = hasVal.AsFunction();
                    if (hasTrap != null)
                    {
                        return hasTrap.Invoke(new FenValue[] { FenValue.FromObject(target), FenValue.FromString(prop) }, _context);
                    }
                    return FenValue.FromBoolean(!target.Get(prop).IsUndefined);
                })));
                
                proxy.Set("deleteProperty", FenValue.FromFunction(new FenFunction("deleteProperty", (delArgs, delThis) =>
                {
                    var prop = delArgs.Length > 0 ? delArgs[0].ToString() : "";
                    var delTrap = handlerVal.Get("deleteProperty").AsFunction();
                    if (delTrap != null)
                    {
                        return delTrap.Invoke(new FenValue[] { FenValue.FromObject(target), FenValue.FromString(prop) }, _context);
                    }
                    target.Delete(prop);
                    return FenValue.FromBoolean(true);
                })));
                
                return FenValue.FromObject(proxy);
            })));

            // Reflect API is defined later in this file (around line 2550)

            // ES6 RegExp constructor - wraps .NET Regex
            SetGlobal("RegExp", FenValue.FromFunction(new FenFunction("RegExp", (FenValue[] args, FenValue thisVal) =>
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
                
                // Build .NET RegexOptions (ECMAScript flag is incompatible with Singleline, so use explicit flags)
                var options = RegexOptions.None;
                if (flags.Contains("i")) options |= RegexOptions.IgnoreCase;
                if (flags.Contains("m")) options |= RegexOptions.Multiline;
                if (flags.Contains("s")) options |= RegexOptions.Singleline;
                
                Regex regex = null;
                try { regex = new Regex(pattern, options); } catch { }
                
                regexObj.Set("test", FenValue.FromFunction(new FenFunction("test", (testArgs, testThis) =>
                {
                    if (testArgs.Length == 0 || regex  == null) return FenValue.FromBoolean(false);
                    return FenValue.FromBoolean(regex.IsMatch(testArgs[0].ToString()));
                })));
                
                regexObj.Set("exec", FenValue.FromFunction(new FenFunction("exec", (execArgs, execThis) =>
                {
                    if (execArgs.Length == 0 || regex  == null) return FenValue.Null;
                    var input = execArgs[0].ToString();
                    int startIndex = (int)(regexObj.Get("lastIndex").ToNumber());
                    bool isGlobal = regexObj.Get("global").ToBoolean();
                    
                    if (startIndex >= input.Length) {
                        if (isGlobal) regexObj.Set("lastIndex", FenValue.FromNumber(0));
                        return FenValue.Null;
                    }
                    
                    var match = regex.Match(input, startIndex);
                    if (!match.Success) {
                        if (isGlobal) regexObj.Set("lastIndex", FenValue.FromNumber(0));
                        return FenValue.Null;
                    }
                    
                    var result = FenObject.CreateArray();
                    result.Set("0", FenValue.FromString(match.Value));
                    for (int i = 1; i < match.Groups.Count; i++)
                        result.Set(i.ToString(), FenValue.FromString(match.Groups[i].Value));
                    result.Set("length", FenValue.FromNumber(match.Groups.Count));
                    result.Set("index", FenValue.FromNumber(match.Index));
                    result.Set("input", FenValue.FromString(input));
                    // ES2018: Named capture groups
                    var groups = new FenObject();
                    bool hasNamedGroups = false;
                    foreach (Group group in match.Groups)
                    {
                        if (!string.IsNullOrEmpty(group.Name) && !int.TryParse(group.Name, out _))
                        {
                            groups.Set(group.Name, FenValue.FromString(group.Value), null);
                            hasNamedGroups = true;
                        }
                    }
                    result.Set("groups", hasNamedGroups ? FenValue.FromObject(groups) : FenValue.Undefined);

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
            intl.Set("DateTimeFormat", FenValue.FromFunction(new FenFunction("DateTimeFormat", (FenValue[] args, FenValue thisVal) =>
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
            intl.Set("NumberFormat", FenValue.FromFunction(new FenFunction("NumberFormat", (FenValue[] args, FenValue thisVal) =>
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
            intl.Set("Collator", FenValue.FromFunction(new FenFunction("Collator", (FenValue[] args, FenValue thisVal) =>
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
            SetGlobal("ArrayBuffer", FenValue.FromFunction(new FenFunction("ArrayBuffer", (FenValue[] args, FenValue thisVal) =>
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
                
                SetGlobal(name, FenValue.FromFunction(new FenFunction(name, (FenValue[] args, FenValue thisVal) =>
                {
                    FenObject bufferObj = null;
                    int byteOffset = 0;
                    int length = 0;
                    byte[] data = null;

                    var firstArgObj = args[0].AsObject();
                    var byteLenVal = firstArgObj?.Get("byteLength") ?? FenValue.Undefined;
                    if (args.Length > 0 && args[0].IsObject && !byteLenVal.IsUndefined && byteLenVal.IsNumber)
                    {
                        // Constructor(buffer [, byteOffset [, length]])
                        bufferObj = firstArgObj as FenObject;
                        data = bufferObj?.NativeObject as byte[];
                        byteOffset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                        var blVal = bufferObj?.Get("byteLength") ?? FenValue.Undefined;
                        int bufferByteLen = (int)blVal.AsNumber();
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
                        length = lenVal.HasValue ? (int)lenVal.Value.ToNumber() : 0;
                        data = new byte[length * elementSize];
                        // Basic copy logic
                        for (int j = 0; j < length; j++)
                        {
                            var element = source.Get(j.ToString());
                            double val = element.IsUndefined ? 0 : element.ToNumber();
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
            SetGlobal("DataView", FenValue.FromFunction(new FenFunction("DataView", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;
                var bufferObj = args[0].AsObject() as FenObject;
                int byteOffset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                int byteLength = args.Length > 2 ? (int)args[2].ToNumber() : (int)(bufferObj != null ? bufferObj.Get("byteLength").ToNumber() : 0) - byteOffset;
                
                var view = new FenObject();
                view.Set("buffer", FenValue.FromObject(bufferObj));
                view.Set("byteOffset", FenValue.FromNumber(byteOffset));
                view.Set("byteLength", FenValue.FromNumber(byteLength));
                
                // Simplified getters/setters
                view.Set("getUint8", FenValue.FromFunction(new FenFunction("getUint8", (vArgs, vThis) => FenValue.FromNumber(0))));
                view.Set("setUint8", FenValue.FromFunction(new FenFunction("setUint8", (vArgs, vThis) => FenValue.Undefined)));
                
                return FenValue.FromObject(view);
            })));

            // Promise - Updated Full Spec Implementation (Phase 1)
            var promiseCtor = new FenFunction("Promise", (FenValue[] args, FenValue thisVal) => 
            {
                if (args.Length == 0 || !args[0].IsFunction) return FenValue.FromError("Promise resolver undefined is not a function");
                return FenValue.FromObject(new JsPromise(args[0], _context));
            });
            var promiseObj = FenValue.FromFunction(promiseCtor);
            var promiseStatics = promiseObj.AsObject();
            promiseStatics.Set("resolve", FenValue.FromFunction(new FenFunction("resolve", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromObject(JsPromise.Resolve(args.Length>0?args[0]:FenValue.Undefined, _context)))));
            promiseStatics.Set("reject", FenValue.FromFunction(new FenFunction("reject", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromObject(JsPromise.Reject(args.Length>0?args[0]:FenValue.Undefined, _context)))));
            promiseStatics.Set("all", FenValue.FromFunction(new FenFunction("all", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromObject(JsPromise.All(args.Length>0?args[0]:FenValue.Undefined, _context)))));
            promiseStatics.Set("race", FenValue.FromFunction(new FenFunction("race", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromObject(JsPromise.Race(args.Length>0?args[0]:FenValue.Undefined, _context)))));
            promiseStatics.Set("allSettled", FenValue.FromFunction(new FenFunction("allSettled", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromObject(JsPromise.AllSettled(args.Length>0?args[0]:FenValue.Undefined, _context)))));
            promiseStatics.Set("any", FenValue.FromFunction(new FenFunction("any", (FenValue[] args, FenValue thisVal) => 
                FenValue.FromObject(JsPromise.Any(args.Length>0?args[0]:FenValue.Undefined, _context)))));
            SetGlobal("Promise", promiseObj);

            // queueMicrotask
            SetGlobal("queueMicrotask", FenValue.FromFunction(new FenFunction("queueMicrotask", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var callback = args[0].AsFunction();
                    Core.EventLoop.EventLoopCoordinator.Instance.ScheduleMicrotask(() => { try { callback.Invoke(new FenValue[0], _context); } catch {} });
                }
                return FenValue.Undefined;
            })));


            // ES6 URL and URLSearchParams - Part of Web API but essential for modern JS
            SetGlobal("URL", FenValue.FromFunction(new FenFunction("URL", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0) return FenValue.Null;
                string urlStr = args[0].ToString();
                string baseStr = args.Length > 1 ? args[1].ToString() : null;
                
                Uri uri;
                if (baseStr != null) Uri.TryCreate(new Uri(baseStr), urlStr, out uri);
                else Uri.TryCreate(urlStr, UriKind.RelativeOrAbsolute, out uri);
                
                if (uri  == null) return FenValue.Null;
                
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

            SetGlobal("URLSearchParams", FenValue.FromFunction(new FenFunction("URLSearchParams", (FenValue[] args, FenValue thisVal) =>
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
                m.Set("cbrt", FenValue.FromFunction(new FenFunction("cbrt", (FenValue[] args, FenValue thisVal) => 
                    FenValue.FromNumber(Math.Pow(args.Length > 0 ? args[0].ToNumber() : double.NaN, 1.0/3.0)))));
                m.Set("hypot", FenValue.FromFunction(new FenFunction("hypot", (args, thisVal) => {
                    double sum = 0;
                    foreach(var arg in args) { double n = arg.ToNumber(); sum += n * n; }
                    return FenValue.FromNumber(Math.Sqrt(sum));
                })));
                m.Set("log2", FenValue.FromFunction(new FenFunction("log2", (FenValue[] args, FenValue thisVal) => 
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

            // String object
            var stringObj = new FenObject();
            stringObj.Set("fromCharCode", FenValue.FromFunction(new FenFunction("fromCharCode", (args, thisVal) => {
                var sb = new StringBuilder();
                foreach(var arg in args) sb.Append((char)arg.ToNumber());
                return FenValue.FromString(sb.ToString());
            })));
            
            stringProto = new FenObject();
            stringProto.Set("padEnd", FenValue.FromFunction(new FenFunction("padEnd", (args, thisVal) => {
                 var str = thisVal.ToString();
                 var targetLength = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                 if (str.Length >= targetLength) return thisVal;
                 var padString = args.Length > 1 ? args[1].ToString() : " ";
                 if (string.IsNullOrEmpty(padString)) return thisVal;
                 
                 var padLen = targetLength - str.Length;
                 var sb = new StringBuilder(str);
                 while(sb.Length < targetLength) {
                     sb.Append(padString);
                 }
                 return FenValue.FromString(sb.ToString().Substring(0, targetLength));
            })));
            stringProto.Set("padStart", FenValue.FromFunction(new FenFunction("padStart", (args, thisVal) => {
                 var str = thisVal.ToString();
                 var targetLength = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                 if (str.Length >= targetLength) return thisVal;
                 var padString = args.Length > 1 ? args[1].ToString() : " ";
                 if (string.IsNullOrEmpty(padString)) return thisVal;
                 
                 var padLen = targetLength - str.Length;
                 var sb = new StringBuilder();
                 while(sb.Length < padLen) sb.Append(padString);
                 if (sb.Length > padLen) sb.Length = padLen; // Truncate excess
                 sb.Append(str);
                 return FenValue.FromString(sb.ToString());
            })));
            stringProto.Set("trimStart", FenValue.FromFunction(new FenFunction("trimStart", (FenValue[] args, FenValue thisVal) => FenValue.FromString(thisVal.ToString().TrimStart()))));
            stringProto.Set("trimEnd", FenValue.FromFunction(new FenFunction("trimEnd", (FenValue[] args, FenValue thisVal) => FenValue.FromString(thisVal.ToString().TrimEnd()))));
            stringProto.Set("trim", FenValue.FromFunction(new FenFunction("trim", (FenValue[] args, FenValue thisVal) => FenValue.FromString(thisVal.ToString().Trim()))));
            
            stringProto.Set("startsWith", FenValue.FromFunction(new FenFunction("startsWith", (args, thisVal) => {
                var str = thisVal.ToString();
                var search = args.Length > 0 ? args[0].ToString() : "undefined";
                var pos = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (pos < 0) pos = 0;
                if (pos >= str.Length) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(str.Substring(pos).StartsWith(search));
            })));
            
            stringProto.Set("endsWith", FenValue.FromFunction(new FenFunction("endsWith", (args, thisVal) => {
                var str = thisVal.ToString();
                var search = args.Length > 0 ? args[0].ToString() : "undefined";
                var len = args.Length > 1 ? (int)args[1].ToNumber() : str.Length;
                if (len > str.Length) len = str.Length;
                var sub = str.Substring(0, len);
                return FenValue.FromBoolean(sub.EndsWith(search));
            })));

            stringProto.Set("includes", FenValue.FromFunction(new FenFunction("includes", (args, thisVal) => {
                 var str = thisVal.ToString();
                 var search = args.Length > 0 ? args[0].ToString() : "undefined";
                 var pos = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                 if (pos < 0) pos = 0;
                 return FenValue.FromBoolean(str.IndexOf(search, pos) != -1);
            })));

            stringObj.Set("prototype", FenValue.FromObject(stringProto));
            if (GetGlobal("String") is FenValue existingStringCtor && existingStringCtor.IsFunction)
            {
                var ctorFn = existingStringCtor.AsFunction();
                foreach (var key in stringObj.Keys())
                {
                    ctorFn.Set(key, stringObj.Get(key));
                }
            }
            else
            {
                SetGlobal("String", FenValue.FromObject(stringObj));
            }

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
            
            // Number already registered at top
            
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
                if (!args[0].IsObject && !args[0].IsFunction) return FenValue.FromBoolean(false);
                var obj = args[0].AsObject();
                return FenValue.FromBoolean(obj is FenObject fo && fo.InternalClass == "Array");
            })));
            arrayObj.Set("from", FenValue.FromFunction(new FenFunction("from", (args, thisVal) => {
                var result = FenObject.CreateArray();
                if (args.Length == 0) { result.Set("length", FenValue.FromNumber(0)); return FenValue.FromObject(result); }
                var source = args[0];
                FenFunction mapFn = args.Length > 1 ? args[1].AsFunction() : null;
                
                if (source.IsString) {
                    var str = source.ToString();
                    for (int i = 0; i < str.Length; i++) {
                        var val = FenValue.FromString(str[i].ToString());
                        result.Set(i.ToString(), mapFn != null ? mapFn.Invoke(new FenValue[] { val, FenValue.FromNumber(i) }, null) : val);
                    }
                    result.Set("length", FenValue.FromNumber(str.Length));
                } else if (source.IsObject) {
                    var obj = source.AsObject();
                    var lenVal = obj.Get("length");
                    int len = lenVal != null && lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                    for (int i = 0; i < len; i++) {
                        var val = obj.Get(i.ToString()) ;
                        result.Set(i.ToString(), mapFn != null ? mapFn.Invoke(new FenValue[] { val, FenValue.FromNumber(i) }, null) : val);
                    }
                    result.Set("length", FenValue.FromNumber(len));
                }
                return FenValue.FromObject(result);
            })));
            arrayObj.Set("of", FenValue.FromFunction(new FenFunction("of", (args, thisVal) => {
                var result = FenObject.CreateArray();
                for (int i = 0; i < args.Length; i++) {
                    result.Set(i.ToString(), args[i]);
                }
                result.Set("length", FenValue.FromNumber(args.Length));
                return FenValue.FromObject(result);
            })));
            if (GetGlobal("Array") is FenValue existingArrayCtor && existingArrayCtor.IsFunction)
            {
                var ctorFn = existingArrayCtor.AsFunction();
                foreach (var key in arrayObj.Keys())
                {
                    ctorFn.Set(key, arrayObj.Get(key));
                }
            }
            else
            {
                SetGlobal("Array", FenValue.FromObject(arrayObj));
            }

            // JSON object
            var json = new FenObject();
            json.Set("parse", FenValue.FromFunction(new FenFunction("parse", (args, thisVal) => {
                if (args.Length == 0) return FenValue.FromError("JSON.parse: no argument");
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
                    return (FenValue)result;
                }
                catch (Exception ex)
                {
                    return FenValue.FromError($"JSON.parse error: {ex.Message}");
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
                    
                    if (args.Length > 1 && args[1] != null && !args[1].IsUndefined)
                    {
                        if (args[1].IsFunction)
                            replacer = args[1].AsFunction() as FenFunction;
                        else if (args[1].IsObject)
                        {
                            var arr = args[1].AsObject();
                            var lenVal = arr?.Get("length");
                            if (lenVal.HasValue && lenVal.Value.IsNumber)
                            {
                                var keys = new List<string>();
                                int len = (int)lenVal.Value.ToNumber();
                                for (int i = 0; i < len; i++)
                                {
                                    var item = arr.Get(i.ToString());
                                    if (!item.IsUndefined) keys.Add(item.ToString());
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
                    return FenValue.FromError($"JSON.stringify error: {ex.Message}");
                }
            })));
            /* [PERF-REMOVED] */
            SetGlobal("JSON", FenValue.FromObject(json));

            // Intl (Phase 2)
            SetGlobal("Intl", FenValue.FromObject(JsIntl.CreateIntlObject(_context)));


            
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
                var argsList = new List<FenValue>();
                if (args.Length > 2 && args[2].IsObject)
                {
                    var argsArr = args[2].AsObject();
                    var len = argsArr?.Get("length");
                    int count = len != null && len.Value.IsNumber ? (int)len.Value.ToNumber() : 0;
                    for (int i = 0; i < count; i++)
                    {
                        var item = argsArr.Get(i.ToString());
                        argsList.Add(item );
                    }
                }
                return (FenValue)(fn?.Invoke(argsList.ToArray(), null) );
            })));
            
            // Reflect.construct(target, argumentsList)
            reflectObj.Set("construct", FenValue.FromFunction(new FenFunction("construct", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var fn = args[0].AsFunction() as FenFunction;
                var argsList = new List<FenValue>();
                if (args.Length > 1 && args[1].IsObject)
                {
                    var argsArr = args[1].AsObject();
                    var lenVal = argsArr?.Get("length");
                    int count = (lenVal.HasValue && lenVal.Value.IsNumber) ? (int)lenVal.Value.ToNumber() : 0;
                    for (int i = 0; i < count; i++)
                    {
                        var item = argsArr.Get(i.ToString());
                        argsList.Add(item);
                    }
                }
                return (FenValue)(fn?.Invoke(argsList.ToArray(), null) );
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
                    else if (args[1] == null)
                    {
                        fenObj.SetPrototype(null);
                        return FenValue.FromBoolean(true);
                    }
                }
                return FenValue.FromBoolean(false);
            })));
            
            // Reflect.isExtensible(target)
            reflectObj.Set("isExtensible", FenValue.FromFunction(new FenFunction("isExtensible", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var target = args[0].AsObject() as FenObject;
                return FenValue.FromBoolean(target != null && target.IsExtensible);
            })));

            // Reflect.preventExtensions(target)
            reflectObj.Set("preventExtensions", FenValue.FromFunction(new FenFunction("preventExtensions", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromBoolean(false);
                if (args[0].AsObject() is FenObject fenObj2) { fenObj2.PreventExtensions(); return FenValue.FromBoolean(true); }
                return FenValue.FromBoolean(false);
            })));

            // Reflect.defineProperty(target, propertyKey, attributes)
            reflectObj.Set("defineProperty", FenValue.FromFunction(new FenFunction("defineProperty", (args, thisVal) =>
            {
                if (args.Length < 3 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var rTarget = args[0].AsObject() as FenObject;
                if (rTarget == null) return FenValue.FromBoolean(false);
                var rKey = args[1].ToString();
                var rDescObj = args[2].AsObject() as FenObject;
                if (rDescObj == null) return FenValue.FromBoolean(false);
                var rpd = new PropertyDescriptor
                {
                    Value = rDescObj.Get("value", null),
                    Writable = rDescObj.Get("writable", null).ToBoolean(),
                    Enumerable = rDescObj.Get("enumerable", null).ToBoolean(),
                    Configurable = rDescObj.Get("configurable", null).ToBoolean()
                };
                var rGetter = rDescObj.Get("get", null);
                var rSetter = rDescObj.Get("set", null);
                if (rGetter.IsFunction) rpd.Getter = rGetter.AsFunction() as FenFunction;
                if (rSetter.IsFunction) rpd.Setter = rSetter.AsFunction() as FenFunction;
                rTarget.DefineOwnProperty(rKey, rpd);
                return FenValue.FromBoolean(true);
            })));

            // Reflect.getOwnPropertyDescriptor(target, propertyKey)
            reflectObj.Set("getOwnPropertyDescriptor", FenValue.FromFunction(new FenFunction("getOwnPropertyDescriptor", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject) return FenValue.Undefined;
                var rTarget2 = args[0].AsObject() as FenObject;
                if (rTarget2 == null) return FenValue.Undefined;
                var rKey2 = args[1].ToString();
                var rDesc = rTarget2.GetOwnPropertyDescriptor(rKey2);
                if (!rDesc.HasValue) return FenValue.Undefined;
                var rResult = new FenObject();
                if (rDesc.Value.Value.HasValue) rResult.Set("value", rDesc.Value.Value.Value, null);
                if (rDesc.Value.Writable.HasValue) rResult.Set("writable", FenValue.FromBoolean(rDesc.Value.Writable.Value), null);
                if (rDesc.Value.Enumerable.HasValue) rResult.Set("enumerable", FenValue.FromBoolean(rDesc.Value.Enumerable.Value), null);
                if (rDesc.Value.Configurable.HasValue) rResult.Set("configurable", FenValue.FromBoolean(rDesc.Value.Configurable.Value), null);
                if (rDesc.Value.Getter != null) rResult.Set("get", FenValue.FromFunction(rDesc.Value.Getter), null);
                if (rDesc.Value.Setter != null) rResult.Set("set", FenValue.FromFunction(rDesc.Value.Setter), null);
                return FenValue.FromObject(rResult);
            })));

            SetGlobal("Reflect", FenValue.FromObject(reflectObj));

            // Proxy - Meta-programming proxy objects
            SetGlobal("Proxy", FenValue.FromFunction(new FenFunction("Proxy", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject || !args[1].IsObject)
                    return FenValue.FromError("Proxy requires target and handler objects");
                
                var target = args[0].AsObject() as FenObject;
                var handler = args[1].AsObject() as FenObject;
                if (target  == null || handler  == null)
                    return FenValue.FromError("Proxy requires valid target and handler");
                
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
            SetGlobal("globalThis", (FenValue)(winGlobal ?? FenValue.Undefined));
            /* [PERF-REMOVED] */

            /* [PERF-REMOVED] */
            // SYMBOL - Create as FenObject with callable NativeObject so we can attach static properties
            var symbolFunc = new FenFunction("Symbol", (args, thisVal) =>
            {
               var desc = args.Length > 0 ? args[0].ToString() : null;
               // JsSymbol implements IValue directly, do not wrap in FenValue.FromObject
               return FenValue.FromSymbol(new FenBrowser.FenEngine.Core.Types.JsSymbol(desc));
            });
            
            // Use FenObject wrapper to allow property attachment (functions are objects in JS)
            var symbolStatic = new FenObject();
            symbolStatic.NativeObject = symbolFunc; // Make it callable
            
            /* [PERF-REMOVED] */
            
            // Symbol.iterator and other well-known symbols
            // JsSymbol.* are static JsSymbol instances (IValue), so pass directly
            symbolStatic.Set("iterator", FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.Iterator));
            symbolStatic.Set("asyncIterator", FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.AsyncIterator));
            symbolStatic.Set("toStringTag", FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.ToStringTag));
            symbolStatic.Set("toPrimitive", FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.ToPrimitive));
            symbolStatic.Set("hasInstance", FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.HasInstance));
             
            // Symbol.for(key)
            symbolStatic.Set("for", FenValue.FromFunction(new FenFunction("for", (args, thisVal) => {
                var key = args.Length > 0 ? args[0].ToString() : "undefined";
                return FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.For(key));
            })));
            
            // Symbol.keyFor(sym)
            symbolStatic.Set("keyFor", FenValue.FromFunction(new FenFunction("keyFor", (args, thisVal) => {
                if (args.Length > 0 && args[0].IsSymbol)
                {
                    var sym = args[0].AsSymbol();
                    return FenValue.FromString(FenBrowser.FenEngine.Core.Types.JsSymbol.KeyFor(sym));
                }
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
                   if (args.Length < 1 || !args[0].IsObject) return FenValue.FromObject(CreateArray(new FenValue[0]));
                   var target = args[0].AsObject() as FenObject;
                   var vals = new List<FenValue>();
                   if (target != null)
                   {
                       foreach(var k in target.Keys()) vals.Add(target.Get(k));
                   }
                   return FenValue.FromObject(CreateArray(vals.ToArray()));
                })));

                // Object.entries(obj)
                objStatic.Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
                {
                   if (args.Length < 1 || !args[0].IsObject) return FenValue.FromObject(CreateArray(new FenValue[0]));
                   var target = args[0].AsObject() as FenObject;
                   var entries = new List<FenValue>();
                   if (target != null)
                   {
                       foreach(var k in target.Keys()) 
                       {
                           var entry = CreateArray(new FenValue[] { FenValue.FromString(k), target.Get(k) });
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
                    if (lenVal.IsNumber)
                    {
                        int len = (int)lenVal.ToNumber();
                        for(int i=0; i<len; i++)
                        {
                            var entry = entriesArr.Get(i.ToString());
                            if (entry.IsObject)
                            {
                                var entryObj = entry.AsObject();
                                var keyVal = entryObj.Get("0");
                                var key = !keyVal.IsUndefined ? keyVal.ToString() : null;
                                var val = entryObj.Get("1");
                                if (key != null) result.Set(key, val );
                            }
                        }
                    }
                    return FenValue.FromObject(result);
                })));

                // Object.getOwnPropertySymbols(obj)
                objStatic.Set("getOwnPropertySymbols", FenValue.FromFunction(new FenFunction("getOwnPropertySymbols", (args, thisVal) =>
                {
                    // For now return empty array as we don't fully track symbol keys in main dictionary
                    return FenValue.FromObject(CreateArray(new FenValue[0])); 
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
                 if (args.Length < 1 || !args[0].IsObject) return FenValue.FromObject(CreateArray(new FenValue[0])); // Should throw
                 var target = args[0].AsObject() as FenObject;
                 var keys = new List<FenValue>();
                 foreach(var k in target.Keys()) keys.Add(FenValue.FromString(k));
                 return FenValue.FromObject(CreateArray(keys.ToArray()));
            })));
            
            // Reflect.apply(target, thisArgument, argumentsList)
            reflect.Set("apply", FenValue.FromFunction(new FenFunction("apply", (args, thisVal) => {
                 if (args.Length < 3 || !args[0].IsFunction) return FenValue.Undefined; // TypeError
                 var func = args[0].AsFunction();
                 var thisArg = args[1];
                 var argsListObj = args[2].AsObject() as FenObject;
                 
                 var argsList = new List<FenValue>();
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
                 if (buf  == null) return FenValue.Undefined; // TypeError
                 int offset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                 int len = args.Length > 2 ? (int)args[2].ToNumber() : -1;
                 return FenValue.FromObject(new JsDataView(buf, offset, len));
            })));
             
            SetGlobal("Uint8Array", FenValue.FromFunction(new FenFunction("Uint8Array", (args, thisVal) => 
                 FenValue.FromObject(new JsUint8Array(args.Length > 0 ? args[0] : null, args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null)))));
                 
            SetGlobal("Float32Array", FenValue.FromFunction(new FenFunction("Float32Array", (args, thisVal) => 
                 FenValue.FromObject(new JsFloat32Array(args.Length > 0 ? args[0] : null, args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null)))));

            // --- XHR ---
            SetGlobal("XMLHttpRequest", FenValue.FromFunction(new FenFunction("XMLHttpRequest", (args, thisVal) =>
                FenValue.FromObject(new XMLHttpRequest(_context, SendNetworkRequestAsync)))));


            


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
                
                return (FenValue)CreatePromise((resolve, reject) =>
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
                        if (hasher  == null) 
                        {
                           reject(FenValue.FromString("NotSupportedError: Algorithm not supported"));
                           return;
                        }
                        hash = hasher.ComputeHash(data);
                    }
                    
                    // Convert hash to ArrayBuffer/Uint8Array simulation (object with numeric keys)
                    // In a real engine this would be a native ArrayBuffer
                    var buffer = CreateArray(new FenValue[0]); // Actually needs to be ArrayBuffer-like
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
                        if (m != null && !m == null && !m.IsUndefined)
                            method = m.ToString().ToUpper();
                        var b = options.Get("body");
                        if (b != null && !b == null && !b.IsUndefined)
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
                return (FenValue)CreateFetchPromise(url, method, body, headers);
            })));

            // WebSocket - Real-time bidirectional communication
            SetGlobal("WebSocket", FenValue.FromFunction(new FenFunction("WebSocket", (args, thisVal) =>
            {
                var url = args.Length > 0 ? args[0].ToString() : "";
                if (string.IsNullOrWhiteSpace(url))
                    return FenValue.FromError("WebSocket: invalid URL");

                return (FenValue)CreateWebSocket(url);
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
                if (args.Length == 0 || !args[0].IsObject) return FenValue.FromError("TypeError: WeakRef: Target must be an object");
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
                if (args.Length == 0 || !args[0].IsFunction) return FenValue.FromError("TypeError: Constructor requires a cleanup callback");
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
            Func<FenValue[], int, (byte[] buffer, int index, bool isInt32)> ValidateAtomic = (vArgs, minArgs) => {
                if (vArgs.Length < minArgs) throw new Exception("TypeError: Missing args");
                if (!vArgs[0].IsObject) throw new Exception("TypeError: Arg 0 must be TypedArray");
                var ta = vArgs[0].AsObject() as FenObject;
                if (ta  == null || !(ta.NativeObject is byte[])) throw new Exception("TypeError: Arg 0 must be TypedArray");
                
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

            // Atomics.wait(typedArray, index, value[, timeout]) — blocks until notified or timeout
            atomics.Set("wait", FenValue.FromFunction(new FenFunction("wait", (args, thisVal) =>
            {
                // In a single-threaded interpreter, we cannot actually block
                // Return "not-equal" immediately (spec-compliant for non-equal value check)
                // Real implementation would block thread until Atomics.notify() is called
                try
                {
                    var (buf, idx, _) = ValidateAtomic(args, 3);
                    int expected = (int)args[2].ToNumber();
                    int offset = idx * 4;
                    int current = BitConverter.ToInt32(buf, offset);

                    // If current value doesn't match expected, return "not-equal"
                    if (current != expected)
                        return FenValue.FromString("not-equal");

                    // In multi-threaded environment, this would block
                    // Single-threaded: return "timed-out" (timeout of 0ms)
                    return FenValue.FromString("timed-out");
                }
                catch
                {
                    return FenValue.FromString("not-equal");
                }
            })));

            // ES2024: Atomics.waitAsync(typedArray, index, value[, timeout]) — async version of wait
            atomics.Set("waitAsync", FenValue.FromFunction(new FenFunction("waitAsync", (args, thisVal) =>
            {
                // In a tree-walking interpreter without shared memory threads, return a resolved async value
                var result = new FenObject();
                result.Set("async", FenValue.FromBoolean(false));
                result.Set("value", FenValue.FromString("not-equal")); // spec-defined string value
                return FenValue.FromObject(result);
            })));

            // Atomics.notify(typedArray, index[, count]) — wake waiting agents
            atomics.Set("notify", FenValue.FromFunction(new FenFunction("notify", (args, thisVal) =>
                FenValue.FromNumber(0)))); // No agents waiting in single-threaded engine

            // Atomics.compareExchange(typedArray, index, expectedValue, replacementValue)
            atomics.Set("compareExchange", FenValue.FromFunction(new FenFunction("compareExchange", (args, thisVal) =>
            {
                try
                {
                    var (buf, idx, _) = ValidateAtomic(args, 4);
                    int expected = (int)args[2].ToNumber();
                    int replacement = (int)args[3].ToNumber();
                    lock (buf)
                    {
                        int offset = idx * 4;
                        int current = BitConverter.ToInt32(buf, offset);
                        if (current == expected)
                        {
                            var bytes = BitConverter.GetBytes(replacement);
                            Array.Copy(bytes, 0, buf, offset, 4);
                        }
                        return FenValue.FromNumber(current);
                    }
                }
                catch { return FenValue.FromNumber(0); }
            })));

            // Atomics.exchange(typedArray, index, value)
            atomics.Set("exchange", FenValue.FromFunction(new FenFunction("exchange", (args, thisVal) =>
            {
                try
                {
                    var (buf, idx, _) = ValidateAtomic(args, 3);
                    int val = (int)args[2].ToNumber();
                    lock (buf)
                    {
                        int offset = idx * 4;
                        int current = BitConverter.ToInt32(buf, offset);
                        var bytes = BitConverter.GetBytes(val);
                        Array.Copy(bytes, 0, buf, offset, 4);
                        return FenValue.FromNumber(current);
                    }
                }
                catch { return FenValue.FromNumber(0); }
            })));

            // Atomics.isLockFree(size) — returns true for sizes 1,2,4 on most platforms
            atomics.Set("isLockFree", FenValue.FromFunction(new FenFunction("isLockFree", (args, thisVal) =>
            {
                int size = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                return FenValue.FromBoolean(size == 1 || size == 2 || size == 4);
            })));

            SetGlobal("Atomics", FenValue.FromObject(atomics));

            // ============================================
            // WebAssembly API (ES2017+)
            // ============================================
            var webAssembly = new FenObject();
            
            // WebAssembly.compile(bufferSource) - Returns Promise<Module>
            webAssembly.Set("compile", FenValue.FromFunction(new FenFunction("compile", (args, thisVal) =>
            {
                // Basic stub - would need full WASM binary parser
                return FenValue.FromObject(Types.JsPromise.Reject(
                    FenValue.FromError("WebAssembly: Full WASM compilation not yet implemented"), 
                    _context));
            })));
            
            // WebAssembly.instantiate(bufferSource, importObject) - Returns Promise<{module, instance}>
            webAssembly.Set("instantiate", FenValue.FromFunction(new FenFunction("instantiate", (args, thisVal) =>
            {
                return FenValue.FromObject(Types.JsPromise.Reject(
                    FenValue.FromError("WebAssembly: Full WASM instantiation not yet implemented"), 
                    _context));
            })));
            
            // WebAssembly.validate(bufferSource) - Returns boolean
            webAssembly.Set("validate", FenValue.FromFunction(new FenFunction("validate", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                // Simple check: WASM files start with magic number 0x00 0x61 0x73 0x6d
                if (args[0].IsObject && args[0].AsObject() is FenObject obj && obj.NativeObject is byte[] bytes)
                {
                    if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x61 && bytes[2] == 0x73 && bytes[3] == 0x6d)
                        return FenValue.FromBoolean(true);
                }
                return FenValue.FromBoolean(false);
            })));
            
            // WebAssembly.Module constructor
            webAssembly.Set("Module", FenValue.FromFunction(new FenFunction("Module", (args, thisVal) =>
            {
                return FenValue.FromError("WebAssembly.Module: Not yet implemented");
            })));
            
            // WebAssembly.Instance constructor
            webAssembly.Set("Instance", FenValue.FromFunction(new FenFunction("Instance", (args, thisVal) =>
            {
                return FenValue.FromError("WebAssembly.Instance: Not yet implemented");
            })));
            
            // WebAssembly.Memory constructor
            webAssembly.Set("Memory", FenValue.FromFunction(new FenFunction("Memory", (args, thisVal) =>
            {
                // Create a simple memory object
                int initial = 1; // pages
                int maximum = 100; // pages
                if (args.Length > 0 && args[0].IsObject)
                {
                    var descriptor = args[0].AsObject();
                    var initialVal = descriptor.Get("initial");
                    if (initialVal.IsNumber) initial = (int)initialVal.ToNumber();
                }
                
                int byteLength = initial * 65536; // 64KB per page
                var memory = new FenObject();
                memory.NativeObject = new byte[byteLength];
                memory.Set("buffer", FenValue.FromObject(new Types.JsArrayBuffer(byteLength)));
                memory.Set("grow", FenValue.FromFunction(new FenFunction("grow", (gArgs, gThis) =>
                {
                    return FenValue.FromNumber(initial); // Return old size
                })));
                
                return FenValue.FromObject(memory);
            })));
            
            // WebAssembly.Table constructor
            webAssembly.Set("Table", FenValue.FromFunction(new FenFunction("Table", (args, thisVal) =>
            {
                int initial = 0;
                if (args.Length > 0 && args[0].IsObject)
                {
                    var descriptor = args[0].AsObject();
                    var initialVal = descriptor.Get("initial");
                    if (initialVal.IsNumber) initial = (int)initialVal.ToNumber();
                }
                
                var table = new FenObject();
                table.NativeObject = new object[initial];
                table.Set("length", FenValue.FromNumber(initial));
                table.Set("get", FenValue.FromFunction(new FenFunction("get", (gArgs, gThis) =>
                {
                    if (gArgs.Length > 0 && gArgs[0].IsNumber)
                    {
                        int index = (int)gArgs[0].ToNumber();
                        var arr = table.NativeObject as object[];
                        if (arr != null && index >= 0 && index < arr.Length)
                            return arr[index] as FenValue? ?? FenValue.Null;
                    }
                    return FenValue.Null;
                })));
                table.Set("set", FenValue.FromFunction(new FenFunction("set", (sArgs, sThis) =>
                {
                    if (sArgs.Length >= 2 && sArgs[0].IsNumber)
                    {
                        int index = (int)sArgs[0].ToNumber();
                        var arr = table.NativeObject as object[];
                        if (arr != null && index >= 0 && index < arr.Length)
                            arr[index] = sArgs[1];
                    }
                    return FenValue.Undefined;
                })));
                table.Set("grow", FenValue.FromFunction(new FenFunction("grow", (gArgs, gThis) =>
                {
                    return FenValue.FromNumber(initial);
                })));
                
                return FenValue.FromObject(table);
            })));
            
            // WebAssembly.Global constructor
            webAssembly.Set("Global", FenValue.FromFunction(new FenFunction("Global", (args, thisVal) =>
            {
                var global = new FenObject();
                FenValue value = FenValue.FromNumber(0);
                if (args.Length > 1) value = args[1];
                
                global.Set("value", value);
                global.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (vArgs, vThis) => value)));
                
                return FenValue.FromObject(global);
            })));
            
            // WebAssembly.CompileError, LinkError, RuntimeError
            webAssembly.Set("CompileError", FenValue.FromFunction(new FenFunction("CompileError", (args, thisVal) =>
            {
                var err = new FenObject();
                err.Set("name", FenValue.FromString("CompileError"));
                err.Set("message", args.Length > 0 ? args[0] : FenValue.FromString(""));
                return FenValue.FromObject(err);
            })));
            
            webAssembly.Set("LinkError", FenValue.FromFunction(new FenFunction("LinkError", (args, thisVal) =>
            {
                var err = new FenObject();
                err.Set("name", FenValue.FromString("LinkError"));
                err.Set("message", args.Length > 0 ? args[0] : FenValue.FromString(""));
                return FenValue.FromObject(err);
            })));
            
            webAssembly.Set("RuntimeError", FenValue.FromFunction(new FenFunction("RuntimeError", (args, thisVal) =>
            {
                var err = new FenObject();
                err.Set("name", FenValue.FromString("RuntimeError"));
                err.Set("message", args.Length > 0 ? args[0] : FenValue.FromString(""));
                return FenValue.FromObject(err);
            })));
            
            SetGlobal("WebAssembly", FenValue.FromObject(webAssembly));

            // ============================================
            // Temporal API (ES2022) - Modern date/time
            // ============================================
            var temporal = new FenObject();

            // Temporal.Now - current time functions
            var temporalNow = new FenObject();
            temporalNow.Set("instant", FenValue.FromFunction(new FenFunction("instant", (args, thisVal) =>
            {
                var instant = new FenObject();
                instant.Set("epochNanoseconds", FenValue.FromNumber(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000));
                instant.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) =>
                    FenValue.FromString(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")))));
                return FenValue.FromObject(instant);
            })));
            temporalNow.Set("timeZoneId", FenValue.FromFunction(new FenFunction("timeZoneId", (args, thisVal) =>
                FenValue.FromString(TimeZoneInfo.Local.Id))));
            temporalNow.Set("zonedDateTimeISO", FenValue.FromFunction(new FenFunction("zonedDateTimeISO", (args, thisVal) =>
            {
                var zdt = new FenObject();
                var now = DateTime.Now;
                zdt.Set("year", FenValue.FromNumber(now.Year));
                zdt.Set("month", FenValue.FromNumber(now.Month));
                zdt.Set("day", FenValue.FromNumber(now.Day));
                zdt.Set("hour", FenValue.FromNumber(now.Hour));
                zdt.Set("minute", FenValue.FromNumber(now.Minute));
                zdt.Set("second", FenValue.FromNumber(now.Second));
                return FenValue.FromObject(zdt);
            })));
            temporal.Set("Now", FenValue.FromObject(temporalNow));

            // Temporal.PlainDate - calendar date
            temporal.Set("PlainDate", FenValue.FromFunction(new FenFunction("PlainDate", (args, thisVal) =>
            {
                int year = args.Length > 0 ? (int)args[0].ToNumber() : DateTime.Now.Year;
                int month = args.Length > 1 ? (int)args[1].ToNumber() : DateTime.Now.Month;
                int day = args.Length > 2 ? (int)args[2].ToNumber() : DateTime.Now.Day;

                var date = new FenObject();
                date.Set("year", FenValue.FromNumber(year));
                date.Set("month", FenValue.FromNumber(month));
                date.Set("day", FenValue.FromNumber(day));
                date.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) =>
                    FenValue.FromString($"{year:D4}-{month:D2}-{day:D2}"))));
                return FenValue.FromObject(date);
            })));

            // Temporal.PlainTime - wall-clock time
            temporal.Set("PlainTime", FenValue.FromFunction(new FenFunction("PlainTime", (args, thisVal) =>
            {
                int hour = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                int minute = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                int second = args.Length > 2 ? (int)args[2].ToNumber() : 0;

                var time = new FenObject();
                time.Set("hour", FenValue.FromNumber(hour));
                time.Set("minute", FenValue.FromNumber(minute));
                time.Set("second", FenValue.FromNumber(second));
                time.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) =>
                    FenValue.FromString($"{hour:D2}:{minute:D2}:{second:D2}"))));
                return FenValue.FromObject(time);
            })));

            // Temporal.PlainDateTime - date and time without timezone
            temporal.Set("PlainDateTime", FenValue.FromFunction(new FenFunction("PlainDateTime", (args, thisVal) =>
            {
                int year = args.Length > 0 ? (int)args[0].ToNumber() : DateTime.Now.Year;
                int month = args.Length > 1 ? (int)args[1].ToNumber() : DateTime.Now.Month;
                int day = args.Length > 2 ? (int)args[2].ToNumber() : DateTime.Now.Day;
                int hour = args.Length > 3 ? (int)args[3].ToNumber() : 0;
                int minute = args.Length > 4 ? (int)args[4].ToNumber() : 0;
                int second = args.Length > 5 ? (int)args[5].ToNumber() : 0;

                var dt = new FenObject();
                dt.Set("year", FenValue.FromNumber(year));
                dt.Set("month", FenValue.FromNumber(month));
                dt.Set("day", FenValue.FromNumber(day));
                dt.Set("hour", FenValue.FromNumber(hour));
                dt.Set("minute", FenValue.FromNumber(minute));
                dt.Set("second", FenValue.FromNumber(second));
                dt.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) =>
                    FenValue.FromString($"{year:D4}-{month:D2}-{day:D2}T{hour:D2}:{minute:D2}:{second:D2}"))));
                return FenValue.FromObject(dt);
            })));

            // Temporal.Instant - absolute point in time
            temporal.Set("Instant", FenValue.FromFunction(new FenFunction("Instant", (args, thisVal) =>
            {
                var instant = new FenObject();
                instant.Set("epochNanoseconds", args.Length > 0 ? args[0] : FenValue.FromNumber(0));
                instant.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) =>
                    FenValue.FromString(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")))));
                return FenValue.FromObject(instant);
            })));

            // Temporal.Duration - length of time
            temporal.Set("Duration", FenValue.FromFunction(new FenFunction("Duration", (args, thisVal) =>
            {
                var duration = new FenObject();
                duration.Set("years", args.Length > 0 ? args[0] : FenValue.FromNumber(0));
                duration.Set("months", args.Length > 1 ? args[1] : FenValue.FromNumber(0));
                duration.Set("days", args.Length > 2 ? args[2] : FenValue.FromNumber(0));
                duration.Set("hours", args.Length > 3 ? args[3] : FenValue.FromNumber(0));
                duration.Set("minutes", args.Length > 4 ? args[4] : FenValue.FromNumber(0));
                duration.Set("seconds", args.Length > 5 ? args[5] : FenValue.FromNumber(0));
                return FenValue.FromObject(duration);
            })));

            // Temporal.ZonedDateTime - date and time with timezone
            temporal.Set("ZonedDateTime", FenValue.FromFunction(new FenFunction("ZonedDateTime", (args, thisVal) =>
            {
                var zdt = new FenObject();
                var now = DateTime.Now;
                zdt.Set("year", FenValue.FromNumber(now.Year));
                zdt.Set("month", FenValue.FromNumber(now.Month));
                zdt.Set("day", FenValue.FromNumber(now.Day));
                zdt.Set("hour", FenValue.FromNumber(now.Hour));
                zdt.Set("minute", FenValue.FromNumber(now.Minute));
                zdt.Set("timeZoneId", FenValue.FromString(TimeZoneInfo.Local.Id));
                return FenValue.FromObject(zdt);
            })));

            // Temporal.TimeZone
            temporal.Set("TimeZone", FenValue.FromFunction(new FenFunction("TimeZone", (args, thisVal) =>
            {
                var tz = new FenObject();
                tz.Set("id", args.Length > 0 ? args[0] : FenValue.FromString("UTC"));
                return FenValue.FromObject(tz);
            })));

            // Temporal.Calendar
            temporal.Set("Calendar", FenValue.FromFunction(new FenFunction("Calendar", (args, thisVal) =>
            {
                var cal = new FenObject();
                cal.Set("id", args.Length > 0 ? args[0] : FenValue.FromString("iso8601"));
                return FenValue.FromObject(cal);
            })));

            SetGlobal("Temporal", FenValue.FromObject(temporal));

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
                var storage = new Dictionary<string, (FenValue key, FenValue value)>();
                map.NativeObject = storage;
                
                // Helper to generate unique key for any value type
                Func<FenValue, string> getMapKey = (key) =>
                {
                    if (key  == null || key == null) return "null";
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
                    var arr = FenObject.CreateArray();
                    int i = 0;
                    foreach (var kvp in storage.Values)
                    {
                        arr.Set(i.ToString(), (FenValue)kvp.key);
                        i++;
                    }
                    arr.Set("length", FenValue.FromNumber(i));
                    return FenValue.FromObject(arr);
                })));

                // values() - Returns array of values
                map.Set("values", FenValue.FromFunction(new FenFunction("values", (_, __) =>
                {
                    var arr = FenObject.CreateArray();
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
                    var arr = FenObject.CreateArray();
                    int i = 0;
                    foreach (var kvp in storage.Values)
                    {
                        var entry = FenObject.CreateArray();
                        entry.Set("0", (FenValue)kvp.key);
                        entry.Set("1", (FenValue)kvp.value);
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
                        callback.Invoke(new FenValue[] { (FenValue)kvp.value, (FenValue)kvp.key, FenValue.FromObject(map) }, null);
                    }
                    return FenValue.Undefined;
                })));
                
                // [Symbol.iterator]() - Returns iterator that yields [key, value] pairs
                map.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, __) =>
                {
                    var iterator = new FenObject();
                    var entries = new List<(FenValue key, FenValue value)>(storage.Values);
                    int index = 0;
                    
                    iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (___, ____) =>
                    {
                        var result = new FenObject();
                        if (index < entries.Count)
                        {
                            var entry = entries[index++];
                            var pair = new FenObject();
                            pair.Set("0", (FenValue)entry.key);
                            pair.Set("1", (FenValue)entry.value);
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
                    int len = (lenVal.HasValue && lenVal.Value.IsNumber) ? (int)lenVal.Value.ToNumber() : 0;
                    for (int i = 0; i < len; i++)
                    {
                        var entry = iterable.Get(i.ToString());
                        if (entry.IsObject)
                        {
                            var entryObj = entry.AsObject();
                            var key = entryObj.Get("0");
                            var value = entryObj.Get("1");
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
                var storage = new Dictionary<string, FenValue>();
                set.NativeObject = storage;
                
                Func<FenValue, string> getSetKey = (val) =>
                {
                    if (val  == null || val == null) return "null";
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
                    var arr = FenObject.CreateArray();
                    int i = 0;
                    foreach (var val in storage.Values)
                    {
                        arr.Set(i.ToString(), val);
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
                    var arr = FenObject.CreateArray();
                    int i = 0;
                    foreach (var kvp in storage.Values)
                    {
                        var entry = FenObject.CreateArray();
                        entry.Set("0", (FenValue)kvp);
                        entry.Set("1", (FenValue)kvp); // [value, value] for Set
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
                    foreach (var val in storage.Values)
                    {
                        callback.Invoke(new FenValue[] { val, val, FenValue.FromObject(set) }, null);
                    }
                    return FenValue.Undefined;
                })));
                
                // [Symbol.iterator]() - Returns iterator that yields values
                set.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, __) =>
                {
                    var iterator = new FenObject();
                    var values = storage.Values.ToList();
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
                
                // ES2025 Set methods

                // union(other) - Returns new Set with all elements from both sets
                set.Set("union", FenValue.FromFunction(new FenFunction("union", (unionArgs, _) =>
                {
                    var result = new FenObject();
                    var resultStorage = new Dictionary<string, FenValue>();

                    // Add all elements from this set
                    foreach (var kvp in storage)
                        resultStorage[kvp.Key] = kvp.Value;

                    // Add all elements from other set
                    if (unionArgs.Length > 0 && unionArgs[0].IsObject)
                    {
                        var other = unionArgs[0].AsObject();
                        var otherStorage = (other as FenObject)?.NativeObject as Dictionary<string, FenValue>;
                        if (otherStorage != null)
                        {
                            foreach (var kvp in otherStorage)
                                resultStorage[kvp.Key] = kvp.Value;
                        }
                    }

                    result.NativeObject = resultStorage;
                    result.Set("size", FenValue.FromNumber(resultStorage.Count));
                    // Copy all Set methods to result
                    foreach (var methodName in new[] { "add", "has", "delete", "clear", "values", "keys", "entries", "forEach", "[Symbol.iterator]" })
                    {
                        var method = set.Get(methodName);
                        if (method.IsFunction)
                            result.Set(methodName, method);
                    }
                    return FenValue.FromObject(result);
                })));

                // intersection(other) - Returns new Set with elements in both sets
                set.Set("intersection", FenValue.FromFunction(new FenFunction("intersection", (intArgs, _) =>
                {
                    var result = new FenObject();
                    var resultStorage = new Dictionary<string, FenValue>();

                    if (intArgs.Length > 0 && intArgs[0].IsObject)
                    {
                        var other = intArgs[0].AsObject();
                        var otherStorage = (other as FenObject)?.NativeObject as Dictionary<string, FenValue>;
                        if (otherStorage != null)
                        {
                            foreach (var kvp in storage)
                            {
                                if (otherStorage.ContainsKey(kvp.Key))
                                    resultStorage[kvp.Key] = kvp.Value;
                            }
                        }
                    }

                    result.NativeObject = resultStorage;
                    result.Set("size", FenValue.FromNumber(resultStorage.Count));
                    foreach (var methodName in new[] { "add", "has", "delete", "clear", "values", "keys", "entries", "forEach", "[Symbol.iterator]" })
                    {
                        var method = set.Get(methodName);
                        if (method.IsFunction)
                            result.Set(methodName, method);
                    }
                    return FenValue.FromObject(result);
                })));

                // difference(other) - Returns new Set with elements in this but not in other
                set.Set("difference", FenValue.FromFunction(new FenFunction("difference", (diffArgs, _) =>
                {
                    var result = new FenObject();
                    var resultStorage = new Dictionary<string, FenValue>();

                    // Add elements from this set that are not in other
                    foreach (var kvp in storage)
                        resultStorage[kvp.Key] = kvp.Value;

                    if (diffArgs.Length > 0 && diffArgs[0].IsObject)
                    {
                        var other = diffArgs[0].AsObject();
                        var otherStorage = (other as FenObject)?.NativeObject as Dictionary<string, FenValue>;
                        if (otherStorage != null)
                        {
                            foreach (var key in otherStorage.Keys)
                                resultStorage.Remove(key);
                        }
                    }

                    result.NativeObject = resultStorage;
                    result.Set("size", FenValue.FromNumber(resultStorage.Count));
                    foreach (var methodName in new[] { "add", "has", "delete", "clear", "values", "keys", "entries", "forEach", "[Symbol.iterator]" })
                    {
                        var method = set.Get(methodName);
                        if (method.IsFunction)
                            result.Set(methodName, method);
                    }
                    return FenValue.FromObject(result);
                })));

                // symmetricDifference(other) - Returns new Set with elements in either but not both
                set.Set("symmetricDifference", FenValue.FromFunction(new FenFunction("symmetricDifference", (symArgs, _) =>
                {
                    var result = new FenObject();
                    var resultStorage = new Dictionary<string, FenValue>();

                    // Add elements from this set
                    foreach (var kvp in storage)
                        resultStorage[kvp.Key] = kvp.Value;

                    if (symArgs.Length > 0 && symArgs[0].IsObject)
                    {
                        var other = symArgs[0].AsObject();
                        var otherStorage = (other as FenObject)?.NativeObject as Dictionary<string, FenValue>;
                        if (otherStorage != null)
                        {
                            foreach (var kvp in otherStorage)
                            {
                                if (resultStorage.ContainsKey(kvp.Key))
                                    resultStorage.Remove(kvp.Key); // Remove if in both
                                else
                                    resultStorage[kvp.Key] = kvp.Value; // Add if only in other
                            }
                        }
                    }

                    result.NativeObject = resultStorage;
                    result.Set("size", FenValue.FromNumber(resultStorage.Count));
                    foreach (var methodName in new[] { "add", "has", "delete", "clear", "values", "keys", "entries", "forEach", "[Symbol.iterator]" })
                    {
                        var method = set.Get(methodName);
                        if (method.IsFunction)
                            result.Set(methodName, method);
                    }
                    return FenValue.FromObject(result);
                })));

                // isSubsetOf(other) - Returns true if all elements of this are in other
                set.Set("isSubsetOf", FenValue.FromFunction(new FenFunction("isSubsetOf", (subArgs, _) =>
                {
                    if (subArgs.Length == 0 || !subArgs[0].IsObject) return FenValue.FromBoolean(false);
                    var other = subArgs[0].AsObject();
                    var otherStorage = (other as FenObject)?.NativeObject as Dictionary<string, FenValue>;
                    if (otherStorage == null) return FenValue.FromBoolean(false);

                    foreach (var key in storage.Keys)
                    {
                        if (!otherStorage.ContainsKey(key))
                            return FenValue.FromBoolean(false);
                    }
                    return FenValue.FromBoolean(true);
                })));

                // isSupersetOf(other) - Returns true if all elements of other are in this
                set.Set("isSupersetOf", FenValue.FromFunction(new FenFunction("isSupersetOf", (superArgs, _) =>
                {
                    if (superArgs.Length == 0 || !superArgs[0].IsObject) return FenValue.FromBoolean(false);
                    var other = superArgs[0].AsObject();
                    var otherStorage = (other as FenObject)?.NativeObject as Dictionary<string, FenValue>;
                    if (otherStorage == null) return FenValue.FromBoolean(true);

                    foreach (var key in otherStorage.Keys)
                    {
                        if (!storage.ContainsKey(key))
                            return FenValue.FromBoolean(false);
                    }
                    return FenValue.FromBoolean(true);
                })));

                // isDisjointFrom(other) - Returns true if no elements are in common
                set.Set("isDisjointFrom", FenValue.FromFunction(new FenFunction("isDisjointFrom", (disjArgs, _) =>
                {
                    if (disjArgs.Length == 0 || !disjArgs[0].IsObject) return FenValue.FromBoolean(true);
                    var other = disjArgs[0].AsObject();
                    var otherStorage = (other as FenObject)?.NativeObject as Dictionary<string, FenValue>;
                    if (otherStorage == null) return FenValue.FromBoolean(true);

                    foreach (var key in storage.Keys)
                    {
                        if (otherStorage.ContainsKey(key))
                            return FenValue.FromBoolean(false);
                    }
                    return FenValue.FromBoolean(true);
                })));

                // Initialize from iterable
                if (args.Length > 0 && args[0].IsObject)
                {
                    var iterable = args[0].AsObject();
                    var lenVal = iterable?.Get("length");
                    int len = lenVal != null && lenVal.Value.IsNumber ? (int)lenVal.Value.ToNumber() : 0;
                    for (int i = 0; i < len; i++)
                    {
                        var value = iterable.Get(i.ToString());
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
                var storage = new Dictionary<int, FenValue>();
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
                    return storage.ContainsKey(keyHash) ? (FenValue)storage[keyHash] : FenValue.Undefined;
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
                return (FenValue)CreateWorker(scriptUrl);
            })));

            // ArrayBuffer - Binary data container
            SetGlobal("ArrayBuffer", FenValue.FromFunction(new FenFunction("ArrayBuffer", (args, thisVal) =>
            {
                var length = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                return (FenValue)CreateArrayBuffer(length);
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
                    var abObj = args[0].AsObject();
                    if (abObj is JsArrayBuffer jsab)
                    {
                        int dvOffset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                        int dvLength = args.Length > 2 ? (int)args[2].ToNumber() : -1;
                        return FenValue.FromObject(new JsDataView(jsab, dvOffset, dvLength));
                    }
                    // Fallback: old FenObject with NativeObject = byte[]
                    var ab = abObj as FenObject;
                    if (ab?.NativeObject is byte[] buffer)
                    {
                        var wrappedAb = new JsArrayBuffer(buffer.Length);
                        System.Buffer.BlockCopy(buffer, 0, wrappedAb.Data, 0, buffer.Length);
                        int dvOffset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                        int dvLength = args.Length > 2 ? (int)args[2].ToNumber() : -1;
                        return FenValue.FromObject(new JsDataView(wrappedAb, dvOffset, dvLength));
                    }
                }
                return FenValue.FromObject(new JsDataView(new JsArrayBuffer(0)));
            })));

            // Object and Array Global constructors (already registered at top as FenFunction)
            var objectGlobal = objectCtor;
            var arrayGlobal = arrayCtor;

            // ============================================================
            // Function constructor with toString (5E)
            // ============================================================

            // Function(...args, body) constructor - creates function from string
            var functionCtor = new FenFunction("Function", (args, thisVal) =>
            {
                try
                {
                    // Last argument is the function body, previous arguments are parameters
                    string body = args.Length > 0 ? args[args.Length - 1].ToString() : "";
                    var paramNames = new List<string>();

                    for (int i = 0; i < args.Length - 1; i++)
                    {
                        paramNames.Add(args[i].ToString());
                    }

                    // Build function source as expression: (function anonymous(param1, param2) { body })
                    // Wrap in parentheses to make it an expression, not a statement
                    string functionSource = $"(function anonymous({string.Join(", ", paramNames)}) {{ {body} }})";

                    // Parse the function
                    var lexer = new Lexer(functionSource);
                    var parser = new Parser(lexer);
                    var program = parser.ParseProgram();

                    if (parser.Errors.Count > 0)
                    {
                        return FenValue.FromError($"SyntaxError: {string.Join(", ", parser.Errors)}");
                    }

                    // Extract the function literal from the parsed program
                    // The parser should return an ExpressionStatement with a FunctionLiteral
                    if (program.Statements.Count > 0 &&
                        program.Statements[0] is ExpressionStatement exprStmt &&
                        exprStmt.Expression is FunctionLiteral funcLiteral)
                    {
                        // Create FenFunction from the parsed function
                        var fenFunc = new FenFunction(funcLiteral.Parameters, funcLiteral.Body, _globalEnv);
                        fenFunc.Source = functionSource;
                        return FenValue.FromFunction(fenFunc);
                    }

                    return FenValue.FromError("SyntaxError: Invalid function syntax");
                }
                catch (Exception ex)
                {
                    return FenValue.FromError($"SyntaxError: {ex.Message}");
                }
            });

            // Function.prototype.toString()
            var functionPrototype = new FenObject();
            functionPrototype.InternalClass = "Function";
            functionPrototype.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                if (thisVal.IsFunction)
                {
                    var fn = thisVal.AsFunction();
                    if (fn != null)
                    {
                        if (fn.IsNative)
                            return FenValue.FromString($"function {fn.Name}() {{ [native code] }}");
                        
                        // ES2019: Return actual source if available
                        if (!string.IsNullOrEmpty(fn.Source))
                            return FenValue.FromString(fn.Source);

                        return FenValue.FromString($"function {fn.Name}() {{ [code] }}");
                    }
                }
                return FenValue.FromString("[object Function]");
            })), null);
            
            // Function.prototype.call(thisArg, ...args)
            functionPrototype.Set("call", FenValue.FromFunction(new FenFunction("call", (args, thisVal) =>
            {
                if (!thisVal.IsFunction) return FenValue.FromError("call requires a function");
                var fn = thisVal.AsFunction();
                var newThis = args.Length > 0 ? args[0] : FenValue.Undefined;
                var fnArgs = args.Length > 1 ? args.Skip(1).ToArray() : new FenValue[0];
                return fn.Invoke(fnArgs, null);
            })), null);
            
            // Function.prototype.apply(thisArg, argsArray)
            functionPrototype.Set("apply", FenValue.FromFunction(new FenFunction("apply", (args, thisVal) =>
            {
                if (!thisVal.IsFunction) return FenValue.FromError("apply requires a function");
                var fn = thisVal.AsFunction();
                var newThis = args.Length > 0 ? args[0] : FenValue.Undefined;
                var fnArgs = new FenValue[0];
                if (args.Length > 1 && args[1].IsObject)
                {
                    var argsObj = args[1].AsObject();
                    var len = (int)argsObj.Get("length", null).ToNumber();
                    fnArgs = new FenValue[len];
                    for (int i = 0; i < len; i++)
                        fnArgs[i] = argsObj.Get(i.ToString(), null);
                }
                return fn.Invoke(fnArgs, null);
            })), null);
            
            // Function.prototype.bind(thisArg, ...args)
            functionPrototype.Set("bind", FenValue.FromFunction(new FenFunction("bind", (args, thisVal) =>
            {
                if (!thisVal.IsFunction) return FenValue.FromError("bind requires a function");
                var fn = thisVal.AsFunction();
                var boundThis = args.Length > 0 ? args[0] : FenValue.Undefined;
                var boundArgs = args.Length > 1 ? args.Skip(1).ToArray() : new FenValue[0];
                return FenValue.FromFunction(new FenFunction("bound", (newArgs, _) =>
                {
                    var allArgs = boundArgs.Concat(newArgs).ToArray();
                    return fn.Invoke(allArgs, null);
                }));
            })), null);
            
            functionCtor.Prototype = functionPrototype;
            functionCtor.Set("prototype", FenValue.FromObject(functionPrototype), null);
            SetGlobal("Function", FenValue.FromFunction(functionCtor));



            // ============================================================
            // String constructor (ES5.1/ES2015)
            // ============================================================
            var stringConstructor = new FenObject();
            stringConstructor.InternalClass = "Function";
            stringConstructor.Set("__call__", FenValue.FromFunction(new FenFunction("String", (args, thisVal) =>
            {
                var str = args.Length > 0 ? args[0].ToString() : "";
                if (thisVal.IsObject && thisVal.AsObject() is FenObject obj && obj.InternalClass == "String")
                {
                    // new String() called
                    obj.NativeObject = str;
                    return thisVal; // Constructor returns the new object
                }
                return FenValue.FromString(str); // Called as function
            })), null);

            // String.fromCharCode(...)
            stringConstructor.Set("fromCharCode", FenValue.FromFunction(new FenFunction("fromCharCode", (args, thisVal) =>
            {
                var sb = new StringBuilder();
                foreach (var arg in args)
                {
                    sb.Append((char)(ushort)arg.ToNumber());
                }
                return FenValue.FromString(sb.ToString());
            })), null);

            // String.fromCodePoint(...) - ES2015
            stringConstructor.Set("fromCodePoint", FenValue.FromFunction(new FenFunction("fromCodePoint", (args, thisVal) =>
            {
                var sb = new StringBuilder();
                foreach (var arg in args)
                {
                    var num = arg.ToNumber();
                    try 
                    {
                        sb.Append(char.ConvertFromUtf32((int)num));
                    }
                    catch
                    {
                        return FenValue.FromError("RangeError: Invalid code point " + num);
                    }
                }
                return FenValue.FromString(sb.ToString());
            })), null);

            // String.raw(template, ...substitutions) - ES2015
            stringConstructor.Set("raw", FenValue.FromFunction(new FenFunction("raw", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                
                var template = args[0];
                if (!template.IsObject) return FenValue.FromError("TypeError: String.raw requires template object");
                
                var cooked = template.AsObject();
                // Check if raw property exists
                var rawVal = cooked.Get("raw", null);
                if (rawVal.IsUndefined || rawVal.IsNull) return FenValue.FromError("TypeError: Cannot convert undefined or null to object");
                
                if (!rawVal.IsObject) return FenValue.FromError("TypeError: raw property must be an object");
                
                var raw = rawVal.AsObject();
                var lenVal = raw.Get("length", null);
                var len = (int)lenVal.ToNumber();
                
                if (len <= 0) return FenValue.FromString("");
                
                var sb = new StringBuilder();
                for (int i = 0; i < len; i++)
                {
                    var segmentArg = raw.Get(i.ToString(), null);
                    var segment = segmentArg != null ? segmentArg.ToString() : "";
                    sb.Append(segment);
                    
                    if (i < len - 1 && i + 1 < args.Length)
                    {
                        sb.Append(args[i + 1].ToString());
                    }
                }
                
                return FenValue.FromString(sb.ToString());
            })), null);

            // String.prototype.replaceAll(searchValue, replaceValue) - ES2021
            stringConstructor.Set("replaceAll", FenValue.FromFunction(new FenFunction("replaceAll", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length < 2) return FenValue.FromString(str);
                var searchStr = args[0].ToString();
                var replaceStr = args[1].ToString();
                if (string.IsNullOrEmpty(searchStr)) return FenValue.FromString(str);
                return FenValue.FromString(str.Replace(searchStr, replaceStr));
            })));

            // String.prototype.matchAll(regexp) - ES2020
            stringConstructor.Set("matchAll", FenValue.FromFunction(new FenFunction("matchAll", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromError("TypeError: matchAll requires a regular expression");
                var regexpVal = args[0];
                var str = thisVal.ToString();
                
                // TODO: Ensure regexp has 'g' flag, or throw TypeError if it doesn't (per spec)
                // For now, we'll just handle it as best effort.
                
                string pattern = regexpVal.ToString();
                // Simple heuristic to strip /.../ part if it's a regex object string conversion
                if (pattern.StartsWith("/") && pattern.LastIndexOf("/") > 0)
                {
                    int lastSlash = pattern.LastIndexOf("/");
                    pattern = pattern.Substring(1, lastSlash - 1);
                }

                System.Text.RegularExpressions.Match match = null;
                try
                {
                    match = System.Text.RegularExpressions.Regex.Match(str, pattern);
                }
                catch
                {
                     return FenValue.FromObject(CreateEmptyIterator());
                }

                var iterator = new FenObject();
                iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (iArgs, iThis) => iThis)));
                
                iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (nArgs, nThis) =>
                {
                     if (match != null && match.Success)
                     {
                         var resultObj = new FenObject();
                         // Array-like match object
                         var matchArr = new FenObject();
                         matchArr.Set("0", FenValue.FromString(match.Value));
                         for (int i = 1; i < match.Groups.Count; i++)
                         {
                             matchArr.Set(i.ToString(), FenValue.FromString(match.Groups[i].Value));
                         }
                         matchArr.Set("index", FenValue.FromNumber(match.Index));
                         matchArr.Set("input", FenValue.FromString(str));
                         matchArr.Set("length", FenValue.FromNumber(match.Groups.Count));
                         
                         resultObj.Set("value", FenValue.FromObject(matchArr));
                         resultObj.Set("done", FenValue.FromBoolean(false));
                         
                         match = match.NextMatch();
                         return FenValue.FromObject(resultObj);
                     }
                     else
                     {
                         var resultObj = new FenObject();
                         resultObj.Set("value", FenValue.Undefined);
                         resultObj.Set("done", FenValue.FromBoolean(true));
                         return FenValue.FromObject(resultObj);
                     }
                })));

                return FenValue.FromObject(iterator);
            })), null);

            // String already registered at top
        }

        private FenObject CreateEmptyIterator()
        {
             var iterator = new FenObject();
             iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, thisVal) => thisVal)));
             iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (args, thisVal) =>
             {
                 var result = new FenObject();
                 result.Set("value", FenValue.Undefined);
                 result.Set("done", FenValue.FromBoolean(true));
                 return FenValue.FromObject(result);
             })));
             return iterator;
        }

        private IValue ConvertJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var obj = new FenObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        obj.Set(prop.Name, (FenValue)ConvertJsonElement(prop.Value));
                    }
                    return FenValue.FromObject(obj);
                case JsonValueKind.Array:
                    var arr = new FenObject();
                    int index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        arr.Set(index.ToString(), (FenValue)ConvertJsonElement(item));
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

        private string ConvertToJsonString(FenValue value)
        {
            if (value.IsString) return JsonSerializer.Serialize(value.ToString());
            if (value.IsNumber) return value.ToString();
            if (value.IsBoolean) return value.ToBoolean().ToString().ToLower();
            if (value == null) return "null";
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
        public void SetDom(Node root, Uri baseUri = null)
        {
            if (root  == null) return;
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
        
        private Dictionary<string, List<FenValue>> _windowEventListeners = new Dictionary<string, List<FenValue>>();

        private FenValue CreateTimer(FenFunction callback, int delay, bool repeat, FenValue[] args)
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
                    callback.Invoke(new FenValue[] { FenValue.FromNumber(now) }, _context);
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

        public void SetGlobal(string name, FenValue value)
        {
            _globalEnv.Set(name, value);
        }

        public IValue GetGlobal(string name)
        {
            var val = _globalEnv.Get(name);
            return val ;
        }

        public void SetVariable(string name, FenValue value)
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
        public IValue ExecuteSimple(string code, System.Threading.CancellationToken cancellationToken)
        {
            return ExecuteSimple(code, "script", false, cancellationToken);
        }

        public IValue ExecuteSimple(string code, string url = "script", bool allowReturn = false, System.Threading.CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string logPath = DiagnosticPaths.GetLogArtifactPath("script_execution.log");
            int codeLen = code?.Length ?? 0;
            // Console.WriteLine($"[DEBUG] ExecuteSimple: Url={url}, CodeLen={codeLen}");
            
            try
            {
                bool previousStrictMode = _context?.StrictMode ?? false;

                // Fast-path strict directive detection so runtime execution honors leading
                // `"use strict"` / `'use strict'` even when parser directive detection drifts.
                static bool StartsWithUseStrictDirective(string source)
                {
                    if (string.IsNullOrEmpty(source)) return false;

                    int i = 0;
                    while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
                    if (i + 12 > source.Length) return false;

                    char quote = source[i];
                    if (quote != '"' && quote != '\'') return false;
                    i++;

                    const string strictText = "use strict";
                    for (int j = 0; j < strictText.Length; j++)
                    {
                        if (i + j >= source.Length || source[i + j] != strictText[j]) return false;
                    }
                    i += strictText.Length;

                    if (i >= source.Length || source[i] != quote) return false;
                    i++;

                    while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
                    return i < source.Length && source[i] == ';';
                }

                // Reset execution timer for each new script execution
                if (_context is ExecutionContext ec)
                {
                    ec.Reset();
                }

                if (_context != null && StartsWithUseStrictDirective(code))
                {
                    _context.StrictMode = true;
                }
                
                // Set context URL for debugging
                if (_context != null) _context.CurrentUrl = url;
                
                try
                {
                    var lexer = new Lexer(code);
                    var parser = new Parser(lexer, allowReturnOutsideFunction: allowReturn);
                    var program = parser.ParseProgram();

                    if (parser.Errors.Count > 0)
                    {
                        sw.Stop();
                        var errMsg = string.Join("\n", parser.Errors);
                        try 
                        { 
                            System.IO.File.AppendAllText(logPath, 
                                $"[PARSE-ERROR] {url} (len={codeLen}, {sw.ElapsedMilliseconds}ms)\n" +
                                $"  Errors ({parser.Errors.Count}):\n  " + 
                                string.Join("\n  ", parser.Errors.Take(10)) + 
                                (parser.Errors.Count > 10 ? $"\n  ... and {parser.Errors.Count - 10} more" : "") + 
                                "\n\n"); 
                        } 
                        catch { }
                        return FenValue.FromError(errMsg);
                    }
                    
                    var interpreter = new Interpreter();
                    interpreter.CancellationToken = cancellationToken;

                    // Register with DevTools
                    DevToolsCore.Instance.SetInterpreter(interpreter);
                    DevToolsCore.Instance.RegisterSource(url, code);

                    var result = interpreter.Eval(program, _globalEnv, _context);

                    sw.Stop();
                    try 
                    { 
                        System.IO.File.AppendAllText(logPath, 
                            $"[SUCCESS] {url} (len={codeLen}, {sw.ElapsedMilliseconds}ms)\n"); 
                    } 
                    catch { }
                    
                    return result ;
                }
                finally
                {
                    if (_context != null)
                    {
                        _context.StrictMode = previousStrictMode;
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                var fullError = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                try 
                { 
                    System.IO.File.AppendAllText(logPath, 
                        $"[RUNTIME-ERROR] {url} (len={codeLen}, {sw.ElapsedMilliseconds}ms)\n" +
                        $"  Exception: {ex.GetType().Name}\n" +
                        $"  Message: {ex.Message}\n" +
                        $"  Stack:\n  " + ex.StackTrace?.Replace("\n", "\n  ") + "\n\n"); 
                } 
                catch { }
                
                FenLogger.Error($"[FenRuntime] Runtime error: {ex.Message}", LogCategory.JavaScript, ex);
                return FenValue.FromError($"[[DEBUG_TRACE]] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }


        #region Helper Methods for Browser APIs

        /// <summary>
        /// Create an array-like object from string array (Privacy: used for navigator.languages, plugins, etc.)
        /// </summary>
        private FenObject CreateArray(string[] items)
        {
            var arr = FenObject.CreateArray();
            for (int i = 0; i < items.Length; i++)
            {
                arr.Set(i.ToString(), FenValue.FromString(items[i]));
            }
            arr.Set("length", FenValue.FromNumber(items.Length));
            return arr;
        }

        private FenObject CreateArray(IValue[] items)
        {
            var arr = FenObject.CreateArray();
            for (int i = 0; i < items.Length; i++)
            {
                arr.Set(i.ToString(), (FenValue)items[i]);
            }
            arr.Set("length", FenValue.FromNumber(items.Length));
            return arr;
        }

        private FenObject CreateArray(FenValue[] items)
        {
            var arr = FenObject.CreateArray();
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
            var arr = FenObject.CreateArray();
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

        /// <summary>
        /// Creates a rejected Promise-like object
        /// </summary>
        private FenValue CreateRejectedPromise(string errorMessage)
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
                        callback.NativeImplementation(new FenValue[] { FenValue.FromString(errorMessage) }, FenValue.Undefined);
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

                    var response = await SendNetworkRequestAsync(request).ConfigureAwait(false);
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
                                        callback.NativeImplementation(new FenValue[] { (FenValue)responseObj }, FenValue.Undefined);
                                    else if (!callback.IsNative)
                                        callback.Invoke(new FenValue[] { (FenValue)responseObj }, _context);
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
                                    callback.NativeImplementation(new FenValue[] { FenValue.FromString(errorMessage) }, FenValue.Undefined);
                                else if (!callback.IsNative)
                                    callback.Invoke(new FenValue[] { FenValue.FromString(errorMessage) }, _context);
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
                            cb.NativeImplementation(new FenValue[] { FenValue.FromString(bodyText ?? "") }, FenValue.Undefined);
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
                            var parsed = (FenValue)ConvertJsonElementStatic(doc.RootElement);
                            if (cb.IsNative && cb.NativeImplementation != null)
                                cb.NativeImplementation(new FenValue[] { parsed }, FenValue.Undefined);
                        }
                        catch (Exception ex)
                        {
                            if (cb.IsNative && cb.NativeImplementation != null)
                                cb.NativeImplementation(new FenValue[] { FenValue.FromError($"JSON parse error: {ex.Message}") }, FenValue.Undefined);
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
                        obj.Set(prop.Name, (FenValue)ConvertJsonElementStatic(prop.Value));
                    }
                    return FenValue.FromObject(obj);
                case JsonValueKind.Array:
                    var arr = new FenObject();
                    // Arrays are represented as objects with numeric keys and length
                    int i = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        arr.Set(i.ToString(), (FenValue)ConvertJsonElementStatic(item));
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
                    return null;
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
                                cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                            cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                                    cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                                    cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                            cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                    request.Set("result", (FenValue)db);
                    request.Set("readyState", FenValue.FromString("done"));
                    
                    // Fire onupgradeneeded for new databases
                    if (isNew)
                    {
                        var onupgrade = request.Get("onupgradeneeded");
                        if (onupgrade.IsFunction)
                        {
                            var cb = onupgrade.AsFunction();
                            if (cb.IsNative && cb.NativeImplementation != null)
                            {
                                var evt = new FenObject();
                                evt.Set("target", FenValue.FromObject(request));
                                evt.Set("oldVersion", FenValue.FromNumber(openResult.OldVersion));
                                evt.Set("newVersion", FenValue.FromNumber(openResult.NewVersion));
                                cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                            }
                        }
                    }
                    
                    // Fire onsuccess
                    var onsuccess = request.Get("onsuccess");
                    if (onsuccess.IsFunction)
                    {
                        var cb = onsuccess.AsFunction();
                        if (cb.IsNative && cb.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                return (FenValue)CreateIDBObjectStore(name, storeName);
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
                    return (FenValue)CreateIDBObjectStore(name, sn);
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
        private FenValue CreateIDBObjectStore(string dbName, string storeName)
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
                    if (cb.IsFunction)
                    {
                        var fn = cb.AsFunction();
                        if (fn.IsNative && fn.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            fn.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                    if (cb.IsFunction)
                    {
                        var fn = cb.AsFunction();
                        if (fn.IsNative && fn.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            fn.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                    if (cb.IsFunction)
                    {
                        var fn = cb.AsFunction();
                        if (fn.IsNative && fn.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            fn.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                    if (cb.IsFunction)
                    {
                        var fn = cb.AsFunction();
                        if (fn.IsNative && fn.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            fn.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                    if (cb.IsFunction)
                    {
                        var fn = cb.AsFunction();
                        if (fn.IsNative && fn.NativeImplementation != null)
                        {
                            var evt = new FenObject();
                            evt.Set("target", FenValue.FromObject(request));
                            fn.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                    if (thenMethod.HasValue && thenMethod.Value.IsFunction) return value;
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
                int len = (lenVal.HasValue && lenVal.Value.IsNumber) ? (int)lenVal.Value.ToNumber() : 0;
                
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
                        var item = iterable.Get(i.ToString()) ;
                        
                        // Handle thenable/promise
                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod.HasValue && thenMethod.Value.IsFunction)
                            {
                                thenMethod.Value.AsFunction().Invoke(new FenValue[] {
                                    FenValue.FromFunction(new FenFunction("resolve", (a, __) =>
                                    {
                                        lock (lockObj)
                                        {
                                            if (rejected) return FenValue.Undefined;
                                            results.Set(index.ToString(), a.Length > 0 ? a[0] : FenValue.Undefined);
                                            completed++;
                                            if (completed == len) resolve?.Invoke(new FenValue[] { FenValue.FromObject(results) }, null);
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
                            if (completed == len) resolve?.Invoke(new FenValue[] { FenValue.FromObject(results) }, null);
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
                int len = (lenVal.HasValue && lenVal.Value.IsNumber) ? (int)lenVal.Value.ToNumber() : 0;
                
                return CreateExecutorPromise(new FenFunction("raceExecutor", (exArgs, _) =>
                {
                    var resolve = exArgs.Length > 0 && exArgs[0].IsFunction ? exArgs[0].AsFunction() : null;
                    var reject = exArgs.Length > 1 && exArgs[1].IsFunction ? exArgs[1].AsFunction() : null;
                    bool settled = false;
                    object lockObj = new object();
                    
                    for (int i = 0; i < len; i++)
                    {
                        var item = iterable.Get(i.ToString()) ;
                        
                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod.HasValue && thenMethod.Value.IsFunction)
                            {
                                thenMethod.Value.AsFunction().Invoke(new FenValue[] {
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
                        resolve?.Invoke(new FenValue[] { item }, null);
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
                int len = (lenVal.HasValue && lenVal.Value.IsNumber) ? (int)lenVal.Value.ToNumber() : 0;
                
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
                        var item = iterable.Get(i.ToString()) ;
                        
                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod.HasValue && thenMethod.Value.IsFunction)
                            {
                                thenMethod.Value.AsFunction().Invoke(new FenValue[] {
                                    FenValue.FromFunction(new FenFunction("resolve", (a, __) =>
                                    {
                                        var result = new FenObject();
                                        result.Set("status", FenValue.FromString("fulfilled"));
                                        result.Set("value", a.Length > 0 ? a[0] : FenValue.Undefined);
                                        lock (lockObj)
                                        {
                                            results.Set(index.ToString(), FenValue.FromObject(result));
                                            completed++;
                                            if (completed == len) resolve?.Invoke(new FenValue[] { FenValue.FromObject(results) }, null);
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
                                            if (completed == len) resolve?.Invoke(new FenValue[] { FenValue.FromObject(results) }, null);
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
                            if (completed == len) resolve?.Invoke(new FenValue[] { FenValue.FromObject(results) }, null);
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
                int len = (lenVal.HasValue && lenVal.Value.IsNumber) ? (int)lenVal.Value.ToNumber() : 0;
                
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
                        var item = iterable.Get(i.ToString()) ;
                        
                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod.HasValue && thenMethod.Value.IsFunction)
                            {
                                thenMethod.Value.AsFunction().Invoke(new FenValue[] {
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
                                                reject?.Invoke(new FenValue[] { FenValue.FromObject(aggErr) }, null);
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
                        resolve?.Invoke(new FenValue[] { item }, null);
                        break;
                    }
                    return FenValue.Undefined;
                }), promiseCtor);
            })));
            
            // ES2024: Promise.withResolvers() - returns { promise, resolve, reject }
            promiseCtor.Set("withResolvers", FenValue.FromFunction(new FenFunction("withResolvers", (args, thisVal) =>
            {
                FenFunction resolveFn = null;
                FenFunction rejectFn = null;
                var promise = CreateExecutorPromise(new FenFunction("withResolversExecutor", (exArgs, _) =>
                {
                    resolveFn = exArgs.Length > 0 ? exArgs[0].AsFunction() : null;
                    rejectFn = exArgs.Length > 1 ? exArgs[1].AsFunction() : null;
                    return FenValue.Undefined;
                }), promiseCtor);
                var result = new FenObject();
                result.Set("promise", promise);
                result.Set("resolve", resolveFn != null ? FenValue.FromFunction(resolveFn) : FenValue.Undefined);
                result.Set("reject", rejectFn != null ? FenValue.FromFunction(rejectFn) : FenValue.Undefined);
                return FenValue.FromObject(result);
            })));

            // ES2025: Promise.try(fn, ...args) - wraps sync/async fn in a promise
            promiseCtor.Set("try", FenValue.FromFunction(new FenFunction("try", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction)
                    return CreateRejectedPromiseValue(FenValue.FromString("TypeError: Promise.try requires a callable"));
                var fn = args[0].AsFunction();
                var fnArgs = args.Skip(1).ToArray();
                try
                {
                    var result = fn.Invoke(fnArgs, null);
                    // If result is a thenable, return it; otherwise wrap in resolved promise
                    if (result.IsObject)
                    {
                        var then = result.AsObject()?.Get("then");
                        if (then.HasValue && then.Value.IsFunction) return result;
                    }
                    return CreateResolvedPromise(result);
                }
                catch (Exception ex)
                {
                    return CreateRejectedPromiseValue(FenValue.FromString(ex.Message));
                }
            })));

            return promiseCtor;
        }

        /// <summary>
        /// Creates a Promise with an executor function (for new Promise((resolve, reject) => {}))
        /// </summary>
        private FenValue CreateExecutorPromise(FenFunction executor, FenObject promiseCtor)
        {
            var promise = new FenObject();
            string state = "pending";
            FenValue result = FenValue.Undefined;
            var fulfillCallbacks = new List<(FenFunction onFulfill, FenFunction onReject, FenFunction chainResolve, FenFunction chainReject)>();
            var rejectCallbacks = new List<(FenFunction onFulfill, FenFunction onReject, FenFunction chainResolve, FenFunction chainReject)>();
            object lockObj = new object();
            
            Action<string, IValue> settle = (newState, value) =>
            {
                lock (lockObj)
                {
                    if (state != "pending") return;
                    state = newState;
                    result = (FenValue)value;
                    promise.Set("__state", FenValue.FromString(state));
                    promise.Set(newState == "fulfilled" ? "__value" : "__reason", (FenValue)value);
                }
                
                var callbacks = newState == "fulfilled" ? fulfillCallbacks : rejectCallbacks;
                foreach (var (onFulfill, onReject, chainResolve, chainReject) in callbacks)
                {
                    try
                    {
                        var handler = newState == "fulfilled" ? onFulfill : onReject;
                        if (handler != null)
                        {
                            var cbResult = handler.Invoke(new FenValue[] { result }, null);
                            chainResolve?.Invoke(new FenValue[] { cbResult }, null);
                        }
                        else if (newState == "fulfilled")
                            chainResolve?.Invoke(new FenValue[] { result }, null);
                        else
                            chainReject?.Invoke(new FenValue[] { result }, null);
                    }
                    catch (Exception ex)
                    {
                        chainReject?.Invoke(new FenValue[] { FenValue.FromString(ex.Message) }, null);
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
                    if (thenMethod.HasValue && thenMethod.Value.IsFunction)
                    {
                        try
                        {
                            thenMethod.Value.AsFunction().Invoke(new FenValue[] {
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
                                    var cbResult = handler.Invoke(new FenValue[] { result }, null);
                                    chainResolve?.Invoke(new FenValue[] { cbResult }, null);
                                }
                                else if (state == "fulfilled")
                                    chainResolve?.Invoke(new FenValue[] { result }, null);
                                else
                                    chainReject?.Invoke(new FenValue[] { result }, null);
                            }
                            catch (Exception ex)
                            {
                                chainReject?.Invoke(new FenValue[] { FenValue.FromString(ex.Message) }, null);
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
                    return thenMethod.AsFunction().Invoke(new FenValue[] { FenValue.Undefined, catchArgs.Length > 0 ? catchArgs[0] : FenValue.Undefined }, null);
                return FenValue.FromObject(promise);
            })));
            
            // finally(onFinally)
            promise.Set("finally", FenValue.FromFunction(new FenFunction("finally", (finallyArgs, _) =>
            {
                var onFinally = finallyArgs.Length > 0 && finallyArgs[0].IsFunction ? finallyArgs[0].AsFunction() : null;
                if (onFinally  == null) return FenValue.FromObject(promise);
                
                var thenMethod = promise.Get("then");
                if (thenMethod != null && thenMethod.IsFunction)
                {
                    return thenMethod.AsFunction().Invoke(new FenValue[] {
                        FenValue.FromFunction(new FenFunction("onFulfill", (a, __) => { onFinally.Invoke(new FenValue[0], null); return a.Length > 0 ? a[0] : FenValue.Undefined; })),
                        FenValue.FromFunction(new FenFunction("onReject", (a, __) => { onFinally.Invoke(new FenValue[0], null); return CreateRejectedPromiseValue(a.Length > 0 ? a[0] : FenValue.Undefined); }))
                    }, null);
                }
                return FenValue.FromObject(promise);
            })));
            
            // Execute the executor
            try
            {
                executor.Invoke(new FenValue[] { FenValue.FromFunction(resolveFn), FenValue.FromFunction(rejectFn) }, null);
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
            promise.Set("__reason", (FenValue)reason);
            
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
            {
                if (args.Length > 1 && args[1].IsFunction)
                {
                    var cb = args[1].AsFunction();
                    var result = cb.Invoke(new FenValue[] { (FenValue)reason }, null);
                    return CreateResolvedPromise(result);
                }
                return thisVal;
            })));
            
            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var cb = args[0].AsFunction();
                    var resVal = cb.Invoke(new FenValue[] { (FenValue)reason }, null);
                    return CreateResolvedPromise(resVal);
                }
                return thisVal;
            })));
            
            promise.Set("finally", FenValue.FromFunction(new FenFunction("finally", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                    args[0].AsFunction().Invoke(new FenValue[0], null);
                return thisVal;
            })));
            
            return FenValue.FromObject(promise);
        }

        /// <summary>
        /// Creates a resolved Promise
        /// </summary>
        private FenValue CreateResolvedPromise(FenValue value)
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
                        var result = cb.NativeImplementation(new FenValue[] { value }, FenValue.Undefined);
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
                        cb.NativeImplementation(new FenValue[0], FenValue.Undefined);
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
                            cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
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
                
                if (buffer  == null)
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
            if (value.IsObject && !value == null)
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
            var result = reviver.Invoke(new FenValue[] { FenValue.FromString(key), value }, null);
            return result != null ? (FenValue)result : FenValue.Undefined;
        }

        /// <summary>
        /// Convert to JSON string with replacer function/array support
        /// </summary>
        private string ConvertToJsonStringWithReplacer(FenValue value, FenFunction replacer, string[] replacerArray, int spaces, string indent, HashSet<object> seen = null)
        {
            if (value  == null || value.IsUndefined) return "undefined";
            if (value == null) return "null";

            // Apply replacer function
            if (replacer != null)
            {
                var holder = new FenObject();
                holder.Set("", (FenValue)value);
                var result = replacer.Invoke(new FenValue[] { FenValue.FromString(""), value }, null);
                if (result != null && !result.IsUndefined)
                    value = result;
                else if (result != null && result.IsUndefined)
                    return null;
            }

            if (value == null) return "null";
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
                if (obj  == null) return "null";

                // Circular reference detection (spec: throw TypeError)
                if (seen == null) seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
                if (!seen.Add(obj))
                    throw new InvalidOperationException("Converting circular structure to JSON");

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
                        var itemStr = ConvertToJsonStringWithReplacer(item , replacer, replacerArray, spaces, newIndent, seen);
                        items.Add(itemStr ?? "null");
                    }
                    seen.Remove(obj);
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
                            var valStr = ConvertToJsonStringWithReplacer(val, replacer, replacerArray, spaces, newIndent, seen);
                            if (valStr != null)
                                pairs.Add($"\"{EscapeJsonString(key)}\"{colonSpace}:{colonSpace}{valStr}");
                        }
                    }
                    seen.Remove(obj);
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




        private FenValue CreateRejectedPromiseValue(FenValue reason)
        {
             return (FenValue)CreatePromise((resolve, reject) => reject(reason));
        }
        

        private IValue CreatePromise(Action<Action<IValue>, Action<IValue>> executor)
        {
            var promise = new FenObject();
            promise.Set("__isPromise__", FenValue.FromBoolean(true));
            promise.Set("__state__", FenValue.FromString("pending"));
            promise.Set("__value__", FenValue.Undefined);
            promise.Set("__reason__", FenValue.Undefined);

            var resolve = new Action<IValue>(value => {
                 if (promise.Get("__state__").ToString() == "pending") {
                     promise.Set("__state__", FenValue.FromString("fulfilled"));
                     promise.Set("__value__", (FenValue)value);
                 }
            });
            
            var reject = new Action<IValue>(reason => {
                 if (promise.Get("__state__").ToString() == "pending") {
                     promise.Set("__state__", FenValue.FromString("rejected"));
                     promise.Set("__reason__", (FenValue)reason);
                 }
            });

            try {
                executor(resolve, reject);
            } catch (Exception ex) {
                reject(FenValue.FromString(ex.Message));
            }

            // then(onFulfilled, onRejected)
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) => {
                var state = promise.Get("__state__").ToString();
                if (state == "fulfilled") {
                     if (args.Length > 0 && args[0].IsFunction) {
                         var res = args[0].AsFunction().Invoke(new FenValue[]{ promise.Get("__value__") }, null);
                         return res ;
                     }
                     return promise.Get("__value__") ;
                }
                return FenValue.FromObject(promise); 
            })));
            
             // catch(onRejected)
             promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) => {
                 var state = promise.Get("__state__").ToString();
                 if (state == "rejected") {
                      if (args.Length > 0 && args[0].IsFunction) {
                          var res = args[0].AsFunction().Invoke(new FenValue[]{ promise.Get("__reason__") }, null);
                          return res ;
                      }
                 }
                 return FenValue.FromObject(promise);
             })));

            return FenValue.FromObject(promise);
        }

        private FenValue ConvertNativeToFenValue(object obj)
        {
            if (obj  == null) return FenValue.Null;
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

        private PropertyDescriptor ToPropertyDescriptor(FenValue descVal, IExecutionContext context)
        {
            if (descVal.IsNull || descVal.IsUndefined)
                throw new InvalidOperationException("Property descriptor must be an object: " + descVal);

            var obj = descVal.AsObject(); // Or ToObject() semantics
            if (obj == null)
                 throw new InvalidOperationException("Property descriptor must be an object");

            var desc = new PropertyDescriptor();

            if (obj.Has("enumerable")) desc.Enumerable = obj.Get("enumerable").ToBoolean();
            if (obj.Has("configurable")) desc.Configurable = obj.Get("configurable").ToBoolean();
            
            bool hasValue = obj.Has("value");
            if (hasValue) desc.Value = obj.Get("value");
            
            bool hasWritable = obj.Has("writable");
            if (hasWritable) desc.Writable = obj.Get("writable").ToBoolean();
            
            bool hasGet = obj.Has("get");
            if (hasGet)
            {
                var g = obj.Get("get");
                if (!g.IsUndefined && !g.IsFunction)
                    throw new InvalidOperationException("Getter must be a function: " + g);
                desc.Getter = g.IsUndefined ? null : g.AsFunction() as FenFunction;
            }
            
            bool hasSet = obj.Has("set");
            if (hasSet)
            {
                var s = obj.Get("set");
                if (!s.IsUndefined && !s.IsFunction)
                    throw new InvalidOperationException("Setter must be a function: " + s);
                desc.Setter = s.IsUndefined ? null : s.AsFunction() as FenFunction;
            }

            if ((desc.Getter != null || desc.Setter != null) && (hasValue || hasWritable))
            {
                throw new InvalidOperationException("Invalid property descriptor. Cannot both have accessors and value or writable attribute");
            }

            return desc;
        }

        private FenValue FromPropertyDescriptor(PropertyDescriptor desc)
        {
            var obj = new FenObject();
            
            if (desc.IsData)
            {
                obj.Set("value", desc.Value ?? FenValue.Undefined); // Should always have value if IsData? Or generic?
                obj.Set("writable", FenValue.FromBoolean(desc.Writable ?? false));
            }
            else
            {
                obj.Set("get", desc.Getter != null ? FenValue.FromFunction(desc.Getter) : FenValue.Undefined);
                obj.Set("set", desc.Setter != null ? FenValue.FromFunction(desc.Setter) : FenValue.Undefined);
            }
            
            obj.Set("enumerable", FenValue.FromBoolean(desc.Enumerable ?? false));
            obj.Set("configurable", FenValue.FromBoolean(desc.Configurable ?? false));
            
            return FenValue.FromObject(obj);
        }

        #endregion
    }
}
