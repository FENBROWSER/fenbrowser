using FenBrowser.Core.Dom.V2;
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
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// FenEngine JavaScript runtime - manages global scope and execution context
    /// </summary>
    public class FenRuntime
    {
        [ThreadStatic]
        private static FenRuntime _activeRuntime;

        private readonly FenEnvironment _globalEnv;
        private readonly IExecutionContext _context;
        private readonly IStorageBackend _storageBackend;
        private readonly IDomBridge _domBridge; // Bridge to Engine's DOM
        private IHistoryBridge _historyBridge;
        private readonly List<HistoryEntryState> _localHistoryEntries = new List<HistoryEntryState>();
        private int _localHistoryIndex = -1;
        private IObject _realmObjectPrototype;
        private IObject _realmFunctionPrototype;
        private IObject _realmArrayPrototype;
        private FenObject _realmIteratorPrototype;
        private FenObject _domNodePrototype;
        private FenObject _domDocumentPrototype;
        private FenObject _domElementPrototype;
        private FenObject _domHtmlElementPrototype;
        private FenObject _domTextPrototype;
        private FenObject _domCommentPrototype;
        private FenObject _domAttrPrototype;
        private FenObject _windowObject;
        private FenObject _locationObject;
        private FenObject _historyObject;

        private readonly Dictionary<int, CancellationTokenSource> _activeTimers =
            new Dictionary<int, CancellationTokenSource>();

        private int _timerIdCounter = 1;
        private readonly object _timerLock = new object();
        private static readonly Random _mathRandom = new Random(); // Cached Random for Math.random()

        private sealed class HistoryEntryState
        {
            public Uri Url { get; set; }
            public FenValue State { get; set; }
            public string Title { get; set; }
        }

        public FenRuntime(IExecutionContext context = null, IStorageBackend storageBackend = null,
            IDomBridge domBridge = null, IHistoryBridge historyBridge = null)
        {
            var previousActiveRuntime = _activeRuntime;
            _activeRuntime = this;

            try
            {
                // Reset the default prototypes so objectPrototype/functionPrototype created in this runtime
                // don't accidentally inherit from a prototype created by a previous FenRuntime instance.
                FenObject.DefaultPrototype = null;
                FenFunction.DefaultFunctionPrototype = null;

                // Clear DOM wrapper identity cache so stale wrappers from the previous page are not reused.
                FenBrowser.FenEngine.DOM.DomWrapperFactory.ClearCache();

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
                CaptureRealmIntrinsics();
                /* [PERF-REMOVED] */
            }
            finally
            {
                _activeRuntime = previousActiveRuntime;
                previousActiveRuntime?.ActivateRealmIntrinsics();
            }
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
        public Action<Uri> NavigationRequested { get; set; }

        public void SetHistoryBridge(IHistoryBridge bridge)
        {
            _historyBridge = bridge;
            SynchronizeHistorySurface();
        }
        private static Task RunDetachedAsync(Func<Task> operation)
        {
            return Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[FenRuntime] Detached async operation failed: {ex.Message}", LogCategory.JavaScript);
                }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        private static Task RunDetached(Action operation)
        {
            return Task.Factory.StartNew(() =>
            {
                try
                {
                    operation();
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[FenRuntime] Detached operation failed: {ex.Message}", LogCategory.JavaScript);
                }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        public void NotifyPopState(object state)
        {
            try
            {
                FenLogger.Debug($"[FenRuntime] NotifyPopState: {state}", LogCategory.Events);
                var clonedState = state != null
                    ? CloneHistoryState(ConvertNativeToFenValue(state))
                    : FenValue.Null;

                SynchronizeHistorySurface();
                EventLoop.EventLoopCoordinator.Instance.ScheduleTask(
                    () => DispatchPopStateEvent(clonedState),
                    EventLoop.TaskSource.History,
                    "history.popstate");
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
                var result = ExecuteSimple(hardeningScript, "fen://prototype-hardening.js");
                if (result.Type == Interfaces.ValueType.Error)
                {
                    FenLogger.Error($"[FenRuntime] Prototype hardening failed in bytecode-only mode: {result}",
                        LogCategory.JavaScript);
                    return;
                }

                FenLogger.Info("[FenRuntime] Prototype hardening applied successfully", LogCategory.JavaScript);
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[FenRuntime] Failed to apply prototype hardening: {ex.Message}",
                    LogCategory.JavaScript);
            }
        }

        public Action<string> OnConsoleMessage; // Delegate for console output
        public Uri BaseUri { get; set; }

        internal static FenRuntime GetActiveRuntime()
        {
            return _activeRuntime;
        }

        internal FenValue CreateThrownErrorValue(ErrorType errorType, string message)
        {
            string errorName = errorType switch
            {
                ErrorType.Type => "TypeError",
                ErrorType.Range => "RangeError",
                ErrorType.Reference => "ReferenceError",
                ErrorType.Syntax => "SyntaxError",
                _ => "Error"
            };

            return CreateThrownErrorValue(errorName, message);
        }

        internal FenValue CreateThrownErrorValue(string errorName, string message)
        {
            string normalizedMessage = message ?? string.Empty;
            string prefixedName = errorName + ":";
            if (normalizedMessage.StartsWith(prefixedName, StringComparison.Ordinal))
            {
                normalizedMessage = normalizedMessage.Substring(prefixedName.Length).TrimStart();
            }

            var ctorValue = GetGlobal(errorName);
            if (ctorValue is FenValue ctorFenValue && ctorFenValue.IsFunction)
            {
                var ctor = ctorFenValue.AsFunction();
                if (ctor != null)
                {
                    var created = ctor.Invoke(new[] { FenValue.FromString(normalizedMessage) }, _context, FenValue.Undefined);
                    if (created.IsObject)
                    {
                        return created;
                    }
                }
            }

            var fallback = new FenObject();
            fallback.Set("name", FenValue.FromString(errorName));
            fallback.Set("message", FenValue.FromString(normalizedMessage));
            fallback.Set("stack", FenValue.FromString($"{errorName}: {normalizedMessage}\n    at <anonymous>"));
            return FenValue.FromObject(fallback);
        }

        internal T RunWithRealmActivation<T>(Func<T> action)
        {
            using var scope = EnterRealmActivationScope();
            return action();
        }

        internal void RunWithRealmActivation(Action action)
        {
            using var scope = EnterRealmActivationScope();
            action();
        }

        private IObject ResolveIntrinsicPrototypeFromGlobal(string ctorName)
        {
            var ctorValue = _globalEnv != null ? _globalEnv.Get(ctorName) : FenValue.Undefined;
            if (!ctorValue.IsObject && !ctorValue.IsFunction)
            {
                return null;
            }

            var ctorObject = ctorValue.AsObject();
            if (ctorObject == null)
            {
                return null;
            }

            var prototypeValue = ctorObject.Get("prototype", _context);
            if ((prototypeValue.IsObject || prototypeValue.IsFunction) && prototypeValue.AsObject() != null)
            {
                return prototypeValue.AsObject();
            }

            return null;
        }

        private void CaptureRealmIntrinsics()
        {
            _realmObjectPrototype = FenObject.DefaultPrototype ?? ResolveIntrinsicPrototypeFromGlobal("Object");
            _realmFunctionPrototype = FenFunction.DefaultFunctionPrototype ?? ResolveIntrinsicPrototypeFromGlobal("Function");
            _realmArrayPrototype = FenObject.DefaultArrayPrototype ?? ResolveIntrinsicPrototypeFromGlobal("Array");
            _realmIteratorPrototype = FenObject.DefaultIteratorPrototype;
        }

        private void ActivateRealmIntrinsics()
        {
            _realmObjectPrototype ??= ResolveIntrinsicPrototypeFromGlobal("Object");
            _realmFunctionPrototype ??= ResolveIntrinsicPrototypeFromGlobal("Function");
            _realmArrayPrototype ??= ResolveIntrinsicPrototypeFromGlobal("Array");

            FenObject.DefaultPrototype = _realmObjectPrototype;
            FenFunction.DefaultFunctionPrototype = _realmFunctionPrototype;
            FenObject.DefaultArrayPrototype = _realmArrayPrototype as FenObject;
            FenObject.DefaultIteratorPrototype = _realmIteratorPrototype;
        }

        internal IObject ResolveObjectPrototypeForNewObject()
        {
            if (_realmObjectPrototype != null)
            {
                return _realmObjectPrototype;
            }

            var objectCtor = _globalEnv != null ? _globalEnv.Get("Object") : FenValue.Undefined;
            if ((objectCtor.IsObject || objectCtor.IsFunction))
            {
                var ctorObject = objectCtor.AsObject();
                if (ctorObject != null)
                {
                    var prototypeValue = ctorObject.Get("prototype", _context);
                    if ((prototypeValue.IsObject || prototypeValue.IsFunction) && prototypeValue.AsObject() != null)
                    {
                        return prototypeValue.AsObject();
                    }
                }
            }

            return FenObject.DefaultPrototype;
        }

        private ActiveRuntimeScope EnterRealmActivationScope()
        {
            return new ActiveRuntimeScope(this);
        }

        private readonly struct ActiveRuntimeScope : IDisposable
        {
            private readonly FenRuntime _previousRuntime;

            public ActiveRuntimeScope(FenRuntime runtime)
            {
                _previousRuntime = _activeRuntime;
                _activeRuntime = runtime;
                runtime?.ActivateRealmIntrinsics();
            }

            public void Dispose()
            {
                _activeRuntime = _previousRuntime;
                _previousRuntime?.ActivateRealmIntrinsics();
            }
        }

        private string GetCurrentOrigin()
        {
            if (BaseUri == null) return "null";
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
            if (value == null) return "null";
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
            try
            {
                FenLogger.Debug("[FenRuntime] InitializeBuiltins called", LogCategory.JavaScript);
            }
            catch
            {
            }

            // ============================================
            // CORE ERROR TYPES (Initialized first)
            // ============================================

            // 1. Error.prototype
            var errorProto = new FenObject();

            // ============================================
            // CORE CONSTRUCTORS (Refactored to FenFunction)
            // ============================================
            var window = new FenObject();
            window.Set("__fen_window_named_access__", FenValue.FromBoolean(true));
            var objectProto = new FenObject();
            var arrayProto = new FenObject();
            var stringProto = new FenObject();
            var numberProto = new FenObject();
            var booleanProto = new FenObject();

            FenFunction CreateBuiltinFunction(string name, int length, Func<FenValue[], FenValue, FenValue> implementation, bool isConstructor = false)
            {
                var fn = new FenFunction(name, implementation)
                {
                    NativeLength = length,
                    IsConstructor = isConstructor
                };
                return fn;
            }

            void DefineBuiltinMethod(FenObject target, string name, int length, Func<FenValue[], FenValue, FenValue> implementation)
            {
                target.SetBuiltin(name, FenValue.FromFunction(CreateBuiltinFunction(name, length, implementation)));
            }

            void DefineGlobalBuiltin(string name, FenValue value)
            {
                _globalEnv.Set(name, value);
                window.DefineOwnProperty(name, PropertyDescriptor.DataNonEnumerable(value));
            }

            arrayProto.SetPrototype(objectProto);
            stringProto.SetPrototype(objectProto);
            numberProto.SetPrototype(objectProto);
            booleanProto.SetPrototype(objectProto);

            // Object
            var objectCtor = new FenFunction("Object", (args, thisVal) =>
            {
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                {
                    return FenValue.FromObject(new FenObject());
                }

                var value = args[0];
                if (value.IsObject || value.IsFunction)
                {
                    return value;
                }

                var wrapper = new FenObject();
                wrapper.Set("__value__", value);

                if (value.IsString)
                {
                    wrapper.InternalClass = "String";
                    wrapper.SetPrototype(stringProto);
                    wrapper.Set("length", FenValue.FromNumber(value.AsString(_context).Length));
                    return FenValue.FromObject(wrapper);
                }

                if (value.IsNumber)
                {
                    wrapper.InternalClass = "Number";
                    wrapper.SetPrototype(numberProto);
                    return FenValue.FromObject(wrapper);
                }

                if (value.IsBoolean)
                {
                    wrapper.InternalClass = "Boolean";
                    wrapper.SetPrototype(booleanProto);
                    return FenValue.FromObject(wrapper);
                }

                if (value.IsBigInt)
                {
                    wrapper.InternalClass = "BigInt";
                    var bigIntCtorVal = GetGlobal("BigInt");
                    if ((bigIntCtorVal.IsObject || bigIntCtorVal.IsFunction) && bigIntCtorVal.AsObject() is FenObject bigIntCtorObj)
                    {
                        var bigIntProtoVal = bigIntCtorObj.Get("prototype", null);
                        if (bigIntProtoVal.IsObject)
                        {
                            wrapper.SetPrototype(bigIntProtoVal.AsObject());
                        }
                    }
                    wrapper.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (vArgs, vThis) =>
                    {
                        if (vThis.IsObject)
                        {
                            var wrapped = vThis.AsObject()?.Get("__value__");
                            if (wrapped.HasValue && wrapped.Value.IsBigInt)
                            {
                                return wrapped.Value;
                            }
                        }
                        throw new FenTypeError("TypeError: BigInt.prototype.valueOf called on incompatible object");
                    })));
                    return FenValue.FromObject(wrapper);
                }

                if (value.IsSymbol)
                {
                    wrapper.InternalClass = "Symbol";
                    wrapper.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (vArgs, vThis) =>
                    {
                        if (vThis.IsObject)
                        {
                            var wrapped = vThis.AsObject()?.Get("__value__");
                            if (wrapped.HasValue && wrapped.Value.IsSymbol)
                            {
                                return wrapped.Value;
                            }
                        }
                        throw new FenTypeError("TypeError: Symbol.prototype.valueOf called on incompatible object");
                    })));
                    return FenValue.FromObject(wrapper);
                }

                return FenValue.FromObject(wrapper);

            });
            objectCtor.Prototype = objectProto;
            objectCtor.DefineOwnProperty("prototype", new PropertyDescriptor
            {
                Value = FenValue.FromObject(objectProto),
                Writable = false,
                Enumerable = false,
                Configurable = false,
            });
            objectProto.SetBuiltin("constructor", FenValue.FromFunction(objectCtor));
            SetGlobal("Object", FenValue.FromFunction(objectCtor));
            window.Set("Object", FenValue.FromFunction(objectCtor));

            // Object static methods
            objectCtor.Set("keys", FenValue.FromFunction(new FenFunction("keys", (args, thisVal) =>
            {
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
                var obj = args[0].AsObject();
                if (obj == null) return FenValue.FromObject(FenObject.CreateArray());
                var keys = obj.Keys()?.ToList() ?? new List<string>();
                var arr = FenObject.CreateArray();
                for (int i = 0; i < keys.Count; i++)
                    arr.Set(i.ToString(), FenValue.FromString(keys[i]));
                arr.Set("length", FenValue.FromNumber(keys.Count));
                return FenValue.FromObject(arr);
            })));

            objectCtor.Set("values", FenValue.FromFunction(new FenFunction("values", (args, thisVal) =>
            {
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
                var obj = args[0].AsObject();
                if (obj == null) return FenValue.FromObject(FenObject.CreateArray());
                var keys = obj.Keys()?.ToList() ?? new List<string>();
                var arr = FenObject.CreateArray();
                for (int i = 0; i < keys.Count; i++)
                    arr.Set(i.ToString(), obj.Get(keys[i]));
                arr.Set("length", FenValue.FromNumber(keys.Count));
                return FenValue.FromObject(arr);
            })));

            objectCtor.Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
            {
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
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

            objectCtor.Set("assign", FenValue.FromFunction(new FenFunction("assign", (args, thisVal) =>
            {
                if (args.Length == 0)
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
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

            objectCtor.Set("defineProperty", FenValue.FromFunction(new FenFunction("defineProperty", (args, thisVal) =>
            {
                if (args.Length < 3) throw new FenTypeError("TypeError: Object.defineProperty requires 3 arguments");
                var obj = args[0].AsObject();
                if (obj == null) throw new FenTypeError("TypeError: Object.defineProperty called on non-object");

                try
                {
                    var desc = ToPropertyDescriptor(args[2], _context);
                    var defined = obj is FenObject fenObj
                        ? fenObj.DefineOwnProperty(args[1], desc)
                        : obj.DefineOwnProperty(args[1].AsString(_context), desc);
                    if (!defined)
                    {
                        throw new FenTypeError("TypeError: Cannot redefine property");
                    }
                }
                catch (InvalidOperationException ex)
                {
                    throw new FenTypeError($"TypeError: {ex.Message}");
                }

                return args[0];
            })));

            objectCtor.Set("defineProperties", FenValue.FromFunction(new FenFunction("defineProperties",
                (args, thisVal) =>
                {
                    if (args.Length < 2)
                        throw new FenTypeError("TypeError: Object.defineProperties requires 2 arguments");
                    var obj = args[0].AsObject();
                    if (obj == null)
                        throw new FenTypeError("TypeError: Object.defineProperties called on non-object");
                    var props = args[1].AsObject();
                    if (props == null) throw new FenTypeError("TypeError: Properties argument must be an object");

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
                            throw new FenTypeError($"TypeError: {ex.Message}");
                        }
                    }

                    foreach (var kvp in descriptors)
                    {
                        if (!obj.DefineOwnProperty(kvp.Key, kvp.Value))
                        {
                            throw new FenTypeError($"TypeError: Cannot redefine property: {kvp.Key}");
                        }
                    }

                    return args[0];
                })));

            objectCtor.Set("create", FenValue.FromFunction(new FenFunction("create", (args, thisVal) =>
            {
                if (args.Length == 0)
                    throw new FenTypeError("TypeError: Object.create requires at least 1 argument");

                IObject proto = null;
                if (args[0].IsNull) proto = null;
                else if (args[0].IsObject) proto = args[0].AsObject();
                else throw new FenTypeError("TypeError: Object prototype may only be an Object or null");

                var obj = new FenObject();
                obj.SetPrototype(proto);

                if (args.Length > 1 && !args[1].IsUndefined)
                {
                    var props = args[1].AsObject();
                    if (props == null) throw new FenTypeError("TypeError: Properties argument must be an object");

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
                                throw new FenTypeError($"TypeError: Cannot define property: {key}");
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            throw new FenTypeError($"TypeError: {ex.Message}");
                        }
                    }
                }

                return FenValue.FromObject(obj);
            })));

            objectCtor.Set("getOwnPropertyDescriptor", FenValue.FromFunction(new FenFunction("getOwnPropertyDescriptor",
                (args, thisVal) =>
                {
                    if (args.Length < 2)
                        throw new FenTypeError("TypeError: Object.getOwnPropertyDescriptor requires 2 arguments");
                    var obj = args[0].AsObject();
                    // Coerce to object if primitive? ES6 says yes, ES5 says throw. 
                    // Test262 usually assumes ES6+ but strict mode might vary. 
                    // FenEngine usually follows loose ES6.
                    if (obj == null && !args[0].IsNull && !args[0].IsUndefined)
                    {
                        // Attempt auto-boxing
                        // For now, return undefined if not object to match old behavior or throw?
                        // Let's throw for now to catch issues.
                        throw new FenTypeError("TypeError: Object.getOwnPropertyDescriptor called on non-object");
                    }

                    var desc = obj is FenObject fenObj
                        ? fenObj.GetOwnPropertyDescriptor(args[1])
                        : obj.GetOwnPropertyDescriptor(args[1].AsString(_context));

                    if (desc.HasValue)
                    {
                        return FromPropertyDescriptor(desc.Value);
                    }

                    return FenValue.Undefined;
                })));

            objectCtor.Set("getPrototypeOf", FenValue.FromFunction(new FenFunction("getPrototypeOf", (args, thisVal) =>
            {
                if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
                var obj = args[0].AsObject();
                // ES6: ToObject(args[0])
                if (obj == null) return FenValue.FromObject(new FenObject()); // Fallback? Or should we box?

                var proto = obj.GetPrototype();
                return proto != null ? FenValue.FromObject(proto) : FenValue.Null;
            })));

            objectCtor.Set("setPrototypeOf", FenValue.FromFunction(new FenFunction("setPrototypeOf", (args, thisVal) =>
            {
                if (args.Length < 2) throw new FenTypeError("TypeError: Object.setPrototypeOf requires 2 arguments");
                var obj = args[0].AsObject();
                if (obj == null) throw new FenTypeError("TypeError: Object.setPrototypeOf called on non-object");

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
                    throw new FenTypeError("TypeError: Object prototype may only be an Object or null");
                }

                return args[0];
            })));

            objectCtor.Set("freeze", FenValue.FromFunction(new FenFunction("freeze", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                var obj = args[0].AsObject();
                if (obj != null) obj.Freeze();
                return args[0];
            })));

            objectCtor.Set("seal", FenValue.FromFunction(new FenFunction("seal", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                var obj = args[0].AsObject();
                if (obj != null) obj.Seal();
                return args[0];
            })));

            objectCtor.Set("preventExtensions", FenValue.FromFunction(new FenFunction("preventExtensions",
                (args, thisVal) =>
                {
                    if (args.Length == 0) return FenValue.Undefined;
                    var obj = args[0].AsObject();
                    if (obj != null) obj.PreventExtensions();
                    return args[0];
                })));

            objectCtor.Set("isExtensible", FenValue.FromFunction(new FenFunction("isExtensible", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var obj = args[0].AsObject();
                return FenValue.FromBoolean(obj != null && obj.IsExtensible);
            })));

            objectCtor.Set("isSealed", FenValue.FromFunction(new FenFunction("isSealed", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(true);
                var obj = args[0].AsObject();
                return FenValue.FromBoolean(obj != null && obj.IsSealed());
            })));

            objectCtor.Set("isFrozen", FenValue.FromFunction(new FenFunction("isFrozen", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(true);
                var obj = args[0].AsObject();
                return FenValue.FromBoolean(obj != null && obj.IsFrozen());
            })));

            objectCtor.Set("is", FenValue.FromFunction(new FenFunction("is", (args, thisVal) =>
            {
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
                        return FenValue.FromBoolean(double.IsPositiveInfinity(1.0 / an) ==
                                                    double.IsPositiveInfinity(1.0 / bn));
                    }

                    return FenValue.FromBoolean(an == bn);
                }

                return FenValue.FromBoolean(a.Equals(b));
            })));

            // Object.prototype.toString
            objectProto.SetBuiltin("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                if (thisVal.IsUndefined) return FenValue.FromString("[object Undefined]");
                if (thisVal.IsNull) return FenValue.FromString("[object Null]");
                var toStringTagKey = JsSymbol.ToStringTag.ToPropertyKey();
                var iteratorKey = JsSymbol.Iterator.ToPropertyKey();

                string BuiltinTag(FenValue v, int depth = 0)
                {
                    if (v.IsBoolean) return "Boolean";
                    if (v.IsNumber) return "Number";
                    if (v.IsString) return "String";
                    if (v.IsSymbol) return "Symbol";
                    if (v.IsBigInt) return "Object";
                    if (v.IsFunction)
                    {
                        var fnObj = v.AsObject();
                        if (fnObj is FenFunction fn)
                        {
                            if (fn.IsAsync) return "AsyncFunction";
                            if (fn.IsGenerator) return "GeneratorFunction";
                        }
                        var fnTag = fnObj?.Get(toStringTagKey, null) ?? FenValue.Undefined;
                        if (fnTag.IsString)
                        {
                            var tagText = fnTag.ToString();
                            if (string.Equals(tagText, "AsyncFunction", StringComparison.Ordinal) ||
                                string.Equals(tagText, "GeneratorFunction", StringComparison.Ordinal))
                            {
                                return tagText;
                            }
                        }
                        return "Function";
                    }

                    var o = v.AsObject() as FenObject;
                    if (o == null) return "Object";

                    if (o.TryGetDirect("__isProxy__", out var isProxy) && isProxy.ToBoolean())
                    {
                        if (o.TryGetDirect("__isRevoked__", out var isRevoked) && isRevoked.ToBoolean())
                            throw new FenTypeError("TypeError: Cannot perform 'Object.prototype.toString' on a proxy that has been revoked");

                        FenValue target;
                        if (!o.TryGetDirect("__proxyTarget__", out target) && !o.TryGetDirect("__target__", out target))
                            target = FenValue.Undefined;
                        if (target.IsUndefined || target.IsNull)
                            throw new FenTypeError("TypeError: Cannot perform 'Object.prototype.toString' on a proxy that has been revoked");
                        if (depth > 16) throw new FenTypeError("TypeError: Proxy chain too deep");
                        return BuiltinTag(target, depth + 1);
                    }
                    var callMethod = o.Get("call", null);
                    var applyMethod = o.Get("apply", null);
                    if (callMethod.IsFunction && applyMethod.IsFunction)
                    {
                        var fnTag = o.Get(toStringTagKey, null);
                        if (fnTag.IsString)
                        {
                            var tagText = fnTag.ToString();
                            if (string.Equals(tagText, "AsyncFunction", StringComparison.Ordinal) ||
                                string.Equals(tagText, "GeneratorFunction", StringComparison.Ordinal))
                                return tagText;
                        }
                        return "Function";
                    }

                    var cls = string.IsNullOrEmpty(o.InternalClass) ? "Object" : o.InternalClass;
                    return cls;
                }

                var obj = (thisVal.IsObject || thisVal.IsFunction) ? thisVal.AsObject() : objectCtor.Invoke(new[] { thisVal }, _context).AsObject();
                var builtinTag = BuiltinTag(thisVal);
                var tagVal = obj?.Get(toStringTagKey, null) ?? FenValue.Undefined;

                var finalTag = tagVal.IsString ? tagVal.ToString() : builtinTag;
                return FenValue.FromString($"[object {finalTag}]");
            })));

            // Array
            var arrayCtor = CreateBuiltinFunction("Array", 1, (args, thisVal) =>
            {
                var arr = FenObject.CreateArray();
                if (args.Length == 1 && args[0].IsNumber)
                {
                    arr.Set("length", args[0]);
                }
                else
                {
                    for (int i = 0; i < args.Length; i++) arr.Set(i.ToString(), args[i]);
                    arr.Set("length", FenValue.FromNumber(args.Length));
                }

                return FenValue.FromObject(arr);
            }, isConstructor: true);
            arrayCtor.Prototype = arrayProto;
            arrayCtor.DefineOwnProperty("prototype", new PropertyDescriptor
            {
                Value = FenValue.FromObject(arrayProto),
                Writable = false,
                Enumerable = false,
                Configurable = false
            });
            arrayProto.SetBuiltin("constructor", FenValue.FromFunction(arrayCtor));

            // arrayProto.join
            arrayProto.SetBuiltin("join", FenValue.FromFunction(new FenFunction("join", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) throw new FenTypeError("TypeError: Array.prototype.join called on null or undefined");
                var lenVal = obj.Get("length");
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                var separator = args.Length > 0 && !args[0].IsUndefined ? args[0].ToString() : ",";
                var list = new System.Collections.Generic.List<string>();
                for (int i = 0; i < len; i++)
                {
                    var item = obj.Get(i.ToString());
                    list.Add(item.IsNull || item.IsUndefined ? "" : item.ToString());
                }
                return FenValue.FromString(string.Join(separator, list));
            })));

            // arrayProto.push
            arrayProto.SetBuiltin("push", FenValue.FromFunction(new FenFunction("push", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) throw new FenTypeError("TypeError: Array.prototype.push called on null or undefined");
                var lenVal = obj.Get("length");
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                for (int i = 0; i < args.Length; i++)
                {
                    obj.Set((len + i).ToString(), args[i]);
                }
                obj.Set("length", FenValue.FromNumber(len + args.Length));
                return FenValue.FromNumber(len + args.Length);
            })));

            // arrayProto.pop
            arrayProto.SetBuiltin("pop", FenValue.FromFunction(new FenFunction("pop", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) throw new FenTypeError("TypeError: Array.prototype.pop called on null or undefined");
                var lenVal = obj.Get("length");
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                if (len == 0)
                {
                    obj.Set("length", FenValue.FromNumber(0));
                    return FenValue.Undefined;
                }
                var lastIndex = (len - 1).ToString();
                var item = obj.Get(lastIndex);
                obj.Delete(lastIndex);
                obj.Set("length", FenValue.FromNumber(len - 1));
                return item;
            })));

            DefineGlobalBuiltin("Array", FenValue.FromFunction(arrayCtor));

            // Array static methods
            DefineBuiltinMethod(arrayCtor, "isArray", 1, (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var obj = args[0].AsObject();
                if (obj == null) return FenValue.FromBoolean(false);
                // Check if object has Array constructor
                var ctor = obj.Get("constructor").AsFunction();
                return FenValue.FromBoolean(ctor != null && ctor.Name == "Array");
            });

            DefineBuiltinMethod(arrayCtor, "from", 1, (args, thisVal) =>
            {
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
            });

            DefineBuiltinMethod(arrayCtor, "of", 0, (args, thisVal) =>
            {
                var arr = FenObject.CreateArray();
                for (int i = 0; i < args.Length; i++)
                {
                    arr.Set(i.ToString(), args[i]);
                }

                arr.Set("length", FenValue.FromNumber(args.Length));
                return FenValue.FromObject(arr);
            });

            // Array.prototype methods
            arrayProto.SetBuiltin("find", FenValue.FromFunction(new FenFunction("find", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.find called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: callback is not a function");
                var thisArg = args.Length > 1 ? args[1] : FenValue.Undefined;
                var len = arr.Get("length").ToNumber();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var result = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) },
                        _context);
                    if (result.ToBoolean()) return elem;
                }

                return FenValue.Undefined;
            })));

            arrayProto.SetBuiltin("findIndex", FenValue.FromFunction(new FenFunction("findIndex", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.findIndex called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromNumber(-1);
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: callback is not a function");
                var thisArg = args.Length > 1 ? args[1] : FenValue.Undefined;
                var len = arr.Get("length").ToNumber();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var result = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) },
                        _context);
                    if (result.ToBoolean()) return FenValue.FromNumber(i);
                }

                return FenValue.FromNumber(-1);
            })));

            arrayProto.SetBuiltin("fill", FenValue.FromFunction(new FenFunction("fill", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.fill called on null or undefined");
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

            arrayProto.SetBuiltin("includes", FenValue.FromFunction(new FenFunction("includes", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.includes called on null or undefined");
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
                    if (elem.Type == searchElement.Type && elem.LooseEquals(searchElement))
                        return FenValue.FromBoolean(true);
                    if (elem.IsNumber && searchElement.IsNumber && double.IsNaN(elem.ToNumber()) &&
                        double.IsNaN(searchElement.ToNumber())) return FenValue.FromBoolean(true);
                }

                return FenValue.FromBoolean(false);
            })));

            arrayProto.SetBuiltin("map", FenValue.FromFunction(new FenFunction("map", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.map called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromObject(FenObject.CreateArray());
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                var result = FenObject.CreateArray();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var mapped = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) },
                        _context);
                    result.Set(i.ToString(), mapped);
                }

                result.Set("length", FenValue.FromNumber(len));
                return FenValue.FromObject(result);
            })));

            arrayProto.SetBuiltin("filter", FenValue.FromFunction(new FenFunction("filter", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.filter called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromObject(FenObject.CreateArray());
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                var result = FenObject.CreateArray();
                int resultIdx = 0;
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var keep = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) },
                        _context);
                    if (keep.ToBoolean())
                    {
                        result.Set(resultIdx.ToString(), elem);
                        resultIdx++;
                    }
                }

                result.Set("length", FenValue.FromNumber(resultIdx));
                return FenValue.FromObject(result);
            })));

            arrayProto.SetBuiltin("reduce", FenValue.FromFunction(new FenFunction("reduce", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.reduce called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                if (len == 0 && args.Length < 2)
                    throw new FenTypeError("TypeError: Reduce of empty array with no initial value");
                var accumulator = args.Length > 1 ? args[1] : arr.Get("0");
                int startIdx = args.Length > 1 ? 0 : 1;
                for (int i = startIdx; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    accumulator =
                        callback.Invoke(new[] { accumulator, elem, FenValue.FromNumber(i), FenValue.FromObject(arr) },
                            _context);
                }

                return accumulator;
            })));

            arrayProto.SetBuiltin("forEach", FenValue.FromFunction(new FenFunction("forEach", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.forEach called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) }, _context);
                }

                return FenValue.Undefined;
            })));

            arrayProto.SetBuiltin("some", FenValue.FromFunction(new FenFunction("some", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.some called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromBoolean(false);
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var result = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) },
                        _context);
                    if (result.ToBoolean()) return FenValue.FromBoolean(true);
                }

                return FenValue.FromBoolean(false);
            })));

            arrayProto.SetBuiltin("every", FenValue.FromFunction(new FenFunction("every", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.every called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromBoolean(true);
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                for (int i = 0; i < len; i++)
                {
                    var elem = arr.Get(i.ToString());
                    var result = callback.Invoke(new[] { elem, FenValue.FromNumber(i), FenValue.FromObject(arr) },
                        _context);
                    if (!result.ToBoolean()) return FenValue.FromBoolean(false);
                }

                return FenValue.FromBoolean(true);
            })));

            arrayProto.SetBuiltin("slice", FenValue.FromFunction(new FenFunction("slice", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.slice called on null or undefined");
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

            arrayProto.SetBuiltin("indexOf", FenValue.FromFunction(new FenFunction("indexOf", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.indexOf called on null or undefined");
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

            arrayProto.SetBuiltin("lastIndexOf", FenValue.FromFunction(new FenFunction("lastIndexOf", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.lastIndexOf called on null or undefined");
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

            arrayProto.SetBuiltin("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.toString called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromString("[object Array]");
                var joinFunc = arr.Get("join");
                if (joinFunc.IsFunction)
                    return joinFunc.AsFunction().Invoke(Array.Empty<FenValue>(), _context, thisVal);
                return FenValue.FromString($"[object {(arr as FenObject)?.InternalClass ?? "Object"}]");
            })));

            arrayProto.SetBuiltin("join", FenValue.FromFunction(new FenFunction("join", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.join called on null or undefined");
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

            arrayProto.SetBuiltin("concat", FenValue.FromFunction(new FenFunction("concat", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.concat called on null or undefined");
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
                    bool spreadable = false;
                    if (arg.IsObject)
                    {
                        var argObj = arg.AsObject();
                        var argFenObj = argObj as FenObject;
                        // Check Symbol.isConcatSpreadable first
                        var spreadSym = argFenObj != null
                            ? argFenObj.GetSymbol(JsSymbol.IsConcatSpreadable)
                            : FenValue.Undefined;
                        if (!spreadSym.IsUndefined)
                        {
                            spreadable = spreadSym.ToBoolean();
                        }
                        else
                        {
                            // Default: spreadable only if it's an actual Array
                            spreadable = argFenObj != null && argFenObj.InternalClass == "Array";
                        }

                        if (spreadable)
                        {
                            var argLen = argObj.Get("length");
                            var len = argLen.IsNumber ? (int)argLen.ToNumber() : 0;
                            for (int i = 0; i < len; i++)
                            {
                                result.Set(resultIdx.ToString(), argObj.Get(i.ToString()));
                                resultIdx++;
                            }
                            continue;
                        }
                    }

                    // Not spreadable, add as single element
                    result.Set(resultIdx.ToString(), arg);
                    resultIdx++;
                }

                result.Set("length", FenValue.FromNumber(resultIdx));
                return FenValue.FromObject(result);
            })));

            arrayProto.SetBuiltin("reverse", FenValue.FromFunction(new FenFunction("reverse", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.reverse called on null or undefined");
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

            arrayProto.SetBuiltin("sort", FenValue.FromFunction(new FenFunction("sort", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.sort called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return thisVal;
                var compareFn = args.Length > 0 && args[0].IsFunction ? args[0].AsFunction() : null;
                var len = (int)arr.Get("length").ToNumber();
                // O(n log n) sort Ã¢â‚¬â€ read into List, sort, write back
                var items = new System.Collections.Generic.List<FenValue>(len);
                for (int i = 0; i < len; i++)
                    items.Add(arr.Get(i.ToString()));
                if (compareFn != null)
                {
                    items.Sort((a, b) =>
                    {
                        var cmp = compareFn.Invoke(new[] { a, b }, _context).ToNumber();
                        return double.IsNaN(cmp) ? 0 : cmp < 0 ? -1 : cmp > 0 ? 1 : 0;
                    });
                }
                else
                {
                    items.Sort((a, b) => string.Compare(
                        a.AsString(_context), b.AsString(_context), StringComparison.Ordinal));
                }
                for (int i = 0; i < len; i++)
                    arr.Set(i.ToString(), items[i]);
                return thisVal;
            })));


            arrayProto.SetBuiltin("push", FenValue.FromFunction(new FenFunction("push", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.push called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromNumber(0);
                var len = (int)arr.Get("length").ToNumber();
                for (int i = 0; i < args.Length; i++)
                    arr.Set((len + i).ToString(), args[i]);
                var newLen = len + args.Length;
                arr.Set("length", FenValue.FromNumber(newLen));
                return FenValue.FromNumber(newLen);
            })));

            arrayProto.SetBuiltin("pop", FenValue.FromFunction(new FenFunction("pop", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.pop called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var len = (int)arr.Get("length").ToNumber();
                if (len == 0) return FenValue.Undefined;
                var result = arr.Get((len - 1).ToString());
                arr.Set("length", FenValue.FromNumber(len - 1));
                return result;
            })));

            arrayProto.SetBuiltin("shift", FenValue.FromFunction(new FenFunction("shift", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.shift called on null or undefined");
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

            arrayProto.SetBuiltin("unshift", FenValue.FromFunction(new FenFunction("unshift", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.unshift called on null or undefined");
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

            // splice Ã¢â‚¬â€ ES3
            arrayProto.SetBuiltin("splice", FenValue.FromFunction(new FenFunction("splice", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.splice called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null)
                {
                    var e = FenObject.CreateArray();
                    e.Set("length", FenValue.FromNumber(0));
                    return FenValue.FromObject(e);
                }

                var len = (int)arr.Get("length").ToNumber();
                if (args.Length == 0)
                {
                    var e = FenObject.CreateArray();
                    e.Set("length", FenValue.FromNumber(0));
                    return FenValue.FromObject(e);
                }

                int start = double.IsNaN(args[0].ToNumber()) ? 0 : (int)args[0].ToNumber();
                if (start < 0) start = Math.Max(0, len + start);
                else start = Math.Min(start, len);
                int deleteCount;
                if (args.Length <= 1) deleteCount = len - start;
                else
                {
                    deleteCount = double.IsNaN(args[1].ToNumber()) ? 0 : (int)args[1].ToNumber();
                    deleteCount = Math.Max(0, Math.Min(deleteCount, len - start));
                }

                var insertItems = new System.Collections.Generic.List<FenValue>();
                for (int i = 2; i < args.Length; i++) insertItems.Add(args[i]);
                int insertCount = insertItems.Count;
                int newLen2 = len - deleteCount + insertCount;
                var removed = FenObject.CreateArray();
                for (int i = 0; i < deleteCount; i++) removed.Set(i.ToString(), arr.Get((start + i).ToString()));
                removed.Set("length", FenValue.FromNumber(deleteCount));
                if (insertCount < deleteCount)
                    for (int i = start + insertCount; i < newLen2; i++)
                        arr.Set(i.ToString(), arr.Get((i + deleteCount - insertCount).ToString()));
                else if (insertCount > deleteCount)
                    for (int i = newLen2 - 1; i >= start + insertCount; i--)
                        arr.Set(i.ToString(), arr.Get((i - insertCount + deleteCount).ToString()));
                for (int i = 0; i < insertCount; i++) arr.Set((start + i).ToString(), insertItems[i]);
                arr.Set("length", FenValue.FromNumber(newLen2));
                return FenValue.FromObject(removed);
            })));

            // reduceRight Ã¢â‚¬â€ ES5
            arrayProto.SetBuiltin("reduceRight", FenValue.FromFunction(new FenFunction("reduceRight", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.reduceRight called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                if (len == 0 && args.Length < 2)
                    throw new FenTypeError("TypeError: Reduce of empty array with no initial value");
                FenValue accumulator = args.Length > 1 ? args[1] : arr.Get((len - 1).ToString());
                int startIdx = args.Length > 1 ? len - 1 : len - 2;
                for (int i = startIdx; i >= 0; i--)
                {
                    var item = arr.Get(i.ToString());
                    accumulator =
                        callback.Invoke(new[] { accumulator, item, FenValue.FromNumber(i), FenValue.FromObject(arr) },
                            _context);
                    if (accumulator.Type == Core.Interfaces.ValueType.Error) return accumulator;
                }

                return accumulator;
            })));

            // copyWithin Ã¢â‚¬â€ ES6
            arrayProto.SetBuiltin("copyWithin", FenValue.FromFunction(new FenFunction("copyWithin", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.copyWithin called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return thisVal;
                var len = (int)arr.Get("length").ToNumber();
                int tgt = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (tgt < 0) tgt = Math.Max(0, len + tgt);
                else tgt = Math.Min(tgt, len);
                int st = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (st < 0) st = Math.Max(0, len + st);
                else st = Math.Min(st, len);
                int en = args.Length > 2 ? (int)args[2].ToNumber() : len;
                if (en < 0) en = Math.Max(0, len + en);
                else en = Math.Min(en, len);
                int count = Math.Min(en - st, len - tgt);
                var buf = new FenValue[Math.Max(0, count)];
                for (int i = 0; i < count; i++) buf[i] = arr.Get((st + i).ToString());
                for (int i = 0; i < count; i++) arr.Set((tgt + i).ToString(), buf[i]);
                return thisVal;
            })));

            // keys Ã¢â‚¬â€ ES6 iterator
            arrayProto.SetBuiltin("keys", FenValue.FromFunction(new FenFunction("keys", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.keys called on null or undefined");
                var src = thisVal.AsObject() as FenObject;
                if (src == null) return FenValue.Undefined;
                int idx = 0;
                var iter = new FenObject();
                iter.Set("next", FenValue.FromFunction(new FenFunction("next", (_, __) =>
                {
                    var res = new FenObject();
                    int l = (int)src.Get("length").ToNumber();
                    if (idx >= l)
                    {
                        res.Set("value", FenValue.Undefined);
                        res.Set("done", FenValue.FromBoolean(true));
                    }
                    else
                    {
                        res.Set("value", FenValue.FromNumber(idx));
                        res.Set("done", FenValue.FromBoolean(false));
                        idx++;
                    }

                    return FenValue.FromObject(res);
                })));
                iter.Set("[Symbol.iterator]",
                    FenValue.FromFunction(new FenFunction("@@iterator", (a, _) => FenValue.FromObject(iter))));
                return FenValue.FromObject(iter);
            })));

            // values Ã¢â‚¬â€ ES6 iterator
            arrayProto.SetBuiltin("values", FenValue.FromFunction(new FenFunction("values", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.values called on null or undefined");
                var src = thisVal.AsObject() as FenObject;
                if (src == null) return FenValue.Undefined;
                int idx = 0;
                var iter = new FenObject();
                iter.Set("next", FenValue.FromFunction(new FenFunction("next", (_, __) =>
                {
                    var res = new FenObject();
                    int l = (int)src.Get("length").ToNumber();
                    if (idx >= l)
                    {
                        res.Set("value", FenValue.Undefined);
                        res.Set("done", FenValue.FromBoolean(true));
                    }
                    else
                    {
                        res.Set("value", src.Get(idx.ToString()));
                        res.Set("done", FenValue.FromBoolean(false));
                        idx++;
                    }

                    return FenValue.FromObject(res);
                })));
                iter.Set("[Symbol.iterator]",
                    FenValue.FromFunction(new FenFunction("@@iterator", (a, _) => FenValue.FromObject(iter))));
                return FenValue.FromObject(iter);
            })));

            // entries Ã¢â‚¬â€ ES6 iterator
            arrayProto.SetBuiltin("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.entries called on null or undefined");
                var src = thisVal.AsObject() as FenObject;
                if (src == null) return FenValue.Undefined;
                int idx = 0;
                var iter = new FenObject();
                iter.Set("next", FenValue.FromFunction(new FenFunction("next", (_, __) =>
                {
                    var res = new FenObject();
                    int l = (int)src.Get("length").ToNumber();
                    if (idx >= l)
                    {
                        res.Set("value", FenValue.Undefined);
                        res.Set("done", FenValue.FromBoolean(true));
                    }
                    else
                    {
                        var pair = FenObject.CreateArray();
                        pair.Set("0", FenValue.FromNumber(idx));
                        pair.Set("1", src.Get(idx.ToString()));
                        pair.Set("length", FenValue.FromNumber(2));
                        res.Set("value", FenValue.FromObject(pair));
                        res.Set("done", FenValue.FromBoolean(false));
                        idx++;
                    }

                    return FenValue.FromObject(res);
                })));
                iter.Set("[Symbol.iterator]",
                    FenValue.FromFunction(new FenFunction("@@iterator", (a, _) => FenValue.FromObject(iter))));
                return FenValue.FromObject(iter);
            })));

            // at Ã¢â‚¬â€ ES2022
            arrayProto.SetBuiltin("at", FenValue.FromFunction(new FenFunction("at", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.at called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var len = (int)arr.Get("length").ToNumber();
                int idx = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (idx < 0) idx = len + idx;
                if (idx < 0 || idx >= len) return FenValue.Undefined;
                return arr.Get(idx.ToString());
            })));

            // flat Ã¢â‚¬â€ ES2019
            arrayProto.SetBuiltin("flat", FenValue.FromFunction(new FenFunction("flat", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.flat called on null or undefined");
                var arr = thisVal.AsObject() as FenObject;
                if (arr == null) return FenValue.FromObject(FenObject.CreateArray());
                double rawDepth = args.Length > 0 && !args[0].IsUndefined ? args[0].ToNumber() : 1;
                int depth = double.IsPositiveInfinity(rawDepth) ? int.MaxValue : (int)rawDepth;
                var result = FenObject.CreateArray();
                int resultIdx = 0;

                void FlattenArr(FenObject source, int d)
                {
                    int l = (int)source.Get("length").ToNumber();
                    for (int i = 0; i < l; i++)
                    {
                        var item = source.Get(i.ToString());
                        if (d > 0 && item.IsObject && (item.AsObject() as FenObject)?.InternalClass == "Array")
                            FlattenArr(item.AsObject() as FenObject, d - 1);
                        else
                        {
                            result.Set(resultIdx.ToString(), item);
                            resultIdx++;
                        }
                    }
                }

                FlattenArr(arr, depth);
                result.Set("length", FenValue.FromNumber(resultIdx));
                return FenValue.FromObject(result);
            })));

            // flatMap Ã¢â‚¬â€ ES2019
            arrayProto.SetBuiltin("flatMap", FenValue.FromFunction(new FenFunction("flatMap", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Array.prototype.flatMap called on null or undefined");
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromObject(FenObject.CreateArray());
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: flatMap callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                var result = FenObject.CreateArray();
                int resultIdx = 0;
                for (int i = 0; i < len; i++)
                {
                    var item = arr.Get(i.ToString());
                    var mapped = callback.Invoke(new[] { item, FenValue.FromNumber(i), FenValue.FromObject(arr) },
                        _context);
                    if (mapped.Type == Core.Interfaces.ValueType.Error) return mapped;
                    if (mapped.IsObject && (mapped.AsObject() as FenObject)?.InternalClass == "Array")
                    {
                        var inner = mapped.AsObject() as FenObject;
                        var il = (int)inner.Get("length").ToNumber();
                        for (int j = 0; j < il; j++)
                        {
                            result.Set(resultIdx.ToString(), inner.Get(j.ToString()));
                            resultIdx++;
                        }
                    }
                    else
                    {
                        result.Set(resultIdx.ToString(), mapped);
                        resultIdx++;
                    }
                }

                result.Set("length", FenValue.FromNumber(resultIdx));
                return FenValue.FromObject(result);
            })));

            // findLast Ã¢â‚¬â€ ES2023
            arrayProto.SetBuiltin("findLast", FenValue.FromFunction(new FenFunction("findLast", (args, thisVal) =>
            {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                for (int i = len - 1; i >= 0; i--)
                {
                    var item = arr.Get(i.ToString());
                    var res = callback.Invoke(new[] { item, FenValue.FromNumber(i), FenValue.FromObject(arr) },
                        _context);
                    if (res.Type == Core.Interfaces.ValueType.Error) return res;
                    if (res.ToBoolean()) return item;
                }

                return FenValue.Undefined;
            })));

            // findLastIndex Ã¢â‚¬â€ ES2023
            arrayProto.SetBuiltin("findLastIndex", FenValue.FromFunction(new FenFunction("findLastIndex", (args, thisVal) =>
            {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromNumber(-1);
                var callback = args.Length > 0 ? args[0].AsFunction() : null;
                if (callback == null) throw new FenTypeError("TypeError: callback is not a function");
                var len = (int)arr.Get("length").ToNumber();
                for (int i = len - 1; i >= 0; i--)
                {
                    var item = arr.Get(i.ToString());
                    var res = callback.Invoke(new[] { item, FenValue.FromNumber(i), FenValue.FromObject(arr) },
                        _context);
                    if (res.Type == Core.Interfaces.ValueType.Error) return res;
                    if (res.ToBoolean()) return FenValue.FromNumber(i);
                }

                return FenValue.FromNumber(-1);
            })));

            // toReversed Ã¢â‚¬â€ ES2023 (non-mutating)
            arrayProto.SetBuiltin("toReversed", FenValue.FromFunction(new FenFunction("toReversed", (args, thisVal) =>
            {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.FromObject(FenObject.CreateArray());
                var len = (int)arr.Get("length").ToNumber();
                var result = FenObject.CreateArray();
                for (int i = 0; i < len; i++) result.Set(i.ToString(), arr.Get((len - 1 - i).ToString()));
                result.Set("length", FenValue.FromNumber(len));
                return FenValue.FromObject(result);
            })));

            // toSorted Ã¢â‚¬â€ ES2023 (non-mutating)
            arrayProto.SetBuiltin("toSorted", FenValue.FromFunction(new FenFunction("toSorted", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined)
                {
                    throw new FenTypeError("TypeError: Array.prototype.toSorted called on null or undefined");
                }

                var arr = thisVal.AsObject();
                if (arr == null) throw new FenTypeError("TypeError: Array.prototype.toSorted called on non-object");
                if (args.Length > 0 && !args[0].IsUndefined && !args[0].IsFunction)
                {
                    throw new FenTypeError("TypeError: The comparison function must be either a function or undefined");
                }

                var compareFn = args.Length > 0 && args[0].IsFunction ? args[0].AsFunction() : null;
                var len = (int)arr.Get("length").ToNumber();
                var items = new System.Collections.Generic.List<KeyValuePair<int, FenValue>>();
                for (int i = 0; i < len; i++) items.Add(new KeyValuePair<int, FenValue>(i, arr.Get(i.ToString())));
                items.Sort((a, b) =>
                {
                    if (a.Value.IsUndefined && b.Value.IsUndefined) return a.Key.CompareTo(b.Key);
                    if (a.Value.IsUndefined) return 1;
                    if (b.Value.IsUndefined) return -1;

                    int comparison;
                    if (compareFn != null)
                    {
                        var compareResult = compareFn.Invoke(new[] { a.Value, b.Value }, _context);
                        var numeric = compareResult.ToNumber();
                        comparison = double.IsNaN(numeric) ? 0 : Math.Sign(numeric);
                    }
                    else
                    {
                        comparison = string.CompareOrdinal(a.Value.AsString(_context), b.Value.AsString(_context));
                    }

                    return comparison != 0 ? comparison : a.Key.CompareTo(b.Key);
                });
                var result = FenObject.CreateArray();
                for (int i = 0; i < items.Count; i++) result.Set(i.ToString(), items[i].Value);
                result.Set("length", FenValue.FromNumber(len));
                return FenValue.FromObject(result);
            })));

            // toSpliced Ã¢â‚¬â€ ES2023 (non-mutating)
            arrayProto.SetBuiltin("toSpliced", FenValue.FromFunction(new FenFunction("toSpliced", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined)
                {
                    throw new FenTypeError("TypeError: Array.prototype.toSpliced called on null or undefined");
                }

                var arr = thisVal.AsObject();
                if (arr == null) throw new FenTypeError("TypeError: Array.prototype.toSpliced called on non-object");

                var len = (int)arr.Get("length").ToNumber();
                int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (start < 0) start = Math.Max(0, len + start);
                else start = Math.Min(start, len);
                int deleteCount;
                if (args.Length <= 1) deleteCount = len - start;
                else
                {
                    deleteCount = (int)args[1].ToNumber();
                    deleteCount = Math.Max(0, Math.Min(deleteCount, len - start));
                }

                var insertItems = new System.Collections.Generic.List<FenValue>();
                for (int i = 2; i < args.Length; i++) insertItems.Add(args[i]);
                var result = FenObject.CreateArray();
                int ri = 0;
                for (int i = 0; i < start; i++)
                {
                    result.Set(ri.ToString(), arr.Get(i.ToString()));
                    ri++;
                }

                foreach (var item in insertItems)
                {
                    result.Set(ri.ToString(), item);
                    ri++;
                }

                for (int i = start + deleteCount; i < len; i++)
                {
                    result.Set(ri.ToString(), arr.Get(i.ToString()));
                    ri++;
                }

                result.Set("length", FenValue.FromNumber(ri));
                return FenValue.FromObject(result);
            })));

            // with Ã¢â‚¬â€ ES2023 (non-mutating)
            arrayProto.SetBuiltin("with", FenValue.FromFunction(new FenFunction("with", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined)
                {
                    throw new FenTypeError("TypeError: Array.prototype.with called on null or undefined");
                }

                var arr = thisVal.AsObject();
                if (arr == null) throw new FenTypeError("TypeError: Array.prototype.with called on non-object");
                var len = (int)arr.Get("length").ToNumber();
                int idx = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (idx < 0) idx = len + idx;
                if (idx < 0 || idx >= len) throw new FenRangeError("RangeError: Invalid index");
                var newVal = args.Length > 1 ? args[1] : FenValue.Undefined;
                var result = FenObject.CreateArray();
                for (int i = 0; i < len; i++) result.Set(i.ToString(), i == idx ? newVal : arr.Get(i.ToString()));
                result.Set("length", FenValue.FromNumber(len));
                return FenValue.FromObject(result);
            })));

            // Fix spec-required .length property for all Array.prototype methods
            // (methods registered via new FenFunction() get length=0 unless NativeLength is set)
            var arrayProtoLengths = new Dictionary<string, int>
            {
                ["concat"] = 1, ["copyWithin"] = 2, ["entries"] = 0, ["every"] = 1,
                ["fill"] = 1, ["filter"] = 1, ["find"] = 1, ["findIndex"] = 1,
                ["findLast"] = 1, ["findLastIndex"] = 1, ["flat"] = 0, ["flatMap"] = 1,
                ["forEach"] = 1, ["includes"] = 1, ["indexOf"] = 1, ["join"] = 1,
                ["keys"] = 0, ["lastIndexOf"] = 1, ["map"] = 1, ["pop"] = 0,
                ["push"] = 1, ["reduce"] = 1, ["reduceRight"] = 1, ["reverse"] = 0,
                ["shift"] = 0, ["slice"] = 2, ["some"] = 1, ["sort"] = 1,
                ["splice"] = 2, ["toString"] = 0, ["toLocaleString"] = 0, ["unshift"] = 1,
                ["values"] = 0, ["at"] = 1, ["toReversed"] = 0, ["toSorted"] = 1,
                ["toSpliced"] = 3, ["with"] = 2,
            };
            foreach (var kvp in arrayProtoLengths)
            {
                var v = arrayProto.Get(kvp.Key);
                if (v.IsFunction) { var methodFn = v.AsFunction(); if (methodFn != null) methodFn.NativeLength = kvp.Value; }
            }

            // Boolean
            var booleanCtor = new FenFunction("Boolean", (args, thisVal) =>
            {
                var val = args.Length > 0 ? args[0].ToBoolean() : false;
                if (!thisVal.IsUndefined && thisVal.AsObject()?.GetPrototype() == booleanProto)
                {
                    thisVal.AsObject().Set("__value__", FenValue.FromBoolean(val));
                    return thisVal;
                }

                return FenValue.FromBoolean(val);
            });
            booleanCtor.Prototype = booleanProto;
            booleanCtor.Set("prototype", FenValue.FromObject(booleanProto));
            booleanProto.SetBuiltin("constructor", FenValue.FromFunction(booleanCtor));
            SetGlobal("Boolean", FenValue.FromFunction(booleanCtor));
            window.Set("Boolean", FenValue.FromFunction(booleanCtor));

            // Boolean.prototype.toString
            booleanProto.SetBuiltin("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                bool b = false;
                if (thisVal.IsBoolean) b = thisVal.ToBoolean();
                else if (thisVal.IsObject && thisVal.AsObject().Has("__value__"))
                {
                    var inner = thisVal.AsObject().Get("__value__");
                    if (inner.IsBoolean) b = inner.ToBoolean();
                    else
                        throw new InvalidOperationException(
                            "TypeError: Boolean.prototype.toString called on incompatible object");
                }
                else
                    throw new InvalidOperationException(
                        "TypeError: Boolean.prototype.toString called on incompatible object");

                return FenValue.FromString(b ? "true" : "false");
            })));

            // Boolean.prototype.valueOf
            booleanProto.SetBuiltin("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) =>
            {
                if (thisVal.IsBoolean) return thisVal;
                if (thisVal.IsObject && thisVal.AsObject().Has("__value__"))
                {
                    var inner = thisVal.AsObject().Get("__value__");
                    if (inner.IsBoolean) return inner;
                }

                throw new InvalidOperationException(
                    "TypeError: Boolean.prototype.valueOf called on incompatible object");
            })));

            // String
            var stringCtor = new FenFunction("String", (args, thisVal) =>
            {
                var val = args.Length > 0
                    ? (args[0].IsSymbol ? args[0].AsSymbol().ToString() : args[0].ToString())
                    : "";
                if (!thisVal.IsUndefined && thisVal.AsObject()?.GetPrototype() == stringProto)
                {
                    thisVal.AsObject().Set("__value__", FenValue.FromString(val));
                    return thisVal;
                }

                return FenValue.FromString(val);
            });
            stringCtor.Prototype = stringProto;
            stringCtor.Set("prototype", FenValue.FromObject(stringProto));
            stringProto.SetBuiltin("constructor", FenValue.FromFunction(stringCtor));
            SetGlobal("String", FenValue.FromFunction(stringCtor));
            window.Set("String", FenValue.FromFunction(stringCtor));

            stringProto.SetBuiltin("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                if (thisVal.IsString)
                {
                    return FenValue.FromString(thisVal.AsString(_context));
                }

                if (thisVal.IsObject)
                {
                    var wrapped = thisVal.AsObject()?.Get("__value__");
                    if (wrapped.HasValue && wrapped.Value.IsString)
                    {
                        return FenValue.FromString(wrapped.Value.AsString(_context));
                    }
                }

                throw new InvalidOperationException("TypeError: String.prototype.toString called on incompatible object");
            })));

            stringProto.SetBuiltin("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) =>
            {
                if (thisVal.IsString)
                {
                    return thisVal;
                }

                if (thisVal.IsObject)
                {
                    var wrapped = thisVal.AsObject()?.Get("__value__");
                    if (wrapped.HasValue && wrapped.Value.IsString)
                    {
                        return wrapped.Value;
                    }
                }

                throw new InvalidOperationException("TypeError: String.prototype.valueOf called on incompatible object");
            })));

            // String.prototype methods
            stringProto.SetBuiltin("isWellFormed", FenValue.FromFunction(new FenFunction("isWellFormed", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.isWellFormed called on null or undefined");
                var str = thisVal.AsString(_context);
                for (int i = 0; i < str.Length; i++)
                {
                    var current = str[i];
                    if (char.IsHighSurrogate(current))
                    {
                        if (i + 1 >= str.Length || !char.IsLowSurrogate(str[i + 1]))
                        {
                            return FenValue.FromBoolean(false);
                        }

                        i++;
                        continue;
                    }

                    if (char.IsLowSurrogate(current))
                    {
                        return FenValue.FromBoolean(false);
                    }
                }

                return FenValue.FromBoolean(true);
            })));

            stringProto.SetBuiltin("toWellFormed", FenValue.FromFunction(new FenFunction("toWellFormed", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.toWellFormed called on null or undefined");
                var str = thisVal.AsString(_context);
                if (str.Length == 0)
                {
                    return FenValue.FromString(str);
                }

                StringBuilder? builder = null;
                for (int i = 0; i < str.Length; i++)
                {
                    var current = str[i];
                    if (char.IsHighSurrogate(current))
                    {
                        if (i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
                        {
                            if (builder != null)
                            {
                                builder.Append(current);
                                builder.Append(str[i + 1]);
                            }

                            i++;
                            continue;
                        }

                        builder ??= new StringBuilder(str.Length);
                        if (builder.Length == 0 && i > 0)
                        {
                            builder.Append(str, 0, i);
                        }

                        builder.Append('\uFFFD');
                        continue;
                    }

                    if (char.IsLowSurrogate(current))
                    {
                        builder ??= new StringBuilder(str.Length);
                        if (builder.Length == 0 && i > 0)
                        {
                            builder.Append(str, 0, i);
                        }

                        builder.Append('\uFFFD');
                        continue;
                    }

                    builder?.Append(current);
                }

                return builder == null
                    ? FenValue.FromString(str)
                    : FenValue.FromString(builder.ToString());
            })));

            stringProto.SetBuiltin("repeat", FenValue.FromFunction(new FenFunction("repeat", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.repeat called on null or undefined");
                var str = thisVal.AsString(_context);
                var count = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (count < 0 || double.IsInfinity(count)) throw new FenRangeError("RangeError: Invalid count value");
                if (count == 0) return FenValue.FromString("");
                var sb = new StringBuilder();
                for (int i = 0; i < count; i++) sb.Append(str);
                return FenValue.FromString(sb.ToString());
            })));

            stringProto.SetBuiltin("includes", FenValue.FromFunction(new FenFunction("includes", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.includes called on null or undefined");
                var str = thisVal.AsString(_context);
                var searchString = args.Length > 0 ? args[0].AsString(_context) : "";
                var position = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (position < 0) position = 0;
                return FenValue.FromBoolean(str.IndexOf(searchString, position, StringComparison.Ordinal) >= 0);
            })));

            stringProto.SetBuiltin("padStart", FenValue.FromFunction(new FenFunction("padStart", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.padStart called on null or undefined");
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

            stringProto.SetBuiltin("padEnd", FenValue.FromFunction(new FenFunction("padEnd", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.padEnd called on null or undefined");
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

            stringProto.SetBuiltin("trim", FenValue.FromFunction(new FenFunction("trim", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.trim called on null or undefined");
                var str = thisVal.AsString(_context);
                return FenValue.FromString(str.Trim());
            })));

            stringProto.SetBuiltin("trimStart", FenValue.FromFunction(new FenFunction("trimStart", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.trimStart called on null or undefined");
                var str = thisVal.AsString(_context);
                return FenValue.FromString(str.TrimStart());
            })));

            {
                var trimLeftFn = new FenFunction("trimStart", (args, thisVal) =>
                {
                    if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.trimLeft called on null or undefined");
                    var str = thisVal.AsString(_context);
                    return FenValue.FromString(str.TrimStart());
                });
                trimLeftFn.IsConstructor = false;
                stringProto.SetBuiltin("trimLeft", FenValue.FromFunction(trimLeftFn));
            }

            stringProto.SetBuiltin("trimEnd", FenValue.FromFunction(new FenFunction("trimEnd", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.trimEnd called on null or undefined");
                var str = thisVal.AsString(_context);
                return FenValue.FromString(str.TrimEnd());
            })));

            {
                var trimRightFn = new FenFunction("trimEnd", (args, thisVal) =>
                {
                    if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.trimRight called on null or undefined");
                    var str = thisVal.AsString(_context);
                    return FenValue.FromString(str.TrimEnd());
                });
                trimRightFn.IsConstructor = false;
                stringProto.SetBuiltin("trimRight", FenValue.FromFunction(trimRightFn));
            }

            stringProto.SetBuiltin("startsWith", FenValue.FromFunction(new FenFunction("startsWith", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.startsWith called on null or undefined");
                var str = thisVal.AsString(_context);
                var searchString = args.Length > 0 ? args[0].AsString(_context) : "";
                var position = args.Length > 1 ? Math.Max(0, (int)args[1].ToNumber()) : 0;
                if (position >= str.Length) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(str.Substring(position).StartsWith(searchString));
            })));

            stringProto.SetBuiltin("endsWith", FenValue.FromFunction(new FenFunction("endsWith", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.endsWith called on null or undefined");
                var str = thisVal.AsString(_context);
                var searchString = args.Length > 0 ? args[0].AsString(_context) : "";
                var endPosition = args.Length > 1 ? (int)args[1].ToNumber() : str.Length;
                if (endPosition < 0) endPosition = 0;
                if (endPosition > str.Length) endPosition = str.Length;
                var substr = str.Substring(0, endPosition);
                return FenValue.FromBoolean(substr.EndsWith(searchString));
            })));

            stringProto.SetBuiltin("slice", FenValue.FromFunction(new FenFunction("slice", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.slice called on null or undefined");
                var str = thisVal.AsString(_context);
                var len = str.Length;
                var start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                var end = args.Length > 1 ? (int)args[1].ToNumber() : len;
                if (start < 0) start = Math.Max(0, len + start);
                if (end < 0) end = Math.Max(0, len + end);
                if (start >= len) return FenValue.FromString("");
                return FenValue.FromString(str.Substring(start, Math.Max(0, Math.Min(end, len) - start)));
            })));

            stringProto.SetBuiltin("substring", FenValue.FromFunction(new FenFunction("substring", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.substring called on null or undefined");
                var str = thisVal.AsString(_context);
                var len = str.Length;
                var start = args.Length > 0 ? Math.Max(0, (int)args[0].ToNumber()) : 0;
                var end = args.Length > 1 ? Math.Max(0, (int)args[1].ToNumber()) : len;
                if (start > end)
                {
                    var temp = start;
                    start = end;
                    end = temp;
                }

                if (start >= len) return FenValue.FromString("");
                return FenValue.FromString(str.Substring(start, Math.Min(end - start, len - start)));
            })));

            stringProto.SetBuiltin("charAt", FenValue.FromFunction(new FenFunction("charAt", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.charAt called on null or undefined");
                var str = thisVal.AsString(_context);
                var index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (index < 0 || index >= str.Length) return FenValue.FromString("");
                return FenValue.FromString(str[index].ToString());
            })));

            stringProto.SetBuiltin("charCodeAt", FenValue.FromFunction(new FenFunction("charCodeAt", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.charCodeAt called on null or undefined");
                var str = thisVal.AsString(_context);
                var index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (index < 0 || index >= str.Length) return FenValue.FromNumber(double.NaN);
                return FenValue.FromNumber((int)str[index]);
            })));

            stringProto.SetBuiltin("indexOf", FenValue.FromFunction(new FenFunction("indexOf", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.indexOf called on null or undefined");
                var str = thisVal.AsString(_context);
                var searchString = args.Length > 0 ? args[0].AsString(_context) : "";
                var position = args.Length > 1 ? Math.Max(0, (int)args[1].ToNumber()) : 0;
                var result = str.IndexOf(searchString, position, StringComparison.Ordinal);
                return FenValue.FromNumber(result);
            })));

            stringProto.SetBuiltin("lastIndexOf", FenValue.FromFunction(new FenFunction("lastIndexOf", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.lastIndexOf called on null or undefined");
                var str = thisVal.AsString(_context);
                var searchString = args.Length > 0 ? args[0].AsString(_context) : "";
                var position = args.Length > 1 ? (int)args[1].ToNumber() : int.MaxValue;
                if (position < 0) return FenValue.FromNumber(-1);
                var searchStart = Math.Min(position, str.Length - searchString.Length);
                if (searchStart < 0) searchStart = 0;
                var result = str.LastIndexOf(searchString, searchStart, StringComparison.Ordinal);
                return FenValue.FromNumber(result);
            })));

            stringProto.SetBuiltin("split", FenValue.FromFunction(new FenFunction("split", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.split called on null or undefined");
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


            stringProto.SetBuiltin("toLowerCase",
                FenValue.FromFunction(new FenFunction("toLowerCase",
                    (args, thisVal) =>
                    {
                        if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.toLowerCase called on null or undefined");
                        return FenValue.FromString(thisVal.AsString(_context).ToLowerInvariant());
                    })));

            stringProto.SetBuiltin("toUpperCase",
                FenValue.FromFunction(new FenFunction("toUpperCase",
                    (args, thisVal) =>
                    {
                        if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.toUpperCase called on null or undefined");
                        return FenValue.FromString(thisVal.AsString(_context).ToUpperInvariant());
                    })));

            stringProto.SetBuiltin("replace", FenValue.FromFunction(new FenFunction("replace", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.replace called on null or undefined");
                var str = thisVal.AsString(_context);
                if (args.Length < 2) return FenValue.FromString(str);
                var search = args[0].AsString(_context);
                var replace = args[1].AsString(_context);
                var index = str.IndexOf(search, StringComparison.Ordinal);
                if (index >= 0)
                    return FenValue.FromString(str.Substring(0, index) + replace +
                                               str.Substring(index + search.Length));
                return FenValue.FromString(str);
            })));
            stringProto.SetBuiltin("match", FenValue.FromFunction(new FenFunction("match", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.match called on null or undefined");
                var str = thisVal.AsString(_context);
                if (args.Length == 0) return FenValue.Null;
                var regexArg = args[0];
                System.Text.RegularExpressions.Regex regex = null;

                if (regexArg.IsObject &&
                    (regexArg.AsObject() as FenObject)?.NativeObject is System.Text.RegularExpressions.Regex r)
                    regex = r;
                else
                {
                    try
                    {
                        regex = new System.Text.RegularExpressions.Regex(regexArg.AsString(_context));
                    }
                    catch
                    {
                        return FenValue.Null;
                    }
                }

                var matches = regex.Matches(str);
                if (matches.Count == 0) return FenValue.Null;

                var result = FenObject.CreateArray();
                for (int i = 0; i < matches.Count; i++)
                    result.Set(i.ToString(), FenValue.FromString(matches[i].Value));
                result.Set("length", FenValue.FromNumber(matches.Count));
                return FenValue.FromObject(result);
            })));

            stringProto.SetBuiltin("search", FenValue.FromFunction(new FenFunction("search", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.search called on null or undefined");
                var str = thisVal.AsString(_context);
                if (args.Length == 0) return FenValue.FromNumber(-1);
                var regexArg = args[0];
                System.Text.RegularExpressions.Regex regex = null;

                if (regexArg.IsObject &&
                    (regexArg.AsObject() as FenObject)?.NativeObject is System.Text.RegularExpressions.Regex r)
                    regex = r;
                else
                {
                    try
                    {
                        regex = new System.Text.RegularExpressions.Regex(regexArg.AsString(_context));
                    }
                    catch
                    {
                        return FenValue.FromNumber(-1);
                    }
                }

                var match = regex.Match(str);
                return FenValue.FromNumber(match.Success ? match.Index : -1);
            })));

            // at Ã¢â‚¬â€ ES2022
            stringProto.SetBuiltin("at", FenValue.FromFunction(new FenFunction("at", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.at called on null or undefined");
                var str = thisVal.AsString(_context);
                int idx = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (idx < 0) idx = str.Length + idx;
                if (idx < 0 || idx >= str.Length) return FenValue.Undefined;
                return FenValue.FromString(str[idx].ToString());
            })));

            // Annex B: String.prototype.substr
            {
                var fn = new FenFunction("substr", (args, thisVal) =>
                {
                    if (thisVal.IsNull || thisVal.IsUndefined)
                        throw new FenTypeError("TypeError: String.prototype.substr called on null or undefined");
                    var str = thisVal.AsString(_context);
                    int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                    if (start < 0) start = Math.Max(0, str.Length + start);
                    if (start >= str.Length) return FenValue.FromString("");
                    int length = args.Length > 1 && !args[1].IsUndefined ? (int)args[1].ToNumber() : str.Length - start;
                    if (length <= 0) return FenValue.FromString("");
                    length = Math.Min(length, str.Length - start);
                    return FenValue.FromString(str.Substring(start, length));
                });
                fn.IsConstructor = false;
                fn.NativeLength = 2;
                stringProto.SetBuiltin("substr", FenValue.FromFunction(fn));
            }

            // Annex B: String.prototype HTML wrappers
            {
                Func<string, string, FenFunction> makeHtmlWrap = (name, tag) =>
                {
                    var fn = new FenFunction(name, (args, thisVal) =>
                    {
                        if (thisVal.IsNull || thisVal.IsUndefined)
                            throw new FenTypeError($"TypeError: String.prototype.{name} called on null or undefined");
                        var s = thisVal.AsString(_context);
                        return FenValue.FromString($"<{tag}>{s}</{tag}>");
                    });
                    fn.IsConstructor = false;
                    return fn;
                };
                Func<string, string, string, FenFunction> makeHtmlWrapAttr = (name, tag, attr) =>
                {
                    var fn = new FenFunction(name, (args, thisVal) =>
                    {
                        if (thisVal.IsNull || thisVal.IsUndefined)
                            throw new FenTypeError($"TypeError: String.prototype.{name} called on null or undefined");
                        var s = thisVal.AsString(_context);
                        var attrVal = args.Length > 0 ? args[0].ToString() : "";
                        // Escape double-quotes in attribute value per spec
                        attrVal = attrVal.Replace("\"", "&quot;");
                        return FenValue.FromString($"<{tag} {attr}=\"{attrVal}\">{s}</{tag}>");
                    });
                    fn.IsConstructor = false;
                    fn.NativeLength = 1;
                    return fn;
                };

                stringProto.SetBuiltin("bold", FenValue.FromFunction(makeHtmlWrap("bold", "b")));
                stringProto.SetBuiltin("italics", FenValue.FromFunction(makeHtmlWrap("italics", "i")));
                stringProto.SetBuiltin("big", FenValue.FromFunction(makeHtmlWrap("big", "big")));
                stringProto.SetBuiltin("small", FenValue.FromFunction(makeHtmlWrap("small", "small")));
                stringProto.SetBuiltin("blink", FenValue.FromFunction(makeHtmlWrap("blink", "blink")));
                stringProto.SetBuiltin("fixed", FenValue.FromFunction(makeHtmlWrap("fixed", "tt")));
                stringProto.SetBuiltin("strike", FenValue.FromFunction(makeHtmlWrap("strike", "strike")));
                stringProto.SetBuiltin("sub", FenValue.FromFunction(makeHtmlWrap("sub", "sub")));
                stringProto.SetBuiltin("sup", FenValue.FromFunction(makeHtmlWrap("sup", "sup")));
                stringProto.SetBuiltin("anchor", FenValue.FromFunction(makeHtmlWrapAttr("anchor", "a", "name")));
                stringProto.SetBuiltin("fontcolor", FenValue.FromFunction(makeHtmlWrapAttr("fontcolor", "font", "color")));
                stringProto.SetBuiltin("fontsize", FenValue.FromFunction(makeHtmlWrapAttr("fontsize", "font", "size")));
                stringProto.SetBuiltin("link", FenValue.FromFunction(makeHtmlWrapAttr("link", "a", "href")));
            }

            // Fix spec-required .length property for String.prototype methods
            var stringProtoLengths = new Dictionary<string, int>
            {
                ["charAt"] = 1, ["charCodeAt"] = 1, ["codePointAt"] = 1,
                ["concat"] = 1, ["endsWith"] = 1, ["includes"] = 1,
                ["indexOf"] = 1, ["lastIndexOf"] = 1, ["match"] = 1,
                ["matchAll"] = 1, ["normalize"] = 0, ["padEnd"] = 1,
                ["padStart"] = 1, ["repeat"] = 1, ["replace"] = 2,
                ["replaceAll"] = 2, ["search"] = 1, ["slice"] = 2,
                ["split"] = 2, ["startsWith"] = 1, ["substring"] = 2,
                ["toLowerCase"] = 0, ["toUpperCase"] = 0, ["trim"] = 0,
                ["trimEnd"] = 0, ["trimStart"] = 0, ["trimLeft"] = 0,
                ["trimRight"] = 0, ["at"] = 1, ["isWellFormed"] = 0,
                ["toWellFormed"] = 0, ["toString"] = 0, ["valueOf"] = 0,
                ["localeCompare"] = 1, ["toLocaleLowerCase"] = 0, ["toLocaleUpperCase"] = 0,
            };
            foreach (var kvp in stringProtoLengths)
            {
                var v = stringProto.Get(kvp.Key);
                if (v.IsFunction) { var methodFn = v.AsFunction(); if (methodFn != null) methodFn.NativeLength = kvp.Value; }
            }

            // Number
            var numberCtor = new FenFunction("Number", (args, thisVal) =>
            {
                var val = args.Length > 0 ? args[0].ToNumber() : 0.0;
                if (!thisVal.IsUndefined && thisVal.AsObject()?.GetPrototype() == numberProto)
                {
                    thisVal.AsObject().Set("__value__", FenValue.FromNumber(val));
                    return thisVal;
                }

                return FenValue.FromNumber(val);
            });
            numberCtor.Prototype = numberProto;

            numberProto.SetBuiltin("toFixed", FenValue.FromFunction(new FenFunction("toFixed", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Number.prototype.toFixed called on null or undefined");
                var num = thisVal.IsNumber
                    ? thisVal.ToNumber()
                    : (thisVal.AsObject()?.Get("__value__").ToNumber() ?? double.NaN);
                var digits = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (double.IsNaN(num)) return FenValue.FromString("NaN");
                if (double.IsPositiveInfinity(num)) return FenValue.FromString("Infinity");
                if (double.IsNegativeInfinity(num)) return FenValue.FromString("-Infinity");
                return FenValue.FromString(num.ToString("F" + Math.Max(0, Math.Min(20, digits))));
            })));

            numberProto.SetBuiltin("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Number.prototype.toString called on null or undefined");
                var num = thisVal.IsNumber
                    ? thisVal.ToNumber()
                    : (thisVal.AsObject()?.Get("__value__").ToNumber() ?? double.NaN);
                var radix = args.Length > 0 && !args[0].IsUndefined ? (int)args[0].ToNumber() : 10;
                if (radix < 2 || radix > 36) throw new FenRangeError("RangeError: radix must be between 2 and 36");
                if (double.IsNaN(num) || double.IsInfinity(num))
                    return FenValue.FromString(num.ToString(System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant());

                if (radix == 10)
                {
                    // Specific formatting rules for scientific notation mapping
                    if (Math.Abs(num) >= 1e21 || (Math.Abs(num) > 0 && Math.Abs(num) <= 1e-7))
                    {
                        return FenValue.FromString(num.ToString("0.####################e+0", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant().Replace("e+0", "e+").Replace("e-0", "e-"));
                    }

                    if (Math.Abs(num) >= 1e20)
                    {
                        return FenValue.FromString(num.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    return FenValue.FromString(num.ToString("G15", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant());
                }

                try
                {
                    return FenValue.FromString(Convert.ToString((long)num, radix));
                }
                catch
                {
                    return FenValue.FromString(num.ToString(System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant());
                }
            })));
            numberProto.SetBuiltin("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) =>
            {
                if (thisVal.IsNumber)
                {
                    return thisVal;
                }

                if (thisVal.IsObject)
                {
                    var wrapped = thisVal.AsObject()?.Get("__value__");
                    if (wrapped.HasValue && wrapped.Value.IsNumber)
                    {
                        return wrapped.Value;
                    }
                }

                throw new InvalidOperationException("TypeError: Number.prototype.valueOf called on incompatible object");
            })));

            numberProto.SetBuiltin("toPrecision", FenValue.FromFunction(new FenFunction("toPrecision", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Number.prototype.toPrecision called on null or undefined");
                var num = thisVal.IsNumber ? thisVal.ToNumber() : (thisVal.AsObject()?.Get("__value__").ToNumber() ?? double.NaN);
                if (args.Length == 0 || args[0].IsUndefined) return FenValue.FromString(num.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
                var precision = (int)args[0].ToNumber();
                if (precision < 1 || precision > 100) throw new FenRangeError("RangeError: toPrecision() argument must be between 1 and 100");
                if (double.IsNaN(num)) return FenValue.FromString("NaN");
                if (double.IsPositiveInfinity(num)) return FenValue.FromString("Infinity");
                if (double.IsNegativeInfinity(num)) return FenValue.FromString("-Infinity");
                return FenValue.FromString(num.ToString("G" + precision, System.Globalization.CultureInfo.InvariantCulture).ToLower());
            })));

            numberProto.SetBuiltin("toExponential", FenValue.FromFunction(new FenFunction("toExponential", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Number.prototype.toExponential called on null or undefined");
                var num = thisVal.IsNumber ? thisVal.ToNumber() : (thisVal.AsObject()?.Get("__value__").ToNumber() ?? double.NaN);
                if (double.IsNaN(num)) return FenValue.FromString("NaN");
                if (double.IsPositiveInfinity(num)) return FenValue.FromString("Infinity");
                if (double.IsNegativeInfinity(num)) return FenValue.FromString("-Infinity");
                if (args.Length == 0 || args[0].IsUndefined)
                    return FenValue.FromString(num.ToString("e", System.Globalization.CultureInfo.InvariantCulture));
                var fractionDigits = (int)args[0].ToNumber();
                if (fractionDigits < 0 || fractionDigits > 100) throw new FenRangeError("RangeError: toExponential() argument must be between 0 and 100");
                return FenValue.FromString(num.ToString("e" + fractionDigits, System.Globalization.CultureInfo.InvariantCulture));
            })));

            numberProto.SetBuiltin("toLocaleString", FenValue.FromFunction(new FenFunction("toLocaleString", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: Number.prototype.toLocaleString called on null or undefined");
                var num = thisVal.IsNumber ? thisVal.ToNumber() : (thisVal.AsObject()?.Get("__value__").ToNumber() ?? double.NaN);
                if (double.IsNaN(num)) return FenValue.FromString("NaN");
                if (double.IsPositiveInfinity(num)) return FenValue.FromString("Infinity");
                if (double.IsNegativeInfinity(num)) return FenValue.FromString("-Infinity");
                return FenValue.FromString(num.ToString("N", System.Globalization.CultureInfo.CurrentCulture));
            })));

            numberCtor.Set("prototype", FenValue.FromObject(numberProto));
            numberProto.SetBuiltin("constructor", FenValue.FromFunction(numberCtor));

            // Number static methods
            numberCtor.Set("isNaN", FenValue.FromFunction(new FenFunction("isNaN", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var val = args[0];
                return FenValue.FromBoolean(val.IsNumber && double.IsNaN(val.ToNumber()));
            })));

            numberCtor.Set("isFinite", FenValue.FromFunction(new FenFunction("isFinite", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var val = args[0];
                if (!val.IsNumber) return FenValue.FromBoolean(false);
                var num = val.ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num));
            })));

            numberCtor.Set("isInteger", FenValue.FromFunction(new FenFunction("isInteger", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var val = args[0];
                if (!val.IsNumber) return FenValue.FromBoolean(false);
                var num = val.ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num) && Math.Floor(num) == num);
            })));

            numberCtor.Set("isSafeInteger", FenValue.FromFunction(new FenFunction("isSafeInteger", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var val = args[0];
                if (!val.IsNumber) return FenValue.FromBoolean(false);
                var num = val.ToNumber();
                const double MAX_SAFE_INTEGER = 9007199254740991.0; // 2^53 - 1
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num) && Math.Floor(num) == num &&
                                            Math.Abs(num) <= MAX_SAFE_INTEGER);
            })));

            numberCtor.Set("parseFloat", FenValue.FromFunction(new FenFunction("parseFloat", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                var str = args[0].ToString().Trim();
                if (double.TryParse(str, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double result))
                {
                    return FenValue.FromNumber(result);
                }

                return FenValue.FromNumber(double.NaN);
            })));

            numberCtor.Set("parseInt", FenValue.FromFunction(new FenFunction("parseInt", (args, thisVal) =>
            {
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
            functionProto.SetBuiltin("call", FenValue.FromFunction(new FenFunction("call", (args, thisVal) =>
            {
                if (!thisVal.IsFunction) throw new FenTypeError("TypeError: call called on non-function");
                var func = thisVal.AsFunction() as FenFunction;
                if (func == null) throw new FenTypeError("TypeError: call called on non-function");
                var newThis = args.Length > 0 ? args[0] : FenValue.Undefined;
                var funcArgs = args.Length > 1 ? args.Skip(1).ToArray() : new FenValue[0];
                return func.Invoke(funcArgs, _context, newThis);
            })));

            // Function.prototype.apply(thisArg, argsArray)
            functionProto.SetBuiltin("apply", FenValue.FromFunction(new FenFunction("apply", (args, thisVal) =>
            {
                if (!thisVal.IsFunction) throw new FenTypeError("TypeError: apply called on non-function");
                var func = thisVal.AsFunction() as FenFunction;
                if (func == null) throw new FenTypeError("TypeError: apply called on non-function");
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
                    throw new FenTypeError("TypeError: apply arguments not iterable");

                return func.Invoke(funcArgs, _context, newThis);
            })));

            // Function.prototype.bind(thisArg, ...args)
            functionProto.SetBuiltin("bind", FenValue.FromFunction(new FenFunction("bind", (args, thisVal) =>
            {
                if (!thisVal.IsFunction) throw new FenTypeError("TypeError: bind called on non-function");
                var originalFunc = thisVal.AsFunction() as FenFunction;
                if (originalFunc == null) throw new FenTypeError("TypeError: bind called on non-function");
                var boundThis = args.Length > 0 ? args[0] : FenValue.Undefined;
                var boundArgs = args.Length > 1 ? args.Skip(1).ToArray() : new FenValue[0];
                var boundFunc = new FenFunction("bound " + (originalFunc.Name ?? "anonymous"), (callArgs, _) =>
                {
                    var finalArgs = boundArgs.Concat(callArgs).ToArray();
                    return originalFunc.Invoke(finalArgs, _context, boundThis);
                });
                boundFunc.BoundTargetFunction = originalFunc;
                // ES spec: bound function length = max(0, target.length - boundArgs.length)
                var origLen = originalFunc.Get("length").ToNumber();
                var boundLen = Math.Max(0, origLen - boundArgs.Length);
                boundFunc.NativeLength = (int)boundLen;
                boundFunc.IsConstructor = originalFunc.IsConstructor;
                return FenValue.FromFunction(boundFunc);
            })));

            // Function.prototype.toString()
            functionProto.SetBuiltin("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                if (!thisVal.IsFunction) return FenValue.FromString("function () { [native code] }");
                var func = thisVal.AsFunction() as FenFunction;
                if (func != null && func.Source != null)
                    return FenValue.FromString(func.Source);
                var name = func?.Name ?? "anonymous";
                return FenValue.FromString("function " + name + "() { [native code] }");
            })));

            // Function.prototype.length (default 0)
            functionProto.DefineOwnProperty("length", new PropertyDescriptor { Value = FenValue.FromNumber(0), Writable = false, Enumerable = false, Configurable = true });
            functionProto.DefineOwnProperty("name", new PropertyDescriptor { Value = FenValue.FromString(""), Writable = false, Enumerable = false, Configurable = true });
            // Set correct lengths on Function.prototype methods
            { var v = functionProto.Get("call"); if (v.IsFunction) { var fn = v.AsFunction(); if (fn != null) fn.NativeLength = 1; } }
            { var v = functionProto.Get("apply"); if (v.IsFunction) { var fn = v.AsFunction(); if (fn != null) fn.NativeLength = 2; } }
            { var v = functionProto.Get("bind"); if (v.IsFunction) { var fn = v.AsFunction(); if (fn != null) fn.NativeLength = 1; } }
            { var v = functionProto.Get("toString"); if (v.IsFunction) { var fn = v.AsFunction(); if (fn != null) fn.NativeLength = 0; } }


            // Symbol (Refactored to use JsSymbol primitive type)
            var symbolProto = new FenObject();
            symbolProto.InternalClass = "Symbol";

            // Symbol.prototype.description getter
            symbolProto.DefineOwnProperty("description", PropertyDescriptor.Accessor(
                new FenFunction("get description", (args, thisVal) =>
                {
                    if (!thisVal.IsSymbol) return FenValue.Undefined;
                    var sym = thisVal.AsSymbol();
                    return sym != null && sym.Description != null ? FenValue.FromString(sym.Description) : FenValue.Undefined;
                }), null, enumerable: false, configurable: true));

            // Symbol.prototype.toString()
            symbolProto.SetBuiltin("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                if (!thisVal.IsSymbol) throw new FenTypeError("TypeError: Symbol.prototype.toString requires that 'this' be a Symbol");
                return FenValue.FromString(thisVal.AsSymbol().ToString());
            })));

            // Symbol.prototype.valueOf()
            symbolProto.SetBuiltin("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) =>
            {
                if (!thisVal.IsSymbol) throw new FenTypeError("TypeError: Symbol.prototype.valueOf requires that 'this' be a Symbol");
                return thisVal; 
            })));

            // Symbol() constructor
            var symbolCtor = new FenFunction("Symbol", (args, thisVal) =>
            {
                if (!thisVal.IsUndefined && thisVal.IsObject)
                    throw new FenTypeError("TypeError: Symbol is not a constructor");
                
                var description = args.Length > 0 && !args[0].IsUndefined ? args[0].AsString(_context) : null;
                return FenValue.FromSymbol(JsSymbol.Create(description));
            });

            symbolCtor.Set("prototype", FenValue.FromObject(symbolProto));
            symbolProto.SetBuiltin("constructor", FenValue.FromFunction(symbolCtor));

            // Symbol.for(key)
            symbolCtor.Set("for", FenValue.FromFunction(new FenFunction("for", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0].AsString(_context) : "undefined";
                return FenValue.FromSymbol(JsSymbol.For(key));
            })));

            // Symbol.keyFor(symbol)
            symbolCtor.Set("keyFor", FenValue.FromFunction(new FenFunction("keyFor", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsSymbol) return FenValue.Undefined; // Spec throws TypeError, but skipping for safety
                var sym = args[0].AsSymbol();
                if (sym != null)
                {
                    var key = JsSymbol.KeyFor(sym);
                    return key != null ? FenValue.FromString(key) : FenValue.Undefined;
                }
                return FenValue.Undefined;
            })));

            // Well-known symbols
            symbolCtor.Set("iterator", FenValue.FromSymbol(JsSymbol.Iterator));
            symbolCtor.Set("toStringTag", FenValue.FromSymbol(JsSymbol.ToStringTag));
            symbolCtor.Set("toPrimitive", FenValue.FromSymbol(JsSymbol.ToPrimitive));
            symbolCtor.Set("hasInstance", FenValue.FromSymbol(JsSymbol.HasInstance));
            symbolCtor.Set("isConcatSpreadable", FenValue.FromSymbol(JsSymbol.IsConcatSpreadable));
            symbolCtor.Set("species", FenValue.FromSymbol(JsSymbol.Species));
            symbolCtor.Set("match", FenValue.FromSymbol(JsSymbol.Match));
            symbolCtor.Set("replace", FenValue.FromSymbol(JsSymbol.Replace));
            symbolCtor.Set("search", FenValue.FromSymbol(JsSymbol.Search));
            symbolCtor.Set("split", FenValue.FromSymbol(JsSymbol.Split));
            symbolCtor.Set("asyncIterator", FenValue.FromSymbol(JsSymbol.AsyncIterator));
            symbolCtor.Set("dispose", FenValue.FromSymbol(JsSymbol.Dispose));
            symbolCtor.Set("asyncDispose", FenValue.FromSymbol(JsSymbol.AsyncDispose));

            SetGlobal("Symbol", FenValue.FromFunction(symbolCtor));

            // Shared Iterator prototype Ã¢â‚¬â€ declared here so array/string iterator instances can use it as their prototype.
            // Methods (map, filter, etc.) are attached after MakeIteratorObject is defined below (~line 3440).
            FenObject iteratorProto = new FenObject();
            iteratorProto.InternalClass = "Iterator";

            var arrayIteratorProto = new FenObject();
            arrayIteratorProto.InternalClass = "Iterator";
            arrayIteratorProto.SetPrototype(iteratorProto);
            arrayIteratorProto.DefineOwnProperty(JsSymbol.ToStringTag.ToPropertyKey(), new PropertyDescriptor { Value = FenValue.FromString("Array Iterator"), Writable = false, Enumerable = false, Configurable = true });

            var stringIteratorProto = new FenObject();
            stringIteratorProto.InternalClass = "Iterator";
            stringIteratorProto.SetPrototype(iteratorProto);
            stringIteratorProto.DefineOwnProperty(JsSymbol.ToStringTag.ToPropertyKey(), new PropertyDescriptor { Value = FenValue.FromString("String Iterator"), Writable = false, Enumerable = false, Configurable = true });

            var mapIteratorProto = new FenObject();
            mapIteratorProto.InternalClass = "Iterator";
            mapIteratorProto.SetPrototype(iteratorProto);
            mapIteratorProto.DefineOwnProperty(JsSymbol.ToStringTag.ToPropertyKey(), new PropertyDescriptor { Value = FenValue.FromString("Map Iterator"), Writable = false, Enumerable = false, Configurable = true });

            var setIteratorProto = new FenObject();
            setIteratorProto.InternalClass = "Iterator";
            setIteratorProto.SetPrototype(iteratorProto);
            setIteratorProto.DefineOwnProperty(JsSymbol.ToStringTag.ToPropertyKey(), new PropertyDescriptor { Value = FenValue.FromString("Set Iterator"), Writable = false, Enumerable = false, Configurable = true });
            // Share with all runtime iterator paths.
            FenObject.DefaultIteratorPrototype = iteratorProto;
            // MakeIteratorObject delegate declared here; assigned after the iterator section.
            Func<IEnumerable<FenValue>, FenObject> MakeIteratorObject = null;

            // Array Iterator
            arrayProto.SetBuiltin("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("@@iterator", (args, thisVal) =>
            {
                var arr = thisVal.AsObject();
                if (arr == null) return FenValue.Undefined;

                var index = 0;
                var iterator = new FenObject();

                // Iterator next() method
                iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (nextArgs, nextThis) =>
                {
                    var result = new FenObject();

                    if (index < (int)arr.Get("length").ToNumber())
                    {
                        result.Set("value", arr.Get(index.ToString()));
                        result.Set("done", FenValue.FromBoolean(false));
                        index++;
                    }
                    else
                    {
                        result.Set("value", FenValue.Undefined);
                        result.Set("done", FenValue.FromBoolean(true));
                    }

                    return FenValue.FromObject(result);
                })));

                iterator.SetPrototype(arrayIteratorProto);
                return FenValue.FromObject(iterator);
            })));

            // String Iterator
            stringProto.SetBuiltin("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("@@iterator", (args, thisVal) =>
            {
                var str = thisVal.AsString(_context);

                var index = 0;
                var iterator = new FenObject();

                // Iterator next() method
                iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (nextArgs, nextThis) =>
                {
                    var result = new FenObject();

                    if (index < str.Length)
                    {
                        result.Set("value", FenValue.FromString(str[index].ToString()));
                        result.Set("done", FenValue.FromBoolean(false));
                        index++;
                    }
                    else
                    {
                        result.Set("value", FenValue.Undefined);
                        result.Set("done", FenValue.FromBoolean(true));
                    }

                    return FenValue.FromObject(result);
                })));

                iterator.SetPrototype(stringIteratorProto);
                return FenValue.FromObject(iterator);
            })));
            window.Set("Symbol", FenValue.FromFunction(symbolCtor));
            // RegExp
            var regexpProto = new FenObject();
            var regexpCtor = new FenFunction("RegExp", (args, thisVal) =>
            {
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
            regexpProto.SetBuiltin("constructor", FenValue.FromFunction(regexpCtor));

            // RegExp.prototype.test(str)
            regexpProto.SetBuiltin("test", FenValue.FromFunction(new FenFunction("test", (args, thisVal) =>
            {
                if (!thisVal.IsObject) return FenValue.FromBoolean(false);
                var obj = thisVal.AsObject();
                var regex = (obj as FenObject)?.NativeObject as System.Text.RegularExpressions.Regex;
                if (regex == null) return FenValue.FromBoolean(false);
                var str = args.Length > 0 ? args[0].AsString(_context) : "";
                return FenValue.FromBoolean(regex.IsMatch(str));
            })));

            // RegExp.prototype.exec(str)
            regexpProto.SetBuiltin("exec", FenValue.FromFunction(new FenFunction("exec", (args, thisVal) =>
            {
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
            var dateCtor = new FenFunction("Date", (args, thisVal) =>
            {
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
                        dt = new DateTime(year, month, day, hour, minute, second, ms, DateTimeKind.Local).ToUniversalTime();
                    }
                    catch
                    {
                        dt = DateTime.MinValue; // Invalid date
                    }
                }

                if (!thisVal.IsUndefined && thisVal.AsObject()?.GetPrototype() == dateProto)
                {
                    thisVal.AsObject().Set("__date__",
                        FenValue.FromNumber(
                            (dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds));
                    return thisVal;
                }

                // Called as function: Date() returns string representation
                return FenValue.FromString(now.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT'K"));
            });

            dateCtor.Set("prototype", FenValue.FromObject(dateProto));
            dateProto.SetBuiltin("constructor", FenValue.FromFunction(dateCtor));

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
                obj.Set("__date__",
                    FenValue.FromNumber((dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds));
            }

            // Date.prototype.getTime()
            dateProto.SetBuiltin("getTime", FenValue.FromFunction(new FenFunction("getTime", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber((GetDate(thisVal) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                        .TotalMilliseconds);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getFullYear()
            dateProto.SetBuiltin("getFullYear", FenValue.FromFunction(new FenFunction("getFullYear", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).ToLocalTime().Year);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getUTCFullYear()
            dateProto.SetBuiltin("getUTCFullYear", FenValue.FromFunction(new FenFunction("getUTCFullYear", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).Year);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getMonth()
            dateProto.SetBuiltin("getMonth", FenValue.FromFunction(new FenFunction("getMonth", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).ToLocalTime().Month - 1);
                } // JS months are 0-indexed
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getUTCMonth()
            dateProto.SetBuiltin("getUTCMonth", FenValue.FromFunction(new FenFunction("getUTCMonth", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).Month - 1);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getDate()
            dateProto.SetBuiltin("getDate", FenValue.FromFunction(new FenFunction("getDate", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).ToLocalTime().Day);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getUTCDate()
            dateProto.SetBuiltin("getUTCDate", FenValue.FromFunction(new FenFunction("getUTCDate", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).Day);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getDay()
            dateProto.SetBuiltin("getDay", FenValue.FromFunction(new FenFunction("getDay", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber((int)GetDate(thisVal).ToLocalTime().DayOfWeek);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getUTCDay()
            dateProto.SetBuiltin("getUTCDay", FenValue.FromFunction(new FenFunction("getUTCDay", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber((int)GetDate(thisVal).DayOfWeek);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getHours()
            dateProto.SetBuiltin("getHours", FenValue.FromFunction(new FenFunction("getHours", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).ToLocalTime().Hour);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getUTCHours()
            dateProto.SetBuiltin("getUTCHours", FenValue.FromFunction(new FenFunction("getUTCHours", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).Hour);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getMinutes()
            dateProto.SetBuiltin("getMinutes", FenValue.FromFunction(new FenFunction("getMinutes", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).ToLocalTime().Minute);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getUTCMinutes()
            dateProto.SetBuiltin("getUTCMinutes", FenValue.FromFunction(new FenFunction("getUTCMinutes", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).Minute);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getTimezoneOffset()
            dateProto.SetBuiltin("getTimezoneOffset", FenValue.FromFunction(new FenFunction("getTimezoneOffset", (args, thisVal) =>
            {
                try
                {
                    var dt = GetDate(thisVal);
                    var offset = TimeZoneInfo.Local.GetUtcOffset(dt);
                    return FenValue.FromNumber(-offset.TotalMinutes);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getSeconds()
            dateProto.SetBuiltin("getSeconds", FenValue.FromFunction(new FenFunction("getSeconds", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).ToLocalTime().Second);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getUTCSeconds()
            dateProto.SetBuiltin("getUTCSeconds", FenValue.FromFunction(new FenFunction("getUTCSeconds", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).Second);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getMilliseconds()
            dateProto.SetBuiltin("getMilliseconds", FenValue.FromFunction(new FenFunction("getMilliseconds", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber(GetDate(thisVal).ToLocalTime().Millisecond);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.getUTCMilliseconds()
            dateProto.SetBuiltin("getUTCMilliseconds", FenValue.FromFunction(new FenFunction("getUTCMilliseconds",
                (args, thisVal) =>
                {
                    try
                    {
                        return FenValue.FromNumber(GetDate(thisVal).Millisecond);
                    }
                    catch
                    {
                        return FenValue.FromNumber(double.NaN);
                    }
                })));

            // Date.prototype.setTime()
            dateProto.SetBuiltin("setTime", FenValue.FromFunction(new FenFunction("setTime", (args, thisVal) =>
            {
                try
                {
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
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.setFullYear()
            dateProto.SetBuiltin("setFullYear", FenValue.FromFunction(new FenFunction("setFullYear", (args, thisVal) =>
            {
                try
                {
                    var dt = GetDate(thisVal);
                    var year = args.Length > 0 ? (int)args[0].ToNumber() : dt.Year;
                    var month = args.Length > 1 ? (int)args[1].ToNumber() + 1 : dt.Month;
                    var day = args.Length > 2 ? (int)args[2].ToNumber() : dt.Day;
                    dt = new DateTime(year, month, day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond,
                        DateTimeKind.Utc);
                    SetDate(thisVal, dt);
                    return FenValue.FromNumber((dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                        .TotalMilliseconds);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.setMonth()
            dateProto.SetBuiltin("setMonth", FenValue.FromFunction(new FenFunction("setMonth", (args, thisVal) =>
            {
                try
                {
                    var dt = GetDate(thisVal);
                    var month = args.Length > 0 ? (int)args[0].ToNumber() + 1 : dt.Month;
                    var day = args.Length > 1 ? (int)args[1].ToNumber() : dt.Day;
                    dt = new DateTime(dt.Year, month, day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond,
                        DateTimeKind.Utc);
                    SetDate(thisVal, dt);
                    return FenValue.FromNumber((dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                        .TotalMilliseconds);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.setDate()
            dateProto.SetBuiltin("setDate", FenValue.FromFunction(new FenFunction("setDate", (args, thisVal) =>
            {
                try
                {
                    var dt = GetDate(thisVal);
                    var day = args.Length > 0 ? (int)args[0].ToNumber() : dt.Day;
                    dt = new DateTime(dt.Year, dt.Month, day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond,
                        DateTimeKind.Utc);
                    SetDate(thisVal, dt);
                    return FenValue.FromNumber((dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                        .TotalMilliseconds);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.valueOf()
            dateProto.SetBuiltin("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) =>
            {
                try
                {
                    return FenValue.FromNumber((GetDate(thisVal) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                        .TotalMilliseconds);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            // Date.prototype.toString()
            dateProto.SetBuiltin("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                try
                {
                    var dt = GetDate(thisVal);
                    if (dt == DateTime.MinValue) return FenValue.FromString("Invalid Date");
                    return FenValue.FromString(dt.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT'K"));
                }
                catch
                {
                    return FenValue.FromString("Invalid Date");
                }
            })));

            // Date.prototype.toISOString()
            dateProto.SetBuiltin("toISOString", FenValue.FromFunction(new FenFunction("toISOString", (args, thisVal) =>
            {
                try
                {
                    var dt = GetDate(thisVal);
                    if (dt == DateTime.MinValue) throw new FenRangeError("RangeError: Invalid time value");
                    return FenValue.FromString(dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                }
                catch
                {
                    throw new FenRangeError("RangeError: Invalid time value");
                }
            })));

            // Annex B: Date.prototype.getYear() Ã¢â‚¬â€ returns year - 1900 for years in range
            {
                var fn = new FenFunction("getYear", (args, thisVal) =>
                {
                    var dt = GetDate(thisVal); // throws TypeError if not a Date
                    if (dt == DateTime.MinValue) return FenValue.FromNumber(double.NaN);
                    var local = dt.ToLocalTime();
                    return FenValue.FromNumber(local.Year - 1900);
                });
                fn.IsConstructor = false;
                dateProto.SetBuiltin("getYear", FenValue.FromFunction(fn));
            }

            // Annex B: Date.prototype.setYear(year) Ã¢â‚¬â€ sets full year = year < 100 ? year+1900 : year
            {
                var fn = new FenFunction("setYear", (args, thisVal) =>
                {
                    var dt = GetDate(thisVal); // throws TypeError if not a Date
                    var local = dt == DateTime.MinValue ? DateTime.Now : dt.ToLocalTime();
                    if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                    int y = (int)args[0].ToNumber();
                    if (y >= 0 && y <= 99) y += 1900;
                    try
                    {
                        var newDt = new DateTime(y, local.Month, local.Day, local.Hour, local.Minute, local.Second, local.Millisecond, DateTimeKind.Local);
                        SetDate(thisVal, newDt.ToUniversalTime());
                        return FenValue.FromNumber((newDt.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
                    }
                    catch { return FenValue.FromNumber(double.NaN); }
                });
                fn.IsConstructor = false;
                fn.NativeLength = 1;
                dateProto.SetBuiltin("setYear", FenValue.FromFunction(fn));
            }

            // Annex B: Date.prototype.toGMTString() Ã¢â‚¬â€ alias for toUTCString
            {
                var fn = new FenFunction("toGMTString", (args, thisVal) =>
                {
                    var dt = GetDate(thisVal); // throws TypeError if not a Date
                    if (dt == DateTime.MinValue) return FenValue.FromString("Invalid Date");
                    return FenValue.FromString(dt.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'"));
                });
                fn.IsConstructor = false;
                dateProto.SetBuiltin("toGMTString", FenValue.FromFunction(fn));
            }

            // Date static methods
            dateCtor.Set("now",
                FenValue.FromFunction(new FenFunction("now",
                    (args, thisVal) =>
                    {
                        return FenValue.FromNumber(
                            (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
                    })));

            dateCtor.Set("parse", FenValue.FromFunction(new FenFunction("parse", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                var str = args[0].AsString(_context);
                if (DateTime.TryParse(str, out var dt))
                    return FenValue.FromNumber(
                        (dt.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
                return FenValue.FromNumber(double.NaN);
            })));

            dateCtor.Set("UTC", FenValue.FromFunction(new FenFunction("UTC", (args, thisVal) =>
            {
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
                    var dt = new DateTime(year, month, day, hour, minute, second, ms, DateTimeKind.Local).ToUniversalTime();
                    return FenValue.FromNumber((dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                        .TotalMilliseconds);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));

            SetGlobal("Date", FenValue.FromFunction(dateCtor));
            window.Set("Date", FenValue.FromFunction(dateCtor));

            errorProto.SetBuiltin("name", FenValue.FromString("Error"));
            errorProto.SetBuiltin("message", FenValue.FromString(""));
            errorProto.SetBuiltin("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
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
                    var opts = args[1].AsObject();
                    if (opts.Has("cause"))
                    {
                        err.Set("cause", opts.Get("cause"));
                    }
                }

                return err;
            }

            // --- REFACTOR: Unified Constructor Registration ---
            // This ensures built-in constructors are typeof 'function' and have correct prototype linkage.
            void RegisterConstructor(string name, FenObject prototype, Func<FenValue[], FenValue, FenValue> ctorLogic,
                FenObject staticMembers = null)
            {
                var ctor = new FenFunction(name, (args, thisVal) =>
                {
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
            errorProto.SetBuiltin("constructor", FenValue.FromFunction(errorCtor));
            SetGlobal("Error", FenValue.FromFunction(errorCtor));

            // 3. Subtypes Definitions
            void DefineErrorType(string name, FenObject parentProto)
            {
                var proto = new FenObject();
                proto.SetPrototype(parentProto);
                proto.SetBuiltin("name", FenValue.FromString(name));

                var ctor = new FenFunction(name, (args, thisVal) =>
                {
                    var message = args.Length > 0 ? args[0].ToString() : "";
                    return FenValue.FromObject(MakeError(name, message, args, proto));
                });
                ctor.Prototype = proto;
                ctor.Set("prototype", FenValue.FromObject(proto));
                proto.SetBuiltin("constructor", FenValue.FromFunction(ctor));
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
            aggProto.SetBuiltin("name", FenValue.FromString("AggregateError"));
            var aggCtor = new FenFunction("AggregateError", (args, thisVal) =>
            {
                var message = args.Length > 1 ? args[1].ToString() : "";
                FenValue[] errorArgs;
                if (args.Length > 2)
                {
                    errorArgs = new[] { args[1], args[2] };
                }
                else if (args.Length > 1)
                {
                    errorArgs = new[] { args[1] };
                }
                else
                {
                    errorArgs = Array.Empty<FenValue>();
                }

                var err = MakeError("AggregateError", message, errorArgs, aggProto);
                var errors = args.Length > 0 ? args[0] : FenValue.Undefined;
                err.Set("errors", errors);
                return FenValue.FromObject(err);
            });
            aggCtor.Prototype = aggProto;
            aggCtor.Set("prototype", FenValue.FromObject(aggProto));
            SetGlobal("AggregateError", FenValue.FromFunction(aggCtor));


            // document object - Bridge to DOM
            var document = new FenObject();
            document.Set("getElementById", FenValue.FromFunction(new FenFunction("getElementById",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (_domBridge == null) return FenValue.Null;
                    if (args.Length == 0) return FenValue.Null;
                    return _domBridge.GetElementById(args[0].ToString());
                })));
            document.Set("querySelector", FenValue.FromFunction(new FenFunction("querySelector",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (_domBridge == null) return FenValue.Null;
                    if (args.Length == 0) return FenValue.Null;
                    return _domBridge.QuerySelector(args[0].ToString());
                })));
            document.Set("getElementsByTagName", FenValue.FromFunction(new FenFunction("getElementsByTagName",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (_domBridge == null) return FenValue.FromObject(new FenObject());
                    var tagName = args.Length > 0 ? args[0].ToString() : "*";
                    return _domBridge.GetElementsByTagName(tagName);
                })));
            document.Set("getElementsByClassName", FenValue.FromFunction(new FenFunction("getElementsByClassName",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (_domBridge == null) return FenValue.FromObject(new FenObject());
                    var classNames = args.Length > 0 ? args[0].ToString() : "";
                    return _domBridge.GetElementsByClassName(classNames);
                })));
            document.Set("createElement", FenValue.FromFunction(new FenFunction("createElement",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (_domBridge == null) return FenValue.Null;
                    if (args.Length == 0) return FenValue.Null;
                    return _domBridge.CreateElement(args[0].ToString());
                })));
            document.Set("createElementNS", FenValue.FromFunction(new FenFunction("createElementNS",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (_domBridge == null) return FenValue.Null;
                    var namespaceUri = args.Length > 0 ? args[0].ToString() : "";
                    var qualifiedName = args.Length > 1 ? args[1].ToString() : "";
                    return _domBridge.CreateElementNS(namespaceUri, qualifiedName);
                })));

            document.Set("createTextNode", FenValue.FromFunction(new FenFunction("createTextNode",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (_domBridge == null) return FenValue.Null;
                    var text = args.Length > 0 ? args[0].ToString() : "";
                    return _domBridge.CreateTextNode(text);
                })));

            // Basic document element references used by WPT harness and DOM tests.
            if (_domBridge != null)
            {
                document.Set("body", _domBridge.QuerySelector("body"));
                document.Set("head", _domBridge.QuerySelector("head"));
                document.Set("documentElement", _domBridge.QuerySelector("html"));
            }
            else
            {
                document.Set("body", FenValue.Null);
                document.Set("head", FenValue.Null);
                document.Set("documentElement", FenValue.Null);
            }

            SetGlobal("document", FenValue.FromObject(document));

            // console object
            var console = new FenObject();
            FenLogger.Debug("[FenRuntime] Creating console object...", LogCategory.JavaScript);
            console.Set("log", FenValue.FromFunction(new FenFunction("log", (FenValue[] args, FenValue thisVal) =>
            {
                try
                {
                    FenLogger.Debug("[FenRuntime] console.log invoked from JS", LogCategory.JavaScript);
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                var messages = new List<string>();
                foreach (var arg in args) messages.Add(arg.ToString());
                var msg = string.Join(" ", messages);
                Console.WriteLine(msg);
                // /* [PERF-REMOVED] */
                try
                {
                    FenLogger.Debug($"[FenRuntime] Console.log: {msg}", LogCategory.JavaScript);
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                try
                {
                    if (OnConsoleMessage == null)
                        FenLogger.Error("[FenRuntime] OnConsoleMessage is NULL!", LogCategory.JavaScript);
                    else FenLogger.Debug("[FenRuntime] Invoking OnConsoleMessage...", LogCategory.JavaScript);
                    OnConsoleMessage?.Invoke(msg);
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[FenRuntime] OnConsoleMessage error: {ex}", LogCategory.JavaScript);
                }

                return FenValue.Undefined;
            })));
            console.Set("error", FenValue.FromFunction(new FenFunction("error", (FenValue[] args, FenValue thisVal) =>
            {
                var msg = string.Join(" ", args.Select(a => a.ToString()));
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR: {msg}");
                Console.ResetColor();
                // /* [PERF-REMOVED] */
                try
                {
                    FenLogger.Error($"[FenRuntime] Console.error: {msg}", LogCategory.JavaScript);
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                try
                {
                    OnConsoleMessage?.Invoke($"[Error] {msg}");
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                return FenValue.Undefined;
            })));
            console.Set("warn", FenValue.FromFunction(new FenFunction("warn", (FenValue[] args, FenValue thisVal) =>
            {
                var msg = string.Join(" ", args.Select(a => a.ToString()));
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"WARN: {msg}");
                Console.ResetColor();
                // /* [PERF-REMOVED] */
                try
                {
                    FenLogger.Info($"[FenRuntime] Console.warn: {msg}", LogCategory.JavaScript);
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                try
                {
                    OnConsoleMessage?.Invoke($"[Warn] {msg}");
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                return FenValue.Undefined;
            })));
            console.Set("info", FenValue.FromFunction(new FenFunction("info", (FenValue[] args, FenValue thisVal) =>
            {
                var msg = string.Join(" ", args.Select(a => a.ToString()));
                Console.WriteLine($"INFO: {msg}");
                // /* [PERF-REMOVED] */
                try
                {
                    FenLogger.Info($"[FenRuntime] Console.info: {msg}", LogCategory.JavaScript);
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                try
                {
                    OnConsoleMessage?.Invoke($"[Info] {msg}");
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                return FenValue.Undefined;
            })));
            console.Set("clear", FenValue.FromFunction(new FenFunction("clear", (FenValue[] args, FenValue thisVal) =>
            {
                Console.Clear();
                try
                {
                    OnConsoleMessage?.Invoke("[Clear]");
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                return FenValue.Undefined;
            })));

            // console.dir - Object inspection
            console.Set("dir", FenValue.FromFunction(new FenFunction("dir", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                var obj = args[0];
                var output = InspectObject(obj, 0);
                Console.WriteLine(output);
                try
                {
                    FenLogger.Debug($"[FenRuntime] Console.dir: {output}", LogCategory.JavaScript);
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                try
                {
                    OnConsoleMessage?.Invoke($"[Dir] {output}");
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                return FenValue.Undefined;
            })));

            // console.table - Tabular data display
            console.Set("table", FenValue.FromFunction(new FenFunction("table", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                var obj = args[0];
                var output = $"[Table] {obj}"; // Simplified - full table formatting would be complex
                Console.WriteLine(output);
                try
                {
                    FenLogger.Debug($"[FenRuntime] Console.table: {output}", LogCategory.JavaScript);
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                try
                {
                    OnConsoleMessage?.Invoke(output);
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                return FenValue.Undefined;
            })));

            // console.group / groupEnd - Indentation
            int _consoleGroupLevel = 0;
            console.Set("group", FenValue.FromFunction(new FenFunction("group", (FenValue[] args, FenValue thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "";
                _consoleGroupLevel++;
                var indent = new string(' ', _consoleGroupLevel * 2);
                var msg = $"{indent}Ã¢â€“Â¼ {label}";
                Console.WriteLine(msg);
                try
                {
                    OnConsoleMessage?.Invoke($"[Group] {label}");
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                return FenValue.Undefined;
            })));

            console.Set("groupCollapsed", FenValue.FromFunction(new FenFunction("groupCollapsed",
                (FenValue[] args, FenValue thisVal) =>
                {
                    var label = args.Length > 0 ? args[0].ToString() : "";
                    _consoleGroupLevel++;
                    var indent = new string(' ', _consoleGroupLevel * 2);
                    var msg = $"{indent}Ã¢â€“Â¶ {label}";
                    Console.WriteLine(msg);
                    try
                    {
                        OnConsoleMessage?.Invoke($"[GroupCollapsed] {label}");
                    }
                    catch
                    {
                    }

                    return FenValue.Undefined;
                })));

            console.Set("groupEnd", FenValue.FromFunction(new FenFunction("groupEnd",
                (FenValue[] args, FenValue thisVal) =>
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

            console.Set("timeEnd", FenValue.FromFunction(new FenFunction("timeEnd",
                (FenValue[] args, FenValue thisVal) =>
                {
                    var label = args.Length > 0 ? args[0].ToString() : "default";
                    if (_consoleTimers.TryGetValue(label, out var start))
                    {
                        var elapsed = (DateTime.Now - start).TotalMilliseconds;
                        var msg = $"{label}: {elapsed:F2}ms";
                        Console.WriteLine(msg);
                        try
                        {
                            OnConsoleMessage?.Invoke($"[Timer] {msg}");
                        }
                        catch
                        {
                        }

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
                try
                {
                    OnConsoleMessage?.Invoke($"[Count] {msg}");
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                return FenValue.Undefined;
            })));

            console.Set("countReset", FenValue.FromFunction(new FenFunction("countReset",
                (FenValue[] args, FenValue thisVal) =>
                {
                    var label = args.Length > 0 ? args[0].ToString() : "default";
                    _consoleCounts[label] = 0;
                    return FenValue.Undefined;
                })));

            // console.assert
            console.Set("assert", FenValue.FromFunction(new FenFunction("assert", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0 || args[0].ToBoolean()) return FenValue.Undefined;
                var msg = args.Length > 1
                    ? string.Join(" ", args.Skip(1).Select(a => a.ToString()))
                    : "Assertion failed";
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Assertion failed: {msg}");
                Console.ResetColor();
                try
                {
                    FenLogger.Error($"[FenRuntime] Console.assert: {msg}", LogCategory.JavaScript);
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                try
                {
                    OnConsoleMessage?.Invoke($"[Assert] {msg}");
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                return FenValue.Undefined;
            })));

            // console.trace
            console.Set("trace", FenValue.FromFunction(new FenFunction("trace", (FenValue[] args, FenValue thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "Trace";
                var stack = Environment.StackTrace;
                Console.WriteLine($"{label}\n{stack}");
                try
                {
                    OnConsoleMessage?.Invoke($"[Trace] {label}");
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

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

            var clearTimeout = FenValue.FromFunction(new FenFunction("clearTimeout",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length > 0) CancelTimer((int)args[0].ToNumber());
                    return FenValue.Undefined;
                }));
            SetGlobal("clearTimeout", clearTimeout);

            var setInterval = FenValue.FromFunction(new FenFunction("setInterval",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length == 0 || !args[0].IsFunction) return FenValue.FromNumber(0);
                    var callback = args[0].AsFunction();
                    int delay = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                    var callbackArgs = args.Skip(2).ToArray();

                    return CreateTimer(callback, delay, true, callbackArgs);
                }));
            SetGlobal("setInterval", setInterval);

            var clearInterval = FenValue.FromFunction(new FenFunction("clearInterval",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length > 0) CancelTimer((int)args[0].ToNumber());
                    return FenValue.Undefined;
                }));
            SetGlobal("clearInterval", clearInterval);

            // requestAnimationFrame
            var requestAnimationFrame = FenValue.FromFunction(new FenFunction("requestAnimationFrame",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length == 0 || !args[0].IsFunction) return FenValue.FromNumber(0);
                    return CreateAnimationFrame(args[0].AsFunction());
                }));
            SetGlobal("requestAnimationFrame", requestAnimationFrame);

            // cancelAnimationFrame
            var cancelAnimationFrame = FenValue.FromFunction(new FenFunction("cancelAnimationFrame",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length > 0) CancelTimer((int)args[0].ToNumber());
                    return FenValue.Undefined;
                }));
            SetGlobal("cancelAnimationFrame", cancelAnimationFrame);

            // queueMicrotask
            var queueMicrotask = FenValue.FromFunction(new FenFunction("queueMicrotask",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length == 0 || !args[0].IsFunction)
                        throw new FenTypeError("queueMicrotask requires a function argument.");

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

            // (Symbol API moved to line 2106)

            // ES2025: Iterator global with helpers
            // Iterator.from(iterable) creates a lazy iterator wrapper with map/filter/take/drop etc.
            var iteratorCtor = new FenFunction("Iterator", (args, thisVal) => FenValue.Undefined);

            // Helper: build an iterator object (MakeIteratorObject was declared above near the array/string iterators).
            // Each returned object has only 'next' on the instance; all other helpers inherit from iteratorProto.
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

                // Inherit all helpers (map, filter, etc.) and [Symbol.iterator] from the shared prototype
                iter.SetPrototype(iteratorProto);
                return iter;
            };

            // Helper: drain an iterator object (via its next()) into a C# IEnumerable.
            // Used by the iteratorProto methods below.
            IEnumerable<FenValue> DrainIteratorObj(FenValue self)
            {
                var selfObj = self.AsObject() as FenObject;
                if (selfObj == null) yield break;
                var nxtVal = selfObj.Get("next");
                if (!nxtVal.IsFunction) yield break;
                var nxtFn = nxtVal.AsFunction();
                while (true)
                {
                    var r = nxtFn.Invoke(Array.Empty<FenValue>(), null);
                    var rObj = r.AsObject() as FenObject;
                    if (rObj == null || rObj.Get("done").ToBoolean()) yield break;
                    yield return rObj.Get("value");
                }
            }

            // Ã¢â€â‚¬Ã¢â€â‚¬ iteratorProto methods Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            // All iterators (from MakeIteratorObject, array iterator, string iterator) share
            // this prototype so that Test262 prototype-chain checks pass.

            // [Symbol.iterator] Ã¢â‚¬â€ returns this (self-iterable protocol)
            iteratorProto.SetBuiltin("[Symbol.iterator]",
                FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, t) => t)));
            iteratorProto.DefineOwnProperty(JsSymbol.ToStringTag.ToPropertyKey(), new PropertyDescriptor
            {
                Value = FenValue.FromString("Iterator"),
                Writable = false,
                Enumerable = false,
                Configurable = true
            });

            // map Ã¢â‚¬â€ lazy transform
            iteratorProto.SetBuiltin("map", FenValue.FromFunction(new FenFunction("map", (a, t) =>
            {
                var fn = a.Length > 0 ? a[0].AsFunction() : null;
                if (fn == null) return FenValue.Undefined;
                var captured = t; // capture by value (FenValue is a struct)
                int i = 0;
                IEnumerable<FenValue> Mapped()
                {
                    foreach (var v in DrainIteratorObj(captured))
                        yield return fn.Invoke(new FenValue[] { v, FenValue.FromNumber(i++) }, null);
                }
                return FenValue.FromObject(MakeIteratorObject(Mapped()));
            })));

            // filter Ã¢â‚¬â€ lazy predicate
            iteratorProto.SetBuiltin("filter", FenValue.FromFunction(new FenFunction("filter", (a, t) =>
            {
                var fn = a.Length > 0 ? a[0].AsFunction() : null;
                if (fn == null) return FenValue.Undefined;
                var captured = t;
                IEnumerable<FenValue> Filtered()
                {
                    foreach (var v in DrainIteratorObj(captured))
                        if (fn.Invoke(new FenValue[] { v }, null).ToBoolean())
                            yield return v;
                }
                return FenValue.FromObject(MakeIteratorObject(Filtered()));
            })));

            // take Ã¢â‚¬â€ first n elements
            iteratorProto.SetBuiltin("take", FenValue.FromFunction(new FenFunction("take", (a, t) =>
            {
                int n = a.Length > 0 ? (int)a[0].ToNumber() : 0;
                var captured = t;
                IEnumerable<FenValue> Taken()
                {
                    int count = 0;
                    foreach (var v in DrainIteratorObj(captured))
                    {
                        if (count++ >= n) break;
                        yield return v;
                    }
                }
                return FenValue.FromObject(MakeIteratorObject(Taken()));
            })));

            // drop Ã¢â‚¬â€ skip first n elements
            iteratorProto.SetBuiltin("drop", FenValue.FromFunction(new FenFunction("drop", (a, t) =>
            {
                int n = a.Length > 0 ? (int)a[0].ToNumber() : 0;
                var captured = t;
                IEnumerable<FenValue> Dropped()
                {
                    int skipped = 0;
                    foreach (var v in DrainIteratorObj(captured))
                    {
                        if (skipped++ < n) continue;
                        yield return v;
                    }
                }
                return FenValue.FromObject(MakeIteratorObject(Dropped()));
            })));

            // flatMap Ã¢â‚¬â€ map + flatten one level
            iteratorProto.SetBuiltin("flatMap", FenValue.FromFunction(new FenFunction("flatMap", (a, t) =>
            {
                var fn = a.Length > 0 ? a[0].AsFunction() : null;
                if (fn == null) return FenValue.Undefined;
                var captured = t;
                IEnumerable<FenValue> FlatMapped()
                {
                    foreach (var v in DrainIteratorObj(captured))
                    {
                        var mapped = fn.Invoke(new FenValue[] { v }, null);
                        var innerObj = mapped.IsObject ? mapped.AsObject() as FenObject : null;
                        if (innerObj != null)
                        {
                            var nv = innerObj.Get("next");
                            if (nv.IsFunction)
                            {
                                foreach (var inner in DrainIteratorObj(mapped)) yield return inner;
                                continue;
                            }
                            var lv = innerObj.Get("length");
                            if (lv.IsNumber)
                            {
                                int l = (int)lv.ToNumber();
                                for (int j = 0; j < l; j++) yield return innerObj.Get(j.ToString());
                                continue;
                            }
                        }
                        yield return mapped;
                    }
                }
                return FenValue.FromObject(MakeIteratorObject(FlatMapped()));
            })));

            // toArray Ã¢â‚¬â€ terminal: collect all into an Array
            iteratorProto.SetBuiltin("toArray", FenValue.FromFunction(new FenFunction("toArray", (a, t) =>
            {
                var arr = FenObject.CreateArray();
                int i = 0;
                foreach (var v in DrainIteratorObj(t)) arr.Set((i++).ToString(), v);
                arr.Set("length", FenValue.FromNumber(i));
                return FenValue.FromObject(arr);
            })));

            // forEach Ã¢â‚¬â€ terminal: call fn for each
            iteratorProto.SetBuiltin("forEach", FenValue.FromFunction(new FenFunction("forEach", (a, t) =>
            {
                var fn = a.Length > 0 ? a[0].AsFunction() : null;
                if (fn == null) return FenValue.Undefined;
                int i = 0;
                foreach (var v in DrainIteratorObj(t)) fn.Invoke(new FenValue[] { v, FenValue.FromNumber(i++) }, null);
                return FenValue.Undefined;
            })));

            // reduce Ã¢â‚¬â€ terminal: fold
            iteratorProto.SetBuiltin("reduce", FenValue.FromFunction(new FenFunction("reduce", (a, t) =>
            {
                var fn = a.Length > 0 ? a[0].AsFunction() : null;
                if (fn == null) return FenValue.Undefined;
                FenValue acc = a.Length > 1 ? a[1] : FenValue.Undefined;
                bool hasInit = a.Length > 1;
                foreach (var v in DrainIteratorObj(t))
                {
                    if (!hasInit) { acc = v; hasInit = true; continue; }
                    acc = fn.Invoke(new FenValue[] { acc, v }, null);
                }
                return acc;
            })));

            // some Ã¢â‚¬â€ terminal: short-circuit OR
            iteratorProto.SetBuiltin("some", FenValue.FromFunction(new FenFunction("some", (a, t) =>
            {
                var fn = a.Length > 0 ? a[0].AsFunction() : null;
                if (fn == null) return FenValue.FromBoolean(false);
                foreach (var v in DrainIteratorObj(t))
                    if (fn.Invoke(new FenValue[] { v }, null).ToBoolean()) return FenValue.FromBoolean(true);
                return FenValue.FromBoolean(false);
            })));

            // every Ã¢â‚¬â€ terminal: short-circuit AND
            iteratorProto.SetBuiltin("every", FenValue.FromFunction(new FenFunction("every", (a, t) =>
            {
                var fn = a.Length > 0 ? a[0].AsFunction() : null;
                if (fn == null) return FenValue.FromBoolean(true);
                foreach (var v in DrainIteratorObj(t))
                    if (!fn.Invoke(new FenValue[] { v }, null).ToBoolean()) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(true);
            })));

            // find Ã¢â‚¬â€ terminal: first match or undefined
            iteratorProto.SetBuiltin("find", FenValue.FromFunction(new FenFunction("find", (a, t) =>
            {
                var fn = a.Length > 0 ? a[0].AsFunction() : null;
                if (fn == null) return FenValue.Undefined;
                foreach (var v in DrainIteratorObj(t))
                    if (fn.Invoke(new FenValue[] { v }, null).ToBoolean()) return v;
                return FenValue.Undefined;
            })));

            // findIndex Ã¢â‚¬â€ terminal: first matching index or -1
            iteratorProto.SetBuiltin("findIndex", FenValue.FromFunction(new FenFunction("findIndex", (a, t) =>
            {
                var fn = a.Length > 0 ? a[0].AsFunction() : null;
                if (fn == null) return FenValue.FromNumber(-1.0);
                int i = 0;
                foreach (var v in DrainIteratorObj(t))
                {
                    if (fn.Invoke(new FenValue[] { v }, null).ToBoolean()) return FenValue.FromNumber(i);
                    i++;
                }
                return FenValue.FromNumber(-1.0);
            })));

            // Ã¢â€â‚¬Ã¢â€â‚¬ iteratorCtor linkage Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            iteratorProto.SetBuiltin("constructor", FenValue.FromFunction(iteratorCtor));
            iteratorCtor.Set("prototype", FenValue.FromObject(iteratorProto));

            iteratorCtor.Set("from", FenValue.FromFunction(new FenFunction("from", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                var iterable = args[0];

                // If it's already an iterator (has .next), wrap it
                if (iterable.IsObject)
                {
                    var obj = iterable.AsObject() as FenObject;
                    if (obj == null) return FenValue.Undefined;

                    // Check Symbol.iterator first
                    var symIterVal = obj.Get("[Symbol.iterator]");
                    if (symIterVal.IsFunction)
                    {
                        var symIter = symIterVal.AsFunction().Invoke(Array.Empty<FenValue>(),
                            new ExecutionContext { ThisBinding = iterable });
                        var symIterObj = symIter.AsObject() as FenObject;
                        if (symIterObj != null)
                        {
                            return symIter;
                        }
                    }

                    var nextVal = obj.Get("next");
                    var nextFn = nextVal.IsFunction ? nextVal.AsFunction() : null;
                    if (nextFn != null)
                    {
                        IEnumerable<FenValue> FromIterator()
                        {
                            while (true)
                            {
                                var r = nextFn.Invoke(Array.Empty<FenValue>(), null);
                                var rObj = r.AsObject() as FenObject;
                                if (rObj == null || rObj.Get("done").ToBoolean()) yield break;
                                yield return rObj.Get("value");
                            }
                        }

                        return FenValue.FromObject(MakeIteratorObject(FromIterator()));
                    }

                    // Array-like
                    var lenVal = obj.Get("length");
                    if (lenVal.IsNumber)
                    {
                        int len = (int)lenVal.ToNumber();

                        IEnumerable<FenValue> FromArray()
                        {
                            for (int i = 0; i < len; i++) yield return obj.Get(i.ToString());
                        }

                        return FenValue.FromObject(MakeIteratorObject(FromArray()));
                    }
                }

                return FenValue.Undefined;
            })));

            SetGlobal("Iterator", FenValue.FromFunction(iteratorCtor));

            // Dynamic import() function - resolves through the active module loader and returns a real Promise.
            SetGlobal("import", FenValue.FromFunction(new FenFunction("import", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0)
                {
                    return FenValue.FromObject(JsPromise.Reject(
                        FenValue.FromString("TypeError: import() requires a module specifier"),
                        _context));
                }

                if (_context.ModuleLoader == null)
                {
                    return FenValue.FromObject(JsPromise.Reject(
                        FenValue.FromString("TypeError: import() requires an active module loader"),
                        _context));
                }

                var specifier = args[0].ToString();
                var referrer = _context.CurrentModulePath
                    ?? BaseUri?.AbsoluteUri
                    ?? string.Empty;

                try
                {
                    var resolvedPath = _context.ModuleLoader.Resolve(specifier, referrer);
                    var exports = _context.ModuleLoader.LoadModule(resolvedPath);
                    if (exports == null)
                    {
                        return FenValue.FromObject(JsPromise.Reject(
                            FenValue.FromString($"TypeError: import() failed to load module '{specifier}'"),
                            _context));
                    }

                    return FenValue.FromObject(JsPromise.Resolve(FenValue.FromObject(exports), _context));
                }
                catch (Exception ex)
                {
                    return FenValue.FromObject(JsPromise.Reject(FenValue.FromString(ex.Message), _context));
                }
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
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
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
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
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
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
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
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.FromObject(result);
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
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
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
                var prop = ToPropertyKeyString(args[1]);
                return FenValue.FromBoolean(obj.Keys()?.Contains(prop) ?? false);
            })));

            // Object.groupBy(items, callback) - ES2024
            objectConstructor.Set("groupBy", FenValue.FromFunction(new FenFunction("groupBy", (args, thisVal) =>
            {
                if (args.Length < 2)
                {
                    throw new FenTypeError("TypeError: Object.groupBy requires items and callback");
                }

                if (!args[1].IsFunction)
                {
                    throw new FenTypeError("TypeError: Object.groupBy callback must be callable");
                }

                if (args[0].IsNull || args[0].IsUndefined)
                {
                    throw new FenTypeError("TypeError: Object.groupBy called on null or undefined");
                }

                var result = new FenObject();
                var callback = args[1].AsFunction();
                var groups = new Dictionary<string, List<FenValue>>();
                var index = 0;

                void AddToGroup(FenValue item)
                {
                    var keyResult = callback.Invoke(new[] { item, FenValue.FromNumber(index) }, _context);
                    var groupKey = ToPropertyKeyString(keyResult);
                    if (!groups.ContainsKey(groupKey)) groups[groupKey] = new List<FenValue>();
                    groups[groupKey].Add(item);
                    index++;
                }

                if (args[0].IsString)
                {
                    var sourceString = args[0].AsString(_context);
                    for (int i = 0; i < sourceString.Length; i++)
                    {
                        AddToGroup(FenValue.FromString(sourceString[i].ToString()));
                    }
                }
                else if (args[0].IsObject)
                {
                    var items = args[0].AsObject();
                    var iteratorKey = JsSymbol.Iterator?.ToPropertyKey();
                    var iteratorMethod = !string.IsNullOrEmpty(iteratorKey) ? items.Get(iteratorKey, _context) : FenValue.Undefined;
                    if (iteratorMethod.IsFunction)
                    {
                        var iteratorValue = iteratorMethod.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(items));
                        if (!iteratorValue.IsObject)
                        {
                            throw new FenTypeError("TypeError: Object.groupBy iterator is not an object");
                        }

                        var iterator = iteratorValue.AsObject();
                        while (true)
                        {
                            var nextMethod = iterator.Get("next", _context);
                            if (!nextMethod.IsFunction)
                            {
                                throw new FenTypeError("TypeError: Object.groupBy iterator does not provide next()");
                            }

                            var nextValue = nextMethod.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(iterator));
                            if (!nextValue.IsObject)
                            {
                                throw new FenTypeError("TypeError: Object.groupBy iterator result is not an object");
                            }

                            var nextResult = nextValue.AsObject();
                            if (nextResult.Get("done", _context).ToBoolean())
                            {
                                break;
                            }

                            AddToGroup(nextResult.Get("value", _context));
                        }
                    }
                    else
                    {
                        var lenVal = items.Get("length", _context);
                        int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                        for (int i = 0; i < len; i++)
                        {
                            AddToGroup(items.Get(i.ToString(), _context));
                        }
                    }
                }
                else
                {
                    throw new FenTypeError("TypeError: Object.groupBy items must be iterable or array-like");
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
            objectConstructor.Set("seal",
                FenValue.FromFunction(new FenFunction("seal",
                    (args, thisVal) => { return args.Length > 0 ? args[0] : FenValue.Undefined; })));

            // Object.create is already defined earlier (line ~377) with full propertiesObject support.
            // Do NOT re-define it here Ã¢â‚¬â€ the earlier definition handles both args correctly.

            // Object.getPrototypeOf(obj) - ES5
            objectConstructor.Set("getPrototypeOf", FenValue.FromFunction(new FenFunction("getPrototypeOf",
                (args, thisVal) =>
                {
                    if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                    {
                        throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
                    }

                    var obj = CoerceObjectLike(args[0], throwOnNullish: true, out var coercionError);
                    if (!coercionError.IsUndefined)
                    {
                        return coercionError;
                    }

                    var proto = obj?.GetPrototype();
                    return proto != null ? FenValue.FromObject(proto) : FenValue.Null;
                })));            // Object.setPrototypeOf(obj, proto) - ES2015
            objectConstructor.Set("setPrototypeOf", FenValue.FromFunction(new FenFunction("setPrototypeOf",
                (args, thisVal) =>
                {
                    if (args.Length < 2)
                    {
                        throw new FenTypeError("TypeError: Object.setPrototypeOf requires 2 arguments");
                    }

                    if (!args[1].IsObject && !args[1].IsFunction && !args[1].IsNull)
                    {
                        throw new FenTypeError("TypeError: Object prototype may only be an Object or null");
                    }

                    // ES2015+: primitive targets are returned unchanged.
                    if (!args[0].IsObject && !args[0].IsFunction)
                    {
                        return args[0];
                    }

                    var obj = args[0].AsObject();
                    var nextProto = args[1].IsNull ? null : args[1].AsObject();

                    var objectProtoCandidate = objectConstructor.Get("prototype", null);
                    if (objectProtoCandidate.IsObject && ReferenceEquals(obj, objectProtoCandidate.AsObject()))
                    {
                        if (nextProto == null)
                        {
                            return args[0];
                        }

                        throw new FenTypeError("TypeError: Cannot set prototype");
                    }

                    var status = obj is FenObject fenObj ? fenObj.TrySetPrototype(nextProto) : true;
                    if (!(obj is FenObject))
                    {
                        obj?.SetPrototype(nextProto);
                    }

                    if (!status)
                    {
                        throw new FenTypeError("TypeError: Cannot set prototype");
                    }

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
            objectConstructor.Set("defineProperty", FenValue.FromFunction(new FenFunction("defineProperty",
                (args, thisVal) =>
                {
                    if (args.Length < 3)
                    {
                        throw new FenTypeError("TypeError: Object.defineProperty requires 3 arguments");
                    }

                    if (!args[0].IsObject && !args[0].IsFunction)
                    {
                        throw new FenTypeError("TypeError: Object.defineProperty called on non-object");
                    }

                    var obj = args[0].AsObject();
                    if (obj == null)
                    {
                        throw new FenTypeError("TypeError: Object.defineProperty called on non-object");
                    }

                    var prop = ToPropertyKeyString(args[1]);

                    if (!args[2].IsObject)
                    {
                        throw new FenTypeError("TypeError: Property description must be an object");
                    }

                    var descObj = args[2].AsObject();
                    if (descObj == null)
                    {
                        throw new FenTypeError("TypeError: Property description must be an object");
                    }

                    // ES5.1 §8.10.5 ToPropertyDescriptor: only set attributes that are present in the descriptor object.
                    // Absent keys must map to null (HasValue=false) so DefineOwnProperty keeps existing values.
                    var desc = new PropertyDescriptor();
                    if (descObj.Has("enumerable")) desc.Enumerable = descObj.Get("enumerable", null).ToBoolean();
                    if (descObj.Has("configurable")) desc.Configurable = descObj.Get("configurable", null).ToBoolean();
                    var getVal = descObj.Get("get", null);
                    var setVal = descObj.Get("set", null);
                    bool hasGet = descObj.Has("get");
                    bool hasSet = descObj.Has("set");
                    if (hasGet || hasSet)
                    {
                        // §8.10.5 step 9: accessor and data fields are mutually exclusive
                        var hasDataFields = descObj.Has("value") || descObj.Has("writable");
                        if (hasDataFields)
                        {
                            throw new FenTypeError(
                                "TypeError: Invalid property descriptor. Cannot both specify accessors and a value or writable attribute");
                        }

                        // §8.10.5 step 7.b/8.b: getter/setter must be callable or undefined
                        if (!getVal.IsUndefined && !getVal.IsFunction)
                            throw new FenTypeError("TypeError: Getter must be a function or undefined");
                        if (!setVal.IsUndefined && !setVal.IsFunction)
                            throw new FenTypeError("TypeError: Setter must be a function or undefined");
                        desc.Getter = getVal.IsFunction ? getVal.AsFunction() : null;
                        desc.Setter = setVal.IsFunction ? setVal.AsFunction() : null;
                    }
                    else
                    {
                        if (descObj.Has("value")) desc.Value = descObj.Get("value", null);
                        if (descObj.Has("writable")) desc.Writable = descObj.Get("writable", null).ToBoolean();
                    }

                    if (!obj.DefineOwnProperty(prop, desc))
                    {
                        throw new FenTypeError($"TypeError: Cannot define property '{prop}'");
                    }

                    return args[0];
                })));

            // ES5.1: Object.defineProperties(obj, props)
            objectConstructor.Set("defineProperties", FenValue.FromFunction(new FenFunction("defineProperties",
                (args, thisVal) =>
                {
                    if (args.Length < 2)
                        throw new FenTypeError("TypeError: Object.defineProperties requires 2 arguments");
                    if (args[0].IsNull || args[0].IsUndefined)
                        throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
                    if (!args[0].IsObject && !args[0].IsFunction)
                        throw new FenTypeError("TypeError: Object.defineProperties called on non-object");
                    var propsObj = args[1].AsObject() as FenObject;
                    if (propsObj == null)
                        throw new FenTypeError("TypeError: Object.defineProperties: second argument must be an object");
                    var defineProperty = objectConstructor.Get("defineProperty", null).AsFunction();
                    foreach (var key in propsObj.Keys(null))
                    {
                        var propResult =
                            defineProperty?.Invoke(
                                new FenValue[] { args[0], FenValue.FromString(key), propsObj.Get(key, null) }, null) ??
                            FenValue.Undefined;
                        if (propResult.Type == JsValueType.Error || propResult.Type == JsValueType.Throw)
                            return propResult;
                    }

                    return args[0];
                })));

            // ES5.1: Object.getOwnPropertyDescriptor(obj, prop)
            objectConstructor.Set("getOwnPropertyDescriptor", FenValue.FromFunction(new FenFunction(
                "getOwnPropertyDescriptor", (args, thisVal) =>
                {
                    if (args.Length < 2) return FenValue.Undefined;
                    if (args[0].IsNull || args[0].IsUndefined)
                        throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
                    // ES2015+: primitives are coerced via ToObject; just use AsObject which handles strings/numbers/booleans
                    var rawObj = args[0].AsObject();
                    if (rawObj == null) return FenValue.Undefined;
                    var prop = ToPropertyKeyString(args[1]);

                    PropertyDescriptor? desc = null;
                    if (rawObj is FenObject fenObj)
                    {
                        desc = fenObj.GetOwnPropertyDescriptor(prop);
                    }
                    else
                    {
                        desc = rawObj.GetOwnPropertyDescriptor(prop);
                    }

                    if (desc == null) return FenValue.Undefined;
                    var result = new FenObject();
                    if (desc.Value.IsAccessor)
                    {
                        result.Set("get",
                            desc.Value.Getter != null ? FenValue.FromFunction(desc.Value.Getter) : FenValue.Undefined);
                        result.Set("set",
                            desc.Value.Setter != null ? FenValue.FromFunction(desc.Value.Setter) : FenValue.Undefined);
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
            objectConstructor.Set("getOwnPropertyDescriptors", FenValue.FromFunction(new FenFunction(
                "getOwnPropertyDescriptors", (args, thisVal) =>
                {
                    var result = new FenObject();
                    if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                        throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
                    if (!args[0].IsObject && !args[0].IsFunction)
                        return FenValue.FromObject(result); // primitives have no own properties
                    var rawObj = args[0].AsObject();
                    if (rawObj == null) return FenValue.FromObject(result);
                    var getDescFn = objectConstructor.Get("getOwnPropertyDescriptor", null).AsFunction();
                    var ownNames = rawObj is FenObject fenOwnObj ? fenOwnObj.GetOwnPropertyNames() : rawObj.GetOwnPropertyNames();
                    foreach (var key in ownNames)
                        result.Set(key,
                            getDescFn?.Invoke(new FenValue[] { args[0], FenValue.FromString(key) }, null) ??
                            FenValue.Undefined);
                    return FenValue.FromObject(result);
                })));

            // ES5.1: Object.getOwnPropertyNames(obj)
            objectConstructor.Set("getOwnPropertyNames", FenValue.FromFunction(new FenFunction("getOwnPropertyNames",
                (args, thisVal) =>
                {
                    if (args.Length == 0 || args[0].IsNull || args[0].IsUndefined)
                        throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
                    if (!args[0].IsObject && !args[0].IsFunction)
                        return
                            FenValue.FromObject(
                                CreateEmptyArray()); // primitives: empty (per ES2015 ToObject coercion returning empty array)
                    var rawObj = args[0].AsObject();
                    var names = new List<string>();
                    if (rawObj is FenObject fenObj)
                    {
                        names.AddRange(fenObj.GetOwnPropertyNames());
                    }
                    else if (rawObj != null)
                    {
                        names.AddRange(rawObj.GetOwnPropertyNames() ?? Enumerable.Empty<string>());
                    }

                    var result = FenObject.CreateArray();
                    for (int i = 0; i < names.Count; i++) result.Set(i.ToString(), FenValue.FromString(names[i]));
                    result.Set("length", FenValue.FromNumber(names.Count));
                    return FenValue.FromObject(result);
                })));

            // ES6: Object.getOwnPropertySymbols(obj) Ã¢â‚¬â€ returns array of own symbol keys
            objectConstructor.Set("getOwnPropertySymbols", FenValue.FromFunction(new FenFunction(
                "getOwnPropertySymbols", (args, thisVal) =>
                {
                    // Symbols stored as @@{id} keys Ã¢â‚¬â€ return empty for now (spec compliant skeleton)
                    return FenValue.FromObject(CreateEmptyArray());
                })));

            // ES5.1: Object.preventExtensions(obj)
            objectConstructor.Set("preventExtensions", FenValue.FromFunction(new FenFunction("preventExtensions",
                (args, thisVal) =>
                {
                    if (args.Length > 0 && args[0].IsObject)
                        (args[0].AsObject() as FenObject)?.PreventExtensions();
                    return args.Length > 0 ? args[0] : FenValue.Undefined;
                })));

            // ES5.1: Object.isExtensible(obj)
            objectConstructor.Set("isExtensible", FenValue.FromFunction(new FenFunction("isExtensible",
                (args, thisVal) =>
                {
                    if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.FromBoolean(false);
                    var obj = args[0].AsObject();
                    return FenValue.FromBoolean(obj?.IsExtensible ?? false);
                })));

            // ES5.1: Object.isFrozen(obj)
            objectConstructor.Set("isFrozen", FenValue.FromFunction(new FenFunction("isFrozen", (args, thisVal) =>
            {
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.FromBoolean(true);
                var obj = args[0].AsObject();
                return FenValue.FromBoolean(obj?.IsFrozen() ?? true);
            })));

            // ES5.1: Object.isSealed(obj)
            objectConstructor.Set("isSealed", FenValue.FromFunction(new FenFunction("isSealed", (args, thisVal) =>
            {
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.FromBoolean(true);
                var obj = args[0].AsObject();
                return FenValue.FromBoolean(obj?.IsSealed() ?? true);
            })));

            // Fix Object.freeze and Object.seal to actually work (FenObject.Freeze/Seal already implemented)
            objectConstructor.Set("freeze", FenValue.FromFunction(new FenFunction("freeze", (args, thisVal) =>
            {
                if (args.Length > 0 && (args[0].IsObject || args[0].IsFunction)) (args[0].AsObject() as FenObject)?.Freeze();
                return args.Length > 0 ? args[0] : FenValue.Undefined;
            })));
            objectConstructor.Set("seal", FenValue.FromFunction(new FenFunction("seal", (args, thisVal) =>
            {
                if (args.Length > 0 && (args[0].IsObject || args[0].IsFunction)) (args[0].AsObject() as FenObject)?.Seal();
                return args.Length > 0 ? args[0] : FenValue.Undefined;
            })));

            // Fix spec-required .length for Object constructor and Object.prototype methods
            var objectCtorLengths = new Dictionary<string, int>
            {
                ["assign"] = 2, ["create"] = 2, ["defineProperties"] = 2, ["defineProperty"] = 3,
                ["entries"] = 1, ["freeze"] = 1, ["fromEntries"] = 1,
                ["getOwnPropertyDescriptor"] = 2, ["getOwnPropertyDescriptors"] = 1,
                ["getOwnPropertyNames"] = 1, ["getOwnPropertySymbols"] = 1,
                ["getPrototypeOf"] = 1, ["hasOwn"] = 2, ["is"] = 2,
                ["isExtensible"] = 1, ["isFrozen"] = 1, ["isSealed"] = 1,
                ["keys"] = 1, ["preventExtensions"] = 1, ["seal"] = 1,
                ["setPrototypeOf"] = 2, ["values"] = 1, ["groupBy"] = 2,
            };
            foreach (var kvp in objectCtorLengths)
            {
                var v = objectConstructor.Get(kvp.Key);
                if (v.IsFunction) { var methodFn = v.AsFunction(); if (methodFn != null) methodFn.NativeLength = kvp.Value; }
            }

            // Object.prototype methods - attached to a shared prototype all objects inherit from.
            IObject CoerceObjectPrototypeThis(FenValue value, string methodName)
            {
                if (value.IsNull || value.IsUndefined)
                {
                    throw new FenTypeError($"TypeError: Object.prototype.{methodName} called on null or undefined");
                }

                if (value.IsObject || value.IsFunction)
                {
                    return value.AsObject();
                }

                var boxed = objectCtor.Invoke(new[] { value }, _context);
                return boxed.AsObject();
            }

            string ToPropertyKeyString(FenValue keyValue)
            {
                var primitiveKey = (keyValue.IsObject || keyValue.IsFunction)
                    ? keyValue.ToPrimitive(_context, "string")
                    : keyValue;
                if (primitiveKey.IsSymbol)
                {
                    return primitiveKey.AsSymbol().ToPropertyKey();
                }

                return primitiveKey.AsString(_context);
            }

            // Object.prototype methods - attached to a shared prototype all objects inherit from.
            objectProto.SetBuiltin("hasOwnProperty", FenValue.FromFunction(new FenFunction("hasOwnProperty", (args, thisVal) =>
            {
                var prop = args.Length > 0 ? ToPropertyKeyString(args[0]) : string.Empty;
                var target = CoerceObjectPrototypeThis(thisVal, "hasOwnProperty");
                return FenValue.FromBoolean(target.GetOwnPropertyDescriptor(prop).HasValue);
            })));

            objectProto.SetBuiltin("isPrototypeOf", FenValue.FromFunction(new FenFunction("isPrototypeOf", (args, thisVal) =>
            {
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction))
                {
                    return FenValue.FromBoolean(false);
                }

                var proto = CoerceObjectPrototypeThis(thisVal, "isPrototypeOf");
                var cur = args[0].AsObject()?.GetPrototype();
                while (cur != null)
                {
                    if (ReferenceEquals(cur, proto))
                    {
                        return FenValue.FromBoolean(true);
                    }

                    cur = cur.GetPrototype();
                }

                return FenValue.FromBoolean(false);
            })));

            objectProto.SetBuiltin("propertyIsEnumerable", FenValue.FromFunction(new FenFunction("propertyIsEnumerable", (args, thisVal) =>
            {
                var target = CoerceObjectPrototypeThis(thisVal, "propertyIsEnumerable");
                var prop = args.Length > 0 ? ToPropertyKeyString(args[0]) : string.Empty;
                var desc = target.GetOwnPropertyDescriptor(prop);
                return FenValue.FromBoolean(desc.HasValue && (desc.Value.Enumerable ?? false));
            })));

                        bool InheritsFromPrototype(IObject start, IObject prototype)
            {
                var current = start;
                while (current != null)
                {
                    if (ReferenceEquals(current, prototype)) return true;
                    current = current.GetPrototype();
                }
                return false;
            }

            string ResolveBuiltinTagForObjectToString(FenValue value, int proxyDepth = 0)
            {
                if (value.IsBoolean) return "Boolean";
                if (value.IsNumber) return "Number";
                if (value.IsString) return "String";
                if (value.IsSymbol) return "Symbol";
                if (value.IsBigInt) return "BigInt";

                if (value.IsFunction)
                {
                    if (value.AsObject() is FenFunction fnTag)
                    {
                        if (fnTag.IsAsync) return "AsyncFunction";
                        if (fnTag.IsGenerator) return "GeneratorFunction";
                    }
                    return "Function";
                }

                var obj = value.AsObject() as FenObject;
                if (obj == null) return "Object";

                if (obj is FenFunction fnObjTag)
                {
                    if (fnObjTag.IsAsync) return "AsyncFunction";
                    if (fnObjTag.IsGenerator) return "GeneratorFunction";
                    return "Function";
                }

                if (obj.TryGetDirect("__isProxy__", out var isProxy) && isProxy.ToBoolean())
                {
                    if (proxyDepth > 16)
                    {
                        throw new FenTypeError("TypeError: Proxy chain too deep");
                    }

                    if (obj.TryGetDirect("__isRevoked__", out var isRevoked) && isRevoked.ToBoolean())
                    {
                        throw new FenTypeError("TypeError: Cannot perform 'Object.prototype.toString' on a proxy that has been revoked");
                    }

                    FenValue target;
                    if (!obj.TryGetDirect("__proxyTarget__", out target) && !obj.TryGetDirect("__target__", out target))
                    {
                        target = FenValue.Undefined;
                    }

                    if (target.IsUndefined || target.IsNull)
                    {
                        throw new FenTypeError("TypeError: Cannot perform 'Object.prototype.toString' on a proxy that has been revoked");
                    }

                    return ResolveBuiltinTagForObjectToString(target, proxyDepth + 1);
                }

                var cls = string.IsNullOrEmpty(obj.InternalClass) ? "Object" : obj.InternalClass;
                if (cls != "Object") return cls;

                var callSlot = obj.Get("__call__", null);
                if (callSlot.IsFunction)
                {
                    var callFn = callSlot.AsObject() as FenFunction;
                    if (callFn != null)
                    {
                        if (callFn.IsAsync) return "AsyncFunction";
                        if (callFn.IsGenerator) return "GeneratorFunction";
                    }
                    return "Function";
                }

                var ctorVal = obj.Get("constructor", null);
                if (ctorVal.IsFunction)
                {
                    var ctorName = ctorVal.AsFunction()?.Name ?? string.Empty;
                    if (string.Equals(ctorName, "Function", StringComparison.Ordinal)) return "Function";
                    if (string.Equals(ctorName, "AsyncFunction", StringComparison.Ordinal)) return "AsyncFunction";
                    if (string.Equals(ctorName, "GeneratorFunction", StringComparison.Ordinal)) return "GeneratorFunction";
                }

                var callMethod = obj.Get("call", null);
                var applyMethod = obj.Get("apply", null);
                if (callMethod.IsFunction && applyMethod.IsFunction)
                {
                    var fnTagVal = obj.Get(JsSymbol.ToStringTag.ToPropertyKey(), null);
                    if (fnTagVal.IsString)
                    {
                        var fnTag = fnTagVal.ToString();
                        if (string.Equals(fnTag, "AsyncFunction", StringComparison.Ordinal) ||
                            string.Equals(fnTag, "GeneratorFunction", StringComparison.Ordinal))
                        {
                            return fnTag;
                        }
                    }
                    return "Function";
                }                if (InheritsFromPrototype(obj, booleanProto)) return "Boolean";
                if (InheritsFromPrototype(obj, numberProto)) return "Number";
                if (InheritsFromPrototype(obj, stringProto)) return "String";
                if (InheritsFromPrototype(obj, dateProto)) return "Date";
                if (InheritsFromPrototype(obj, errorProto)) return "Error";

                return "Object";
            }

            objectProto.SetBuiltin("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                if (thisVal.IsUndefined) return FenValue.FromString("[object Undefined]");
                if (thisVal.IsNull) return FenValue.FromString("[object Null]");

                var builtinTag = ResolveBuiltinTagForObjectToString(thisVal);

                var objForTag = (thisVal.IsObject || thisVal.IsFunction)
                    ? thisVal.AsObject()
                    : CoerceObjectPrototypeThis(thisVal, "toString");

                var tag = objForTag?.Get("[Symbol.toStringTag]", null) ?? FenValue.Undefined;
                if (tag.IsUndefined)
                {
                    var toStringTagKey = JsSymbol.ToStringTag.ToPropertyKey();
                    tag = objForTag?.Get(toStringTagKey, null) ?? FenValue.Undefined;
                }

                var finalTag = tag.IsString ? tag.ToString() : builtinTag;
                return FenValue.FromString($"[object {finalTag}]");
            })));

            objectProto.SetBuiltin("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) =>
            {
                var target = CoerceObjectPrototypeThis(thisVal, "valueOf");
                return FenValue.FromObject(target);
            })));

            objectProto.SetBuiltin("toLocaleString", FenValue.FromFunction(new FenFunction("toLocaleString", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined)
                {
                    throw new FenTypeError("TypeError: Object.prototype.toLocaleString called on null or undefined");
                }

                IObject lookupTarget;
                FenValue receiver;
                if (thisVal.IsObject || thisVal.IsFunction)
                {
                    lookupTarget = thisVal.AsObject();
                    receiver = thisVal;
                }
                else
                {
                    var boxed = objectCtor.Invoke(new[] { thisVal }, _context);
                    lookupTarget = boxed.AsObject();
                    receiver = thisVal;
                }

                FenValue toStringFnVal;
                if (lookupTarget is FenObject fenLookup)
                {
                    toStringFnVal = fenLookup.GetWithReceiver("toString", receiver, _context);
                }
                else
                {
                    toStringFnVal = lookupTarget?.Get("toString", null) ?? FenValue.Undefined;
                }
                if (!toStringFnVal.IsFunction)
                {
                    throw new FenTypeError("TypeError: toString is not a function");
                }

                return toStringFnVal.AsFunction().Invoke(Array.Empty<FenValue>(), _context, receiver);
            })));

            // Annex B legacy accessors on Object.prototype.
            objectProto.SetBuiltin("__defineGetter__", FenValue.FromFunction(new FenFunction("__defineGetter__", (args, thisVal) =>
            {
                var target = CoerceObjectPrototypeThis(thisVal, "__defineGetter__");
                if (args.Length < 2 || !args[1].IsFunction)
                {
                    throw new FenTypeError("TypeError: Object.prototype.__defineGetter__: Expecting function");
                }

                var key = ToPropertyKeyString(args[0]);
                var getter = args[1].AsFunction() as FenFunction;
                var desc = PropertyDescriptor.Accessor(getter, null, enumerable: true, configurable: true);
                if (!target.DefineOwnProperty(key, desc))
                {
                    throw new FenTypeError($"TypeError: Cannot redefine property: {key}");
                }

                return FenValue.Undefined;
            })));

            objectProto.SetBuiltin("__defineSetter__", FenValue.FromFunction(new FenFunction("__defineSetter__", (args, thisVal) =>
            {
                var target = CoerceObjectPrototypeThis(thisVal, "__defineSetter__");
                if (args.Length < 2 || !args[1].IsFunction)
                {
                    throw new FenTypeError("TypeError: Object.prototype.__defineSetter__: Expecting function");
                }

                var key = ToPropertyKeyString(args[0]);
                var setter = args[1].AsFunction() as FenFunction;
                var desc = PropertyDescriptor.Accessor(null, setter, enumerable: true, configurable: true);
                if (!target.DefineOwnProperty(key, desc))
                {
                    throw new FenTypeError($"TypeError: Cannot redefine property: {key}");
                }

                return FenValue.Undefined;
            })));

            objectProto.SetBuiltin("__lookupGetter__", FenValue.FromFunction(new FenFunction("__lookupGetter__", (args, thisVal) =>
            {
                var target = CoerceObjectPrototypeThis(thisVal, "__lookupGetter__");
                var key = args.Length > 0 ? ToPropertyKeyString(args[0]) : string.Empty;

                for (var cur = target; cur != null; cur = cur.GetPrototype())
                {
                    var desc = cur.GetOwnPropertyDescriptor(key);
                    if (desc.HasValue)
                    {
                        return desc.Value.Getter != null ? FenValue.FromFunction(desc.Value.Getter) : FenValue.Undefined;
                    }
                }

                return FenValue.Undefined;
            })));

            objectProto.SetBuiltin("__lookupSetter__", FenValue.FromFunction(new FenFunction("__lookupSetter__", (args, thisVal) =>
            {
                var target = CoerceObjectPrototypeThis(thisVal, "__lookupSetter__");
                var key = args.Length > 0 ? ToPropertyKeyString(args[0]) : string.Empty;

                for (var cur = target; cur != null; cur = cur.GetPrototype())
                {
                    var desc = cur.GetOwnPropertyDescriptor(key);
                    if (desc.HasValue)
                    {
                        return desc.Value.Setter != null ? FenValue.FromFunction(desc.Value.Setter) : FenValue.Undefined;
                    }
                }

                return FenValue.Undefined;
            })));
            // Annex B legacy __proto__ accessor on Object.prototype.
            var objectProtoGetter = new FenFunction("get __proto__", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined)
                {
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
                }

                IObject target;
                if (thisVal.IsObject || thisVal.IsFunction)
                {
                    target = thisVal.AsObject();
                }
                else
                {
                    var boxed = objectCtor.Invoke(new[] { thisVal }, _context);
                    target = boxed.AsObject();
                }

                var proto = target?.GetPrototype();
                return proto != null ? FenValue.FromObject(proto) : FenValue.Null;
            });
            objectProtoGetter.NativeLength = 0;

            var objectProtoSetter = new FenFunction("set __proto__", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined)
                {
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
                }

                var protoArg = args.Length > 0 ? args[0] : FenValue.Undefined;
                if (!protoArg.IsObject && !protoArg.IsFunction && !protoArg.IsNull)
                {
                    return FenValue.Undefined;
                }

                if (!thisVal.IsObject && !thisVal.IsFunction)
                {
                    return FenValue.Undefined;
                }

                var target = thisVal.AsObject();
                var newProto = protoArg.IsNull ? null : protoArg.AsObject();
                bool status;
                if (target is FenObject fenTarget)
                {
                    status = fenTarget.TrySetPrototype(newProto);
                }
                else
                {
                    target.SetPrototype(newProto);
                    status = true;
                }

                if (!status)
                {
                    throw new FenTypeError("TypeError: Cannot set prototype");
                }

                return FenValue.Undefined;
            });
            objectProtoSetter.NativeLength = 1;

            objectProto.DefineOwnProperty("__proto__", PropertyDescriptor.Accessor(
                objectProtoGetter, objectProtoSetter, enumerable: false, configurable: true));
            // Ensure Object.prototype built-ins expose spec function lengths.
            objectProto.Get("hasOwnProperty", null).AsFunction().NativeLength = 1;
            objectProto.Get("isPrototypeOf", null).AsFunction().NativeLength = 1;
            objectProto.Get("propertyIsEnumerable", null).AsFunction().NativeLength = 1;
            objectProto.Get("toString", null).AsFunction().NativeLength = 0;
            objectProto.Get("valueOf", null).AsFunction().NativeLength = 0;
            objectProto.Get("toLocaleString", null).AsFunction().NativeLength = 0;
            objectProto.Get("__defineGetter__", null).AsFunction().NativeLength = 2;
            objectProto.Get("__defineSetter__", null).AsFunction().NativeLength = 2;
            objectProto.Get("__lookupGetter__", null).AsFunction().NativeLength = 1;
            objectProto.Get("__lookupSetter__", null).AsFunction().NativeLength = 1;
            objectProto.Get("hasOwnProperty", null).AsFunction().IsConstructor = false;
            objectProto.Get("isPrototypeOf", null).AsFunction().IsConstructor = false;
            objectProto.Get("propertyIsEnumerable", null).AsFunction().IsConstructor = false;
            objectProto.Get("toString", null).AsFunction().IsConstructor = false;
            objectProto.Get("valueOf", null).AsFunction().IsConstructor = false;
            objectProto.Get("toLocaleString", null).AsFunction().IsConstructor = false;
            objectProto.Get("__defineGetter__", null).AsFunction().IsConstructor = false;
            objectProto.Get("__defineSetter__", null).AsFunction().IsConstructor = false;
            objectProto.Get("__lookupGetter__", null).AsFunction().IsConstructor = false;
            objectProto.Get("__lookupSetter__", null).AsFunction().IsConstructor = false;
            objectProtoGetter.IsConstructor = false;
            objectProtoSetter.IsConstructor = false;
            objectProto.Get("hasOwnProperty", null).AsFunction().SetPrototype(functionProto);
            objectProto.Get("isPrototypeOf", null).AsFunction().SetPrototype(functionProto);
            objectProto.Get("propertyIsEnumerable", null).AsFunction().SetPrototype(functionProto);
            objectProto.Get("toString", null).AsFunction().SetPrototype(functionProto);
            objectProto.Get("valueOf", null).AsFunction().SetPrototype(functionProto);
            objectProto.Get("toLocaleString", null).AsFunction().SetPrototype(functionProto);
            objectProto.Get("__defineGetter__", null).AsFunction().SetPrototype(functionProto);
            objectProto.Get("__defineSetter__", null).AsFunction().SetPrototype(functionProto);
            objectProto.Get("__lookupGetter__", null).AsFunction().SetPrototype(functionProto);
            objectProto.Get("__lookupSetter__", null).AsFunction().SetPrototype(functionProto);
            objectProtoGetter.SetPrototype(functionProto);
            objectProtoSetter.SetPrototype(functionProto);

            objectConstructor.DefineOwnProperty("prototype", new PropertyDescriptor
            {
                Value = FenValue.FromObject(objectProto),
                Writable = false,
                Enumerable = false,
                Configurable = false,
            });

            // CRITICAL: Set DefaultPrototype so all subsequently created FenObject instances
            // (user-defined function prototypes, class instances, etc.) inherit from Object.prototype.
            // This enables hasOwnProperty, isPrototypeOf, valueOf, toString on all objects.
            FenObject.DefaultPrototype = objectProto;
            FenObject.DefaultArrayPrototype = arrayProto;


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
            navigator.Set("javaEnabled",
                FenValue.FromFunction(new FenFunction("javaEnabled",
                    (FenValue[] args, FenValue thisVal) => FenValue.FromBoolean(false))));

            // Network Information Spoofing
            var connection = new FenObject();
            connection.Set("effectiveType", FenValue.FromString("4g"));
            connection.Set("rtt", FenValue.FromNumber(50));
            connection.Set("downlink", FenValue.FromNumber(10));
            connection.Set("saveData", FenValue.FromBoolean(false));
            navigator.Set("connection", FenValue.FromObject(connection));

            var serial = new FenObject();
            serial.Set("onconnect", FenValue.Null);
            serial.Set("ondisconnect", FenValue.Null);
            serial.Set("getPorts", FenValue.FromFunction(new FenFunction("getPorts",
                (FenValue[] args, FenValue thisVal) =>
                {
                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateEmptyArray())));
                })));
            serial.Set("requestPort", FenValue.FromFunction(new FenFunction("requestPort",
                (FenValue[] args, FenValue thisVal) =>
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("NotFoundError: No serial ports available"));
                })));
            navigator.Set("serial", FenValue.FromObject(serial));

            /* [PERF-REMOVED] */
            SetGlobal("navigator", FenValue.FromObject(navigator));

            // location object (navigation capable)
            var location = new FenObject();
            UpdateLocationState(location, BaseUri);
            location.Set("assign", FenValue.FromFunction(new FenFunction("assign", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length > 0)
                {
                    RequestWindowNavigation(location, args[0].ToString());
                }

                return FenValue.Undefined;
            })));
            location.Set("replace", FenValue.FromFunction(new FenFunction("replace", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length > 0)
                {
                    RequestWindowNavigation(location, args[0].ToString());
                }

                return FenValue.Undefined;
            })));
            location.Set("reload", FenValue.FromFunction(new FenFunction("reload", (FenValue[] args, FenValue thisVal) =>
            {
                ReloadWindowLocation(location);
                return FenValue.Undefined;
            })));
            location.Set("toString", FenValue.FromFunction(new FenFunction("toString", (FenValue[] args, FenValue thisVal) =>
            {
                return location.Get("href");
            })));
            SetGlobal("location", FenValue.FromObject(location));

            // history object
            var history = new FenObject();
            _historyObject = history;
            history.DefineOwnProperty("length", PropertyDescriptor.Accessor(
                new FenFunction("get length", (FenValue[] args, FenValue thisVal) =>
                {
                    return FenValue.FromNumber(GetHistoryLength());
                }),
                null,
                enumerable: true,
                configurable: true));
            history.DefineOwnProperty("state", PropertyDescriptor.Accessor(
                new FenFunction("get state", (FenValue[] args, FenValue thisVal) =>
                {
                    return GetHistoryStateValue();
                }),
                null,
                enumerable: true,
                configurable: true));

            history.Set("pushState", FenValue.FromFunction(new FenFunction("pushState",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length >= 2)
                    {
                        var clonedState = CloneHistoryState(args[0]);
                        var title = args[1].ToString();
                        var url = args.Length > 2 ? args[2].ToString() : null;
                        var target = string.IsNullOrWhiteSpace(url)
                            ? GetCurrentHistoryUrl()
                            : ResolveLocationTarget(url, location);
                        if (!string.IsNullOrWhiteSpace(url) && target == null)
                        {
                            throw new FenTypeError($"TypeError: Failed to resolve history URL '{url}'");
                        }

                        if (_historyBridge != null)
                        {
                            _historyBridge.PushState(clonedState, title, url);
                        }
                        else
                        {
                            PushLocalHistoryState(clonedState, title, target);
                        }

                        SynchronizeHistorySurface(target);
                    }

                    return FenValue.Undefined;
                })));

            history.Set("replaceState", FenValue.FromFunction(new FenFunction("replaceState",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length >= 2)
                    {
                        var clonedState = CloneHistoryState(args[0]);
                        var title = args[1].ToString();
                        var url = args.Length > 2 ? args[2].ToString() : null;
                        var target = string.IsNullOrWhiteSpace(url)
                            ? GetCurrentHistoryUrl()
                            : ResolveLocationTarget(url, location);
                        if (!string.IsNullOrWhiteSpace(url) && target == null)
                        {
                            throw new FenTypeError($"TypeError: Failed to resolve history URL '{url}'");
                        }

                        if (_historyBridge != null)
                        {
                            _historyBridge.ReplaceState(clonedState, title, url);
                        }
                        else
                        {
                            ReplaceLocalHistoryState(clonedState, title, target);
                        }

                        SynchronizeHistorySurface(target);
                    }

                    return FenValue.Undefined;
                })));

            history.Set("go", FenValue.FromFunction(new FenFunction("go", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length > 0)
                {
                    int delta = (int)args[0].ToNumber();
                    if (_historyBridge != null)
                    {
                        _historyBridge.Go(delta);
                    }
                    else
                    {
                        TraverseLocalHistory(delta);
                    }
                }
                else
                {
                    if (_historyBridge != null)
                    {
                        _historyBridge.Go(0); // reload
                    }
                    else
                    {
                        TraverseLocalHistory(0);
                    }
                }

                return FenValue.Undefined;
            })));

            history.Set("back", FenValue.FromFunction(new FenFunction("back", (FenValue[] args, FenValue thisVal) =>
            {
                if (_historyBridge != null)
                {
                    _historyBridge.Go(-1);
                }
                else
                {
                    TraverseLocalHistory(-1);
                }
                return FenValue.Undefined;
            })));

            history.Set("forward", FenValue.FromFunction(new FenFunction("forward",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (_historyBridge != null)
                    {
                        _historyBridge.Go(1);
                    }
                    else
                    {
                        TraverseLocalHistory(1);
                    }
                    return FenValue.Undefined;
                })));

            SetGlobal("history", FenValue.FromObject(history));

            // screen object - Privacy-focused (use common resolution to prevent fingerprinting)
            var screen = new FenObject();
            screen.Set("width", FenValue.FromNumber(1920)); // Common resolution
            screen.Set("height", FenValue.FromNumber(1080)); // Common resolution
            screen.Set("availWidth", FenValue.FromNumber(1920));
            screen.Set("availHeight", FenValue.FromNumber(1040)); // Minus taskbar
            screen.Set("colorDepth", FenValue.FromNumber(24)); // Standard 24-bit color
            screen.Set("pixelDepth", FenValue.FromNumber(24));
            screen.Set("orientation", FenValue.FromObject(CreateScreenOrientation()));
            SetGlobal("screen", FenValue.FromObject(screen));

            // localStorage - Partitioned using StorageApi
            var localStorage = FenBrowser.FenEngine.WebAPIs.StorageApi.CreateLocalStorage(GetCurrentOrigin);
            SetGlobal("localStorage", FenValue.FromObject(localStorage));

            // sessionStorage - Partitioned by tab/session identity and origin, so reloads in the
            // same browsing context keep state while different tabs remain isolated.
            var sessionStorage = FenBrowser.FenEngine.WebAPIs.StorageApi.CreateSessionStorage(
                GetCurrentOrigin,
                () => _domBridge?.SessionStoragePartitionId);
            SetGlobal("sessionStorage", FenValue.FromObject(sessionStorage));

            // window object - Comprehensive with all standard properties
            window.Set("console", FenValue.FromObject(console));
            window.Set("navigator", FenValue.FromObject(navigator));
            _locationObject = location;
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
            window.Set("event", FenValue.Undefined);

                        // EventTarget prototype for Window + generic EventTarget APIs.
            var eventTargetPrototype = new FenObject();
            eventTargetPrototype.SetPrototype(objectProto);

            void DetachAbortSignalListener(FenObject entryObj)
            {
                if (entryObj == null)
                {
                    return;
                }

                var signal = entryObj.Get("__signal");
                var abortCallback = entryObj.Get("__abortCallback");
                if (!signal.IsObject || !abortCallback.IsFunction)
                {
                    return;
                }

                var removeAbortListener = signal.AsObject()?.Get("removeEventListener") ?? FenValue.Undefined;
                if (removeAbortListener.IsFunction)
                {
                    removeAbortListener.AsFunction()?.Invoke(new[]
                    {
                        FenValue.FromString("abort"),
                        abortCallback
                    }, _context, signal);
                }

                entryObj.Delete("__abortCallback");
            }

            void AttachAbortSignalListener(IObject targetObj, string eventType, FenValue callback, bool capture, FenValue signal, FenObject entryObj)
            {
                if (targetObj == null || string.IsNullOrWhiteSpace(eventType) || !signal.IsObject || entryObj == null)
                {
                    return;
                }

                var addAbortListener = signal.AsObject()?.Get("addEventListener") ?? FenValue.Undefined;
                if (!addAbortListener.IsFunction)
                {
                    return;
                }

                FenFunction abortHandler = new FenFunction("_abortEventTargetListener", (abortArgs, abortThis) =>
                {
                    var listenersVal = targetObj.Get("__fen_listeners__");
                    if (!listenersVal.IsObject)
                    {
                        return FenValue.Undefined;
                    }

                    var listenersObj = listenersVal.AsObject() as FenObject;
                    var arrVal = listenersObj?.Get(eventType) ?? FenValue.Undefined;
                    var arr = arrVal.IsObject ? arrVal.AsObject() as FenObject : null;
                    if (arr == null)
                    {
                        return FenValue.Undefined;
                    }

                    int len = (int)arr.Get("length").ToNumber();
                    var kept = FenObject.CreateArray();
                    int k = 0;
                    for (int i = 0; i < len; i++)
                    {
                        var item = arr.Get(i.ToString());
                        var remove = false;
                        if (item.IsObject)
                        {
                            var itemObj = item.AsObject() as FenObject;
                            var itemCallback = itemObj?.Get("callback") ?? FenValue.Undefined;
                            var itemCapture = itemObj?.Get("capture").ToBoolean() ?? false;
                            if (itemCallback.Equals(callback) && itemCapture == capture)
                            {
                                DetachAbortSignalListener(itemObj);
                                remove = true;
                            }
                        }

                        if (!remove)
                        {
                            kept.Set(k.ToString(), item);
                            k++;
                        }
                    }

                    kept.Set("length", FenValue.FromNumber(k));
                    listenersObj?.Set(eventType, FenValue.FromObject(kept));
                    return FenValue.Undefined;
                });

                var abortCallback = FenValue.FromFunction(abortHandler);
                entryObj.Set("__signal", signal);
                entryObj.Set("__abortCallback", abortCallback);
                addAbortListener.AsFunction()?.Invoke(new[]
                {
                    FenValue.FromString("abort"),
                    abortCallback
                }, _context, signal);
            }

            var addEventListenerFunc = FenValue.FromFunction(new FenFunction("addEventListener",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length < 2)
                    {
                        return FenValue.Undefined;
                    }

                    var eventType = args[0].ToString();
                    var callback = args[1];
                    var callbackIsValid = callback.IsFunction || (callback.IsObject && !callback.IsNull);
                    if (eventType == null || !callbackIsValid || callback.IsUndefined || callback.IsNull)
                    {
                        return FenValue.Undefined;
                    }

                    bool capture = false;
                    bool once = false;
                    bool passive = false;
                    FenValue signal = FenValue.Undefined;
                    if (args.Length >= 3)
                    {
                        if (args[2].IsBoolean)
                        {
                            capture = args[2].ToBoolean();
                        }
                        else if (args[2].IsObject)
                        {
                            var opts = args[2].AsObject();
                            var cap = opts.Get("capture");
                            capture = cap.IsBoolean && cap.ToBoolean();
                            var one = opts.Get("once");
                            once = one.IsBoolean && one.ToBoolean();
                            var pas = opts.Get("passive");
                            passive = pas.IsBoolean && pas.ToBoolean();
                            var sig = opts.Get("signal");
                            if (sig.IsObject)
                            {
                                signal = sig;
                            }
                        }
                    }

                    if (signal.IsObject && signal.AsObject()?.Get("aborted").ToBoolean() == true)
                    {
                        return FenValue.Undefined;
                    }


                    if (thisVal.IsObject && thisVal.AsObject() is ElementWrapper elementTarget)
                    {
                        FenBrowser.FenEngine.DOM.EventTarget.Registry.Add(elementTarget.Element, eventType, callback, capture, once, passive, signal);
                        return FenValue.Undefined;
                    }

                    // Generic EventTarget instance listeners
                    if (thisVal.IsObject)
                    {
                        var targetObj = thisVal.AsObject();
                        var listenersVal = targetObj.Get("__fen_listeners__");
                        var listenersObj = listenersVal.IsObject ? listenersVal.AsObject() as FenObject : null;
                        if (listenersObj == null)
                        {
                            listenersObj = new FenObject();
                            targetObj.Set("__fen_listeners__", FenValue.FromObject(listenersObj));
                        }

                        var arrVal = listenersObj.Get(eventType);
                        var arr = arrVal.IsObject ? arrVal.AsObject() as FenObject : null;
                        if (arr == null)
                        {
                            arr = FenObject.CreateArray();
                            listenersObj.Set(eventType, FenValue.FromObject(arr));
                        }

                        int len = (int)arr.Get("length").ToNumber();
                        bool duplicate = false;
                        for (int i = 0; i < len; i++)
                        {
                            var entry = arr.Get(i.ToString());
                            if (!entry.IsObject) continue;
                            var entryObj = entry.AsObject();
                            var existingCallback = entryObj.Get("callback");
                            var existingCapture = entryObj.Get("capture").ToBoolean();
                            if (existingCallback.Equals(callback) && existingCapture == capture)
                            {
                                duplicate = true;
                                break;
                            }
                        }

                        if (!duplicate)
                        {
                            var entry = new FenObject();
                            entry.Set("callback", callback);
                            entry.Set("capture", FenValue.FromBoolean(capture));
                            entry.Set("once", FenValue.FromBoolean(once));
                            entry.Set("passive", FenValue.FromBoolean(passive));
                            if (signal.IsObject)
                            {
                                AttachAbortSignalListener(targetObj, eventType, callback, capture, signal, entry);
                            }
                            arr.Set(len.ToString(), FenValue.FromObject(entry));
                            arr.Set("length", FenValue.FromNumber(len + 1));
                        }

                        return FenValue.Undefined;
                    }

                    if (!_windowEventListeners.TryGetValue(eventType, out var windowListeners))
                    {
                        windowListeners = new List<WindowEventListener>();
                        _windowEventListeners[eventType] = windowListeners;
                    }

                    if (!windowListeners.Any(l => l.Callback.Equals(callback) && l.Capture == capture))
                    {
                        windowListeners.Add(new WindowEventListener(callback, capture, once, passive));
                    }
                    return FenValue.Undefined;
                }));

            var removeEventListenerFunc = FenValue.FromFunction(new FenFunction("removeEventListener",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length < 2)
                    {
                        return FenValue.Undefined;
                    }

                    var eventType = args[0].ToString();
                    var callback = args[1];
                    bool capture = false;
                    if (args.Length >= 3 && args[2].IsBoolean)
                    {
                        capture = args[2].ToBoolean();
                    }


                    if (thisVal.IsObject && thisVal.AsObject() is ElementWrapper elementTarget)
                    {
                        FenBrowser.FenEngine.DOM.EventTarget.Registry.Remove(elementTarget.Element, eventType, callback, capture);
                        return FenValue.Undefined;
                    }

                    if (thisVal.IsObject)
                    {
                        var targetObj = thisVal.AsObject();
                        var listenersVal = targetObj.Get("__fen_listeners__");
                        if (listenersVal.IsObject)
                        {
                            var listenersObj = listenersVal.AsObject() as FenObject;
                            var arrVal = listenersObj?.Get(eventType) ?? FenValue.Undefined;
                            var arr = arrVal.IsObject ? arrVal.AsObject() as FenObject : null;
                            if (arr != null)
                            {
                                int len = (int)arr.Get("length").ToNumber();
                                var kept = FenObject.CreateArray();
                                int k = 0;
                                for (int i = 0; i < len; i++)
                                {
                                    var item = arr.Get(i.ToString());
                                    bool remove = false;
                                    if (item.IsObject)
                                    {
                                        var itemObj = item.AsObject() as FenObject;
                                        var itemCallback = itemObj?.Get("callback") ?? FenValue.Undefined;
                                        var itemCapture = itemObj?.Get("capture").ToBoolean() ?? false;
                                        remove = itemCallback.Equals(callback) && itemCapture == capture;
                                        if (remove)
                                        {
                                            DetachAbortSignalListener(itemObj);
                                        }
                                    }

                                    if (!remove)
                                    {
                                        kept.Set(k.ToString(), item);
                                        k++;
                                    }
                                }
                                kept.Set("length", FenValue.FromNumber(k));
                                listenersObj.Set(eventType, FenValue.FromObject(kept));
                            }
                        }
                        return FenValue.Undefined;
                    }

                    if (_windowEventListeners.ContainsKey(eventType))
                    {
                        _windowEventListeners[eventType].RemoveAll(l => l.Callback.Equals(callback) && l.Capture == capture);
                    }

                    return FenValue.Undefined;
                }));

            var dispatchEventFunc = FenValue.FromFunction(new FenFunction("dispatchEvent",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction))
                    {
                        throw new FenTypeError("TypeError: Failed to execute 'dispatchEvent': parameter 1 is not of type 'Event'.");
                    }

                    var eventObj = args[0].AsObject() as DomEvent;
                    if (eventObj == null)
                    {
                        var obj = args[0].AsObject();
                        var typeVal = obj?.Get("type") ?? FenValue.Undefined;
                        var type = !typeVal.IsUndefined ? typeVal.ToString() : "";
                        eventObj = new DomEvent(type, false, false, false, _context);
                    }

                    if (!eventObj.Initialized)
                    {
                        throw new InvalidOperationException("InvalidStateError: Failed to execute 'dispatchEvent' on 'EventTarget': The event's initialized flag is not set.");
                    }

                    if (thisVal.IsObject && thisVal.AsObject() is ElementWrapper elementTarget)
                    {
                        var elementNotPrevented = FenBrowser.FenEngine.DOM.EventTarget.DispatchEvent(elementTarget.Element, eventObj, _context);
                        return FenValue.FromBoolean(elementNotPrevented);
                    }

                    if (thisVal.IsObject)
                    {
                        var thisObj = thisVal.AsObject();
                        if (thisObj is FenObject fenObj && fenObj.NativeObject is Element nativeElement)
                        {
                            var elementNotPrevented = FenBrowser.FenEngine.DOM.EventTarget.DispatchEvent(nativeElement, eventObj, _context);
                            return FenValue.FromBoolean(elementNotPrevented);
                        }

                        // Some wrapper objects expose NativeObject without IObject/FenObject typing.
                        var nativeObjectProp = thisObj?.GetType().GetProperty("NativeObject");
                        if (nativeObjectProp?.GetValue(thisObj) is Element reflectedElement)
                        {
                            var reflectedNotPrevented = FenBrowser.FenEngine.DOM.EventTarget.DispatchEvent(reflectedElement, eventObj, _context);
                            return FenValue.FromBoolean(reflectedNotPrevented);
                        }
                    }

                    var effectiveThis = thisVal;
                    if (!effectiveThis.IsObject || effectiveThis.IsNull || effectiveThis.IsUndefined)
                    {
                        effectiveThis = FenValue.FromObject(window);
                    }

                    // Generic EventTarget dispatch path (window/custom targets): AT_TARGET only.
                    eventObj.ResetState();
                    eventObj.Set("eventPhase", FenValue.FromNumber(DomEvent.AT_TARGET));
                    eventObj.Set("target", effectiveThis);
                    eventObj.Set("srcElement", effectiveThis);

                    var typeName = eventObj.Type ?? "";

                    if (effectiveThis.IsObject)
                    {
                        var targetObj = effectiveThis.AsObject();
                        var listenersVal = targetObj.Get("__fen_listeners__");
                        if (listenersVal.IsObject)
                        {
                            var listenersObj = listenersVal.AsObject() as FenObject;
                            var arrVal = listenersObj?.Get(typeName) ?? FenValue.Undefined;
                            var arr = arrVal.IsObject ? arrVal.AsObject() as FenObject : null;
                            if (arr != null)
                            {
                                int len = (int)arr.Get("length").ToNumber();
                                for (int i = 0; i < len; i++)
                                {
                                    var listenerEntry = arr.Get(i.ToString());
                                    var callback = listenerEntry;
                                    var onceListener = false;

                                    if (listenerEntry.IsObject)
                                    {
                                        var entryObj = listenerEntry.AsObject();
                                        var cbVal = entryObj.Get("callback");
                                        if (!cbVal.IsUndefined)
                                        {
                                            callback = cbVal;
                                            onceListener = entryObj.Get("once").ToBoolean();
                                        }
                                    }

                                    FenFunction callbackFn = null;
                                    var callbackThis = effectiveThis;
                                    if (callback.IsFunction)
                                    {
                                        callbackFn = callback.AsFunction() as FenFunction;
                                    }
                                    else if (callback.IsObject)
                                    {
                                        var handleEvent = callback.AsObject().Get("handleEvent");
                                        if (handleEvent.IsFunction)
                                        {
                                            callbackFn = handleEvent.AsFunction() as FenFunction;
                                            callbackThis = callback;
                                        }
                                    }

                                    if (callbackFn == null) continue;

                                    _context.ThisBinding = callbackThis;
                                    callbackFn.Invoke(new[] { FenValue.FromObject(eventObj) }, _context, callbackThis);

                                    if (onceListener)
                                    {
                                        if (listenerEntry.IsObject)
                                        {
                                            DetachAbortSignalListener(listenerEntry.AsObject() as FenObject);
                                        }

                                        var kept = FenObject.CreateArray();
                                        int k = 0;
                                        for (int j = 0; j < len; j++)
                                        {
                                            if (j == i) continue;
                                            kept.Set(k.ToString(), arr.Get(j.ToString()));
                                            k++;
                                        }
                                        kept.Set("length", FenValue.FromNumber(k));
                                        listenersObj.Set(typeName, FenValue.FromObject(kept));
                                        arr = kept;
                                        len = k;
                                        i--;
                                    }

                                    var cancelBubbleVal = eventObj.Get("cancelBubble");
                                    if (cancelBubbleVal.IsBoolean && cancelBubbleVal.ToBoolean())
                                        eventObj.StopPropagation();
                                    var returnValueVal = eventObj.Get("returnValue");
                                    if (returnValueVal.IsBoolean && !returnValueVal.ToBoolean())
                                        eventObj.PreventDefault();
                                    if (eventObj.ImmediatePropagationStopped)
                                        break;
                                }
                            }
                        }
                        var branchReturnValue = eventObj.Get("returnValue");
                        var branchNotPrevented = !eventObj.DefaultPrevented && !(branchReturnValue.IsBoolean && !branchReturnValue.ToBoolean());
                        return FenValue.FromBoolean(branchNotPrevented);
                    }

                    if (_windowEventListeners.TryGetValue(typeName, out var listeners))
                    {
                        foreach (var listener in listeners.ToList())
                        {
                            FenFunction callbackFn = null;
                            var callbackThis = FenValue.FromObject(window);
                            var callback = listener.Callback;

                            if (callback.IsFunction)
                            {
                                callbackFn = callback.AsFunction() as FenFunction;
                            }
                            else if (callback.IsObject)
                            {
                                var handleEvent = callback.AsObject().Get("handleEvent");
                                if (handleEvent.IsFunction)
                                {
                                    callbackFn = handleEvent.AsFunction() as FenFunction;
                                    callbackThis = callback;
                                }
                            }

                            if (callbackFn == null) continue;

                            _context.ThisBinding = callbackThis;
                            callbackFn.Invoke(new[] { FenValue.FromObject(eventObj) }, _context, callbackThis);

                            if (listener.Once)
                            {
                                listeners.Remove(listener);
                            }

                            var cancelBubbleVal = eventObj.Get("cancelBubble");
                            if (cancelBubbleVal.IsBoolean && cancelBubbleVal.ToBoolean())
                                eventObj.StopPropagation();
                            var returnValueVal = eventObj.Get("returnValue");
                            if (returnValueVal.IsBoolean && !returnValueVal.ToBoolean())
                                eventObj.PreventDefault();
                            if (eventObj.ImmediatePropagationStopped)
                                break;
                        }
                    }

                    var onHandler = window.Get("on" + typeName);
                    if (onHandler.IsFunction)
                    {
                        _context.ThisBinding = FenValue.FromObject(window);
                        onHandler.AsFunction().Invoke(new[] { FenValue.FromObject(eventObj) }, _context, FenValue.FromObject(window));
                    }

                    eventObj.FinalizeDispatchState();
                    eventObj.Set("target", FenValue.Null);
                    eventObj.Set("srcElement", FenValue.Null);
                    eventObj.Set("currentTarget", FenValue.Null);
                    eventObj.Set("eventPhase", FenValue.FromNumber(DomEvent.NONE));

                    var finalReturnValue = eventObj.Get("returnValue");
                    var finalNotPrevented = !eventObj.DefaultPrevented && !(finalReturnValue.IsBoolean && !finalReturnValue.ToBoolean());
                    return FenValue.FromBoolean(finalNotPrevented);
                }));

            eventTargetPrototype.SetBuiltin("addEventListener", addEventListenerFunc);
            eventTargetPrototype.SetBuiltin("removeEventListener", removeEventListenerFunc);
            eventTargetPrototype.SetBuiltin("dispatchEvent", dispatchEventFunc);

            var eventTargetCtor = new FenFunction("EventTarget", (FenValue[] args, FenValue thisVal) => { var targetObj = new FenObject(); targetObj.SetPrototype(eventTargetPrototype); return FenValue.FromObject(targetObj); });
            eventTargetCtor.Prototype = eventTargetPrototype;
            eventTargetCtor.Set("prototype", FenValue.FromObject(eventTargetPrototype));
            eventTargetPrototype.SetBuiltin("constructor", FenValue.FromFunction(eventTargetCtor));
            SetGlobal("EventTarget", FenValue.FromFunction(eventTargetCtor));

            // Window extends EventTarget: methods are inherited, not own properties.
            window.SetPrototype(eventTargetPrototype);

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

                RequestWindowNavigation(location, requestedUrl);

                // Same-window fallback for now until full popup/tab orchestration is wired.
                return FenValue.FromObject(window);
            }));
            window.Set("open", windowOpenFunc);

            _windowObject = window;
            SetGlobal("window", FenValue.FromObject(window));
            // Bridge DOM EventTarget static dispatch to FenRuntime top-level listener storage.
            FenBrowser.FenEngine.DOM.EventTarget.ResolveWindowTarget = _ => window;
            FenBrowser.FenEngine.DOM.EventTarget.ResolveDocumentTarget = _ =>
            {
                var d = GetGlobal("document");
                return d.IsObject ? d.AsObject() : null;
            };
            FenBrowser.FenEngine.DOM.EventTarget.ExternalListenerInvoker = (targetObj, domEvt, execCtx, capturePhase, atTargetPhase) =>
            {
                if (targetObj is not IObject target || domEvt == null || string.IsNullOrWhiteSpace(domEvt.Type)) return;

                domEvt.Set("currentTarget", FenValue.FromObject(target));

                var listenersVal = target.Get("__fen_listeners__");
                if (listenersVal.IsObject)
                {
                    var listenersObj = listenersVal.AsObject() as FenObject;
                    var arrVal = listenersObj?.Get(domEvt.Type) ?? FenValue.Undefined;
                    var arr = arrVal.IsObject ? arrVal.AsObject() as FenObject : null;
                    if (arr != null)
                    {
                        int len = (int)arr.Get("length").ToNumber();
                        for (int i = 0; i < len; i++)
                        {
                            if (domEvt.ImmediatePropagationStopped) break;

                            var entryVal = arr.Get(i.ToString());
                            if (!entryVal.IsObject) continue;

                            var entryObj = entryVal.AsObject();
                            var callback = entryObj.Get("callback");
                            var capture = entryObj.Get("capture").ToBoolean();
                            if (capture != capturePhase) continue;

                            FenFunction callbackFn = null;
                            FenValue callbackThis = FenValue.FromObject(target);
                            if (callback.IsFunction)
                            {
                                callbackFn = callback.AsFunction() as FenFunction;
                            }
                            else if (callback.IsObject)
                            {
                                var handleEvent = callback.AsObject().Get("handleEvent");
                                if (handleEvent.IsFunction)
                                {
                                    callbackFn = handleEvent.AsFunction() as FenFunction;
                                    callbackThis = callback;
                                }
                            }

                            if (callbackFn == null) continue;

                            _context.ThisBinding = callbackThis;
                            callbackFn.Invoke(new[] { FenValue.FromObject(domEvt) }, _context, callbackThis);

                            if (entryObj.Get("once").ToBoolean())
                            {
                                DetachAbortSignalListener(entryObj as FenObject);
                                for (int j = i + 1; j < len; j++)
                                {
                                    arr.Set((j - 1).ToString(), arr.Get(j.ToString()));
                                }
                                len--;
                                arr.Delete(len.ToString());
                                arr.Set("length", FenValue.FromNumber(len));
                                i--;
                            }

                            var cancelBubbleVal = domEvt.Get("cancelBubble");
                            if (cancelBubbleVal.IsBoolean && cancelBubbleVal.ToBoolean())
                                domEvt.StopPropagation();
                            var returnValueVal = domEvt.Get("returnValue");
                            if (returnValueVal.IsBoolean && !returnValueVal.ToBoolean())
                                domEvt.PreventDefault();
                        }
                    }
                }

                if (!capturePhase && !domEvt.ImmediatePropagationStopped)
                {
                    var onHandler = target.Get("on" + domEvt.Type);
                    if (onHandler.IsFunction)
                    {
                        var thisArg = FenValue.FromObject(target);
                        _context.ThisBinding = thisArg;
                        var handlerResult = onHandler.AsFunction().Invoke(new[] { FenValue.FromObject(domEvt) }, _context, thisArg);
                        if (handlerResult.IsBoolean && !handlerResult.ToBoolean())
                        {
                            domEvt.PreventDefault();
                        }

                        var cancelBubbleVal = domEvt.Get("cancelBubble");
                        if (cancelBubbleVal.IsBoolean && cancelBubbleVal.ToBoolean())
                            domEvt.StopPropagation();
                        var returnValueVal = domEvt.Get("returnValue");
                        if (returnValueVal.IsBoolean && !returnValueVal.ToBoolean())
                            domEvt.PreventDefault();
                    }
                }
            };
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
            // Minimal WPT test_driver shim fallback.
            var testDriverObj = new FenObject();
            testDriverObj.Set("click", FenValue.FromFunction(new FenFunction("click", (tdArgs, tdThis) => FenValue.FromObject(JsPromise.Resolve(FenValue.Undefined, _context)))));
            var testDriverVal = FenValue.FromObject(testDriverObj);
            SetGlobal("test_driver", testDriverVal);
            window.Set("test_driver", testDriverVal);


            // globalThis - ES2020 (reference to global object)
            SetGlobal("globalThis", FenValue.FromObject(window));
            SetGlobal("this", FenValue.FromObject(window));

            // Array constructor with static methods - ES2015+
            var arrayConstructor = arrayCtor;

            // Array.isArray(value) - ES5
            DefineBuiltinMethod(arrayConstructor, "isArray", 1, (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var val = args[0];
                if (!val.IsObject && !val.IsFunction) return FenValue.FromBoolean(false);
                var obj = val.AsObject();
                return FenValue.FromBoolean(obj is FenObject fo && fo.InternalClass == "Array");
            });

            // Array.from(arrayLike, mapFn, thisArg) - ES2015
            DefineBuiltinMethod(arrayConstructor, "from", 1, (args, thisVal) =>
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
                        var mapped = mapFn != null
                            ? mapFn.Invoke(new FenValue[] { item, FenValue.FromNumber(i) }, null)
                            : item;
                        result.Set(idx.ToString(), mapped, null);
                        idx++;
                    }
                }
                else if (source.IsObject)
                {
                    var obj = source.AsObject() as FenObject;
                    if (obj != null)
                    {
                        // ES2015: prefer Symbol.iterator over array-like
                        var symIterVal = obj.Get("[Symbol.iterator]");
                        if (!symIterVal.IsUndefined)
                        {
                            if (!symIterVal.IsFunction)
                            {
                                throw new FenTypeError("TypeError: [Symbol.iterator] must be a function");
                            }

                            var iterator = symIterVal.AsFunction().Invoke(Array.Empty<FenValue>(),
                                new ExecutionContext { ThisBinding = source });
                            var iterObj = iterator.AsObject() as FenObject;
                            if (iterObj == null)
                            {
                                throw new FenTypeError("TypeError: iterator is not an object");
                            }

                            var nextFnVal = iterObj.Get("next");
                            if (!nextFnVal.IsFunction)
                            {
                                throw new FenTypeError("TypeError: iterator.next is not callable");
                            }

                            var nextFn = nextFnVal.AsFunction();
                            while (true)
                            {
                                var nextResult = nextFn.Invoke(Array.Empty<FenValue>(), null);
                                var nextObj = nextResult.AsObject() as FenObject;
                                if (nextObj == null)
                                {
                                    throw new FenTypeError("TypeError: iterator.next() must return an object");
                                }

                                if (nextObj.Get("done").ToBoolean()) break;

                                var item = nextObj.Get("value");
                                var mapped = mapFn != null
                                    ? mapFn.Invoke(new FenValue[] { item, FenValue.FromNumber(idx) }, null)
                                    : item;
                                result.Set(idx.ToString(), mapped, null);
                                idx++;
                            }
                        }
                        else
                        {
                            // Fallback: array-like (has .length)
                            var lenVal = obj.Get("length", null);
                            int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                            for (int i = 0; i < len; i++)
                            {
                                var item = obj.Get(i.ToString(), null);
                                var mapped = mapFn != null
                                    ? mapFn.Invoke(new FenValue[] { item, FenValue.FromNumber(i) }, null)
                                    : item;
                                result.Set(idx.ToString(), mapped, null);
                                idx++;
                            }
                        }
                    }
                }

                result.Set("length", FenValue.FromNumber(idx), null);
                return FenValue.FromObject(result);
            });

            // Array.of(...elements) - ES2015
            DefineBuiltinMethod(arrayConstructor, "of", 0, (args, thisVal) =>
            {
                var result = FenObject.CreateArray();
                for (int i = 0; i < args.Length; i++)
                {
                    result.Set(i.ToString(), args[i], null);
                }

                result.Set("length", FenValue.FromNumber(args.Length), null);
                return FenValue.FromObject(result);
            });

            // Array.fromAsync(asyncIterable, mapFn?, thisArg?) - ES2024
            // This runtime executes the sync-resolvable subset and rejects deterministic
            // type/iterator protocol errors to match spec-facing expectations.
            DefineBuiltinMethod(arrayConstructor, "fromAsync", 1, (args, thisVal) =>
            {
                FenValue Reject(FenValue reason) =>
                    FenValue.FromObject(JsPromise.Reject(reason, _context));

                FenValue RejectTypeError(string message) =>
                    Reject(CreateThrownErrorValue("TypeError", message));

                if (args.Length == 0 || args[0].IsUndefined || args[0].IsNull)
                    return RejectTypeError("Array.fromAsync requires an iterable or array-like input");

                var input = args[0];
                bool mapperProvided = args.Length > 1 && !args[1].IsUndefined;
                var mapFnAsync = mapperProvided && args[1].IsFunction ? args[1].AsFunction() : null;
                var mapThisArg = args.Length > 2 ? args[2] : FenValue.Undefined;

                if (mapperProvided && mapFnAsync == null)
                    return RejectTypeError("Array.fromAsync mapper must be callable");

                FenValue GetProperty(FenObject target, FenValue key) => target.Get(key, _context);

                FenValue QueueElement(FenObject pendingValues, ref int index, FenValue element)
                {
                    FenValue queued = element;
                    if (queued.IsObject && queued.AsObject() is JsPromise promise && mapFnAsync != null)
                    {
                        var thenMethod = promise.Get("then", _context);
                        if (thenMethod.IsFunction)
                        {
                            int capturedIndex = index;
                            queued = thenMethod.AsFunction().Invoke(
                                new[]
                                {
                                    FenValue.FromFunction(new FenFunction("fromAsyncMap", (mapArgs, mapThis) =>
                                    {
                                        var resolved = mapArgs.Length > 0 ? mapArgs[0] : FenValue.Undefined;
                                        return mapFnAsync.Invoke(
                                            new[] { resolved, FenValue.FromNumber(capturedIndex) },
                                            _context,
                                            mapThisArg);
                                    }))
                                },
                                _context,
                                FenValue.FromObject(promise));
                        }
                    }
                    else if (mapFnAsync != null)
                    {
                        queued = mapFnAsync.Invoke(
                            new[] { queued, FenValue.FromNumber(index) },
                            _context,
                            mapThisArg);
                    }

                    pendingValues.Set(index.ToString(), queued, null);
                    index++;
                    return queued;
                }

                FenValue AwaitSyncResolved(FenValue candidate, string unresolvedMessage)
                {
                    if (candidate.Type == JsValueType.Throw)
                        return candidate;

                    if (candidate.IsObject && candidate.AsObject() is JsPromise promise)
                    {
                        if (promise.IsRejected)
                            return FenValue.FromThrow(promise.Result);
                        if (!promise.IsFulfilled)
                            throw new FenTypeError(unresolvedMessage ?? "TypeError: Unresolved");
                        return promise.Result;
                    }

                    return candidate;
                }

                int CoerceArrayLikeLength(FenValue lengthValue)
                {
                    if (lengthValue.IsUndefined || lengthValue.IsNull)
                        return 0;

                    double numericLength = lengthValue.AsNumber(_context);
                    if (double.IsNaN(numericLength) || numericLength <= 0)
                        return 0;
                    if (double.IsPositiveInfinity(numericLength))
                        return int.MaxValue;
                    if (numericLength >= int.MaxValue)
                        return int.MaxValue;

                    return (int)Math.Floor(numericLength);
                }

                try
                {
                    var pendingValues = FenObject.CreateArray();
                    int idxAsync = 0;

                    // Strings are array-like and handled explicitly, even when boxed iteration is unavailable.
                    if (input.IsString)
                    {
                        var str = input.ToString() ?? string.Empty;
                        for (int i = 0; i < str.Length; i++)
                        {
                            var elem = FenValue.FromString(str[i].ToString());
                            QueueElement(pendingValues, ref idxAsync, elem);
                        }

                        pendingValues.Set("length", FenValue.FromNumber(idxAsync), null);
                        return FenValue.FromObject(JsPromise.All(FenValue.FromObject(pendingValues), _context));
                    }

                    FenObject iterableObj = input.IsObject ? input.AsObject() as FenObject : null;
                    if (iterableObj == null)
                    {
                        // Primitive non-null/non-undefined values (number/boolean/symbol/bigint) are treated
                        // as object-coercible array-like values with length defaulting to zero.
                        pendingValues.Set("length", FenValue.FromNumber(0), null);
                        return FenValue.FromObject(JsPromise.All(FenValue.FromObject(pendingValues), _context));
                    }

                    var asyncIterMethod = GetProperty(iterableObj, FenValue.FromSymbol(JsSymbol.AsyncIterator));
                    if (!asyncIterMethod.IsUndefined && !asyncIterMethod.IsNull && !asyncIterMethod.IsFunction)
                        return RejectTypeError("[Symbol.asyncIterator] must be a function");

                    FenValue syncIterMethod = FenValue.Undefined;
                    if (asyncIterMethod.IsUndefined || asyncIterMethod.IsNull)
                    {
                        syncIterMethod = GetProperty(iterableObj, FenValue.FromSymbol(JsSymbol.Iterator));
                        if (!syncIterMethod.IsUndefined && !syncIterMethod.IsNull && !syncIterMethod.IsFunction)
                            return RejectTypeError("[Symbol.iterator] must be a function");
                    }

                    FenValue iteratorVal = FenValue.Undefined;
                    if (asyncIterMethod.IsFunction)
                        iteratorVal = asyncIterMethod.AsFunction().Invoke(Array.Empty<FenValue>(), _context, input);
                    else if (syncIterMethod.IsFunction)
                        iteratorVal = syncIterMethod.AsFunction().Invoke(Array.Empty<FenValue>(), _context, input);

                    if (iteratorVal.Type == JsValueType.Throw)
                        return Reject(iteratorVal.GetThrownValue());

                    var iteratorObj = iteratorVal.IsObject ? iteratorVal.AsObject() as FenObject : null;
                    if (iteratorObj != null)
                    {
                        var nextFnVal = iteratorObj.Get("next");
                        if (!nextFnVal.IsFunction)
                            return RejectTypeError("iterator.next is not callable");

                        var nextFn = nextFnVal.AsFunction();
                        while (true)
                        {
                            var nextResult = nextFn.Invoke(Array.Empty<FenValue>(), _context, iteratorVal);

                            if (nextResult.Type == JsValueType.Throw)
                                return Reject(nextResult.GetThrownValue());

                            if (nextResult.IsObject && nextResult.AsObject() is JsPromise nextPromise)
                            {
                                if (nextPromise.IsRejected)
                                    return Reject(nextPromise.Result);
                                if (!nextPromise.IsFulfilled)
                                    return RejectTypeError("async iterator result is unresolved in synchronous runtime path");
                                nextResult = nextPromise.Result;
                                if (nextResult.Type == JsValueType.Throw)
                                    return Reject(nextResult.GetThrownValue());
                            }

                            if (!nextResult.IsObject)
                                return RejectTypeError("iterator.next() must return an object");

                            var nextResultObj = nextResult.AsObject() as FenObject;
                            if (nextResultObj == null)
                                return RejectTypeError("iterator.next() must return an object");

                            if (nextResultObj.Get("done").ToBoolean())
                                break;

                            var elem = AwaitSyncResolved(nextResultObj.Get("value"), "TypeError: async element value is unresolved in synchronous runtime path");
                            if (elem.Type == JsValueType.Throw)
                            {
                                return FenValue.FromObject(JsPromise.Reject(elem.GetThrownValue(), _context));
                            }

                            var queued = QueueElement(pendingValues, ref idxAsync, elem);
                            if (queued.Type == JsValueType.Throw)
                            {
                                return FenValue.FromObject(JsPromise.Reject(queued.GetThrownValue(), _context));
                            }
                        }
                    }
                    else
                    {
                        // Array-like fallback
                        int len = CoerceArrayLikeLength(iterableObj.Get("length", _context));

                        for (int i = 0; i < len; i++)
                        {
                            var queued = QueueElement(pendingValues, ref idxAsync, iterableObj.Get(i.ToString(), _context));
                            if (queued.Type == JsValueType.Throw)
                            {
                                return FenValue.FromObject(JsPromise.Reject(queued.GetThrownValue(), _context));
                            }
                        }
                    }

                    pendingValues.Set("length", FenValue.FromNumber(idxAsync), null);
                    return FenValue.FromObject(JsPromise.All(FenValue.FromObject(pendingValues), _context));
                }
                catch (Exception ex)
                {
                    if (TryExtractThrownValue(ex, out var thrownValue))
                    {
                        return Reject(thrownValue);
                    }

                    return Reject(CreateThrownErrorValue("TypeError", ex.Message));
                }
            });
            // Event constructor (DOM Level 3)
            var eventPrototype = new FenObject();
            var eventCtorFn = new FenFunction("Event", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'Event': 1 argument required, but only 0 present.");
                }

                var type = args[0].ToString();
                bool bubbles = false;
                bool cancelable = false;
                bool composed = false;

                if (args.Length > 1 && args[1].IsObject)
                {
                    var opts = args[1].AsObject();
                    if (opts != null)
                    {
                        bubbles = opts.Get("bubbles").ToBoolean();
                        cancelable = opts.Get("cancelable").ToBoolean();
                        composed = opts.Get("composed").ToBoolean();
                    }
                }

                var evt = new FenBrowser.FenEngine.DOM.DomEvent(type, bubbles, cancelable, composed, _context);
                evt.SetPrototype(eventPrototype);
                evt.Delete("path");
                evt.Delete("getPreventDefault");
                return FenValue.FromObject(evt);
            });
            eventCtorFn.Prototype = eventPrototype;
            eventCtorFn.Set("prototype", FenValue.FromObject(eventPrototype));
            var eventCtor = FenValue.FromFunction(eventCtorFn);
            eventPrototype.SetBuiltin("constructor", eventCtor);
            eventPrototype.Delete("path");
            eventPrototype.Delete("getPreventDefault");
            eventCtorFn.Set("NONE", FenValue.FromNumber(FenBrowser.FenEngine.DOM.DomEvent.NONE));
            eventCtorFn.Set("CAPTURING_PHASE", FenValue.FromNumber(FenBrowser.FenEngine.DOM.DomEvent.CAPTURING_PHASE));
            eventCtorFn.Set("AT_TARGET", FenValue.FromNumber(FenBrowser.FenEngine.DOM.DomEvent.AT_TARGET));
            eventCtorFn.Set("BUBBLING_PHASE", FenValue.FromNumber(FenBrowser.FenEngine.DOM.DomEvent.BUBBLING_PHASE));
            eventPrototype.Set("NONE", FenValue.FromNumber(FenBrowser.FenEngine.DOM.DomEvent.NONE));
            eventPrototype.Set("CAPTURING_PHASE", FenValue.FromNumber(FenBrowser.FenEngine.DOM.DomEvent.CAPTURING_PHASE));
            eventPrototype.Set("AT_TARGET", FenValue.FromNumber(FenBrowser.FenEngine.DOM.DomEvent.AT_TARGET));
            eventPrototype.Set("BUBBLING_PHASE", FenValue.FromNumber(FenBrowser.FenEngine.DOM.DomEvent.BUBBLING_PHASE));
            SetGlobal("Event", eventCtor);

            static FenValue ReadInitOption(FenValue[] ctorArgs, string key)
            {
                if (ctorArgs.Length <= 1 || !ctorArgs[1].IsObject)
                {
                    return FenValue.Undefined;
                }

                var opts = ctorArgs[1].AsObject();
                return opts?.Get(key) ?? FenValue.Undefined;
            }

            static string ReadInitString(FenValue[] ctorArgs, string key, string fallback = "")
            {
                var value = ReadInitOption(ctorArgs, key);
                return value.IsUndefined || value.IsNull ? fallback : value.ToString();
            }

            static double ReadInitNumber(FenValue[] ctorArgs, string key, double fallback = 0)
            {
                var value = ReadInitOption(ctorArgs, key);
                return value.IsNumber ? value.ToNumber() : fallback;
            }

            static bool ReadInitBool(FenValue[] ctorArgs, string key, bool fallback = false)
            {
                var value = ReadInitOption(ctorArgs, key);
                return value.IsUndefined ? fallback : value.ToBoolean();
            }

            static FenValue ReadInitAny(FenValue[] ctorArgs, string key, FenValue fallback)
            {
                var value = ReadInitOption(ctorArgs, key);
                return value.IsUndefined ? fallback : value;
            }

            FenValue DefineEventSubclass(string name, FenObject parentPrototype, Action<FenBrowser.FenEngine.DOM.DomEvent, FenValue[]> initializer = null, Action<FenObject> prototypeInitializer = null)
            {
                var subProto = new FenObject();
                subProto.SetPrototype(parentPrototype);
                prototypeInitializer?.Invoke(subProto);

                var subCtorFn = new FenFunction(name, (FenValue[] ctorArgs, FenValue thisVal) =>
                {
                    if (ctorArgs.Length == 0)
                    {
                        throw new FenTypeError($"TypeError: Failed to construct '{name}': 1 argument required, but only 0 present.");
                    }

                    var subType = ctorArgs[0].ToString();
                    var subBubbles = ReadInitBool(ctorArgs, "bubbles");
                    var subCancelable = ReadInitBool(ctorArgs, "cancelable");
                    var subComposed = ReadInitBool(ctorArgs, "composed");

                    var subEvt = new FenBrowser.FenEngine.DOM.DomEvent(subType, subBubbles, subCancelable, subComposed, _context);
                    subEvt.SetPrototype(subProto);
                    initializer?.Invoke(subEvt, ctorArgs);
                    return FenValue.FromObject(subEvt);
                });
                subCtorFn.Prototype = subProto;
                subCtorFn.Set("prototype", FenValue.FromObject(subProto));
                var ctorVal = FenValue.FromFunction(subCtorFn);
                subProto.SetBuiltin("constructor", ctorVal);
                SetGlobal(name, ctorVal);
                return ctorVal;
            }

            var uiEventCtor = DefineEventSubclass("UIEvent", eventPrototype, (evt, ctorArgs) =>
            {
                var viewValue = ReadInitAny(ctorArgs, "view", FenValue.Null);
                if (!viewValue.IsUndefined && !viewValue.IsNull && !viewValue.IsObject)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'UIEvent': member view is not of type Window.");
                }

                evt.Set("view", viewValue.IsUndefined ? FenValue.Null : viewValue);
                evt.Set("detail", FenValue.FromNumber(ReadInitNumber(ctorArgs, "detail")));
                evt.Set("which", FenValue.FromNumber(0));
            });
            var uiEventPrototype = uiEventCtor.AsFunction().Prototype;

            FenValue ModifierStateGetter(string propName) => FenValue.FromFunction(new FenFunction("getModifierState", (modifierArgs, modifierThis) =>
            {
                if (!modifierThis.IsObject)
                {
                    return FenValue.FromBoolean(false);
                }

                var query = modifierArgs.Length > 0 ? modifierArgs[0].ToString() : string.Empty;
                var obj = modifierThis.AsObject();
                return query switch
                {
                    "Alt" => FenValue.FromBoolean(obj.Get("altKey").ToBoolean()),
                    "Control" => FenValue.FromBoolean(obj.Get("ctrlKey").ToBoolean()),
                    "Meta" => FenValue.FromBoolean(obj.Get("metaKey").ToBoolean()),
                    "Shift" => FenValue.FromBoolean(obj.Get("shiftKey").ToBoolean()),
                    _ => FenValue.FromBoolean(false)
                };
            }));

            var mouseEventCtor = DefineEventSubclass("MouseEvent", uiEventPrototype, (evt, ctorArgs) =>
            {
                var viewValue = ReadInitAny(ctorArgs, "view", FenValue.Null);
                if (!viewValue.IsUndefined && !viewValue.IsNull && !viewValue.IsObject)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'MouseEvent': member view is not of type Window.");
                }
                evt.Set("view", viewValue.IsUndefined ? FenValue.Null : viewValue);
                evt.Set("detail", FenValue.FromNumber(ReadInitNumber(ctorArgs, "detail")));
                evt.Set("screenX", FenValue.FromNumber(ReadInitNumber(ctorArgs, "screenX")));
                evt.Set("screenY", FenValue.FromNumber(ReadInitNumber(ctorArgs, "screenY")));
                evt.Set("clientX", FenValue.FromNumber(ReadInitNumber(ctorArgs, "clientX")));
                evt.Set("clientY", FenValue.FromNumber(ReadInitNumber(ctorArgs, "clientY")));
                evt.Set("pageX", FenValue.FromNumber(ReadInitNumber(ctorArgs, "clientX")));
                evt.Set("pageY", FenValue.FromNumber(ReadInitNumber(ctorArgs, "clientY")));
                evt.Set("offsetX", FenValue.FromNumber(0));
                evt.Set("offsetY", FenValue.FromNumber(0));
                evt.Set("x", FenValue.FromNumber(ReadInitNumber(ctorArgs, "clientX")));
                evt.Set("y", FenValue.FromNumber(ReadInitNumber(ctorArgs, "clientY")));
                evt.Set("ctrlKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "ctrlKey")));
                evt.Set("shiftKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "shiftKey")));
                evt.Set("altKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "altKey")));
                evt.Set("metaKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "metaKey")));
                evt.Set("button", FenValue.FromNumber(ReadInitNumber(ctorArgs, "button")));
                evt.Set("buttons", FenValue.FromNumber(ReadInitNumber(ctorArgs, "buttons")));
                evt.Set("movementX", FenValue.FromNumber(ReadInitNumber(ctorArgs, "movementX")));
                evt.Set("movementY", FenValue.FromNumber(ReadInitNumber(ctorArgs, "movementY")));
                evt.Set("relatedTarget", ReadInitAny(ctorArgs, "relatedTarget", FenValue.Null));
                evt.Set("which", FenValue.FromNumber(ReadInitNumber(ctorArgs, "which", ReadInitNumber(ctorArgs, "button") + 1)));
            }, subProto =>
            {
                subProto.SetBuiltin("getModifierState", ModifierStateGetter("mouse"));
            });
            var mouseEventPrototype = mouseEventCtor.AsFunction().Prototype;

            var keyboardEventCtor = DefineEventSubclass("KeyboardEvent", uiEventPrototype, (evt, ctorArgs) =>
            {
                var viewValue = ReadInitAny(ctorArgs, "view", FenValue.Null);
                if (!viewValue.IsUndefined && !viewValue.IsNull && !viewValue.IsObject)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'KeyboardEvent': member view is not of type Window.");
                }
                evt.Set("view", viewValue.IsUndefined ? FenValue.Null : viewValue);
                evt.Set("detail", FenValue.FromNumber(ReadInitNumber(ctorArgs, "detail")));
                evt.Set("key", FenValue.FromString(ReadInitString(ctorArgs, "key")));
                evt.Set("code", FenValue.FromString(ReadInitString(ctorArgs, "code")));
                evt.Set("location", FenValue.FromNumber(ReadInitNumber(ctorArgs, "location")));
                evt.Set("repeat", FenValue.FromBoolean(ReadInitBool(ctorArgs, "repeat")));
                evt.Set("isComposing", FenValue.FromBoolean(ReadInitBool(ctorArgs, "isComposing")));
                evt.Set("ctrlKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "ctrlKey")));
                evt.Set("shiftKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "shiftKey")));
                evt.Set("altKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "altKey")));
                evt.Set("metaKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "metaKey")));
                var keyCode = ReadInitNumber(ctorArgs, "keyCode");
                var charCode = ReadInitNumber(ctorArgs, "charCode");
                evt.Set("keyCode", FenValue.FromNumber(keyCode));
                evt.Set("charCode", FenValue.FromNumber(charCode));
                evt.Set("which", FenValue.FromNumber(keyCode != 0 ? keyCode : charCode));
            }, subProto =>
            {
                subProto.SetBuiltin("getModifierState", ModifierStateGetter("keyboard"));
            });

            var gamepadEventCtor = DefineEventSubclass("GamepadEvent", eventPrototype);
            var beforeUnloadEventCtor = DefineEventSubclass("BeforeUnloadEvent", eventPrototype, (evt, ctorArgs) =>
            {
                evt.Set("returnValue", FenValue.FromString(ReadInitString(ctorArgs, "returnValue")));
            });
            var focusEventCtor = DefineEventSubclass("FocusEvent", uiEventPrototype, (evt, ctorArgs) =>
            {
                var viewValue = ReadInitAny(ctorArgs, "view", FenValue.Null);
                if (!viewValue.IsUndefined && !viewValue.IsNull && !viewValue.IsObject)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'FocusEvent': member view is not of type Window.");
                }
                evt.Set("view", viewValue.IsUndefined ? FenValue.Null : viewValue);
                evt.Set("detail", FenValue.FromNumber(ReadInitNumber(ctorArgs, "detail")));
                evt.Set("relatedTarget", ReadInitAny(ctorArgs, "relatedTarget", FenValue.Null));
            });
            var inputEventCtor = DefineEventSubclass("InputEvent", uiEventPrototype, (evt, ctorArgs) =>
            {
                var viewValue = ReadInitAny(ctorArgs, "view", FenValue.Null);
                if (!viewValue.IsUndefined && !viewValue.IsNull && !viewValue.IsObject)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'InputEvent': member view is not of type Window.");
                }
                evt.Set("view", viewValue.IsUndefined ? FenValue.Null : viewValue);
                evt.Set("detail", FenValue.FromNumber(ReadInitNumber(ctorArgs, "detail")));
                evt.Set("data", ReadInitAny(ctorArgs, "data", FenValue.Null));
                evt.Set("inputType", FenValue.FromString(ReadInitString(ctorArgs, "inputType")));
                evt.Set("isComposing", FenValue.FromBoolean(ReadInitBool(ctorArgs, "isComposing")));
                evt.Set("dataTransfer", ReadInitAny(ctorArgs, "dataTransfer", FenValue.Null));
            });
            var compositionEventCtor = DefineEventSubclass("CompositionEvent", uiEventPrototype, (evt, ctorArgs) =>
            {
                var viewValue = ReadInitAny(ctorArgs, "view", FenValue.Null);
                if (!viewValue.IsUndefined && !viewValue.IsNull && !viewValue.IsObject)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'CompositionEvent': member view is not of type Window.");
                }
                evt.Set("view", viewValue.IsUndefined ? FenValue.Null : viewValue);
                evt.Set("detail", FenValue.FromNumber(ReadInitNumber(ctorArgs, "detail")));
                evt.Set("data", FenValue.FromString(ReadInitString(ctorArgs, "data")));
            });
            var pointerEventCtor = DefineEventSubclass("PointerEvent", mouseEventPrototype, (evt, ctorArgs) =>
            {
                var viewValue = ReadInitAny(ctorArgs, "view", FenValue.Null);
                if (!viewValue.IsUndefined && !viewValue.IsNull && !viewValue.IsObject)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'PointerEvent': member view is not of type Window.");
                }
                evt.Set("view", viewValue.IsUndefined ? FenValue.Null : viewValue);
                evt.Set("detail", FenValue.FromNumber(ReadInitNumber(ctorArgs, "detail")));
                evt.Set("screenX", FenValue.FromNumber(ReadInitNumber(ctorArgs, "screenX")));
                evt.Set("screenY", FenValue.FromNumber(ReadInitNumber(ctorArgs, "screenY")));
                evt.Set("clientX", FenValue.FromNumber(ReadInitNumber(ctorArgs, "clientX")));
                evt.Set("clientY", FenValue.FromNumber(ReadInitNumber(ctorArgs, "clientY")));
                evt.Set("ctrlKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "ctrlKey")));
                evt.Set("shiftKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "shiftKey")));
                evt.Set("altKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "altKey")));
                evt.Set("metaKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "metaKey")));
                evt.Set("button", FenValue.FromNumber(ReadInitNumber(ctorArgs, "button")));
                evt.Set("buttons", FenValue.FromNumber(ReadInitNumber(ctorArgs, "buttons")));
                evt.Set("pointerId", FenValue.FromNumber(ReadInitNumber(ctorArgs, "pointerId")));
                evt.Set("width", FenValue.FromNumber(ReadInitNumber(ctorArgs, "width")));
                evt.Set("height", FenValue.FromNumber(ReadInitNumber(ctorArgs, "height")));
                evt.Set("pressure", FenValue.FromNumber(ReadInitNumber(ctorArgs, "pressure")));
                evt.Set("tangentialPressure", FenValue.FromNumber(ReadInitNumber(ctorArgs, "tangentialPressure")));
                evt.Set("tiltX", FenValue.FromNumber(ReadInitNumber(ctorArgs, "tiltX")));
                evt.Set("tiltY", FenValue.FromNumber(ReadInitNumber(ctorArgs, "tiltY")));
                evt.Set("twist", FenValue.FromNumber(ReadInitNumber(ctorArgs, "twist")));
                evt.Set("pointerType", FenValue.FromString(ReadInitString(ctorArgs, "pointerType")));
                evt.Set("isPrimary", FenValue.FromBoolean(ReadInitBool(ctorArgs, "isPrimary")));
            }, subProto =>
            {
                subProto.SetBuiltin("getModifierState", ModifierStateGetter("pointer"));
            });
            var wheelEventCtor = DefineEventSubclass("WheelEvent", mouseEventPrototype, (evt, ctorArgs) =>
            {
                var viewValue = ReadInitAny(ctorArgs, "view", FenValue.Null);
                if (!viewValue.IsUndefined && !viewValue.IsNull && !viewValue.IsObject)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'WheelEvent': member view is not of type Window.");
                }
                evt.Set("view", viewValue.IsUndefined ? FenValue.Null : viewValue);
                evt.Set("detail", FenValue.FromNumber(ReadInitNumber(ctorArgs, "detail")));
                evt.Set("screenX", FenValue.FromNumber(ReadInitNumber(ctorArgs, "screenX")));
                evt.Set("screenY", FenValue.FromNumber(ReadInitNumber(ctorArgs, "screenY")));
                evt.Set("clientX", FenValue.FromNumber(ReadInitNumber(ctorArgs, "clientX")));
                evt.Set("clientY", FenValue.FromNumber(ReadInitNumber(ctorArgs, "clientY")));
                evt.Set("ctrlKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "ctrlKey")));
                evt.Set("shiftKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "shiftKey")));
                evt.Set("altKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "altKey")));
                evt.Set("metaKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "metaKey")));
                evt.Set("button", FenValue.FromNumber(ReadInitNumber(ctorArgs, "button")));
                evt.Set("buttons", FenValue.FromNumber(ReadInitNumber(ctorArgs, "buttons")));
                evt.Set("deltaX", FenValue.FromNumber(ReadInitNumber(ctorArgs, "deltaX")));
                evt.Set("deltaY", FenValue.FromNumber(ReadInitNumber(ctorArgs, "deltaY")));
                evt.Set("deltaZ", FenValue.FromNumber(ReadInitNumber(ctorArgs, "deltaZ")));
                evt.Set("deltaMode", FenValue.FromNumber(ReadInitNumber(ctorArgs, "deltaMode")));
            }, subProto =>
            {
                subProto.SetBuiltin("getModifierState", ModifierStateGetter("wheel"));
            });
            var touchEventCtor = DefineEventSubclass("TouchEvent", uiEventPrototype, (evt, ctorArgs) =>
            {
                var viewValue = ReadInitAny(ctorArgs, "view", FenValue.Null);
                if (!viewValue.IsUndefined && !viewValue.IsNull && !viewValue.IsObject)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'TouchEvent': member view is not of type Window.");
                }
                evt.Set("view", viewValue.IsUndefined ? FenValue.Null : viewValue);
                evt.Set("detail", FenValue.FromNumber(ReadInitNumber(ctorArgs, "detail")));
                evt.Set("touches", ReadInitAny(ctorArgs, "touches", FenValue.FromObject(FenObject.CreateArray())));
                evt.Set("targetTouches", ReadInitAny(ctorArgs, "targetTouches", FenValue.FromObject(FenObject.CreateArray())));
                evt.Set("changedTouches", ReadInitAny(ctorArgs, "changedTouches", FenValue.FromObject(FenObject.CreateArray())));
                evt.Set("ctrlKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "ctrlKey")));
                evt.Set("shiftKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "shiftKey")));
                evt.Set("altKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "altKey")));
                evt.Set("metaKey", FenValue.FromBoolean(ReadInitBool(ctorArgs, "metaKey")));
            }, subProto =>
            {
                subProto.SetBuiltin("getModifierState", ModifierStateGetter("touch"));
            });
            var animationEventCtor = DefineEventSubclass("AnimationEvent", eventPrototype, (evt, ctorArgs) =>
            {
                evt.Set("animationName", FenValue.FromString(ReadInitString(ctorArgs, "animationName")));
                evt.Set("elapsedTime", FenValue.FromNumber(ReadInitNumber(ctorArgs, "elapsedTime")));
                evt.Set("pseudoElement", FenValue.FromString(ReadInitString(ctorArgs, "pseudoElement")));
            });
            var transitionEventCtor = DefineEventSubclass("TransitionEvent", eventPrototype, (evt, ctorArgs) =>
            {
                evt.Set("propertyName", FenValue.FromString(ReadInitString(ctorArgs, "propertyName")));
                evt.Set("elapsedTime", FenValue.FromNumber(ReadInitNumber(ctorArgs, "elapsedTime")));
                evt.Set("pseudoElement", FenValue.FromString(ReadInitString(ctorArgs, "pseudoElement")));
            });

            var errorEventProto = new FenObject();
            errorEventProto.SetPrototype(eventPrototype);
            var errorEventCtorFn = new FenFunction("ErrorEvent", (FenValue[] ctorArgs, FenValue thisVal) =>
            {
                if (ctorArgs.Length == 0)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'ErrorEvent': 1 argument required, but only 0 present.");
                }

                var evtType = ctorArgs[0].ToString();
                bool evtBubbles = false;
                bool evtCancelable = false;
                bool evtComposed = false;
                string message = string.Empty;
                string filename = string.Empty;
                double lineno = 0;
                double colno = 0;
                FenValue errorValue = FenValue.Null;

                if (ctorArgs.Length > 1 && ctorArgs[1].IsObject)
                {
                    var opts = ctorArgs[1].AsObject();
                    if (opts != null)
                    {
                        evtBubbles = opts.Get("bubbles").ToBoolean();
                        evtCancelable = opts.Get("cancelable").ToBoolean();
                        evtComposed = opts.Get("composed").ToBoolean();

                        var msgVal = opts.Get("message");
                        message = msgVal.IsUndefined || msgVal.IsNull ? string.Empty : msgVal.ToString();

                        var fileVal = opts.Get("filename");
                        filename = fileVal.IsUndefined || fileVal.IsNull ? string.Empty : fileVal.ToString();

                        var lineVal = opts.Get("lineno");
                        if (!lineVal.IsUndefined && !lineVal.IsNull && lineVal.IsNumber)
                            lineno = lineVal.ToNumber();

                        var colVal = opts.Get("colno");
                        if (!colVal.IsUndefined && !colVal.IsNull && colVal.IsNumber)
                            colno = colVal.ToNumber();

                        var errVal = opts.Get("error");
                        errorValue = errVal.IsUndefined ? FenValue.Null : errVal;
                    }
                }

                var subEvt = new FenBrowser.FenEngine.DOM.DomEvent(evtType, evtBubbles, evtCancelable, evtComposed, _context);
                subEvt.SetPrototype(errorEventProto);
                subEvt.Set("message", FenValue.FromString(message));
                subEvt.Set("filename", FenValue.FromString(filename));
                subEvt.Set("lineno", FenValue.FromNumber(lineno));
                subEvt.Set("colno", FenValue.FromNumber(colno));
                subEvt.Set("error", errorValue);
                return FenValue.FromObject(subEvt);
            });
            errorEventCtorFn.Prototype = errorEventProto;
            errorEventCtorFn.Set("prototype", FenValue.FromObject(errorEventProto));
            var errorEventCtor = FenValue.FromFunction(errorEventCtorFn);
            errorEventProto.SetBuiltin("constructor", errorEventCtor);
            SetGlobal("ErrorEvent", errorEventCtor);

            // CustomEvent constructor (DOM Level 3)
            var customEventProto = new FenObject();
            customEventProto.SetPrototype(eventPrototype);
            var customEventCtorFn = new FenFunction("CustomEvent", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'CustomEvent': 1 argument required, but only 0 present.");
                }

                var type = args[0].ToString();
                bool bubbles = false;
                bool cancelable = false;
                IValue detail = FenValue.Null;

                if (args.Length > 1 && args[1].IsObject)
                {
                    var opts = args[1].AsObject();
                    if (opts != null)
                    {
                        var bubblesVal = opts.Get("bubbles");
                        bubbles = bubblesVal.IsUndefined ? false : bubblesVal.ToBoolean();
                        var cancelableVal = opts.Get("cancelable");
                        cancelable = cancelableVal.IsUndefined ? false : cancelableVal.ToBoolean();
                        detail = opts.Get("detail");
                    }
                }

                var custom = new FenBrowser.FenEngine.DOM.CustomEvent(type, bubbles, cancelable, detail);
                custom.SetPrototype(customEventProto);
                return FenValue.FromObject(custom);
            });
            customEventCtorFn.Prototype = customEventProto;
            customEventCtorFn.Set("prototype", FenValue.FromObject(customEventProto));
            var customEventCtor = FenValue.FromFunction(customEventCtorFn);
            customEventProto.SetBuiltin("constructor", customEventCtor);
            SetGlobal("CustomEvent", customEventCtor);

            // Expose core DOM constructors on window as non-enumerable, configurable globals.
            static void DefineWindowInterface(FenObject windowObj, string name, FenValue value)
            {
                windowObj.DefineOwnProperty(name, new PropertyDescriptor
                {
                    Value = value,
                    Writable = true,
                    Enumerable = false,
                    Configurable = true
                });
            }

            FenFunction CreatePrototypeForwarder(string displayName, string targetMethod)
            {
                return new FenFunction(displayName, (forwardArgs, forwardThis) =>
                {
                    var targetObject = forwardThis.AsObject();
                    if (targetObject == null)
                    {
                        throw new FenTypeError($"TypeError: Illegal invocation of {displayName}");
                    }

                    var targetMember = targetObject.Get(targetMethod, _context);
                    if (!targetMember.IsFunction)
                    {
                        throw new FenTypeError($"TypeError: {displayName} called on incompatible object");
                    }

                    return targetMember.AsFunction().Invoke(forwardArgs ?? Array.Empty<FenValue>(), _context, forwardThis);
                });
            }

            FenValue CreateInterfaceConstructor(string name, FenObject prototype, Func<FenValue[], FenValue, FenValue> implementation = null)
            {
                var ctorFn = new FenFunction(name, implementation ?? ((args, thisVal) => FenValue.FromObject(new FenObject())));
                ctorFn.Prototype = prototype;
                ctorFn.Set("prototype", FenValue.FromObject(prototype));
                var ctorValue = FenValue.FromFunction(ctorFn);
                prototype.SetBuiltin("constructor", ctorValue);
                return ctorValue;
            }

            FenValue CreatePlaceholderInterface(string name, FenObject parentPrototype = null)
            {
                var prototype = new FenObject();
                prototype.InternalClass = name + "Prototype";
                if (parentPrototype != null)
                {
                    prototype.SetPrototype(parentPrototype);
                }

                return CreateInterfaceConstructor(name, prototype);
            }

            // Provide minimal constructor interfaces required by baseline DOM WPT files.
            var nodePrototype = new FenObject();
            var nodeCtorFn = new FenFunction("Node", (FenValue[] args, FenValue thisVal) => FenValue.FromObject(new FenObject()));
            nodeCtorFn.Prototype = nodePrototype;
            nodeCtorFn.Set("prototype", FenValue.FromObject(nodePrototype));
            var nodeCtorVal = FenValue.FromFunction(nodeCtorFn);
            _domNodePrototype = nodePrototype;
            SetGlobal("Node", nodeCtorVal);
            DefineWindowInterface(window, "Node", nodeCtorVal);

            var attrPrototype = new FenObject();
            attrPrototype.SetPrototype(nodePrototype);
            var attrCtorFn = new FenFunction("Attr", (FenValue[] args, FenValue thisVal) =>
            {
                var name = args.Length > 0 ? args[0].ToString() : "";
                var wrapper = new AttrWrapper(new Attr(string.IsNullOrWhiteSpace(name) ? "attr" : name, ""), _context);
                wrapper.SetPrototype(attrPrototype);
                return FenValue.FromObject(wrapper);
            });
            attrCtorFn.Prototype = attrPrototype;
            attrCtorFn.Set("prototype", FenValue.FromObject(attrPrototype));
            var attrCtorVal = FenValue.FromFunction(attrCtorFn);
            _domAttrPrototype = attrPrototype;
            SetGlobal("Attr", attrCtorVal);
            DefineWindowInterface(window, "Attr", attrCtorVal);

            var textPrototype = new FenObject();
            textPrototype.SetPrototype(nodePrototype);
            var textCtorFn = new FenFunction("Text", (FenValue[] args, FenValue thisVal) =>
            {
                var data = args.Length > 0 ? args[0].ToString() : string.Empty;
                var wrapperVal = DomWrapperFactory.Wrap(new Text(data), _context);
                if (wrapperVal.IsObject)
                {
                    wrapperVal.AsObject().SetPrototype(textPrototype);
                }
                return wrapperVal;
            });
            textCtorFn.Prototype = textPrototype;
            textCtorFn.Set("prototype", FenValue.FromObject(textPrototype));
            var textCtorVal = FenValue.FromFunction(textCtorFn);
            _domTextPrototype = textPrototype;
            SetGlobal("Text", textCtorVal);
            DefineWindowInterface(window, "Text", textCtorVal);

            var commentPrototype = new FenObject();
            commentPrototype.SetPrototype(nodePrototype);
            var commentCtorFn = new FenFunction("Comment", (FenValue[] args, FenValue thisVal) =>
            {
                var data = args.Length > 0 ? args[0].ToString() : string.Empty;
                var wrapperVal = DomWrapperFactory.Wrap(new Comment(data), _context);
                if (wrapperVal.IsObject)
                {
                    wrapperVal.AsObject().SetPrototype(commentPrototype);
                }
                return wrapperVal;
            });
            commentCtorFn.Prototype = commentPrototype;
            commentCtorFn.Set("prototype", FenValue.FromObject(commentPrototype));
            var commentCtorVal = FenValue.FromFunction(commentCtorFn);
            _domCommentPrototype = commentPrototype;
            SetGlobal("Comment", commentCtorVal);
            DefineWindowInterface(window, "Comment", commentCtorVal);

            var elementPrototype = new FenObject();
            elementPrototype.InternalClass = "ElementPrototype";
            elementPrototype.SetPrototype(nodePrototype);
            var matchesForwarder = FenValue.FromFunction(CreatePrototypeForwarder("matches", "matches"));
            elementPrototype.Set("matches", matchesForwarder);
            elementPrototype.Set("matchesSelector", matchesForwarder);
            elementPrototype.Set("webkitMatchesSelector", matchesForwarder);
            elementPrototype.Set("mozMatchesSelector", matchesForwarder);
            elementPrototype.Set("msMatchesSelector", matchesForwarder);
            elementPrototype.Set("oMatchesSelector", matchesForwarder);
            elementPrototype.Set("closest", FenValue.FromFunction(CreatePrototypeForwarder("closest", "closest")));
            elementPrototype.Set("querySelector", FenValue.FromFunction(CreatePrototypeForwarder("querySelector", "querySelector")));
            elementPrototype.Set("querySelectorAll", FenValue.FromFunction(CreatePrototypeForwarder("querySelectorAll", "querySelectorAll")));
            elementPrototype.Set("getElementsByTagName", FenValue.FromFunction(CreatePrototypeForwarder("getElementsByTagName", "getElementsByTagName")));
            elementPrototype.Set("getElementsByTagNameNS", FenValue.FromFunction(CreatePrototypeForwarder("getElementsByTagNameNS", "getElementsByTagNameNS")));
            elementPrototype.Set("getElementsByClassName", FenValue.FromFunction(CreatePrototypeForwarder("getElementsByClassName", "getElementsByClassName")));
            var elementCtorVal = CreateInterfaceConstructor("Element", elementPrototype);
            _domElementPrototype = elementPrototype;
            SetGlobal("Element", elementCtorVal);
            DefineWindowInterface(window, "Element", elementCtorVal);

            var htmlElementPrototype = new FenObject();
            htmlElementPrototype.InternalClass = "HTMLElementPrototype";
            htmlElementPrototype.SetPrototype(elementPrototype);
            var htmlElementCtorVal = CreateInterfaceConstructor("HTMLElement", htmlElementPrototype);
            _domHtmlElementPrototype = htmlElementPrototype;
            SetGlobal("HTMLElement", htmlElementCtorVal);
            DefineWindowInterface(window, "HTMLElement", htmlElementCtorVal);

            var documentPrototype = new FenObject();
            documentPrototype.InternalClass = "DocumentPrototype";
            documentPrototype.SetPrototype(nodePrototype);
            documentPrototype.Set("hasOwnProperty", FenValue.FromFunction(new FenFunction("hasOwnProperty",
                (args, thisVal) =>
                {
                    if (args.Length == 0)
                    {
                        return FenValue.FromBoolean(false);
                    }

                    return FenValue.FromBoolean(!string.IsNullOrEmpty(args[0].ToString()));
                })));
            documentPrototype.Set("querySelector", FenValue.FromFunction(CreatePrototypeForwarder("querySelector", "querySelector")));
            documentPrototype.Set("querySelectorAll", FenValue.FromFunction(CreatePrototypeForwarder("querySelectorAll", "querySelectorAll")));
            documentPrototype.Set("getElementById", FenValue.FromFunction(CreatePrototypeForwarder("getElementById", "getElementById")));
            documentPrototype.Set("createElement", FenValue.FromFunction(CreatePrototypeForwarder("createElement", "createElement")));
            documentPrototype.Set("createElementNS", FenValue.FromFunction(CreatePrototypeForwarder("createElementNS", "createElementNS")));
            documentPrototype.Set("createEvent", FenValue.FromFunction(CreatePrototypeForwarder("createEvent", "createEvent")));
            documentPrototype.Set("createRange", FenValue.FromFunction(CreatePrototypeForwarder("createRange", "createRange")));
            documentPrototype.Set("getElementsByClassName", FenValue.FromFunction(CreatePrototypeForwarder("getElementsByClassName", "getElementsByClassName")));
            documentPrototype.Set("getElementsByTagName", FenValue.FromFunction(CreatePrototypeForwarder("getElementsByTagName", "getElementsByTagName")));
            documentPrototype.Set("getElementsByTagNameNS", FenValue.FromFunction(CreatePrototypeForwarder("getElementsByTagNameNS", "getElementsByTagNameNS")));
            var documentCtorVal = CreateInterfaceConstructor("Document", documentPrototype);
            _domDocumentPrototype = documentPrototype;
            SetGlobal("Document", documentCtorVal);
            DefineWindowInterface(window, "Document", documentCtorVal);

            var requiredInterfaces = new[]
            {
                "AbortController", "AbortSignal", "Document", "DOMImplementation", "DocumentFragment",
                "ProcessingInstruction", "DocumentType", "Element", "CharacterData", "Text", "Comment",
                "NodeIterator", "TreeWalker", "NodeFilter", "NodeList", "HTMLCollection", "DOMTokenList", "HTMLElement"
            };

            foreach (var ifaceName in requiredInterfaces)
            {
                var existing = GetGlobal(ifaceName);
                FenValue ifaceValue;
                if (existing is FenValue existingFen && !existingFen.IsUndefined && !existingFen.IsNull)
                {
                    ifaceValue = existingFen;
                }
                else
                {
                    FenObject parentPrototype = null;
                    switch (ifaceName)
                    {
                        case "CharacterData":
                        case "DocumentFragment":
                        case "DocumentType":
                        case "ProcessingInstruction":
                            parentPrototype = nodePrototype;
                            break;
                    }

                    ifaceValue = CreatePlaceholderInterface(ifaceName, parentPrototype);
                    SetGlobal(ifaceName, ifaceValue);
                }

                DefineWindowInterface(window, ifaceName, ifaceValue);
            }

            var eventTargetVal = GetGlobal("EventTarget");
            if (eventTargetVal is FenValue eventTargetFen)
            {
                DefineWindowInterface(window, "EventTarget", eventTargetFen);
            }
            DefineWindowInterface(window, "Event", eventCtor);
            DefineWindowInterface(window, "UIEvent", uiEventCtor);
            DefineWindowInterface(window, "MouseEvent", mouseEventCtor);
            DefineWindowInterface(window, "KeyboardEvent", keyboardEventCtor);
            DefineWindowInterface(window, "GamepadEvent", gamepadEventCtor);
            DefineWindowInterface(window, "BeforeUnloadEvent", beforeUnloadEventCtor);
            DefineWindowInterface(window, "FocusEvent", focusEventCtor);
            DefineWindowInterface(window, "InputEvent", inputEventCtor);
            DefineWindowInterface(window, "CompositionEvent", compositionEventCtor);
            DefineWindowInterface(window, "PointerEvent", pointerEventCtor);
            DefineWindowInterface(window, "WheelEvent", wheelEventCtor);
            DefineWindowInterface(window, "TouchEvent", touchEventCtor);
            DefineWindowInterface(window, "AnimationEvent", animationEventCtor);
            DefineWindowInterface(window, "TransitionEvent", transitionEventCtor);
            DefineWindowInterface(window, "ErrorEvent", errorEventCtor);
            DefineWindowInterface(window, "CustomEvent", customEventCtor);

            var cssInterface = new FenObject();
            cssInterface.Set("supports", FenValue.FromFunction(new FenFunction("supports", (cssArgs, thisVal) =>
            {
                bool RejectProperty(string propertyName)
                {
                    if (string.IsNullOrWhiteSpace(propertyName))
                    {
                        return true;
                    }

                    propertyName = propertyName.Trim();
                    return string.Equals(propertyName, "--", StringComparison.Ordinal) ||
                           string.Equals(propertyName, "user-modify", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(propertyName, "-webkit-user-modify", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(propertyName, "-moz-user-modify", StringComparison.OrdinalIgnoreCase);
                }

                bool RejectCondition(string conditionText)
                {
                    if (string.IsNullOrWhiteSpace(conditionText))
                    {
                        return true;
                    }

                    var trimmed = conditionText.Trim();
                    return trimmed.IndexOf("user-modify", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           (trimmed.IndexOf("-webkit-user-modify", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            trimmed.IndexOf("-moz-user-modify", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            trimmed.IndexOf("user-modify", StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (cssArgs.Length == 0)
                {
                    return FenValue.FromBoolean(false);
                }

                if (cssArgs.Length == 1)
                {
                    var conditionText = cssArgs[0].ToString();
                    return FenValue.FromBoolean(!RejectCondition(conditionText));
                }

                var propertyName = cssArgs[0].ToString();
                var valueText = cssArgs[1].ToString();
                if (RejectProperty(propertyName) || string.IsNullOrWhiteSpace(valueText))
                {
                    return FenValue.FromBoolean(false);
                }

                return FenValue.FromBoolean(true);
            })));
            cssInterface.Set("escape", FenValue.FromFunction(new FenFunction("escape", (cssArgs, thisVal) =>
            {
                var input = cssArgs.Length > 0 ? cssArgs[0].ToString() ?? string.Empty : string.Empty;
                if (input.Length == 0)
                {
                    return FenValue.FromString(string.Empty);
                }

                var sb = new StringBuilder(input.Length * 2);
                for (var i = 0; i < input.Length; i++)
                {
                    var ch = input[i];
                    var isAsciiLetterOrDigit = (ch >= 'a' && ch <= 'z') ||
                                               (ch >= 'A' && ch <= 'Z') ||
                                               (ch >= '0' && ch <= '9');
                    if (isAsciiLetterOrDigit || ch == '_' || ch == '-')
                    {
                        sb.Append(ch);
                    }
                    else
                    {
                        sb.Append('\\');
                        sb.Append(ch);
                    }
                }

                return FenValue.FromString(sb.ToString());
            })));
            var cssInterfaceValue = FenValue.FromObject(cssInterface);
            SetGlobal("CSS", cssInterfaceValue);
            DefineWindowInterface(window, "CSS", cssInterfaceValue);

            // Minimal XPathResult constants needed by WPT XPath tests.
            var xpathResult = new FenObject();
            xpathResult.Set("ANY_TYPE", FenValue.FromNumber(0));
            xpathResult.Set("NUMBER_TYPE", FenValue.FromNumber(1));
            xpathResult.Set("STRING_TYPE", FenValue.FromNumber(2));
            xpathResult.Set("BOOLEAN_TYPE", FenValue.FromNumber(3));
            xpathResult.Set("UNORDERED_NODE_ITERATOR_TYPE", FenValue.FromNumber(4));
            xpathResult.Set("ORDERED_NODE_ITERATOR_TYPE", FenValue.FromNumber(5));
            xpathResult.Set("UNORDERED_NODE_SNAPSHOT_TYPE", FenValue.FromNumber(6));
            xpathResult.Set("ORDERED_NODE_SNAPSHOT_TYPE", FenValue.FromNumber(7));
            xpathResult.Set("ANY_UNORDERED_NODE_TYPE", FenValue.FromNumber(8));
            xpathResult.Set("FIRST_ORDERED_NODE_TYPE", FenValue.FromNumber(9));
            var xpathResultVal = FenValue.FromObject(xpathResult);
            SetGlobal("XPathResult", xpathResultVal);
            DefineWindowInterface(window, "XPathResult", xpathResultVal);

            // Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ performance object Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            var perfStartTime = System.Diagnostics.Stopwatch.GetTimestamp();
            var perfFreq = (double)System.Diagnostics.Stopwatch.Frequency;
            var performanceEntries = new List<FenObject>();
            var performanceObservers = new List<Dictionary<string, object>>();

            double GetPerformanceNow()
            {
                return (System.Diagnostics.Stopwatch.GetTimestamp() - perfStartTime) / perfFreq * 1000.0;
            }

            FenObject CreatePerformanceEntriesArray(IEnumerable<FenObject> entries)
            {
                var arr = FenObject.CreateArray();
                var index = 0;
                foreach (var entry in entries)
                {
                    arr.Set(index.ToString(), FenValue.FromObject(entry));
                    index++;
                }

                arr.Set("length", FenValue.FromNumber(index));
                return arr;
            }

            FenObject CreatePerformanceEntry(string name, string entryType, double startTime, double duration)
            {
                var entry = new FenObject();
                entry.Set("name", FenValue.FromString(name ?? string.Empty));
                entry.Set("entryType", FenValue.FromString(entryType ?? string.Empty));
                entry.Set("startTime", FenValue.FromNumber(Math.Round(startTime, 2)));
                entry.Set("duration", FenValue.FromNumber(Math.Round(duration, 2)));
                entry.Set("toJSON", FenValue.FromFunction(new FenFunction("toJSON", (entryArgs, entryThis) =>
                {
                    var json = new FenObject();
                    json.Set("name", entry.Get("name"));
                    json.Set("entryType", entry.Get("entryType"));
                    json.Set("startTime", entry.Get("startTime"));
                    json.Set("duration", entry.Get("duration"));
                    return FenValue.FromObject(json);
                })));
                return entry;
            }

            bool EntryMatchesName(FenObject entry, string? name)
            {
                return string.IsNullOrEmpty(name) ||
                       string.Equals(entry.Get("name").ToString(), name, StringComparison.Ordinal);
            }

            bool EntryMatchesType(FenObject entry, string? entryType)
            {
                return string.IsNullOrEmpty(entryType) ||
                       string.Equals(entry.Get("entryType").ToString(), entryType, StringComparison.Ordinal);
            }

            Dictionary<string, object> GetOrCreatePerformanceObserverState(FenObject observer, FenFunction callback)
            {
                foreach (var state in performanceObservers)
                {
                    if (ReferenceEquals(state["observer"], observer))
                    {
                        state["callback"] = callback;
                        return state;
                    }
                }

                var created = new Dictionary<string, object>
                {
                    ["observer"] = observer,
                    ["callback"] = callback,
                    ["entryTypes"] = new HashSet<string>(StringComparer.Ordinal),
                    ["queue"] = new List<FenObject>()
                };
                performanceObservers.Add(created);
                return created;
            }

            HashSet<string> ReadObserverEntryTypes(FenObject options)
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                var entryTypesValue = options.Get("entryTypes");
                if (entryTypesValue.IsObject)
                {
                    var entryTypesObject = entryTypesValue.AsObject();
                    var lengthValue = entryTypesObject.Get("length");
                    var length = lengthValue.IsNumber ? Math.Max(0, (int)lengthValue.ToNumber()) : 0;
                    for (var i = 0; i < length; i++)
                    {
                        var typeName = entryTypesObject.Get(i.ToString()).ToString();
                        if (!string.IsNullOrWhiteSpace(typeName))
                        {
                            result.Add(typeName.Trim());
                        }
                    }
                }

                var singleTypeValue = options.Get("type");
                if (!singleTypeValue.IsUndefined && !singleTypeValue.IsNull)
                {
                    var singleType = singleTypeValue.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(singleType))
                    {
                        result.Add(singleType);
                    }
                }

                return result;
            }

            void FlushPerformanceObserver(Dictionary<string, object> state)
            {
                var queue = (List<FenObject>)state["queue"];
                if (queue.Count == 0)
                {
                    return;
                }

                var callback = (FenFunction)state["callback"];
                var observer = (FenObject)state["observer"];
                var snapshot = new List<FenObject>(queue);
                queue.Clear();

                var entryList = new FenObject();
                entryList.Set("getEntries", FenValue.FromFunction(new FenFunction("getEntries", (listArgs, listThis) =>
                    FenValue.FromObject(CreatePerformanceEntriesArray(snapshot)))));
                entryList.Set("getEntriesByType", FenValue.FromFunction(new FenFunction("getEntriesByType", (listArgs, listThis) =>
                {
                    var requestedType = listArgs.Length > 0 ? listArgs[0].ToString() : string.Empty;
                    return FenValue.FromObject(CreatePerformanceEntriesArray(snapshot.Where(entry => EntryMatchesType(entry, requestedType))));
                })));
                entryList.Set("getEntriesByName", FenValue.FromFunction(new FenFunction("getEntriesByName", (listArgs, listThis) =>
                {
                    var requestedName = listArgs.Length > 0 ? listArgs[0].ToString() : string.Empty;
                    var requestedType = listArgs.Length > 1 ? listArgs[1].ToString() : string.Empty;
                    return FenValue.FromObject(CreatePerformanceEntriesArray(snapshot.Where(entry =>
                        EntryMatchesName(entry, requestedName) && EntryMatchesType(entry, requestedType))));
                })));

                _context.ScheduleCallback(() =>
                {
                    callback.Invoke(new[] { FenValue.FromObject(entryList), FenValue.FromObject(observer) }, _context);
                }, 0);
            }

            void QueuePerformanceEntry(FenObject entry)
            {
                performanceEntries.Add(entry);
                foreach (var observerState in performanceObservers.ToList())
                {
                    var entryTypes = (HashSet<string>)observerState["entryTypes"];
                    if (!entryTypes.Contains(entry.Get("entryType").ToString()))
                    {
                        continue;
                    }

                    var queue = (List<FenObject>)observerState["queue"];
                    queue.Add(entry);
                    FlushPerformanceObserver(observerState);
                }
            }

            var performanceObj = new FenObject();
            performanceObj.Set("now", FenValue.FromFunction(new FenFunction("now", (args, thisVal) =>
            {
                return FenValue.FromNumber(Math.Round(GetPerformanceNow(), 2));
            })));
            performanceObj.Set("timeOrigin", FenValue.FromNumber(
                (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds));
            performanceObj.Set("getEntries",
                FenValue.FromFunction(new FenFunction("getEntries",
                    (args, thisVal) => FenValue.FromObject(CreatePerformanceEntriesArray(performanceEntries)))));
            performanceObj.Set("getEntriesByType",
                FenValue.FromFunction(new FenFunction("getEntriesByType",
                    (args, thisVal) =>
                    {
                        var requestedType = args.Length > 0 ? args[0].ToString() : string.Empty;
                        return FenValue.FromObject(CreatePerformanceEntriesArray(
                            performanceEntries.Where(entry => EntryMatchesType(entry, requestedType))));
                    })));
            performanceObj.Set("getEntriesByName",
                FenValue.FromFunction(new FenFunction("getEntriesByName",
                    (args, thisVal) =>
                    {
                        var requestedName = args.Length > 0 ? args[0].ToString() : string.Empty;
                        var requestedType = args.Length > 1 ? args[1].ToString() : string.Empty;
                        return FenValue.FromObject(CreatePerformanceEntriesArray(
                            performanceEntries.Where(entry =>
                                EntryMatchesName(entry, requestedName) && EntryMatchesType(entry, requestedType))));
                    })));
            performanceObj.Set("mark",
                FenValue.FromFunction(new FenFunction("mark", (args, thisVal) =>
                {
                    var markName = args.Length > 0 ? args[0].ToString() : string.Empty;
                    if (string.IsNullOrWhiteSpace(markName))
                    {
                        throw new FenTypeError("TypeError: performance.mark requires a non-empty mark name");
                    }

                    var entry = CreatePerformanceEntry(markName, "mark", GetPerformanceNow(), 0);
                    QueuePerformanceEntry(entry);
                    return FenValue.Undefined;
                })));
            performanceObj.Set("measure",
                FenValue.FromFunction(new FenFunction("measure", (args, thisVal) =>
                {
                    var measureName = args.Length > 0 ? args[0].ToString() : string.Empty;
                    if (string.IsNullOrWhiteSpace(measureName))
                    {
                        throw new FenTypeError("TypeError: performance.measure requires a non-empty measure name");
                    }

                    var endTime = GetPerformanceNow();
                    var startTime = 0.0;

                    if (args.Length > 1 && !args[1].IsUndefined && !args[1].IsNull)
                    {
                        var startMarkName = args[1].ToString();
                        var startEntry = performanceEntries.LastOrDefault(entry =>
                            EntryMatchesType(entry, "mark") && EntryMatchesName(entry, startMarkName));
                        if (startEntry == null)
                        {
                            throw new FenTypeError($"SyntaxError: The mark '{startMarkName}' does not exist");
                        }

                        startTime = startEntry.Get("startTime").ToNumber();
                    }

                    if (args.Length > 2 && !args[2].IsUndefined && !args[2].IsNull)
                    {
                        var endMarkName = args[2].ToString();
                        var endEntry = performanceEntries.LastOrDefault(entry =>
                            EntryMatchesType(entry, "mark") && EntryMatchesName(entry, endMarkName));
                        if (endEntry == null)
                        {
                            throw new FenTypeError($"SyntaxError: The mark '{endMarkName}' does not exist");
                        }

                        endTime = endEntry.Get("startTime").ToNumber();
                    }

                    var entry = CreatePerformanceEntry(measureName, "measure", startTime, Math.Max(0, endTime - startTime));
                    QueuePerformanceEntry(entry);
                    return FenValue.Undefined;
                })));
            performanceObj.Set("clearMarks",
                FenValue.FromFunction(new FenFunction("clearMarks", (args, thisVal) =>
                {
                    var requestedName = args.Length > 0 ? args[0].ToString() : string.Empty;
                    performanceEntries.RemoveAll(entry =>
                        EntryMatchesType(entry, "mark") && EntryMatchesName(entry, requestedName));
                    return FenValue.Undefined;
                })));
            performanceObj.Set("clearMeasures",
                FenValue.FromFunction(new FenFunction("clearMeasures", (args, thisVal) =>
                {
                    var requestedName = args.Length > 0 ? args[0].ToString() : string.Empty;
                    performanceEntries.RemoveAll(entry =>
                        EntryMatchesType(entry, "measure") && EntryMatchesName(entry, requestedName));
                    return FenValue.Undefined;
                })));
            SetGlobal("performance", FenValue.FromObject(performanceObj));
            window.Set("performance", FenValue.FromObject(performanceObj));

            var performanceObserverCtor = new FenFunction("PerformanceObserver", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction)
                {
                    throw new FenTypeError("TypeError: PerformanceObserver requires a callback");
                }

                var callback = args[0].AsFunction();
                var observer = new FenObject();
                var state = GetOrCreatePerformanceObserverState(observer, callback);

                observer.Set("observe", FenValue.FromFunction(new FenFunction("observe", (observeArgs, observeThis) =>
                {
                    if (observeArgs.Length == 0 || !observeArgs[0].IsObject)
                    {
                        throw new FenTypeError("TypeError: PerformanceObserver.observe requires an options object");
                    }

                    var options = observeArgs[0].AsObject() as FenObject;
                    if (options == null)
                    {
                        throw new FenTypeError("TypeError: PerformanceObserver.observe requires an options object");
                    }

                    var entryTypes = ReadObserverEntryTypes(options);
                    if (entryTypes.Count == 0)
                    {
                        throw new FenTypeError("TypeError: PerformanceObserver.observe requires type or entryTypes");
                    }

                    state["entryTypes"] = entryTypes;

                    var queue = (List<FenObject>)state["queue"];
                    queue.Clear();

                    var buffered = options.Get("buffered").ToBoolean();
                    if (buffered)
                    {
                        foreach (var entry in performanceEntries)
                        {
                            if (entryTypes.Contains(entry.Get("entryType").ToString()))
                            {
                                queue.Add(entry);
                            }
                        }

                        FlushPerformanceObserver(state);
                    }

                    return FenValue.Undefined;
                })));
                observer.Set("disconnect", FenValue.FromFunction(new FenFunction("disconnect", (disconnectArgs, disconnectThis) =>
                {
                    var queue = (List<FenObject>)state["queue"];
                    queue.Clear();
                    ((HashSet<string>)state["entryTypes"]).Clear();
                    return FenValue.Undefined;
                })));
                observer.Set("takeRecords", FenValue.FromFunction(new FenFunction("takeRecords", (takeArgs, takeThis) =>
                {
                    var queue = (List<FenObject>)state["queue"];
                    var snapshot = new List<FenObject>(queue);
                    queue.Clear();
                    return FenValue.FromObject(CreatePerformanceEntriesArray(snapshot));
                })));

                return FenValue.FromObject(observer);
            });
            performanceObserverCtor.Set("supportedEntryTypes", FenValue.FromObject(CreateArray(new[]
            {
                FenValue.FromString("mark"),
                FenValue.FromString("measure")
            })));
            SetGlobal("PerformanceObserver", FenValue.FromFunction(performanceObserverCtor));
            window.Set("PerformanceObserver", FenValue.FromFunction(performanceObserverCtor));

            // Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ TextEncoder Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            SetGlobal("TextEncoder", FenValue.FromFunction(new FenFunction("TextEncoder", (args, thisVal) =>
            {
                var encoder = new FenObject();
                encoder.Set("encoding", FenValue.FromString("utf-8"));
                encoder.Set("encode", FenValue.FromFunction(new FenFunction("encode", (encArgs, encThis) =>
                {
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
                encoder.Set("encodeInto", FenValue.FromFunction(new FenFunction("encodeInto", (encArgs, encThis) =>
                {
                    var result = new FenObject();
                    result.Set("read", FenValue.FromNumber(0));
                    result.Set("written", FenValue.FromNumber(0));
                    return FenValue.FromObject(result);
                })));
                return FenValue.FromObject(encoder);
            })));

            // Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ TextDecoder Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            SetGlobal("TextDecoder", FenValue.FromFunction(new FenFunction("TextDecoder", (args, thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "utf-8";
                var decoder = new FenObject();
                decoder.Set("encoding", FenValue.FromString(label));
                decoder.Set("fatal", FenValue.FromBoolean(false));
                decoder.Set("ignoreBOM", FenValue.FromBoolean(false));
                decoder.Set("decode", FenValue.FromFunction(new FenFunction("decode", (decArgs, decThis) =>
                {
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

            // Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ AbortController / AbortSignal Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            SetGlobal("AbortController", FenValue.FromFunction(new FenFunction("AbortController", (args, thisVal) =>
            {
                var controller = new FenObject();
                var signal = new FenObject();
                signal.Set("aborted", FenValue.FromBoolean(false));
                signal.Set("reason", FenValue.Undefined);
                signal.Set("onabort", FenValue.Undefined);
                var signalListeners = new List<FenObject>();
                signal.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener",
                    (sigArgs, sigThis) =>
                    {
                        if (sigArgs.Length < 2)
                        {
                            return FenValue.Undefined;
                        }

                        var type = sigArgs[0].ToString();
                        var callback = sigArgs[1];
                        var callbackIsValid = callback.IsFunction || (callback.IsObject && !callback.IsNull);
                        if (!string.Equals(type, "abort", StringComparison.OrdinalIgnoreCase) ||
                            !callbackIsValid || callback.IsUndefined || callback.IsNull)
                        {
                            return FenValue.Undefined;
                        }

                        var capture = false;
                        var once = false;
                        if (sigArgs.Length >= 3)
                        {
                            if (sigArgs[2].IsBoolean)
                            {
                                capture = sigArgs[2].ToBoolean();
                            }
                            else if (sigArgs[2].IsObject)
                            {
                                var opts = sigArgs[2].AsObject();
                                capture = opts.Get("capture").ToBoolean();
                                once = opts.Get("once").ToBoolean();
                            }
                        }

                        foreach (var existing in signalListeners)
                        {
                            if (existing.Get("callback").Equals(callback) &&
                                existing.Get("capture").ToBoolean() == capture)
                            {
                                return FenValue.Undefined;
                            }
                        }

                        var entry = new FenObject();
                        entry.Set("callback", callback);
                        entry.Set("capture", FenValue.FromBoolean(capture));
                        entry.Set("once", FenValue.FromBoolean(once));
                        signalListeners.Add(entry);
                        return FenValue.Undefined;
                    })));
                signal.Set("removeEventListener",
                    FenValue.FromFunction(new FenFunction("removeEventListener",
                        (sigArgs, sigThis) =>
                        {
                            if (sigArgs.Length < 2)
                            {
                                return FenValue.Undefined;
                            }

                            var type = sigArgs[0].ToString();
                            var callback = sigArgs[1];
                            var capture = false;
                            if (sigArgs.Length >= 3)
                            {
                                if (sigArgs[2].IsBoolean)
                                {
                                    capture = sigArgs[2].ToBoolean();
                                }
                                else if (sigArgs[2].IsObject)
                                {
                                    capture = sigArgs[2].AsObject().Get("capture").ToBoolean();
                                }
                            }

                            if (!string.Equals(type, "abort", StringComparison.OrdinalIgnoreCase))
                            {
                                return FenValue.Undefined;
                            }

                            for (int i = signalListeners.Count - 1; i >= 0; i--)
                            {
                                var existing = signalListeners[i];
                                if (existing.Get("callback").Equals(callback) &&
                                    existing.Get("capture").ToBoolean() == capture)
                                {
                                    signalListeners.RemoveAt(i);
                                    break;
                                }
                            }

                            return FenValue.Undefined;
                        })));
                signal.Set("throwIfAborted", FenValue.FromFunction(new FenFunction("throwIfAborted",
                    (sigArgs, sigThis) =>
                    {
                        if (signal.Get("aborted").ToBoolean())
                            throw new FenTypeError("AbortError: signal is aborted");
                        return FenValue.Undefined;
                    })));

                controller.Set("signal", FenValue.FromObject(signal));
                controller.Set("abort", FenValue.FromFunction(new FenFunction("abort", (abortArgs, abortThis) =>
                {
                    if (signal.Get("aborted").ToBoolean())
                    {
                        return FenValue.Undefined;
                    }

                    signal.Set("aborted", FenValue.FromBoolean(true));
                    var reason = abortArgs.Length > 0 ? abortArgs[0] : FenValue.FromString("AbortError");
                    signal.Set("reason", reason);
                    var abortEvent = new DomEvent("abort", false, false, false, _context);
                    abortEvent.Set("target", FenValue.FromObject(signal), _context);

                    var onAbort = signal.Get("onabort");
                    if (onAbort.IsFunction)
                    {
                        onAbort.AsFunction()?.Invoke(new[] { FenValue.FromObject(abortEvent) }, _context, FenValue.FromObject(signal));
                    }

                    foreach (var listener in signalListeners.ToList())
                    {
                        var callback = listener.Get("callback");
                        FenFunction callbackFn = null;
                        var callbackThis = FenValue.FromObject(signal);
                        if (callback.IsFunction)
                        {
                            callbackFn = callback.AsFunction() as FenFunction;
                        }
                        else if (callback.IsObject)
                        {
                            var handleEvent = callback.AsObject().Get("handleEvent");
                            if (handleEvent.IsFunction)
                            {
                                callbackFn = handleEvent.AsFunction() as FenFunction;
                                callbackThis = callback;
                            }
                        }

                        callbackFn?.Invoke(new[] { FenValue.FromObject(abortEvent) }, _context, callbackThis);

                        if (listener.Get("once").ToBoolean())
                        {
                            signalListeners.Remove(listener);
                        }
                    }

                    return FenValue.Undefined;
                })));

                return FenValue.FromObject(controller);
            })));

            // Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ WebSocket Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            SetGlobal("WebSocket", FenValue.FromFunction(new FenFunction("WebSocket", (args, thisVal) =>
            {
                var url = args.Length > 0 ? args[0].ToString() : string.Empty;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var wsUri) ||
                    (!wsUri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) &&
                     !wsUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new FenTypeError("TypeError: WebSocket error");
                }

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

                var listeners = new Dictionary<string, List<FenValue>>(StringComparer.OrdinalIgnoreCase);
                var socket = new ClientWebSocket();
                var socketCts = new CancellationTokenSource();
                var wsLock = new object();
                ws.NativeObject = socket;

                Action<string, Action<FenObject>> dispatch = (eventType, initializer) =>
                {
                    var evt = new FenObject();
                    evt.Set("type", FenValue.FromString(eventType));
                    evt.Set("target", FenValue.FromObject(ws));
                    initializer?.Invoke(evt);

                    var prop = ws.Get($"on{eventType}");
                    if (prop.IsFunction)
                    {
                        prop.AsFunction()?.Invoke(new[] { FenValue.FromObject(evt) }, _context);
                    }

                    if (listeners.TryGetValue(eventType, out var handlers))
                    {
                        foreach (var handler in handlers.ToArray())
                        {
                            if (handler.IsFunction)
                            {
                                handler.AsFunction()?.Invoke(new[] { FenValue.FromObject(evt) }, _context);
                            }
                        }
                    }
                };

                ws.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (eArgs, eThis) =>
                {
                    if (eArgs.Length < 2 || !eArgs[0].IsString || !eArgs[1].IsFunction)
                        return FenValue.Undefined;

                    var type = eArgs[0].ToString();
                    if (!listeners.TryGetValue(type, out var handlerList))
                    {
                        handlerList = new List<FenValue>();
                        listeners[type] = handlerList;
                    }

                    handlerList.Add(eArgs[1]);
                    return FenValue.Undefined;
                })));

                ws.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener",
                    (eArgs, eThis) =>
                    {
                        if (eArgs.Length < 2 || !eArgs[0].IsString)
                            return FenValue.Undefined;

                        var type = eArgs[0].ToString();
                        if (listeners.TryGetValue(type, out var handlerList))
                        {
                            var callback = eArgs[1];
                            handlerList.RemoveAll(existing =>
                                ReferenceEquals(existing.AsObject(), callback.AsObject()));
                        }

                        return FenValue.Undefined;
                    })));

                ws.Set("send", FenValue.FromFunction(new FenFunction("send", (sendArgs, sendThis) =>
                {
                    if (sendArgs.Length < 1)
                        return FenValue.Undefined;

                    var payload = sendArgs[0].ToString() ?? string.Empty;
                    lock (wsLock)
                    {
                        if ((int)ws.Get("readyState").ToNumber() != 1)
                            throw new FenTypeError("TypeError: WebSocket error");
                    }

                    _ = RunDetachedAsync(async () =>
                    {
                        try
                        {
                            var bytes = Encoding.UTF8.GetBytes(payload);
                            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                                socketCts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            dispatch("error", evt => evt.Set("message", FenValue.FromString(ex.Message)));
                        }
                    });

                    return FenValue.Undefined;
                })));

                ws.Set("close", FenValue.FromFunction(new FenFunction("close", (closeArgs, closeThis) =>
                {
                    lock (wsLock)
                    {
                        var readyState = (int)ws.Get("readyState").ToNumber();
                        if (readyState == 2 || readyState == 3)
                            return FenValue.Undefined;
                        ws.Set("readyState", FenValue.FromNumber(2)); // CLOSING
                    }

                    _ = RunDetachedAsync(async () =>
                    {
                        try
                        {
                            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                            {
                                await socket
                                    .CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None)
                                    .ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                        }
                        finally
                        {
                            lock (wsLock)
                            {
                                ws.Set("readyState", FenValue.FromNumber(3));
                            }

                            dispatch("close", evt =>
                            {
                                evt.Set("code", FenValue.FromNumber(1000));
                                evt.Set("reason", FenValue.FromString("closed"));
                                evt.Set("wasClean", FenValue.FromBoolean(true));
                            });
                            try
                            {
                                socket.Dispose();
                            }
                            catch
                            {
                            }

                            try
                            {
                                socketCts.Cancel();
                                socketCts.Dispose();
                            }
                            catch
                            {
                            }
                        }
                    });

                    return FenValue.Undefined;
                })));

                _ = RunDetachedAsync(async () =>
                {
                    try
                    {
                        await socket.ConnectAsync(wsUri, socketCts.Token).ConfigureAwait(false);
                        lock (wsLock)
                        {
                            ws.Set("readyState", FenValue.FromNumber(1)); // OPEN
                            ws.Set("protocol", FenValue.FromString(socket.SubProtocol ?? string.Empty));
                        }

                        dispatch("open", null);

                        var buffer = new byte[8192];
                        while (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                        {
                            var segment = new ArraySegment<byte>(buffer);
                            using var builder = new System.IO.MemoryStream();
                            WebSocketReceiveResult receive;
                            do
                            {
                                receive = await socket.ReceiveAsync(segment, socketCts.Token).ConfigureAwait(false);
                                if (receive.Count > 0)
                                {
                                    builder.Write(buffer, 0, receive.Count);
                                }
                            } while (!receive.EndOfMessage && socket.State == WebSocketState.Open);

                            if (receive.MessageType == WebSocketMessageType.Close)
                            {
                                lock (wsLock)
                                {
                                    ws.Set("readyState", FenValue.FromNumber(3));
                                }

                                dispatch("close", evt =>
                                {
                                    evt.Set("code",
                                        FenValue.FromNumber((int)(receive.CloseStatus ??
                                                                  WebSocketCloseStatus.NormalClosure)));
                                    evt.Set("reason",
                                        FenValue.FromString(receive.CloseStatusDescription ?? string.Empty));
                                    evt.Set("wasClean", FenValue.FromBoolean(true));
                                });
                                break;
                            }

                            if (receive.MessageType == WebSocketMessageType.Text)
                            {
                                var message = Encoding.UTF8.GetString(builder.ToArray());
                                dispatch("message", evt => evt.Set("data", FenValue.FromString(message)));
                            }
                            else if (receive.MessageType == WebSocketMessageType.Binary)
                            {
                                var b64 = Convert.ToBase64String(builder.ToArray());
                                dispatch("message", evt => evt.Set("data", FenValue.FromString(b64)));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (wsLock)
                        {
                            ws.Set("readyState", FenValue.FromNumber(3));
                        }

                        dispatch("error", evt => evt.Set("message", FenValue.FromString(ex.Message)));
                    }
                });

                return FenValue.FromObject(ws);
            })));
            // Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ structuredClone Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            SetGlobal("structuredClone", FenValue.FromFunction(new FenFunction("structuredClone", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;

                var visited = new Dictionary<FenObject, FenValue>();
                int cloneBudget = 0;

                FenValue DeepClone(FenValue v)
                {
                    if (v.IsFunction)
                    {
                        throw new FenTypeError("DataCloneError: structuredClone cannot clone functions");
                    }

                    if (!v.IsObject) return v; // primitives are copied by value
                    var src = v.AsObject() as FenObject;
                    if (src == null) return v;

                    if (visited.TryGetValue(src, out var existing))
                    {
                        return existing;
                    }

                    cloneBudget++;
                    if (cloneBudget > 10000)
                    {
                        throw new FenResourceError("DataCloneError: structuredClone graph is too large");
                    }

                    if (src is FenBrowser.FenEngine.Core.Types.JsArrayBuffer srcBuffer)
                    {
                        var bufferClone = new FenBrowser.FenEngine.Core.Types.JsArrayBuffer(srcBuffer.Data.Length);
                        Array.Copy(srcBuffer.Data, bufferClone.Data, srcBuffer.Data.Length);
                        var clonedBufferValue = FenValue.FromObject(bufferClone);
                        visited[src] = clonedBufferValue;
                        return clonedBufferValue;
                    }

                    if (src is FenBrowser.FenEngine.Core.Types.JsUint8Array srcUint8Array)
                    {
                        var clonedBufferValue = DeepClone(FenValue.FromObject(srcUint8Array.Buffer));
                        var clonedBuffer = clonedBufferValue.AsObject() as FenBrowser.FenEngine.Core.Types.JsArrayBuffer;
                        var typedArrayClone = new FenBrowser.FenEngine.Core.Types.JsUint8Array(
                            FenValue.FromObject(clonedBuffer),
                            FenValue.FromNumber(srcUint8Array.ByteOffset),
                            FenValue.FromNumber(srcUint8Array.Length));
                        var typedArrayCloneValue = FenValue.FromObject(typedArrayClone);
                        visited[src] = typedArrayCloneValue;
                        return typedArrayCloneValue;
                    }

                    if (src is FenBrowser.FenEngine.Core.Types.JsFloat32Array srcFloat32Array)
                    {
                        var clonedBufferValue = DeepClone(FenValue.FromObject(srcFloat32Array.Buffer));
                        var clonedBuffer = clonedBufferValue.AsObject() as FenBrowser.FenEngine.Core.Types.JsArrayBuffer;
                        var typedArrayClone = new FenBrowser.FenEngine.Core.Types.JsFloat32Array(
                            FenValue.FromObject(clonedBuffer),
                            FenValue.FromNumber(srcFloat32Array.ByteOffset),
                            FenValue.FromNumber(srcFloat32Array.Length));
                        var typedArrayCloneValue = FenValue.FromObject(typedArrayClone);
                        visited[src] = typedArrayCloneValue;
                        return typedArrayCloneValue;
                    }

                    if (src is FenBrowser.FenEngine.Core.Types.JsDataView srcDataView)
                    {
                        var clonedBufferValue = DeepClone(FenValue.FromObject(srcDataView.Buffer));
                        var clonedBuffer = clonedBufferValue.AsObject() as FenBrowser.FenEngine.Core.Types.JsArrayBuffer;
                        var viewClone = new FenBrowser.FenEngine.Core.Types.JsDataView(
                            clonedBuffer,
                            srcDataView.ByteOffset,
                            srcDataView.ByteLength);
                        var viewCloneValue = FenValue.FromObject(viewClone);
                        visited[src] = viewCloneValue;
                        return viewCloneValue;
                    }

                    bool isArray = src.InternalClass == "Array";
                    var clone = isArray ? FenObject.CreateArray() : new FenObject();
                    clone.InternalClass = src.InternalClass;
                    clone.SetPrototype(src.GetPrototype());
                    var cloneValue = FenValue.FromObject(clone);
                    visited[src] = cloneValue;
                    foreach (var key in src.Keys())
                    {
                        clone.Set(key, DeepClone(src.Get(key)));
                    }

                    return cloneValue;
                }

                return DeepClone(args[0]);
            })));

            // crypto and Intl are registered later in InitializeBuiltins (fuller implementations)

            // Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ getComputedStyle Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            var getComputedStyleFn = FenValue.FromFunction(new FenFunction("getComputedStyle", (args, thisVal) =>
            {
                static string ToCamelCase(string propertyName)
                {
                    if (string.IsNullOrEmpty(propertyName) || propertyName.IndexOf('-') < 0)
                    {
                        return propertyName ?? string.Empty;
                    }

                    var parts = propertyName.Split('-', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                    {
                        return propertyName;
                    }

                    var builder = new System.Text.StringBuilder(parts[0]);
                    for (int i = 1; i < parts.Length; i++)
                    {
                        if (parts[i].Length == 0)
                        {
                            continue;
                        }

                        builder.Append(char.ToUpperInvariant(parts[i][0]));
                        if (parts[i].Length > 1)
                        {
                            builder.Append(parts[i].Substring(1));
                        }
                    }

                    return builder.ToString();
                }

                static string NormalizeBorderLineWidthValue(string value)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return value ?? string.Empty;
                    }

                    var trimmed = value.Trim();
                    if (!trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmed;
                    }

                    if (!double.TryParse(trimmed.Substring(0, trimmed.Length - 2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var numeric))
                    {
                        return trimmed;
                    }

                    if (numeric <= 0)
                    {
                        return "0px";
                    }

                    var rounded = numeric < 1 ? 1 : Math.Floor(numeric);
                    return rounded.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";
                }

                static string NormalizeComputedValue(string propertyName, string value)
                {
                    if (propertyName != null &&
                        (propertyName.Contains("border", StringComparison.OrdinalIgnoreCase) || string.Equals(propertyName, "outline-width", StringComparison.OrdinalIgnoreCase)) &&
                        propertyName.EndsWith("width", StringComparison.OrdinalIgnoreCase))
                    {
                        return NormalizeBorderLineWidthValue(value);
                    }

                    return value ?? string.Empty;
                }

                bool IsCurrentColorSentinel(SkiaSharp.SKColor color)
                {
                    return color.Red == 255 && color.Green == 0 && color.Blue == 255 && color.Alpha == 1;
                }

                string SerializeComputedColor(SkiaSharp.SKColor color)
                {
                    if (color.Alpha >= 255)
                    {
                        return $"rgb({color.Red}, {color.Green}, {color.Blue})";
                    }

                    var alpha = Math.Round(color.Alpha / 255.0, 3);
                    return string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "rgba({0}, {1}, {2}, {3:0.###})",
                        color.Red,
                        color.Green,
                        color.Blue,
                        alpha);
                }

                Dictionary<string, string> ParseInlineStyleMap(string styleText)
                {
                    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(styleText))
                    {
                        return result;
                    }

                    foreach (var declaration in styleText.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var colonIndex = declaration.IndexOf(':');
                        if (colonIndex <= 0 || colonIndex >= declaration.Length - 1)
                        {
                            continue;
                        }

                        var name = declaration.Substring(0, colonIndex).Trim();
                        var value = declaration.Substring(colonIndex + 1).Trim();
                        if (name.Length == 0)
                        {
                            continue;
                        }

                        result[name] = value;
                    }

                    return result;
                }

                List<string> SplitFunctionArguments(string input)
                {
                    var result = new List<string>();
                    if (string.IsNullOrEmpty(input))
                    {
                        return result;
                    }

                    var depth = 0;
                    var start = 0;
                    for (var i = 0; i < input.Length; i++)
                    {
                        var c = input[i];
                        if (c == '(')
                        {
                            depth++;
                        }
                        else if (c == ')')
                        {
                            depth = Math.Max(0, depth - 1);
                        }
                        else if (c == ',' && depth == 0)
                        {
                            result.Add(input.Substring(start, i - start).Trim());
                            start = i + 1;
                        }
                    }

                    if (start < input.Length)
                    {
                        result.Add(input.Substring(start).Trim());
                    }

                    return result;
                }

                bool TryExtractFunctionBody(string value, string functionName, out string inner)
                {
                    inner = string.Empty;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return false;
                    }

                    var trimmed = value.Trim();
                    if (!trimmed.StartsWith(functionName, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var open = trimmed.IndexOf('(');
                    var close = trimmed.LastIndexOf(')');
                    if (open <= 0 || close <= open)
                    {
                        return false;
                    }

                    inner = trimmed.Substring(open + 1, close - open - 1);
                    return true;
                }

                double ToLinearComponent(byte component)
                {
                    var srgb = component / 255.0;
                    return srgb <= 0.04045
                        ? srgb / 12.92
                        : Math.Pow((srgb + 0.055) / 1.055, 2.4);
                }

                SkiaSharp.SKColor PickBlackOrWhiteContrast(SkiaSharp.SKColor color)
                {
                    var luminance =
                        0.2126 * ToLinearComponent(color.Red) +
                        0.7152 * ToLinearComponent(color.Green) +
                        0.0722 * ToLinearComponent(color.Blue);

                    var contrastWithBlack = (luminance + 0.05) / 0.05;
                    var contrastWithWhite = 1.05 / (luminance + 0.05);
                    return contrastWithWhite >= contrastWithBlack
                        ? SkiaSharp.SKColors.White
                        : SkiaSharp.SKColors.Black;
                }

                bool UsesDarkColorScheme(
                    FenBrowser.Core.Css.CssComputed computedStyle,
                    IReadOnlyDictionary<string, string> inlineStyles)
                {
                    var fallback = FenBrowser.FenEngine.Rendering.CssParser.PrefersDarkMode;
                    string scheme = null;
                    if (inlineStyles != null && inlineStyles.TryGetValue("color-scheme", out var inlineScheme))
                    {
                        scheme = inlineScheme;
                    }
                    else if (computedStyle?.Map?.TryGetValue("color-scheme", out var computedScheme) == true)
                    {
                        scheme = computedScheme;
                    }

                    if (string.IsNullOrWhiteSpace(scheme))
                    {
                        return fallback;
                    }

                    var normalized = scheme.Trim().ToLowerInvariant();
                    if (normalized == "dark")
                    {
                        return true;
                    }

                    if (normalized == "light")
                    {
                        return false;
                    }

                    return fallback;
                }

                bool TryResolveComputedColor(
                    string rawValue,
                    string currentColorValue,
                    bool useDarkScheme,
                    out SkiaSharp.SKColor color)
                {
                    color = default;
                    if (string.IsNullOrWhiteSpace(rawValue))
                    {
                        return false;
                    }

                    var trimmed = rawValue.Trim();
                    if (string.Equals(trimmed, "currentcolor", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(currentColorValue) ||
                            string.Equals(currentColorValue.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        return TryResolveComputedColor(currentColorValue, string.Empty, useDarkScheme, out color);
                    }

                    if (TryExtractFunctionBody(trimmed, "light-dark", out var lightDarkInner))
                    {
                        var parts = SplitFunctionArguments(lightDarkInner);
                        if (parts.Count >= 2)
                        {
                            var branch = useDarkScheme ? parts[1] : parts[0];
                            return TryResolveComputedColor(branch, currentColorValue, useDarkScheme, out color);
                        }

                        return false;
                    }

                    if (TryExtractFunctionBody(trimmed, "contrast-color", out var contrastInner))
                    {
                        if (!TryResolveComputedColor(contrastInner, currentColorValue, useDarkScheme, out var baseColor))
                        {
                            return false;
                        }

                        color = PickBlackOrWhiteContrast(baseColor);
                        return true;
                    }

                    var parsed = FenBrowser.FenEngine.Rendering.CssParser.ParseColor(trimmed);
                    if (!parsed.HasValue)
                    {
                        return false;
                    }

                    if (IsCurrentColorSentinel(parsed.Value))
                    {
                        if (string.IsNullOrWhiteSpace(currentColorValue) ||
                            string.Equals(currentColorValue.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        return TryResolveComputedColor(currentColorValue, string.Empty, useDarkScheme, out color);
                    }

                    color = parsed.Value;
                    return true;
                }

                string ResolveCurrentColorValue(
                    FenBrowser.Core.Css.CssComputed computedStyle,
                    IReadOnlyDictionary<string, string> inlineStyles)
                {
                    var useDarkScheme = UsesDarkColorScheme(computedStyle, inlineStyles);
                    if (inlineStyles != null && inlineStyles.TryGetValue("color", out var inlineColor) &&
                        TryResolveComputedColor(inlineColor, string.Empty, useDarkScheme, out var inlineResolved))
                    {
                        return SerializeComputedColor(inlineResolved);
                    }

                    if (computedStyle?.ForegroundColor.HasValue == true)
                    {
                        return SerializeComputedColor(computedStyle.ForegroundColor.Value);
                    }

                    if (computedStyle?.Map?.TryGetValue("color", out var computedColor) == true &&
                        TryResolveComputedColor(computedColor, string.Empty, useDarkScheme, out var resolvedComputedColor))
                    {
                        return SerializeComputedColor(resolvedComputedColor);
                    }

                    return "rgb(0, 0, 0)";
                }

                string ResolveComputedCssValue(
                    string propertyName,
                    string rawValue,
                    FenBrowser.Core.Css.CssComputed computedStyle,
                    IReadOnlyDictionary<string, string> inlineStyles)
                {
                    var normalized = NormalizeComputedValue(propertyName, rawValue);
                    if (string.IsNullOrWhiteSpace(rawValue))
                    {
                        return normalized;
                    }

                    var lowerProperty = propertyName?.ToLowerInvariant() ?? string.Empty;
                    var looksLikeColorValue =
                        lowerProperty.Contains("color", StringComparison.Ordinal) ||
                        string.Equals(lowerProperty, "fill", StringComparison.Ordinal) ||
                        string.Equals(lowerProperty, "stroke", StringComparison.Ordinal) ||
                        rawValue.Contains("light-dark(", StringComparison.OrdinalIgnoreCase) ||
                        rawValue.Contains("contrast-color(", StringComparison.OrdinalIgnoreCase) ||
                        rawValue.Contains("currentcolor", StringComparison.OrdinalIgnoreCase);

                    if (!looksLikeColorValue)
                    {
                        return normalized;
                    }

                    var useDarkScheme = UsesDarkColorScheme(computedStyle, inlineStyles);
                    var currentColorValue = ResolveCurrentColorValue(computedStyle, inlineStyles);
                    return TryResolveComputedColor(rawValue, currentColorValue, useDarkScheme, out var resolvedColor)
                        ? SerializeComputedColor(resolvedColor)
                        : normalized;
                }

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
                        FenObject CreateComputedStyleObject(IReadOnlyDictionary<string, string> values)
                        {
                            var styleObject = new FenObject();
                            styleObject.Set("getPropertyValue", FenValue.FromFunction(new FenFunction("getPropertyValue",
                                (gpArgs, _) =>
                                {
                                    if (gpArgs.Length == 0)
                                    {
                                        return FenValue.FromString(string.Empty);
                                    }

                                    var propertyName = gpArgs[0].ToString();
                                    return FenValue.FromString(values.TryGetValue(propertyName, out var propertyValue)
                                        ? NormalizeComputedValue(propertyName, propertyValue)
                                        : string.Empty);
                                })));

                            var propertyCount = 0;
                            foreach (var kvp in values)
                            {
                                var normalizedValue = NormalizeComputedValue(kvp.Key, kvp.Value);
                                styleObject.Set(kvp.Key, FenValue.FromString(normalizedValue));
                                var camelName = ToCamelCase(kvp.Key);
                                if (!string.Equals(camelName, kvp.Key, StringComparison.Ordinal))
                                {
                                    styleObject.Set(camelName, FenValue.FromString(normalizedValue));
                                }

                                propertyCount++;
                            }

                            styleObject.Set("length", FenValue.FromNumber(propertyCount));
                            return styleObject;
                        }

                        var pseudoText = args.Length > 1 ? args[1].ToString() : string.Empty;
                        if (!string.IsNullOrWhiteSpace(pseudoText) &&
                            pseudoText.IndexOf("highlight", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (HighlightApiBindings.TryResolveComputedHighlightStyles(nativeEl, pseudoText, out var highlightValues))
                            {
                                return FenValue.FromObject(CreateComputedStyleObject(highlightValues));
                            }

                            return FenValue.FromObject(CreateComputedStyleObject(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                        }

                        var computedStyle = FenBrowser.Core.Css.NodeStyleExtensions.GetComputedStyle(nativeEl) ?? new FenBrowser.Core.Css.CssComputed();
                        var inlineStyles = ParseInlineStyleMap(nativeEl.GetAttribute("style") ?? string.Empty);
                        var resolvedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        void SetResolvedProperty(string propertyName, string propertyValue)
                        {
                            if (string.IsNullOrEmpty(propertyName))
                            {
                                return;
                            }

                            var resolvedValue = ResolveComputedCssValue(propertyName, propertyValue, computedStyle, inlineStyles);
                            resolvedValues[propertyName] = resolvedValue ?? string.Empty;
                        }

                        if (computedStyle.Map != null)
                        {
                            foreach (var kvp in computedStyle.Map)
                            {
                                SetResolvedProperty(kvp.Key, kvp.Value);
                            }
                        }

                        if (computedStyle.ForegroundColor.HasValue)
                        {
                            SetResolvedProperty("color", SerializeComputedColor(computedStyle.ForegroundColor.Value));
                        }

                        if (computedStyle.BackgroundColor.HasValue)
                        {
                            SetResolvedProperty("background-color", SerializeComputedColor(computedStyle.BackgroundColor.Value));
                        }

                        foreach (var inlineStyle in inlineStyles)
                        {
                            SetResolvedProperty(inlineStyle.Key, inlineStyle.Value);
                        }

                        var csObj = new FenObject();
                        // Expose as a proxy-like object with getPropertyValue
                        csObj.Set("getPropertyValue", FenValue.FromFunction(new FenFunction("getPropertyValue",
                            (gpArgs, gpThis) =>
                            {
                                if (gpArgs.Length == 0) return FenValue.FromString("");
                                var prop = gpArgs[0].ToString();
                                var normalizedProp =
                                    string.Equals(prop, "grid-row-gap", StringComparison.OrdinalIgnoreCase) ? "row-gap" :
                                    string.Equals(prop, "grid-column-gap", StringComparison.OrdinalIgnoreCase) ? "column-gap" :
                                    string.Equals(prop, "grid-gap", StringComparison.OrdinalIgnoreCase) ? "gap" :
                                    prop;

                                string ReadGapValue(string key)
                                {
                                    string value = null;
                                    if (computedStyle.Map?.ContainsKey(key) == true)
                                    {
                                        value = computedStyle.Map[key];
                                    }
                                    else if (computedStyle.Map?.ContainsKey("grid-" + key) == true)
                                    {
                                        value = computedStyle.Map["grid-" + key];
                                    }

                                    value = (value ?? string.Empty).Trim().ToLowerInvariant();
                                    if (string.IsNullOrEmpty(value)) return "normal";
                                    if (value == "0") return "0px";
                                    return value;
                                }

                                string val;
                                if (string.Equals(normalizedProp, "row-gap", StringComparison.OrdinalIgnoreCase))
                                {
                                    val = ReadGapValue("row-gap");
                                }
                                else if (string.Equals(normalizedProp, "column-gap", StringComparison.OrdinalIgnoreCase))
                                {
                                    val = ReadGapValue("column-gap");
                                }
                                else if (string.Equals(normalizedProp, "gap", StringComparison.OrdinalIgnoreCase))
                                {
                                    var row = ReadGapValue("row-gap");
                                    var column = ReadGapValue("column-gap");
                                    val = row == column ? row : row + " " + column;
                                }
                                else
                                {
                                    val = resolvedValues.TryGetValue(normalizedProp, out var resolvedValue)
                                        ? resolvedValue
                                        : "";
                                }
                                return FenValue.FromString(NormalizeComputedValue(normalizedProp, val));
                            })));
                        // Common properties
                        if (resolvedValues.Count > 0)
                        {
                            foreach (var kvp in resolvedValues)
                            {
                                var normalizedValue = NormalizeComputedValue(kvp.Key, kvp.Value);
                                csObj.Set(kvp.Key, FenValue.FromString(normalizedValue));
                                var camelName = ToCamelCase(kvp.Key);
                                if (!string.Equals(camelName, kvp.Key, StringComparison.Ordinal))
                                {
                                    csObj.Set(camelName, FenValue.FromString(normalizedValue));
                                }
                            }
                        }

                        csObj.Set("display", FenValue.FromString(computedStyle.Display ?? "block"));
                        csObj.Set("visibility", FenValue.FromString(computedStyle.Visibility ?? "visible"));
                        csObj.Set("position", FenValue.FromString(computedStyle.Position ?? "static"));
                        var containValue = resolvedValues.TryGetValue("contain", out var containResolved) && !string.IsNullOrWhiteSpace(containResolved)
                            ? containResolved
                            : "none";
                        csObj.Set("contain", FenValue.FromString(containValue));
                        var contentVisibilityValue = resolvedValues.TryGetValue("content-visibility", out var contentVisibilityResolved) && !string.IsNullOrWhiteSpace(contentVisibilityResolved)
                            ? contentVisibilityResolved
                            : "visible";
                        csObj.Set("content-visibility", FenValue.FromString(contentVisibilityValue));
                        csObj.Set("contentVisibility", FenValue.FromString(contentVisibilityValue));
                        var rowGap = computedStyle.Map?.ContainsKey("row-gap") == true ? computedStyle.Map["row-gap"] :
                                     computedStyle.Map?.ContainsKey("grid-row-gap") == true ? computedStyle.Map["grid-row-gap"] : "normal";
                        var columnGap = computedStyle.Map?.ContainsKey("column-gap") == true ? computedStyle.Map["column-gap"] :
                                        computedStyle.Map?.ContainsKey("grid-column-gap") == true ? computedStyle.Map["grid-column-gap"] : "normal";
                        rowGap = string.Equals(rowGap, "0", StringComparison.Ordinal) ? "0px" : (string.IsNullOrWhiteSpace(rowGap) ? "normal" : rowGap);
                        columnGap = string.Equals(columnGap, "0", StringComparison.Ordinal) ? "0px" : (string.IsNullOrWhiteSpace(columnGap) ? "normal" : columnGap);
                        var shorthandGap = rowGap == columnGap ? rowGap : rowGap + " " + columnGap;
                        csObj.Set("row-gap", FenValue.FromString(rowGap));
                        csObj.Set("column-gap", FenValue.FromString(columnGap));
                        csObj.Set("gap", FenValue.FromString(shorthandGap));
                        csObj.Set("grid-row-gap", FenValue.FromString(rowGap));
                        csObj.Set("grid-column-gap", FenValue.FromString(columnGap));
                        csObj.Set("grid-gap", FenValue.FromString(shorthandGap));

                        var borderWidth = computedStyle.Map?.ContainsKey("border-width") == true
                            ? computedStyle.Map["border-width"]
                            : computedStyle.Map?.ContainsKey("border-top-width") == true
                                && string.Equals(computedStyle.Map["border-top-width"], computedStyle.Map.GetValueOrDefault("border-right-width"), StringComparison.Ordinal)
                                && string.Equals(computedStyle.Map["border-top-width"], computedStyle.Map.GetValueOrDefault("border-bottom-width"), StringComparison.Ordinal)
                                && string.Equals(computedStyle.Map["border-top-width"], computedStyle.Map.GetValueOrDefault("border-left-width"), StringComparison.Ordinal)
                                    ? computedStyle.Map["border-top-width"]
                                    : string.Empty;
                        borderWidth = NormalizeComputedValue("border-width", borderWidth);
                        csObj.Set("border-width", FenValue.FromString(borderWidth));
                        csObj.Set("borderWidth", FenValue.FromString(borderWidth));

                        var outlineWidth = NormalizeComputedValue(
                            "outline-width",
                            computedStyle.Map?.ContainsKey("outline-width") == true ? computedStyle.Map["outline-width"] : string.Empty);
                        csObj.Set("outline-width", FenValue.FromString(outlineWidth));
                        csObj.Set("outlineWidth", FenValue.FromString(outlineWidth));
                        return FenValue.FromObject(csObj);
                    }
                }

                return FenValue.FromObject(new FenObject());
            }));
            SetGlobal("getComputedStyle", getComputedStyleFn);
            window.Set("getComputedStyle", getComputedStyleFn);

            // Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ matchMedia Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            var matchMediaFn = FenValue.FromFunction(new FenFunction("matchMedia", (args, thisVal) =>
            {
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
                mql.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (eArgs, eThis) =>
                {
                    if (eArgs.Length >= 2) mqlListeners.Add(eArgs[1]);
                    return FenValue.Undefined;
                })));
                mql.Set("removeEventListener",
                    FenValue.FromFunction(new FenFunction("removeEventListener",
                        (eArgs, eThis) => FenValue.Undefined)));
                mql.Set("addListener", FenValue.FromFunction(new FenFunction("addListener", (eArgs, eThis) =>
                {
                    if (eArgs.Length >= 1 && eArgs[0].IsFunction) mqlListeners.Add(eArgs[0]);
                    return FenValue.Undefined;
                })));
                mql.Set("removeListener",
                    FenValue.FromFunction(new FenFunction("removeListener", (eArgs, eThis) => FenValue.Undefined)));
                return FenValue.FromObject(mql);
            }));
            SetGlobal("matchMedia", matchMediaFn);
            window.Set("matchMedia", matchMediaFn);

            // Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ requestIdleCallback / cancelIdleCallback Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            SetGlobal("requestIdleCallback", FenValue.FromFunction(new FenFunction("requestIdleCallback",
                (args, thisVal) =>
                {
                    if (args.Length > 0 && args[0].IsFunction)
                    {
                        var cb = args[0].AsFunction();
                        // Execute via event loop task (idle = next available slot)
                        EventLoop.EventLoopCoordinator.Instance.ScheduleTask(() =>
                        {
                            var deadline = new FenObject();
                            deadline.Set("didTimeout", FenValue.FromBoolean(false));
                            deadline.Set("timeRemaining",
                                FenValue.FromFunction(new FenFunction("timeRemaining",
                                    (a, t) => FenValue.FromNumber(50))));
                            cb?.Invoke(new FenValue[] { FenValue.FromObject(deadline) }, _context);
                        }, EventLoop.TaskSource.Other, "requestIdleCallback");
                        return FenValue.FromNumber(1);
                    }

                    return FenValue.FromNumber(0);
                })));
            SetGlobal("cancelIdleCallback",
                FenValue.FromFunction(new FenFunction("cancelIdleCallback", (args, thisVal) => FenValue.Undefined)));
            window.Set("requestIdleCallback", (FenValue)GetGlobal("requestIdleCallback"));
            window.Set("cancelIdleCallback", (FenValue)GetGlobal("cancelIdleCallback"));

            // Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ queueMicrotask at global scope Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            SetGlobal("queueMicrotask", FenValue.FromFunction(new FenFunction("queueMicrotask", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var cb = args[0].AsFunction();
                    EventLoop.EventLoopCoordinator.Instance.ScheduleMicrotask(() =>
                    {
                        cb?.Invoke(Array.Empty<FenValue>(), _context);
                    });
                }

                return FenValue.Undefined;
            })));

            // Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ btoa / atob Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
            SetGlobal("btoa", FenValue.FromFunction(new FenFunction("btoa", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromString("");
                try
                {
                    var bytes = Encoding.Latin1.GetBytes(args[0].ToString());
                    return FenValue.FromString(Convert.ToBase64String(bytes));
                }
                catch
                {
                    return FenValue.FromString("");
                }
            })));
            SetGlobal("atob", FenValue.FromFunction(new FenFunction("atob", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromString("");
                try
                {
                    var bytes = Convert.FromBase64String(args[0].ToString());
                    return FenValue.FromString(Encoding.Latin1.GetString(bytes));
                }
                catch
                {
                    return FenValue.FromString("");
                }
            })));
            window.Set("btoa", (FenValue)GetGlobal("btoa"));
            window.Set("atob", (FenValue)GetGlobal("atob"));

            // Custom Elements Registry (Web Components)
            var customElementsRegistry = new FenBrowser.FenEngine.DOM.CustomElementRegistry(_context);
            SetGlobal("customElements", FenValue.FromObject(customElementsRegistry.ToFenObject()));

            // requestAnimationFrame / cancelAnimationFrame

            // Use a simple counter and store callbacks in window.__raf_queue
            var requestAnimationFrameFunc = FenValue.FromFunction(new FenFunction("requestAnimationFrame",
                (FenValue[] args, FenValue thisVal) =>
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

                        // ES2022: hasIndices ('d' flag) Ã¢â‚¬â€ populate .indices array
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
                    throw new FenSyntaxError($"SyntaxError: Invalid regular expression: {ex.Message}");
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
            math.Set("max", FenValue.FromFunction(new FenFunction("max", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromNumber(double.NegativeInfinity);
                double max = args[0].ToNumber();
                for (int i = 1; i < args.Length; i++) max = Math.Max(max, args[i].ToNumber());
                return FenValue.FromNumber(max);
            })));
            math.Set("min", FenValue.FromFunction(new FenFunction("min", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromNumber(double.PositiveInfinity);
                double min = args[0].ToNumber();
                for (int i = 1; i < args.Length; i++) min = Math.Min(min, args[i].ToNumber());
                return FenValue.FromNumber(min);
            })));
            math.Set("pow", FenValue.FromFunction(new FenFunction("pow", (FenValue[] args, FenValue thisVal) =>
                FenValue.FromNumber(Math.Pow(args.Length > 0 ? args[0].ToNumber() : double.NaN,
                    args.Length > 1 ? args[1].ToNumber() : double.NaN)))));
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
                FenValue.FromNumber(Math.Atan2(args.Length > 0 ? args[0].ToNumber() : double.NaN,
                    args.Length > 1 ? args[1].ToNumber() : double.NaN)))));
            math.Set("hypot", FenValue.FromFunction(new FenFunction("hypot", (args, thisVal) =>
            {
                double sum = 0;
                foreach (var arg in args)
                {
                    var n = arg.ToNumber();
                    sum += n * n;
                }

                return FenValue.FromNumber(Math.Sqrt(sum));
            })));
            // ES2015+ Math methods
            math.Set("cbrt",
                FenValue.FromFunction(new FenFunction("cbrt",
                    (args, t) => FenValue.FromNumber(Math.Cbrt(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("log2",
                FenValue.FromFunction(new FenFunction("log2",
                    (args, t) => FenValue.FromNumber(Math.Log2(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("log1p", FenValue.FromFunction(new FenFunction("log1p", (args, t) =>
            {
                var x = args.Length > 0 ? args[0].ToNumber() : double.NaN;
                return FenValue.FromNumber(Math.Log(1 + x));
            })));
            math.Set("expm1", FenValue.FromFunction(new FenFunction("expm1", (args, t) =>
            {
                var x = args.Length > 0 ? args[0].ToNumber() : double.NaN;
                return FenValue.FromNumber(Math.Exp(x) - 1);
            })));
            math.Set("sinh",
                FenValue.FromFunction(new FenFunction("sinh",
                    (args, t) => FenValue.FromNumber(Math.Sinh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("cosh",
                FenValue.FromFunction(new FenFunction("cosh",
                    (args, t) => FenValue.FromNumber(Math.Cosh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("tanh",
                FenValue.FromFunction(new FenFunction("tanh",
                    (args, t) => FenValue.FromNumber(Math.Tanh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("asinh",
                FenValue.FromFunction(new FenFunction("asinh",
                    (args, t) => FenValue.FromNumber(Math.Asinh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("acosh",
                FenValue.FromFunction(new FenFunction("acosh",
                    (args, t) => FenValue.FromNumber(Math.Acosh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("atanh",
                FenValue.FromFunction(new FenFunction("atanh",
                    (args, t) => FenValue.FromNumber(Math.Atanh(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("clz32", FenValue.FromFunction(new FenFunction("clz32", (args, t) =>
            {
                var n = (uint)(int)(args.Length > 0 ? args[0].ToNumber() : 0);
                if (n == 0) return FenValue.FromNumber(32);
                int clz = 0;
                while ((n & 0x80000000) == 0)
                {
                    clz++;
                    n <<= 1;
                }

                return FenValue.FromNumber(clz);
            })));
            math.Set("fround",
                FenValue.FromFunction(new FenFunction("fround",
                    (args, t) =>
                        FenValue.FromNumber((double)(float)(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("imul", FenValue.FromFunction(new FenFunction("imul", (args, t) =>
            {
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
                        throw new FenRangeError("RangeError: Cannot convert non-integer to BigInt");
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
                    throw new FenSyntaxError($"SyntaxError: Cannot convert {str} to a BigInt");
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
                if (args.Length < 2)
                {
                    throw new FenTypeError("TypeError: Map.groupBy requires items and callback");
                }

                if (!args[1].IsFunction)
                {
                    throw new FenTypeError("TypeError: Map.groupBy callback must be callable");
                }

                if (args[0].IsNull || args[0].IsUndefined)
                {
                    throw new FenTypeError("TypeError: Map.groupBy called on null or undefined");
                }

                var callback = args[1].AsFunction();
                var mapGet = result.Get("get").AsFunction();
                var mapSet = result.Get("set").AsFunction();
                var index = 0;

                void AddToGroup(FenValue item)
                {
                    var groupKey = callback.Invoke(new[] { item, FenValue.FromNumber(index) }, _context);
                    var existing = mapGet.Invoke(new FenValue[] { groupKey }, null);
                    FenObject groupArr;
                    if (existing.IsUndefined)
                    {
                        groupArr = FenObject.CreateArray();
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

                    index++;
                }

                if (args[0].IsString)
                {
                    var sourceString = args[0].AsString(_context);
                    for (int i = 0; i < sourceString.Length; i++)
                    {
                        AddToGroup(FenValue.FromString(sourceString[i].ToString()));
                    }
                }
                else if (args[0].IsObject)
                {
                    var items = args[0].AsObject();
                    var iteratorKey = JsSymbol.Iterator?.ToPropertyKey();
                    var iteratorMethod = !string.IsNullOrEmpty(iteratorKey) ? items.Get(iteratorKey, _context) : FenValue.Undefined;
                    if (iteratorMethod.IsFunction)
                    {
                        var iteratorValue = iteratorMethod.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(items));
                        if (!iteratorValue.IsObject)
                        {
                            throw new FenTypeError("TypeError: Map.groupBy iterator is not an object");
                        }

                        var iterator = iteratorValue.AsObject();
                        while (true)
                        {
                            var nextMethod = iterator.Get("next", _context);
                            if (!nextMethod.IsFunction)
                            {
                                throw new FenTypeError("TypeError: Map.groupBy iterator does not provide next()");
                            }

                            var nextValue = nextMethod.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(iterator));
                            if (!nextValue.IsObject)
                            {
                                throw new FenTypeError("TypeError: Map.groupBy iterator result is not an object");
                            }

                            var nextResult = nextValue.AsObject();
                            if (nextResult.Get("done", _context).ToBoolean())
                            {
                                break;
                            }

                            AddToGroup(nextResult.Get("value", _context));
                        }
                    }
                    else
                    {
                        var lenVal = items.Get("length", _context);
                        int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                        for (int i = 0; i < len; i++)
                        {
                            AddToGroup(items.Get(i.ToString(), _context));
                        }
                    }
                }
                else
                {
                    throw new FenTypeError("TypeError: Map.groupBy items must be iterable or array-like");
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
                            set.Get("add").AsFunction()?.Invoke(new FenValue[] { val }, _context);
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

            // DisposableStack Ã¢â‚¬â€ ES2024 explicit resource management (Stage 4)
            {
                var disposableStackProto = new FenObject();

                SetGlobal("DisposableStack", FenValue.FromFunction(new FenFunction("DisposableStack",
                    (FenValue[] args, FenValue thisVal) =>
                {
                    var stack = new FenObject();
                    stack.SetPrototype(disposableStackProto);

                    var disposeList = new System.Collections.Generic.List<(string type, FenValue fn, FenValue val)>();
                    bool disposed = false;

                    // use(resource) Ã¢â‚¬â€ registers resource[Symbol.dispose]() for LIFO disposal
                    stack.Set("use", FenValue.FromFunction(new FenFunction("use", (useArgs, _) =>
                    {
                        var resource = useArgs.Length > 0 ? useArgs[0] : FenValue.Undefined;
                        if (resource.IsNull || resource.IsUndefined) return resource;
                        var resObj = resource.AsObject() as FenObject;
                        if (resObj != null)
                        {
                            var disposeMethod = resObj.Get("[Symbol.dispose]");
                            if (disposeMethod.IsFunction)
                                disposeList.Add(("resource", disposeMethod, resource));
                        }
                        return resource;
                    })));

                    // adopt(value, onDispose) Ã¢â‚¬â€ calls onDispose(value) on disposal
                    stack.Set("adopt", FenValue.FromFunction(new FenFunction("adopt", (adoptArgs, _) =>
                    {
                        var val = adoptArgs.Length > 0 ? adoptArgs[0] : FenValue.Undefined;
                        var fn = adoptArgs.Length > 1 ? adoptArgs[1] : FenValue.Undefined;
                        if (fn.IsFunction) disposeList.Add(("adopt", fn, val));
                        return val;
                    })));

                    // defer(fn) Ã¢â‚¬â€ calls fn() on disposal
                    stack.Set("defer", FenValue.FromFunction(new FenFunction("defer", (deferArgs, _) =>
                    {
                        var fn = deferArgs.Length > 0 ? deferArgs[0] : FenValue.Undefined;
                        if (fn.IsFunction) disposeList.Add(("defer", fn, FenValue.Undefined));
                        return FenValue.Undefined;
                    })));

                    // move() Ã¢â‚¬â€ transfer ownership to a new DisposableStack (simplified)
                    stack.Set("move", FenValue.FromFunction(new FenFunction("move", (moveArgs, _) =>
                    {
                        if (disposed) throw new FenTypeError("TypeError: DisposableStack already disposed");
                        disposed = true;
                        var dsGlobal = GetGlobal("DisposableStack");
                        var dsFn = dsGlobal?.AsFunction();
                        return dsFn != null
                            ? dsFn.Invoke(Array.Empty<FenValue>(), (IExecutionContext)null)
                            : FenValue.Undefined;
                    })));

                    // [Symbol.dispose]() Ã¢â‚¬â€ LIFO disposal
                    stack.Set("[Symbol.dispose]", FenValue.FromFunction(new FenFunction("[Symbol.dispose]", (_, __) =>
                    {
                        if (disposed) return FenValue.Undefined;
                        disposed = true;
                        for (int di = disposeList.Count - 1; di >= 0; di--)
                        {
                            var (type, fn, val) = disposeList[di];
                            try
                            {
                                if (type == "adopt")
                                    fn.AsFunction()?.Invoke(new FenValue[] { val }, null);
                                else if (type == "defer")
                                    fn.AsFunction()?.Invoke(Array.Empty<FenValue>(), null);
                                else // "resource" Ã¢â‚¬â€ fn is the [Symbol.dispose] method, val is the resource
                                    fn.AsFunction()?.Invoke(Array.Empty<FenValue>(),
                                        new ExecutionContext { ThisBinding = val });
                            }
                            catch { /* suppress to continue LIFO; real impl would use SuppressedError */ }
                        }
                        return FenValue.Undefined;
                    })));

                    // disposed getter Ã¢â‚¬â€ lazily reflect state
                    // Store as a regular property; real spec uses an accessor, but this is sufficient for most tests
                    stack.Set("disposed", FenValue.FromBoolean(false));

                    return FenValue.FromObject(stack);
                })));
            }

            // ES6 Proxy constructor - Enables metaprogramming
            SetGlobal("Proxy", FenValue.FromFunction(new FenFunction("Proxy", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;

                var target = args[0].AsObject();
                var handlerVal = args[1].AsObject();

                if (target == null || handlerVal == null) throw new FenTypeError("TypeError: Proxy requires valid target and handler");

                // Create a proxy object that intercepts operations
                FenObject proxy;
                var targetCallable = args[0].IsFunction || (target.Get("call").IsFunction && target.Get("apply").IsFunction);
                if (targetCallable)
                {
                    FenFunction targetFn = args[0].AsFunction() ?? (target as FenFunction);
                    proxy = new FenFunction("proxy", (pArgs, pThis) =>
                    {
                        var applyTrap = handlerVal.Get("apply").AsFunction();
                        if (applyTrap != null)
                        {
                            var argsArr = FenObject.CreateArray();
                            for (int i = 0; i < pArgs.Length; i++) argsArr.Set(i.ToString(), pArgs[i]);
                            argsArr.Set("length", FenValue.FromNumber(pArgs.Length));
                            return applyTrap.Invoke(new[] { args[0], pThis, FenValue.FromObject(argsArr) }, _context);
                        }
                        if (targetFn != null) return targetFn.Invoke(pArgs, _context, pThis);
                        var callMethod = target.Get("call").AsFunction();
                        if (callMethod != null)
                        {
                            var callArgs = new FenValue[pArgs.Length + 1];
                            callArgs[0] = pThis;
                            for (int i = 0; i < pArgs.Length; i++) callArgs[i + 1] = pArgs[i];
                            return callMethod.Invoke(callArgs, _context, args[0]);
                        }
                        return FenValue.Undefined;
                    });

                    if (proxy is FenFunction proxyFnObj)
                    {
                        if (targetFn != null)
                        {
                            proxyFnObj.IsAsync = targetFn.IsAsync;
                            proxyFnObj.IsGenerator = targetFn.IsGenerator;
                        }
                        var targetTag = target.Get(JsSymbol.ToStringTag.ToPropertyKey());
                        if (targetTag.IsString)
                        {
                            var targetTagText = targetTag.ToString();
                            if (string.Equals(targetTagText, "AsyncFunction", StringComparison.Ordinal))
                            {
                                proxyFnObj.IsAsync = true;
                                proxyFnObj.IsGenerator = false;
                            }
                            else if (string.Equals(targetTagText, "GeneratorFunction", StringComparison.Ordinal))
                            {
                                proxyFnObj.IsGenerator = true;
                                proxyFnObj.IsAsync = false;
                            }
                        }
                    }

                    var targetProto = target.GetPrototype();
                    if (targetProto != null)
                    {
                        proxy.SetPrototype(targetProto);
                    }
                }
                else
                {
                    proxy = new FenObject();
                }
                proxy.SetBuiltin("__isProxy__", FenValue.FromBoolean(true));
                proxy.SetBuiltin("__target__", args[0]);
                proxy.SetBuiltin("__proxyTarget__", args[0]);
                proxy.SetBuiltin("__handler__", FenValue.FromObject(handlerVal));

                // ---------------------------------------------------------------
                // Wire up all 13 Proxy traps as internal slots (__proxy*__).
                // FenObject.Get/Set/Has/Delete/GetPrototype/etc. check these slots
                // first when __isProxy__ is true (ECMA-262 §10.5).
                // ---------------------------------------------------------------

                // Trap: get — ECMA-262 §10.5.8
                proxy.SetBuiltin("__proxyGet__", FenValue.FromFunction(new FenFunction("[[Get]]", (getArgs, getThis) =>
                {
                    // getArgs[0]=target, getArgs[1]=key, getArgs[2]=receiver
                    var propKey = getArgs.Length > 1 ? getArgs[1].ToString() : "";
                    var receiver = getArgs.Length > 2 ? getArgs[2] : FenValue.Undefined;
                    var getTrap = handlerVal.Get("get").AsFunction();
                    if (getTrap != null)
                        return getTrap.Invoke(new FenValue[] { FenValue.FromObject(target), FenValue.FromString(propKey), receiver }, _context, FenValue.FromObject(handlerVal));
                    return target.Get(propKey, _context);
                })));

                // Trap: set — ECMA-262 §10.5.9
                proxy.SetBuiltin("__proxySet__", FenValue.FromFunction(new FenFunction("[[Set]]", (setArgs, setThis) =>
                {
                    // setArgs[0]=target, setArgs[1]=key, setArgs[2]=value, setArgs[3]=receiver
                    var propKey = setArgs.Length > 1 ? setArgs[1].ToString() : "";
                    var val = setArgs.Length > 2 ? setArgs[2] : FenValue.Undefined;
                    var receiver = setArgs.Length > 3 ? setArgs[3] : FenValue.Undefined;
                    var setTrap = handlerVal.Get("set").AsFunction();
                    if (setTrap != null)
                    {
                        var result = setTrap.Invoke(new FenValue[] { FenValue.FromObject(target), FenValue.FromString(propKey), val, receiver }, _context, FenValue.FromObject(handlerVal));
                        return result;
                    }
                    target.Set(propKey, val, _context);
                    return FenValue.FromBoolean(true);
                })));

                // Trap: has — ECMA-262 §10.5.7 (used by `in` operator)
                proxy.SetBuiltin("__proxyHas__", FenValue.FromFunction(new FenFunction("[[Has]]", (hasArgs, hasThis) =>
                {
                    // hasArgs[0]=target, hasArgs[1]=key
                    var propKey = hasArgs.Length > 1 ? hasArgs[1].ToString() : "";
                    var hasTrap = handlerVal.Get("has").AsFunction();
                    if (hasTrap != null)
                        return hasTrap.Invoke(new FenValue[] { FenValue.FromObject(target), FenValue.FromString(propKey) }, _context, FenValue.FromObject(handlerVal));
                    return FenValue.FromBoolean(target.Has(propKey, _context));
                })));

                // Trap: deleteProperty — ECMA-262 §10.5.10
                proxy.SetBuiltin("__proxyDeleteProperty__", FenValue.FromFunction(new FenFunction("[[Delete]]", (delArgs, delThis) =>
                {
                    var propKey = delArgs.Length > 1 ? delArgs[1].ToString() : "";
                    var delTrap = handlerVal.Get("deleteProperty").AsFunction();
                    if (delTrap != null)
                        return delTrap.Invoke(new FenValue[] { FenValue.FromObject(target), FenValue.FromString(propKey) }, _context, FenValue.FromObject(handlerVal));
                    return FenValue.FromBoolean(target.Delete(propKey, _context));
                })));

                // Trap: getOwnPropertyDescriptor — ECMA-262 §10.5.5
                proxy.SetBuiltin("__proxyGetOwnPropertyDescriptor__", FenValue.FromFunction(new FenFunction("[[GetOwnProperty]]", (gopdArgs, gopdThis) =>
                {
                    var propKey = gopdArgs.Length > 1 ? gopdArgs[1].ToString() : "";
                    var gopdTrap = handlerVal.Get("getOwnPropertyDescriptor").AsFunction();
                    if (gopdTrap != null)
                        return gopdTrap.Invoke(new FenValue[] { FenValue.FromObject(target), FenValue.FromString(propKey) }, _context, FenValue.FromObject(handlerVal));
                    // Forward to target
                    if (target is FenObject fenTarget)
                    {
                        var desc = fenTarget.GetOwnPropertyDescriptor(propKey);
                        if (desc == null) return FenValue.Undefined;
                        var descObj = new FenObject();
                        if (desc.Value.Value.HasValue) descObj.Set("value", desc.Value.Value.Value);
                        if (desc.Value.Writable.HasValue) descObj.Set("writable", FenValue.FromBoolean(desc.Value.Writable.Value));
                        if (desc.Value.Enumerable.HasValue) descObj.Set("enumerable", FenValue.FromBoolean(desc.Value.Enumerable.Value));
                        if (desc.Value.Configurable.HasValue) descObj.Set("configurable", FenValue.FromBoolean(desc.Value.Configurable.Value));
                        return FenValue.FromObject(descObj);
                    }
                    return FenValue.Undefined;
                })));

                // Trap: defineProperty — ECMA-262 §10.5.6
                proxy.SetBuiltin("__proxyDefineProperty__", FenValue.FromFunction(new FenFunction("[[DefineOwnProperty]]", (dpArgs, dpThis) =>
                {
                    var propKey = dpArgs.Length > 1 ? dpArgs[1].ToString() : "";
                    var descArg = dpArgs.Length > 2 ? dpArgs[2] : FenValue.Undefined;
                    var dpTrap = handlerVal.Get("defineProperty").AsFunction();
                    if (dpTrap != null)
                        return dpTrap.Invoke(new FenValue[] { FenValue.FromObject(target), FenValue.FromString(propKey), descArg }, _context, FenValue.FromObject(handlerVal));
                    // Forward to target
                    if (target is FenObject fenTarget && descArg.IsObject)
                    {
                        var descObj = descArg.AsObject();
                        var pd = new PropertyDescriptor();
                        var valProp = descObj.Get("value", _context);
                        if (!valProp.IsUndefined) pd.Value = valProp;
                        var writableProp = descObj.Get("writable", _context);
                        if (!writableProp.IsUndefined) pd.Writable = writableProp.ToBoolean();
                        var enumProp = descObj.Get("enumerable", _context);
                        if (!enumProp.IsUndefined) pd.Enumerable = enumProp.ToBoolean();
                        var confProp = descObj.Get("configurable", _context);
                        if (!confProp.IsUndefined) pd.Configurable = confProp.ToBoolean();
                        return FenValue.FromBoolean(fenTarget.DefineOwnProperty(propKey, pd));
                    }
                    return FenValue.FromBoolean(false);
                })));

                // Trap: getPrototypeOf — ECMA-262 §10.5.1
                proxy.SetBuiltin("__proxyGetPrototypeOf__", FenValue.FromFunction(new FenFunction("[[GetPrototypeOf]]", (gpoArgs, gpoThis) =>
                {
                    var gpoTrap = handlerVal.Get("getPrototypeOf").AsFunction();
                    if (gpoTrap != null)
                        return gpoTrap.Invoke(new FenValue[] { FenValue.FromObject(target) }, _context, FenValue.FromObject(handlerVal));
                    var proto = target.GetPrototype();
                    return proto != null ? FenValue.FromObject(proto) : FenValue.Null;
                })));

                // Trap: setPrototypeOf — ECMA-262 §10.5.2
                proxy.SetBuiltin("__proxySetPrototypeOf__", FenValue.FromFunction(new FenFunction("[[SetPrototypeOf]]", (spoArgs, spoThis) =>
                {
                    var protoArg = spoArgs.Length > 1 ? spoArgs[1] : FenValue.Null;
                    var spoTrap = handlerVal.Get("setPrototypeOf").AsFunction();
                    if (spoTrap != null)
                        return spoTrap.Invoke(new FenValue[] { FenValue.FromObject(target), protoArg }, _context, FenValue.FromObject(handlerVal));
                    if (target is FenObject fenTarget)
                    {
                        var newProto = protoArg.IsNull ? null : protoArg.AsObject();
                        return FenValue.FromBoolean(fenTarget.TrySetPrototype(newProto));
                    }
                    return FenValue.FromBoolean(false);
                })));

                // Trap: isExtensible — ECMA-262 §10.5.3
                proxy.SetBuiltin("__proxyIsExtensible__", FenValue.FromFunction(new FenFunction("[[IsExtensible]]", (ieArgs, ieThis) =>
                {
                    var ieTrap = handlerVal.Get("isExtensible").AsFunction();
                    if (ieTrap != null)
                        return ieTrap.Invoke(new FenValue[] { FenValue.FromObject(target) }, _context, FenValue.FromObject(handlerVal));
                    return FenValue.FromBoolean(target.IsExtensible);
                })));

                // Trap: preventExtensions — ECMA-262 §10.5.4
                proxy.SetBuiltin("__proxyPreventExtensions__", FenValue.FromFunction(new FenFunction("[[PreventExtensions]]", (peArgs, peThis) =>
                {
                    var peTrap = handlerVal.Get("preventExtensions").AsFunction();
                    if (peTrap != null)
                        return peTrap.Invoke(new FenValue[] { FenValue.FromObject(target) }, _context, FenValue.FromObject(handlerVal));
                    if (target is FenObject fenTarget) fenTarget.PreventExtensions();
                    return FenValue.FromBoolean(true);
                })));

                // Trap: ownKeys — ECMA-262 §10.5.11
                proxy.SetBuiltin("__proxyOwnKeys__", FenValue.FromFunction(new FenFunction("[[OwnPropertyKeys]]", (okArgs, okThis) =>
                {
                    var okTrap = handlerVal.Get("ownKeys").AsFunction();
                    if (okTrap != null)
                        return okTrap.Invoke(new FenValue[] { FenValue.FromObject(target) }, _context, FenValue.FromObject(handlerVal));
                    // Forward: collect own property names from target
                    var keysArr = FenObject.CreateArray();
                    int i = 0;
                    if (target is FenObject fenTarget)
                    {
                        foreach (var k in fenTarget.GetOwnPropertyNames())
                        {
                            keysArr.Set(i.ToString(), FenValue.FromString(k));
                            i++;
                        }
                    }
                    keysArr.Set("length", FenValue.FromNumber(i));
                    return FenValue.FromObject(keysArr);
                })));

                // Trap: apply — ECMA-262 §10.5.12 (target must be callable)
                // The apply trap is already wired into the FenFunction lambda above when targetCallable is true.
                // We also store it as a slot so FenObject.Get can route calls transparently.
                proxy.SetBuiltin("__proxyApply__", FenValue.FromFunction(new FenFunction("[[Call]]", (applyArgs, applyThis) =>
                {
                    // applyArgs[0]=target, applyArgs[1]=thisArg, applyArgs[2]=argumentsList
                    var thisArg = applyArgs.Length > 1 ? applyArgs[1] : FenValue.Undefined;
                    var argsList = applyArgs.Length > 2 ? applyArgs[2] : FenValue.Undefined;
                    var applyTrap = handlerVal.Get("apply").AsFunction();
                    if (applyTrap != null)
                        return applyTrap.Invoke(new FenValue[] { FenValue.FromObject(target), thisArg, argsList }, _context, FenValue.FromObject(handlerVal));
                    // Forward: call target directly
                    FenValue[] callArgs = Array.Empty<FenValue>();
                    if (argsList.IsObject)
                    {
                        var listObj = argsList.AsObject();
                        int len = (int)listObj.Get("length", _context).ToNumber();
                        callArgs = new FenValue[len];
                        for (int i = 0; i < len; i++) callArgs[i] = listObj.Get(i.ToString(), _context);
                    }
                    var targetFnForApply = args[0].AsFunction();
                    if (targetFnForApply != null) return targetFnForApply.Invoke(callArgs, _context, thisArg);
                    return FenValue.Undefined;
                })));

                // Trap: construct — ECMA-262 §10.5.13 (target must be constructable)
                proxy.SetBuiltin("__proxyConstruct__", FenValue.FromFunction(new FenFunction("[[Construct]]", (ctorArgs, ctorThis) =>
                {
                    // ctorArgs[0]=target, ctorArgs[1]=argumentsList, ctorArgs[2]=newTarget
                    var argsList = ctorArgs.Length > 1 ? ctorArgs[1] : FenValue.Undefined;
                    var newTargetArg = ctorArgs.Length > 2 ? ctorArgs[2] : args[0];
                    var ctorTrap = handlerVal.Get("construct").AsFunction();
                    FenValue[] callArgs = Array.Empty<FenValue>();
                    if (argsList.IsObject)
                    {
                        var listObj = argsList.AsObject();
                        int len = (int)listObj.Get("length", _context).ToNumber();
                        callArgs = new FenValue[len];
                        for (int i = 0; i < len; i++) callArgs[i] = listObj.Get(i.ToString(), _context);
                    }
                    if (ctorTrap != null)
                        return ctorTrap.Invoke(new FenValue[] { FenValue.FromObject(target), argsList, newTargetArg }, _context, FenValue.FromObject(handlerVal));
                    // Forward: construct target
                    var targetCtorFn = args[0].AsFunction();
                    if (targetCtorFn != null) return targetCtorFn.Invoke(callArgs, _context);
                    return FenValue.Undefined;
                })));

                return FenValue.FromObject(proxy);
            })));

            // Reflect API is defined later in this file (around line 2550)

            // ES6 RegExp constructor - wraps .NET Regex
            // Mutable state for legacy static accessor properties (RegExp.$1-$9, etc.)
            var _lastRegExpGroups = new string[10]; // [0] = full match, [1]-[9] = capture groups
            var _lastRegExpInput = "";
            var _lastRegExpLeftContext = "";
            var _lastRegExpRightContext = "";

            void UpdateLegacyRegExpStatics(Match m, string input)
            {
                _lastRegExpInput = input;
                _lastRegExpGroups[0] = m.Value;
                for (int gi = 1; gi <= 9; gi++)
                    _lastRegExpGroups[gi] = gi < m.Groups.Count ? m.Groups[gi].Value : "";
                _lastRegExpLeftContext = input.Substring(0, m.Index);
                _lastRegExpRightContext = input.Substring(m.Index + m.Length);
            }

            var regexpProtoEs6 = new FenObject();
            var regexpCtorEs6 = new FenFunction("RegExp", (FenValue[] args, FenValue thisVal) =>
            {
                var pattern = args.Length > 0 ? args[0].ToString() : "";
                var flags = args.Length > 1 ? args[1].ToString() : "";

                var regexObj = new FenObject();
                regexObj.InternalClass = "RegExp";
                regexObj.SetPrototype(regexpProtoEs6);
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
                try
                {
                    regex = new Regex(pattern, options);
                }
                catch (Exception logEx)
                {
                    FenLogger.Warn($"[FenRuntime] Failed writing top-level runtime diagnostics: {logEx.Message}", LogCategory.JavaScript);
                }

                regexObj.NativeObject = regex;

                return FenValue.FromObject(regexObj);
            });

            // RegExp.prototype methods (shared across all instances)
            regexpProtoEs6.InternalClass = "RegExp";
            regexpProtoEs6.SetBuiltin("constructor", FenValue.FromFunction(regexpCtorEs6));

            regexpProtoEs6.SetBuiltin("test", FenValue.FromFunction(new FenFunction("test", (testArgs, testThis) =>
            {
                if (!testThis.IsObject) return FenValue.FromBoolean(false);
                var thisObj = testThis.AsObject() as FenObject;
                var nativeRegex = thisObj?.NativeObject as Regex;
                if (testArgs.Length == 0 || nativeRegex == null) return FenValue.FromBoolean(false);
                var input = testArgs[0].ToString();
                var m = nativeRegex.Match(input);
                if (m.Success) UpdateLegacyRegExpStatics(m, input);
                return FenValue.FromBoolean(m.Success);
            })));

            regexpProtoEs6.SetBuiltin("exec", FenValue.FromFunction(new FenFunction("exec", (execArgs, execThis) =>
            {
                if (!execThis.IsObject) return FenValue.Null;
                var thisObj = execThis.AsObject() as FenObject;
                var nativeRegex = thisObj?.NativeObject as Regex;
                if (execArgs.Length == 0 || nativeRegex == null) return FenValue.Null;
                var input = execArgs[0].ToString();
                int startIndex = (int)(thisObj.Get("lastIndex").ToNumber());
                bool isGlobal = thisObj.Get("global").ToBoolean();

                if (startIndex < 0) startIndex = 0;
                if (startIndex >= input.Length + 1)
                {
                    if (isGlobal) thisObj.Set("lastIndex", FenValue.FromNumber(0));
                    return FenValue.Null;
                }

                var match = nativeRegex.Match(input, Math.Min(startIndex, input.Length));
                if (!match.Success)
                {
                    if (isGlobal) thisObj.Set("lastIndex", FenValue.FromNumber(0));
                    return FenValue.Null;
                }

                UpdateLegacyRegExpStatics(match, input);
                var result = FenObject.CreateArray();
                result.Set("0", FenValue.FromString(match.Value));
                for (int i = 1; i < match.Groups.Count; i++)
                    result.Set(i.ToString(), FenValue.FromString(match.Groups[i].Value));
                result.Set("length", FenValue.FromNumber(match.Groups.Count));
                result.Set("index", FenValue.FromNumber(match.Index));
                result.Set("input", FenValue.FromString(input));
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
                if (isGlobal) thisObj.Set("lastIndex", FenValue.FromNumber(match.Index + match.Length));
                return FenValue.FromObject(result);
            })));

            var regexpSymbolMatch = FenValue.FromFunction(new FenFunction("[Symbol.match]", (matchArgs, matchThis) =>
            {
                if (!matchThis.IsObject) throw new FenTypeError("TypeError: RegExp.prototype[Symbol.match] called on non-object");
                var thisObj = matchThis.AsObject() as FenObject;
                if (thisObj == null || thisObj.InternalClass != "RegExp") throw new FenTypeError("TypeError: this is not a RegExp object");

                var input = matchArgs.Length > 0 ? matchArgs[0].AsString(_context) : string.Empty;
                bool isGlobal = thisObj.Get("global").ToBoolean();
                if (!isGlobal)
                {
                    var execFn = regexpProtoEs6.Get("exec").AsFunction();
                    return execFn != null
                        ? execFn.Invoke(new[] { FenValue.FromString(input) }, _context, matchThis)
                        : FenValue.Null;
                }

                var nativeRegex = thisObj.NativeObject as Regex;
                if (nativeRegex == null)
                {
                    return FenValue.Null;
                }

                var matches = nativeRegex.Matches(input);
                if (matches.Count == 0)
                {
                    thisObj.Set("lastIndex", FenValue.FromNumber(0));
                    return FenValue.Null;
                }

                var result = FenObject.CreateArray();
                for (int i = 0; i < matches.Count; i++)
                {
                    result.Set(i.ToString(), FenValue.FromString(matches[i].Value));
                }

                result.Set("length", FenValue.FromNumber(matches.Count));
                UpdateLegacyRegExpStatics(matches[matches.Count - 1], input);
                thisObj.Set("lastIndex", FenValue.FromNumber(0));
                return FenValue.FromObject(result);
            }));
            regexpProtoEs6.SetSymbol(JsSymbol.Match, regexpSymbolMatch);
            regexpProtoEs6.SetBuiltin("[Symbol.match]", regexpSymbolMatch);

            // RegExp.prototype.compile (Annex B) - recompiles the regex in place
            regexpProtoEs6.SetBuiltin("compile", FenValue.FromFunction(new FenFunction("compile", (compileArgs, compileThis) =>
            {
                if (!compileThis.IsObject) throw new FenTypeError("TypeError: RegExp.prototype.compile called on non-object");
                var thisObj = compileThis.AsObject() as FenObject;
                if (thisObj == null || thisObj.InternalClass != "RegExp") throw new FenTypeError("TypeError: this is not a RegExp object");
                var pat = compileArgs.Length > 0 ? compileArgs[0].ToString() : "";
                var fl = compileArgs.Length > 1 ? compileArgs[1].ToString() : "";
                var opts = RegexOptions.None;
                if (fl.Contains("i")) opts |= RegexOptions.IgnoreCase;
                if (fl.Contains("m")) opts |= RegexOptions.Multiline;
                if (fl.Contains("s")) opts |= RegexOptions.Singleline;
                try
                {
                    thisObj.NativeObject = new Regex(pat, opts);
                    thisObj.Set("source", FenValue.FromString(pat));
                    thisObj.Set("flags", FenValue.FromString(fl));
                    thisObj.Set("global", FenValue.FromBoolean(fl.Contains("g")));
                    thisObj.Set("ignoreCase", FenValue.FromBoolean(fl.Contains("i")));
                    thisObj.Set("multiline", FenValue.FromBoolean(fl.Contains("m")));
                    thisObj.Set("lastIndex", FenValue.FromNumber(0));
                }
                catch (Exception ex) { FenLogger.Warn($"[FenRuntime] RegExp.compile failed: {ex.Message}", LogCategory.JavaScript); }
                return compileThis;
            })));

            regexpProtoEs6.SetBuiltin("toString", FenValue.FromFunction(new FenFunction("toString", (toStrArgs, toStrThis) =>
            {
                if (!toStrThis.IsObject) return FenValue.FromString("/(?:)/");
                var thisObj = toStrThis.AsObject() as FenObject;
                var src = thisObj?.Get("source").ToString() ?? "(?:)";
                var fl = thisObj?.Get("flags").ToString() ?? "";
                return FenValue.FromString($"/{src}/{fl}");
            })));

            regexpCtorEs6.Set("prototype", FenValue.FromObject(regexpProtoEs6));

            // Helper: verify that the getter/setter is called on the RegExp constructor itself.
            void ValidateRegExpReceiver(FenValue thisVal)
            {
                if (!thisVal.IsFunction)
                    throw new FenTypeError("TypeError: RegExp legacy accessor called on non-RegExp constructor");
                var regexpGlobal = _globalEnv.Get("RegExp");
                if (!ReferenceEquals(thisVal.AsObject(), regexpGlobal.AsObject()))
                    throw new FenTypeError("TypeError: RegExp legacy accessor called on invalid receiver");
            }

            // Annex B legacy static accessor properties on RegExp constructor
            // These are non-enumerable, configurable accessor properties.
            for (int gi = 1; gi <= 9; gi++)
            {
                var groupIndex = gi; // capture for closure
                var getterName = $"get ${groupIndex}";
                regexpCtorEs6.DefineOwnProperty($"${groupIndex}", PropertyDescriptor.Accessor(
                    new FenFunction(getterName, (a, thisVal) =>
                    {
                        ValidateRegExpReceiver(thisVal);
                        return FenValue.FromString(_lastRegExpGroups[groupIndex] ?? "");
                    }),
                    setter: null, enumerable: false, configurable: true));
            }
            // input / $_ Ã¢â‚¬â€ spec requires both a getter AND a setter
            regexpCtorEs6.DefineOwnProperty("input", PropertyDescriptor.Accessor(
                new FenFunction("get input", (a, thisVal) => { ValidateRegExpReceiver(thisVal); return FenValue.FromString(_lastRegExpInput); }),
                new FenFunction("set input", (a, thisVal) => { ValidateRegExpReceiver(thisVal); _lastRegExpInput = a.Length > 0 ? a[0].ToString() : ""; return FenValue.Undefined; }),
                enumerable: false, configurable: true));
            regexpCtorEs6.DefineOwnProperty("$_", PropertyDescriptor.Accessor(
                new FenFunction("get $_", (a, thisVal) => { ValidateRegExpReceiver(thisVal); return FenValue.FromString(_lastRegExpInput); }),
                new FenFunction("set $_", (a, thisVal) => { ValidateRegExpReceiver(thisVal); _lastRegExpInput = a.Length > 0 ? a[0].ToString() : ""; return FenValue.Undefined; }),
                enumerable: false, configurable: true));
            // lastMatch / $&
            regexpCtorEs6.DefineOwnProperty("lastMatch", PropertyDescriptor.Accessor(
                new FenFunction("get lastMatch", (a, thisVal) => { ValidateRegExpReceiver(thisVal); return FenValue.FromString(_lastRegExpGroups[0] ?? ""); }),
                setter: null, enumerable: false, configurable: true));
            regexpCtorEs6.DefineOwnProperty("$&", PropertyDescriptor.Accessor(
                new FenFunction("get $&", (a, thisVal) => { ValidateRegExpReceiver(thisVal); return FenValue.FromString(_lastRegExpGroups[0] ?? ""); }),
                setter: null, enumerable: false, configurable: true));
            // lastParen / $+
            regexpCtorEs6.DefineOwnProperty("lastParen", PropertyDescriptor.Accessor(
                new FenFunction("get lastParen", (a, thisVal) =>
                {
                    ValidateRegExpReceiver(thisVal);
                    for (int pi = 9; pi >= 1; pi--)
                        if (!string.IsNullOrEmpty(_lastRegExpGroups[pi])) return FenValue.FromString(_lastRegExpGroups[pi]);
                    return FenValue.FromString("");
                }),
                setter: null, enumerable: false, configurable: true));
            regexpCtorEs6.DefineOwnProperty("$+", PropertyDescriptor.Accessor(
                new FenFunction("get $+", (a, thisVal) =>
                {
                    ValidateRegExpReceiver(thisVal);
                    for (int pi = 9; pi >= 1; pi--)
                        if (!string.IsNullOrEmpty(_lastRegExpGroups[pi])) return FenValue.FromString(_lastRegExpGroups[pi]);
                    return FenValue.FromString("");
                }),
                setter: null, enumerable: false, configurable: true));
            // leftContext / $`
            regexpCtorEs6.DefineOwnProperty("leftContext", PropertyDescriptor.Accessor(
                new FenFunction("get leftContext", (a, thisVal) => { ValidateRegExpReceiver(thisVal); return FenValue.FromString(_lastRegExpLeftContext); }),
                setter: null, enumerable: false, configurable: true));
            regexpCtorEs6.DefineOwnProperty("$`", PropertyDescriptor.Accessor(
                new FenFunction("get $`", (a, thisVal) => { ValidateRegExpReceiver(thisVal); return FenValue.FromString(_lastRegExpLeftContext); }),
                setter: null, enumerable: false, configurable: true));
            // rightContext / $'
            regexpCtorEs6.DefineOwnProperty("rightContext", PropertyDescriptor.Accessor(
                new FenFunction("get rightContext", (a, thisVal) => { ValidateRegExpReceiver(thisVal); return FenValue.FromString(_lastRegExpRightContext); }),
                setter: null, enumerable: false, configurable: true));
            regexpCtorEs6.DefineOwnProperty("$'", PropertyDescriptor.Accessor(
                new FenFunction("get $'", (a, thisVal) => { ValidateRegExpReceiver(thisVal); return FenValue.FromString(_lastRegExpRightContext); }),
                setter: null, enumerable: false, configurable: true));

            SetGlobal("RegExp", FenValue.FromFunction(regexpCtorEs6));

            // ES6 Intl API - Internationalization (basic stubs)
            var intl = new FenObject();

            // Intl.DateTimeFormat
            intl.Set("DateTimeFormat", FenValue.FromFunction(new FenFunction("DateTimeFormat",
                (FenValue[] args, FenValue thisVal) =>
                {
                    var locale = args.Length > 0 ? args[0].ToString() : "en-US";
                    var formatter = new FenObject();
                    formatter.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) =>
                    {
                        if (fArgs.Length == 0) return FenValue.FromString("");
                        double timestamp = fArgs[0].ToNumber();
                        var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp).DateTime;
                        return FenValue.FromString(dt.ToString("G",
                            System.Globalization.CultureInfo.GetCultureInfo(locale)));
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
            intl.Set("NumberFormat", FenValue.FromFunction(new FenFunction("NumberFormat",
                (FenValue[] args, FenValue thisVal) =>
                {
                    var locale = args.Length > 0 ? args[0].ToString() : "en-US";
                    var formatter = new FenObject();
                    formatter.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) =>
                    {
                        if (fArgs.Length == 0) return FenValue.FromString("");
                        double num = fArgs[0].ToNumber();
                        return FenValue.FromString(num.ToString("N",
                            System.Globalization.CultureInfo.GetCultureInfo(locale)));
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
            intl.Set("Collator", FenValue.FromFunction(new FenFunction("Collator",
                (FenValue[] args, FenValue thisVal) =>
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
            SetGlobal("ArrayBuffer", FenValue.FromFunction(new FenFunction("ArrayBuffer",
                (FenValue[] args, FenValue thisVal) =>
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
            string[] typedArrayNames =
            {
                "Uint8Array", "Int8Array", "Uint16Array", "Int16Array", "Uint32Array", "Int32Array", "Float32Array",
                "Float64Array", "Uint8ClampedArray"
            };
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
            SetGlobal("DataView", FenValue.FromFunction(new FenFunction("DataView",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.Null;
                    var bufferObj = args[0].AsObject() as FenObject;
                    int byteOffset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                    int byteLength = args.Length > 2
                        ? (int)args[2].ToNumber()
                        : (int)(bufferObj != null ? bufferObj.Get("byteLength").ToNumber() : 0) - byteOffset;

                    var view = new FenObject();
                    view.Set("buffer", FenValue.FromObject(bufferObj));
                    view.Set("byteOffset", FenValue.FromNumber(byteOffset));
                    view.Set("byteLength", FenValue.FromNumber(byteLength));

                    // Simplified getters/setters
                    view.Set("getUint8",
                        FenValue.FromFunction(new FenFunction("getUint8", (vArgs, vThis) => FenValue.FromNumber(0))));
                    view.Set("setUint8",
                        FenValue.FromFunction(new FenFunction("setUint8", (vArgs, vThis) => FenValue.Undefined)));

                    return FenValue.FromObject(view);
                })));

            // Promise - Updated Full Spec Implementation (Phase 1)
            var promiseCtor = new FenFunction("Promise", (FenValue[] args, FenValue thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction)
                    throw new FenTypeError("TypeError: Promise resolver undefined is not a function");
                return FenValue.FromObject(new JsPromise(args[0], _context));
            });
            var promiseObj = FenValue.FromFunction(promiseCtor);
            var promiseStatics = promiseObj.AsObject();
            promiseStatics.Set("resolve", FenValue.FromFunction(new FenFunction("resolve",
                (FenValue[] args, FenValue thisVal) =>
                    FenValue.FromObject(JsPromise.Resolve(args.Length > 0 ? args[0] : FenValue.Undefined, _context)))));
            promiseStatics.Set("reject", FenValue.FromFunction(new FenFunction("reject",
                (FenValue[] args, FenValue thisVal) =>
                    FenValue.FromObject(JsPromise.Reject(args.Length > 0 ? args[0] : FenValue.Undefined, _context)))));
            promiseStatics.Set("all", FenValue.FromFunction(new FenFunction("all",
                (FenValue[] args, FenValue thisVal) =>
                    FenValue.FromObject(JsPromise.All(args.Length > 0 ? args[0] : FenValue.Undefined, _context)))));
            promiseStatics.Set("race", FenValue.FromFunction(new FenFunction("race",
                (FenValue[] args, FenValue thisVal) =>
                    FenValue.FromObject(JsPromise.Race(args.Length > 0 ? args[0] : FenValue.Undefined, _context)))));
            promiseStatics.Set("allSettled", FenValue.FromFunction(new FenFunction("allSettled",
                (FenValue[] args, FenValue thisVal) =>
                    FenValue.FromObject(JsPromise.AllSettled(args.Length > 0 ? args[0] : FenValue.Undefined,
                        _context)))));
            promiseStatics.Set("any", FenValue.FromFunction(new FenFunction("any",
                (FenValue[] args, FenValue thisVal) =>
                    FenValue.FromObject(JsPromise.Any(args.Length > 0 ? args[0] : FenValue.Undefined, _context)))));
            SetGlobal("Promise", promiseObj);

            // queueMicrotask
            SetGlobal("queueMicrotask", FenValue.FromFunction(new FenFunction("queueMicrotask",
                (FenValue[] args, FenValue thisVal) =>
                {
                    if (args.Length > 0 && args[0].IsFunction)
                    {
                        var callback = args[0].AsFunction();
                        Core.EventLoop.EventLoopCoordinator.Instance.ScheduleMicrotask(() =>
                        {
                            try
                            {
                                callback.Invoke(new FenValue[0], _context);
                            }
                            catch
                            {
                            }
                        });
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
                urlObj.Set("origin",
                    FenValue.FromString(uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port)));

                // searchParams
                var searchParams = new FenObject();
                // Basic manual parsing for searchParams to avoid HttpUtility dependency
                var queryStr = uri.Query.StartsWith("?") ? uri.Query.Substring(1) : uri.Query;
                var qp = queryStr.Split('&', StringSplitOptions.RemoveEmptyEntries);

                searchParams.Set("get", FenValue.FromFunction(new FenFunction("get", (spArgs, spThis) =>
                {
                    string key = spArgs.Length > 0 ? spArgs[0].ToString() : "";
                    foreach (var p in qp)
                    {
                        var kv = p.Split('=');
                        if (System.Net.WebUtility.UrlDecode(kv[0]) == key)
                            return FenValue.FromString(kv.Length > 1 ? System.Net.WebUtility.UrlDecode(kv[1]) : "");
                    }

                    return FenValue.Null;
                })));

                urlObj.Set("searchParams", FenValue.FromObject(searchParams));
                urlObj.Set("toString",
                    FenValue.FromFunction(new FenFunction("toString", (a, t) => FenValue.FromString(uri.AbsoluteUri))));

                return FenValue.FromObject(urlObj);
            })));

            SetGlobal("URLSearchParams", FenValue.FromFunction(new FenFunction("URLSearchParams",
                (FenValue[] args, FenValue thisVal) =>
                {
                    var sp = new FenObject();
                    string query = args.Length > 0 ? args[0].ToString() : "";
                    if (query.StartsWith("?")) query = query.Substring(1);
                    var qpList = new List<KeyValuePair<string, string>>();
                    foreach (var p in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = p.Split('=');
                        qpList.Add(new KeyValuePair<string, string>(System.Net.WebUtility.UrlDecode(kv[0]),
                            kv.Length > 1 ? System.Net.WebUtility.UrlDecode(kv[1]) : ""));
                    }

                    sp.Set("get", FenValue.FromFunction(new FenFunction("get", (spArgs, spThis) =>
                    {
                        string key = spArgs.Length > 0 ? spArgs[0].ToString() : "";
                        var match = qpList.Find(x => x.Key == key);
                        return match.Key != null ? FenValue.FromString(match.Value) : FenValue.Null;
                    })));
                    sp.Set("has", FenValue.FromFunction(new FenFunction("has", (spArgs, spThis) =>
                    {
                        string key = spArgs.Length > 0 ? spArgs[0].ToString() : "";
                        return FenValue.FromBoolean(qpList.Exists(x => x.Key == key));
                    })));
                    sp.Set("toString", FenValue.FromFunction(new FenFunction("toString", (a, t) =>
                    {
                        var sb = new StringBuilder();
                        foreach (var p in qpList)
                        {
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
            if (mathObj.IsObject)
            {
                var m = mathObj.AsObject();
                m.Set("cbrt", FenValue.FromFunction(new FenFunction("cbrt", (FenValue[] args, FenValue thisVal) =>
                    FenValue.FromNumber(Math.Pow(args.Length > 0 ? args[0].ToNumber() : double.NaN, 1.0 / 3.0)))));
                m.Set("hypot", FenValue.FromFunction(new FenFunction("hypot", (args, thisVal) =>
                {
                    double sum = 0;
                    foreach (var arg in args)
                    {
                        double n = arg.ToNumber();
                        sum += n * n;
                    }

                    return FenValue.FromNumber(Math.Sqrt(sum));
                })));
                m.Set("log2", FenValue.FromFunction(new FenFunction("log2", (FenValue[] args, FenValue thisVal) =>
                    FenValue.FromNumber(Math.Log(args.Length > 0 ? args[0].ToNumber() : double.NaN, 2)))));
            }

            // Global functions: parseInt, parseFloat, isNaN, isFinite
            SetGlobal("parseInt", FenValue.FromFunction(new FenFunction("parseInt", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                var str = args[0].ToString().Trim();
                int radix = args.Length > 1 ? (int)args[1].ToNumber() : 10;
                if (radix == 0) radix = 10;
                if (radix < 2 || radix > 36) return FenValue.FromNumber(double.NaN);

                bool negative = false;
                if (str.StartsWith("-"))
                {
                    negative = true;
                    str = str.Substring(1);
                }
                else if (str.StartsWith("+"))
                {
                    str = str.Substring(1);
                }

                if (radix == 16 && (str.StartsWith("0x") || str.StartsWith("0X"))) str = str.Substring(2);
                else if (radix == 10 && (str.StartsWith("0x") || str.StartsWith("0X")))
                {
                    radix = 16;
                    str = str.Substring(2);
                }

                try
                {
                    long result = Convert.ToInt64(str, radix);
                    return FenValue.FromNumber(negative ? -result : result);
                }
                catch
                {
                    // Parse as much as possible
                    string validChars = "0123456789abcdefghijklmnopqrstuvwxyz".Substring(0, radix);
                    var sb = new StringBuilder();
                    foreach (char c in str.ToLowerInvariant())
                    {
                        if (validChars.Contains(c)) sb.Append(c);
                        else break;
                    }

                    if (sb.Length == 0) return FenValue.FromNumber(double.NaN);
                    try
                    {
                        long result = Convert.ToInt64(sb.ToString(), radix);
                        return FenValue.FromNumber(negative ? -result : result);
                    }
                    catch
                    {
                        return FenValue.FromNumber(double.NaN);
                    }
                }
            })));

            SetGlobal("parseFloat", FenValue.FromFunction(new FenFunction("parseFloat", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                var str = args[0].ToString().Trim();
                // Parse leading numeric portion
                var sb = new StringBuilder();
                bool hasDecimal = false;
                bool hasExp = false;
                for (int i = 0; i < str.Length; i++)
                {
                    char c = str[i];
                    if (i == 0 && (c == '+' || c == '-'))
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (char.IsDigit(c))
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (c == '.' && !hasDecimal && !hasExp)
                    {
                        hasDecimal = true;
                        sb.Append(c);
                        continue;
                    }

                    if ((c == 'e' || c == 'E') && !hasExp && sb.Length > 0)
                    {
                        hasExp = true;
                        sb.Append(c);
                        if (i + 1 < str.Length && (str[i + 1] == '+' || str[i + 1] == '-'))
                        {
                            sb.Append(str[++i]);
                        }

                        continue;
                    }

                    break;
                }

                if (sb.Length == 0 || sb.ToString() == "+" || sb.ToString() == "-")
                    return FenValue.FromNumber(double.NaN);
                if (double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double result))
                    return FenValue.FromNumber(result);
                return FenValue.FromNumber(double.NaN);
            })));

            SetGlobal("NaN", FenValue.FromNumber(double.NaN));
            SetGlobal("Infinity", FenValue.FromNumber(double.PositiveInfinity));

            SetGlobal("isNaN", FenValue.FromFunction(new FenFunction("isNaN", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(true);
                var num = args[0].ToNumber();
                return FenValue.FromBoolean(double.IsNaN(num));
            })));

            // eval Ã¢â‚¬â€ global function; direct eval is handled in runtime execution path.
            // This entry makes typeof eval === "function" and supports indirect eval.
            SetGlobal("eval", FenValue.FromFunction(new FenFunction("eval", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                if (!args[0].IsString) return args[0]; // non-string eval returns its argument

                // SECURITY: Check JsPermissions.Eval Ã¢â‚¬â€ denied unless explicitly granted.
                // Browser contexts grant it by default; CSP 'unsafe-eval' enforcement revokes it.
                if (_context != null && !_context.Permissions.Check(FenBrowser.FenEngine.Security.JsPermissions.Eval))
                {
                    _context.Permissions.LogViolation(
                        FenBrowser.FenEngine.Security.JsPermissions.Eval,
                        "eval()",
                        "eval() blocked by permission policy");
                    throw new FenTypeError("EvalError: Refused to evaluate a string as JavaScript because 'unsafe-eval' is not an allowed source of script in the current security policy.");
                }

                // SECURITY: Limit input size to prevent DoS through giant eval strings
                var code = args[0].ToString();
                if (code.Length > 1_000_000)
                    throw new FenTypeError("EvalError: eval() input exceeds maximum allowed size (1 MB).");

                // Indirect eval: run in global scope and propagate exceptions as throws.
                try
                {
                    var evalResult = ExecuteSimple(code, allowReturn: false);
                    if (evalResult is FenValue fv)
                    {
                        if (fv.IsError)
                        {
                            var err = fv.AsError() ?? fv.AsString();
                            if (err.StartsWith("SyntaxError:", StringComparison.Ordinal)) throw new FenSyntaxError(err);
                            if (err.StartsWith("ReferenceError:", StringComparison.Ordinal)) throw new FenReferenceError(err);
                            if (err.StartsWith("TypeError:", StringComparison.Ordinal)) throw new FenTypeError(err);
                            throw new FenInternalError($"EvalError: {err}");
                        }
                        return fv;
                    }
                    return FenValue.Undefined;
                }
                catch (FenSyntaxError)
                {
                    throw;
                }
                catch (FenReferenceError)
                {
                    throw;
                }
                catch (FenTypeError)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new FenInternalError($"EvalError: {ex.Message}");
                }
            })));

            // String object
            var stringObj = new FenObject();
            stringObj.Set("fromCharCode", FenValue.FromFunction(new FenFunction("fromCharCode", (args, thisVal) =>
            {
                var sb = new StringBuilder();
                foreach (var arg in args) sb.Append((char)arg.ToNumber());
                return FenValue.FromString(sb.ToString());
            })));

            stringProto.SetBuiltin("padEnd", FenValue.FromFunction(new FenFunction("padEnd", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.padEnd called on null or undefined");
                var str = thisVal.AsString(_context);
                var targetLength = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (str.Length >= targetLength) return FenValue.FromString(str);
                var padString = args.Length > 1 ? args[1].AsString(_context) : " ";
                if (string.IsNullOrEmpty(padString)) return FenValue.FromString(str);

                var sb = new StringBuilder(str);
                while (sb.Length < targetLength)
                {
                    sb.Append(padString);
                }

                return FenValue.FromString(sb.ToString().Substring(0, targetLength));
            })));
            stringProto.SetBuiltin("padStart", FenValue.FromFunction(new FenFunction("padStart", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.padStart called on null or undefined");
                var str = thisVal.AsString(_context);
                var targetLength = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (str.Length >= targetLength) return FenValue.FromString(str);
                var padString = args.Length > 1 ? args[1].AsString(_context) : " ";
                if (string.IsNullOrEmpty(padString)) return FenValue.FromString(str);

                var padLen = targetLength - str.Length;
                var sb = new StringBuilder();
                while (sb.Length < padLen) sb.Append(padString);
                if (sb.Length > padLen) sb.Length = padLen;
                sb.Append(str);
                return FenValue.FromString(sb.ToString());
            })));
            stringProto.SetBuiltin("trimStart",
                FenValue.FromFunction(new FenFunction("trimStart",
                    (FenValue[] args, FenValue thisVal) =>
                    {
                        if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.trimStart called on null or undefined");
                        return FenValue.FromString(thisVal.AsString(_context).TrimStart());
                    })));
            stringProto.SetBuiltin("trimEnd",
                FenValue.FromFunction(new FenFunction("trimEnd",
                    (FenValue[] args, FenValue thisVal) =>
                    {
                        if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.trimEnd called on null or undefined");
                        return FenValue.FromString(thisVal.AsString(_context).TrimEnd());
                    })));
            stringProto.SetBuiltin("trim",
                FenValue.FromFunction(new FenFunction("trim",
                    (FenValue[] args, FenValue thisVal) =>
                    {
                        if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.trim called on null or undefined");
                        return FenValue.FromString(thisVal.AsString(_context).Trim());
                    })));

            stringProto.SetBuiltin("startsWith", FenValue.FromFunction(new FenFunction("startsWith", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.startsWith called on null or undefined");
                var str = thisVal.AsString(_context);
                var search = args.Length > 0 ? args[0].AsString(_context) : "";
                var pos = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (pos < 0) pos = 0;
                if (pos >= str.Length) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(str.Substring(pos).StartsWith(search));
            })));

            stringProto.SetBuiltin("endsWith", FenValue.FromFunction(new FenFunction("endsWith", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.endsWith called on null or undefined");
                var str = thisVal.AsString(_context);
                var search = args.Length > 0 ? args[0].AsString(_context) : "";
                var len = args.Length > 1 ? (int)args[1].ToNumber() : str.Length;
                if (len > str.Length) len = str.Length;
                var sub = str.Substring(0, len);
                return FenValue.FromBoolean(sub.EndsWith(search));
            })));

            stringProto.SetBuiltin("includes", FenValue.FromFunction(new FenFunction("includes", (args, thisVal) =>
            {
                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.includes called on null or undefined");
                var str = thisVal.AsString(_context);
                var search = args.Length > 0 ? args[0].AsString(_context) : "";
                var pos = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (pos < 0) pos = 0;
                return FenValue.FromBoolean(str.IndexOf(search, pos, StringComparison.Ordinal) != -1);
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

            SetGlobal("isFinite", FenValue.FromFunction(new FenFunction("isFinite", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var num = args[0].ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num));
            })));

            // Number object with static methods
            var numberObj = new FenObject();
            numberObj.Set("isNaN", FenValue.FromFunction(new FenFunction("isNaN", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsNumber) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(double.IsNaN(args[0].ToNumber()));
            })));
            numberObj.Set("isFinite", FenValue.FromFunction(new FenFunction("isFinite", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsNumber) return FenValue.FromBoolean(false);
                var num = args[0].ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num));
            })));
            numberObj.Set("isInteger", FenValue.FromFunction(new FenFunction("isInteger", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsNumber) return FenValue.FromBoolean(false);
                var num = args[0].ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num) && Math.Floor(num) == num);
            })));
            numberObj.Set("parseInt", FenValue.FromFunction(new FenFunction("parseInt", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                var str = args[0].ToString().Trim();
                int radix = args.Length > 1 ? (int)args[1].ToNumber() : 10;
                if (radix == 0) radix = 10;
                if (radix < 2 || radix > 36) return FenValue.FromNumber(double.NaN);
                bool negative = str.StartsWith("-");
                if (negative) str = str.Substring(1);
                if (str.StartsWith("+")) str = str.Substring(1);
                if ((str.StartsWith("0x") || str.StartsWith("0X")))
                {
                    if (radix == 10 || radix == 16) radix = 16;
                    str = str.Substring(2);
                }

                try
                {
                    long result = Convert.ToInt64(str, radix);
                    return FenValue.FromNumber(negative ? -result : result);
                }
                catch
                {
                    return FenValue.FromNumber(double.NaN);
                }
            })));
            numberObj.Set("parseFloat", FenValue.FromFunction(new FenFunction("parseFloat", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromNumber(double.NaN);
                if (double.TryParse(args[0].ToString().Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double result))
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
            numberObj.Set("isSafeInteger", FenValue.FromFunction(new FenFunction("isSafeInteger", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsNumber) return FenValue.FromBoolean(false);
                var num = args[0].ToNumber();
                return FenValue.FromBoolean(!double.IsNaN(num) && !double.IsInfinity(num) && Math.Floor(num) == num &&
                                            Math.Abs(num) <= 9007199254740991);
            })));

            // Number prototype methods (toFixed, toPrecision, toExponential)
            // These will be accessed on number values
            numberObj.Set("prototype", FenValue.FromObject(new FenObject()));

            // Number already registered at top

            // encodeURI / decodeURI
            SetGlobal("encodeURI", FenValue.FromFunction(new FenFunction("encodeURI", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromString("");
                var str = args[0].ToString();
                return FenValue.FromString(Uri.EscapeUriString(str));
            })));

            SetGlobal("decodeURI", FenValue.FromFunction(new FenFunction("decodeURI", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromString("");
                try
                {
                    return FenValue.FromString(Uri.UnescapeDataString(args[0].ToString()));
                }
                catch
                {
                    return FenValue.FromString(args[0].ToString());
                }
            })));

            SetGlobal("encodeURIComponent", FenValue.FromFunction(new FenFunction("encodeURIComponent",
                (args, thisVal) =>
                {
                    if (args.Length == 0) return FenValue.FromString("");
                    return FenValue.FromString(Uri.EscapeDataString(args[0].ToString()));
                })));

            SetGlobal("decodeURIComponent", FenValue.FromFunction(new FenFunction("decodeURIComponent",
                (args, thisVal) =>
                {
                    if (args.Length == 0) return FenValue.FromString("");
                    try
                    {
                        return FenValue.FromString(Uri.UnescapeDataString(args[0].ToString()));
                    }
                    catch
                    {
                        return FenValue.FromString(args[0].ToString());
                    }
                })));

            // btoa / atob (Base64 encoding/decoding)
            SetGlobal("btoa", FenValue.FromFunction(new FenFunction("btoa", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromString("");
                var str = args[0].ToString();
                var bytes = System.Text.Encoding.UTF8.GetBytes(str);
                return FenValue.FromString(Convert.ToBase64String(bytes));
            })));

            SetGlobal("atob", FenValue.FromFunction(new FenFunction("atob", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromString("");
                try
                {
                    var bytes = Convert.FromBase64String(args[0].ToString());
                    return FenValue.FromString(System.Text.Encoding.UTF8.GetString(bytes));
                }
                catch
                {
                    return FenValue.FromString("");
                }
            })));

            // escape / unescape (Annex B.2.1.1 / B.2.1.2)
            var escapeFn = new FenFunction("escape", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromString("undefined");
                var str = args[0].ToString();
                const string noEscape = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789@*_+-./";
                var sb = new System.Text.StringBuilder();
                foreach (char c in str)
                {
                    if (noEscape.IndexOf(c) >= 0)
                        sb.Append(c);
                    else if (c >= 256)
                        sb.Append('%').Append('u').Append(((int)c).ToString("X4"));
                    else
                        sb.Append('%').Append(((int)c).ToString("X2"));
                }
                return FenValue.FromString(sb.ToString());
            });
            escapeFn.NativeLength = 1;
            escapeFn.IsConstructor = false;
            SetGlobal("escape", FenValue.FromFunction(escapeFn));

            var unescapeFn = new FenFunction("unescape", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromString("undefined");
                var str = args[0].ToString();
                var sb = new System.Text.StringBuilder();
                int i = 0;
                while (i < str.Length)
                {
                    char c = str[i];
                    if (c == '%')
                    {
                        if (i + 5 < str.Length && str[i + 1] == 'u' &&
                            IsHexDigit(str[i+2]) && IsHexDigit(str[i+3]) && IsHexDigit(str[i+4]) && IsHexDigit(str[i+5]))
                        {
                            sb.Append((char)Convert.ToInt32(str.Substring(i + 2, 4), 16));
                            i += 6;
                        }
                        else if (i + 2 < str.Length && IsHexDigit(str[i+1]) && IsHexDigit(str[i+2]))
                        {
                            sb.Append((char)Convert.ToInt32(str.Substring(i + 1, 2), 16));
                            i += 3;
                        }
                        else
                        {
                            sb.Append(c);
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                        i++;
                    }
                }
                return FenValue.FromString(sb.ToString());
            });
            unescapeFn.NativeLength = 1;
            unescapeFn.IsConstructor = false;
            SetGlobal("unescape", FenValue.FromFunction(unescapeFn));

            // Array object with static methods
            var arrayObj = new FenObject();
            DefineBuiltinMethod(arrayObj, "isArray", 1, (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                if (!args[0].IsObject && !args[0].IsFunction) return FenValue.FromBoolean(false);
                var obj = args[0].AsObject();
                return FenValue.FromBoolean(obj is FenObject fo && fo.InternalClass == "Array");
            });
            DefineBuiltinMethod(arrayObj, "from", 1, (args, thisVal) =>
            {
                var result = FenObject.CreateArray();
                if (args.Length == 0)
                {
                    result.Set("length", FenValue.FromNumber(0));
                    return FenValue.FromObject(result);
                }

                var source = args[0];
                FenFunction mapFn = args.Length > 1 ? args[1].AsFunction() : null;
                int idx = 0;

                if (source.IsString)
                {
                    var str = source.ToString();
                    for (int i = 0; i < str.Length; i++)
                    {
                        var val = FenValue.FromString(str[i].ToString());
                        result.Set(i.ToString(),
                            mapFn != null ? mapFn.Invoke(new FenValue[] { val, FenValue.FromNumber(i) }, null) : val);
                    }

                    result.Set("length", FenValue.FromNumber(str.Length));
                }
                else if (source.IsObject)
                {
                    var obj = source.AsObject() as FenObject;
                    if (obj != null)
                    {
                        // ES2015: prefer Symbol.iterator over array-like
                        var symIterVal = obj.Get("[Symbol.iterator]");
                        if (symIterVal.IsFunction)
                        {
                            var iterator = symIterVal.AsFunction().Invoke(Array.Empty<FenValue>(),
                                new ExecutionContext { ThisBinding = source });
                            var iterObj = iterator.AsObject() as FenObject;
                            if (iterObj != null)
                            {
                                var nextFnVal = iterObj.Get("next");
                                if (nextFnVal.IsFunction)
                                {
                                    var nextFn = nextFnVal.AsFunction();
                                    while (true)
                                    {
                                        var nextResult = nextFn.Invoke(Array.Empty<FenValue>(), null);
                                        var nextObj = nextResult.AsObject() as FenObject;
                                        if (nextObj == null || nextObj.Get("done").ToBoolean()) break;
                                        var item = nextObj.Get("value");
                                        var mapped = mapFn != null
                                            ? mapFn.Invoke(new FenValue[] { item, FenValue.FromNumber(idx) }, null)
                                            : item;
                                        result.Set(idx.ToString(), mapped);
                                        idx++;
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Fallback: array-like (has .length)
                            var lenVal = obj.Get("length");
                            int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                            for (int i = 0; i < len; i++)
                            {
                                var val = obj.Get(i.ToString());
                                result.Set(idx.ToString(),
                                    mapFn != null ? mapFn.Invoke(new FenValue[] { val, FenValue.FromNumber(idx) }, null) : val);
                                idx++;
                            }
                        }
                    }

                    result.Set("length", FenValue.FromNumber(idx));
                }

                return FenValue.FromObject(result);
            });
            DefineBuiltinMethod(arrayObj, "of", 0, (args, thisVal) =>
            {
                var result = FenObject.CreateArray();
                for (int i = 0; i < args.Length; i++)
                {
                    result.Set(i.ToString(), args[i]);
                }

                result.Set("length", FenValue.FromNumber(args.Length));
                return FenValue.FromObject(result);
            });
            if (GetGlobal("Array") is FenValue existingArrayCtor && existingArrayCtor.IsFunction)
            {
                var ctorFn = existingArrayCtor.AsFunction();
                foreach (var key in arrayObj.Keys())
                {
                    ctorFn.DefineOwnProperty(key, PropertyDescriptor.DataNonEnumerable(arrayObj.Get(key)));
                }
            }
            else
            {
                SetGlobal("Array", FenValue.FromObject(arrayObj));
            }

            // JSON object
            var json = new FenObject();
            json.Set("parse", FenValue.FromFunction(new FenFunction("parse", (args, thisVal) =>
            {
                if (args.Length == 0) throw new FenTypeError("TypeError: JSON.parse requires one argument");
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
                    throw new FenSyntaxError($"SyntaxError: JSON.parse: {ex.Message}");
                }
            })));
            json.Set("stringify", FenValue.FromFunction(new FenFunction("stringify", (args, thisVal) =>
            {
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

                    return FenValue.FromString(ConvertToJsonStringWithReplacer(args[0], replacer, replacerArray, spaces,
                        ""));
                }
                catch (Exception ex)
                {
                    throw new FenTypeError($"TypeError: JSON.stringify: {ex.Message}");
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
                var result = target is FenObject fenTarget
                    ? fenTarget.Get(args[1], _context)
                    : target?.Get(args[1].AsString(_context));
                return result != null ? (FenValue)result : FenValue.Undefined;
            })));
            /* [PERF-REMOVED] */

            // Reflect.set(target, propertyKey, value)
            reflectObj.Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) =>
            {
                return ReflectSetOperation(args);
            })));

            // Reflect.has(target, propertyKey)
            reflectObj.Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var target = args[0].AsObject();
                var hasProperty = target is FenObject fenTarget
                    ? fenTarget.Has(args[1], _context)
                    : target?.Has(args[1].AsString(_context), _context) == true;
                return FenValue.FromBoolean(hasProperty);
            })));

            // Reflect.deleteProperty(target, propertyKey)
            reflectObj.Set("deleteProperty", FenValue.FromFunction(new FenFunction("deleteProperty", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var target = args[0].AsObject() as FenObject;
                return FenValue.FromBoolean(target?.Delete(args[1], _context) ?? false);
            })));

            // Reflect.ownKeys(target)
            reflectObj.Set("ownKeys", FenValue.FromFunction(new FenFunction("ownKeys", (args, thisVal) =>
            {
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.FromObject(CreateArray(new string[0]));
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
                        argsList.Add(item);
                    }
                }

                return (FenValue)(fn?.Invoke(argsList.ToArray(), null));
            })));

            // Reflect.construct(target, argumentsList[, newTarget])
            reflectObj.Set("construct", FenValue.FromFunction(new FenFunction("construct", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction)
                    throw new FenTypeError("TypeError: Reflect.construct target must be a function");
                var fn = args[0].AsFunction() as FenFunction;
                // Check newTarget (3rd arg): if provided, it must be a constructor
                FenValue newTargetValue = args[0];
                if (args.Length > 2)
                {
                    newTargetValue = args[2];
                    if (!newTargetValue.IsFunction)
                        throw new FenTypeError("TypeError: newTarget must be a function");
                    var newTargetFn = newTargetValue.AsFunction() as FenFunction;
                    if (newTargetFn != null && !newTargetFn.IsConstructor)
                        throw new FenTypeError("TypeError: newTarget is not a constructor");
                    if (newTargetFn != null && newTargetFn.IsArrowFunction)
                        throw new FenTypeError("TypeError: newTarget is not a constructor");
                }
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

                return fn != null
                    ? ConstructForReflect(fn, argsList.ToArray(), newTargetValue)
                    : FenValue.FromError("TypeError: Reflect.construct target must be a function");
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
                if (args.Length < 2 || (!args[0].IsObject && !args[0].IsFunction))
                {
                    return FenValue.FromBoolean(false);
                }

                if (!args[1].IsObject && !args[1].IsFunction && !args[1].IsNull)
                {
                    return FenValue.FromBoolean(false);
                }

                var target = args[0].AsObject();
                if (target == null) return FenValue.FromBoolean(false);
                var nextProto = args[1].IsNull ? null : args[1].AsObject();

                var objectProtoCandidate = objectConstructor.Get("prototype", null);
                if (objectProtoCandidate.IsObject && ReferenceEquals(target, objectProtoCandidate.AsObject()))
                {
                    return FenValue.FromBoolean(nextProto == null);
                }

                if (target is FenObject targetFo)
                {
                    return FenValue.FromBoolean(targetFo.TrySetPrototype(nextProto));
                }

                try
                {
                    target.SetPrototype(nextProto);
                    return FenValue.FromBoolean(true);
                }
                catch
                {
                    return FenValue.FromBoolean(false);
                }
            })));

            // Reflect.isExtensible(target)
            reflectObj.Set("isExtensible", FenValue.FromFunction(new FenFunction("isExtensible", (args, thisVal) =>
            {
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.FromBoolean(false);
                var target = args[0].AsObject() as FenObject;
                return FenValue.FromBoolean(target != null && target.IsExtensible);
            })));

            // Reflect.preventExtensions(target)
            reflectObj.Set("preventExtensions", FenValue.FromFunction(new FenFunction("preventExtensions",
                (args, thisVal) =>
                {
                    if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.FromBoolean(false);
                    if (args[0].AsObject() is FenObject fenObj2)
                    {
                        fenObj2.PreventExtensions();
                        return FenValue.FromBoolean(true);
                    }

                    return FenValue.FromBoolean(false);
                })));

            // Reflect.defineProperty(target, propertyKey, attributes)
            reflectObj.Set("defineProperty", FenValue.FromFunction(new FenFunction("defineProperty", (args, thisVal) =>
            {
                if (args.Length < 3 || !args[0].IsObject) return FenValue.FromBoolean(false);
                var rTarget = args[0].AsObject() as FenObject;
                if (rTarget == null) return FenValue.FromBoolean(false);
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
                return FenValue.FromBoolean(rTarget.DefineOwnProperty(args[1], rpd));
            })));

            // Reflect.getOwnPropertyDescriptor(target, propertyKey)
            reflectObj.Set("getOwnPropertyDescriptor", FenValue.FromFunction(new FenFunction("getOwnPropertyDescriptor",
                (args, thisVal) =>
                {
                    if (args.Length < 2 || !args[0].IsObject) return FenValue.Undefined;
                    var rTarget2 = args[0].AsObject() as FenObject;
                    if (rTarget2 == null) return FenValue.Undefined;
                    var rDesc = rTarget2.GetOwnPropertyDescriptor(args[1]);
                    if (!rDesc.HasValue) return FenValue.Undefined;
                    var rResult = new FenObject();
                    if (rDesc.Value.Value.HasValue) rResult.Set("value", rDesc.Value.Value.Value, null);
                    if (rDesc.Value.Writable.HasValue)
                        rResult.Set("writable", FenValue.FromBoolean(rDesc.Value.Writable.Value), null);
                    if (rDesc.Value.Enumerable.HasValue)
                        rResult.Set("enumerable", FenValue.FromBoolean(rDesc.Value.Enumerable.Value), null);
                    if (rDesc.Value.Configurable.HasValue)
                        rResult.Set("configurable", FenValue.FromBoolean(rDesc.Value.Configurable.Value), null);
                    if (rDesc.Value.Getter != null) rResult.Set("get", FenValue.FromFunction(rDesc.Value.Getter), null);
                    if (rDesc.Value.Setter != null) rResult.Set("set", FenValue.FromFunction(rDesc.Value.Setter), null);
                    return FenValue.FromObject(rResult);
                })));

            SetGlobal("Reflect", FenValue.FromObject(reflectObj));

            // Proxy - Meta-programming proxy objects
            SetGlobal("Proxy", FenValue.FromFunction(new FenFunction("Proxy", (args, thisVal) =>
            {
                if (args.Length < 2 || (!args[0].IsObject && !args[0].IsFunction) || !args[1].IsObject)
                    throw new FenTypeError("TypeError: Proxy requires target and handler objects");

                var target = args[0].AsObject();
                var handler = args[1].AsObject();
                if (target == null || handler == null)
                    throw new FenTypeError("TypeError: Proxy requires valid target and handler");

                FenObject proxy;
                var targetCallable = args[0].IsFunction || (target.Get("call").IsFunction && target.Get("apply").IsFunction);
                if (targetCallable)
                {
                    FenFunction targetFn = args[0].AsFunction() ?? (target as FenFunction);
                    proxy = new FenFunction("proxy", (pArgs, pThis) =>
                    {
                        var applyTrapInner = handler.Get("apply");
                        if (applyTrapInner.IsFunction)
                        {
                            var argsArr = FenObject.CreateArray();
                            for (int i = 0; i < pArgs.Length; i++) argsArr.Set(i.ToString(), pArgs[i]);
                            argsArr.Set("length", FenValue.FromNumber(pArgs.Length));
                            return applyTrapInner.AsFunction().Invoke(new[] { args[0], pThis, FenValue.FromObject(argsArr) }, _context);
                        }

                        if (targetFn != null) return targetFn.Invoke(pArgs, _context, pThis);
                        var callMethod = target.Get("call").AsFunction();
                        if (callMethod != null)
                        {
                            var callArgs = new FenValue[pArgs.Length + 1];
                            callArgs[0] = pThis;
                            for (int i = 0; i < pArgs.Length; i++) callArgs[i + 1] = pArgs[i];
                            return callMethod.Invoke(callArgs, _context, args[0]);
                        }

                        return FenValue.Undefined;
                    });

                    if (proxy is FenFunction proxyFnObj)
                    {
                        if (targetFn != null)
                        {
                            proxyFnObj.IsAsync = targetFn.IsAsync;
                            proxyFnObj.IsGenerator = targetFn.IsGenerator;
                        }
                        var targetTag = target.Get(JsSymbol.ToStringTag.ToPropertyKey());
                        if (targetTag.IsString)
                        {
                            var targetTagText = targetTag.ToString();
                            if (string.Equals(targetTagText, "AsyncFunction", StringComparison.Ordinal))
                            {
                                proxyFnObj.IsAsync = true;
                                proxyFnObj.IsGenerator = false;
                            }
                            else if (string.Equals(targetTagText, "GeneratorFunction", StringComparison.Ordinal))
                            {
                                proxyFnObj.IsGenerator = true;
                                proxyFnObj.IsAsync = false;
                            }
                        }
                    }

                    var targetProto = target.GetPrototype();
                    if (targetProto != null) proxy.SetPrototype(targetProto);
                }
                else
                {
                    proxy = new FenObject();
                }
                proxy.SetDirect("__target__", args[0]);
                proxy.SetDirect("__proxyTarget__", args[0]);
                proxy.SetDirect("__handler__", FenValue.FromObject(handler));

                // Proxy get trap
                var getTrap = handler.Get("get");
                var setTrap = handler.Get("set");
                var hasTrap = handler.Get("has");
                var deletePropertyTrap = handler.Get("deleteProperty");
                var ownKeysTrap = handler.Get("ownKeys");
                var definePropertyTrap = handler.Get("defineProperty");
                var getOwnPropertyDescriptorTrap = handler.Get("getOwnPropertyDescriptor");
                var getPrototypeOfTrap = handler.Get("getPrototypeOf");
                var setPrototypeOfTrap = handler.Get("setPrototypeOf");
                var applyTrap = handler.Get("apply");

                // Store traps on proxy for FenObject to find
                if (getTrap.IsFunction) proxy.SetDirect("__proxyGet__", getTrap);
                if (setTrap.IsFunction) proxy.SetDirect("__proxySet__", setTrap);
                if (hasTrap.IsFunction) proxy.SetDirect("__proxyHas__", hasTrap);
                if (deletePropertyTrap.IsFunction) proxy.SetDirect("__proxyDelete__", deletePropertyTrap);
                if (ownKeysTrap.IsFunction) proxy.SetDirect("__proxyOwnKeys__", ownKeysTrap);
                if (definePropertyTrap.IsFunction) proxy.SetDirect("__proxyDefineProperty__", definePropertyTrap);
                if (getOwnPropertyDescriptorTrap.IsFunction) proxy.SetDirect("__proxyGetOwnPropertyDescriptor__", getOwnPropertyDescriptorTrap);
                if (getPrototypeOfTrap.IsFunction) proxy.SetDirect("__proxyGetPrototypeOf__", getPrototypeOfTrap);
                if (setPrototypeOfTrap.IsFunction) proxy.SetDirect("__proxySetPrototypeOf__", setPrototypeOfTrap);
                if (applyTrap.IsFunction) proxy.SetDirect("__proxyApply__", applyTrap);
                proxy.SetDirect("__isProxy__", FenValue.FromBoolean(true));

                return FenValue.FromObject(proxy);
            })));
            /* [PERF-REMOVED] */

            // GLOBALTHIS
            // Use the 'window' object we created earlier (it was SetGlobal'd as "window")
            var winGlobal = GetGlobal("window");
            SetGlobal("globalThis", (FenValue)(winGlobal ?? FenValue.Undefined));
            SetGlobal("this", (FenValue)(winGlobal ?? FenValue.Undefined));
            /* [PERF-REMOVED] */

            /* [PERF-REMOVED] */
            // SYMBOL - preserve existing callable Symbol function registration.
            // Some code paths run this enrichment pass later; avoid replacing Symbol with a plain object.
            var symbolStaticValue = GetGlobal("Symbol");
            FenObject symbolStatic = null;
            if (symbolStaticValue.IsFunction || symbolStaticValue.IsObject)
                symbolStatic = symbolStaticValue.AsObject() as FenObject;

            if (symbolStatic == null)
            {
                var fallbackSymbol = new FenFunction("Symbol", (args, thisVal) =>
                {
                    var desc = args.Length > 0 ? args[0].ToString() : null;
                    return FenValue.FromSymbol(new FenBrowser.FenEngine.Core.Types.JsSymbol(desc));
                });
                SetGlobal("Symbol", FenValue.FromFunction(fallbackSymbol));
                symbolStatic = fallbackSymbol;
            }

            /* [PERF-REMOVED] */

            // Symbol.iterator and other well-known symbols
            // JsSymbol.* are static JsSymbol instances (IValue), so pass directly
            symbolStatic.Set("iterator", FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.Iterator));
            symbolStatic.Set("asyncIterator",
                FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.AsyncIterator));
            symbolStatic.Set("toStringTag", FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.ToStringTag));
            symbolStatic.Set("toPrimitive", FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.ToPrimitive));
            symbolStatic.Set("hasInstance", FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.HasInstance));
            symbolStatic.Set("dispose", FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.Dispose));
            symbolStatic.Set("asyncDispose", FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.AsyncDispose));

            // Symbol.for(key)
            symbolStatic.Set("for", FenValue.FromFunction(new FenFunction("for", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0].ToString() : "undefined";
                return FenValue.FromSymbol(FenBrowser.FenEngine.Core.Types.JsSymbol.For(key));
            })));

            // Symbol.keyFor(sym)
            symbolStatic.Set("keyFor", FenValue.FromFunction(new FenFunction("keyFor", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsSymbol)
                {
                    var sym = args[0].AsSymbol();
                    var key = FenBrowser.FenEngine.Core.Types.JsSymbol.KeyFor(sym);
                    return key == null ? FenValue.Undefined : FenValue.FromString(key);
                }

                return FenValue.Undefined;
            })));

            /* [PERF-REMOVED] */

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
                        foreach (var k in target.Keys()) vals.Add(target.Get(k));
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
                        foreach (var k in target.Keys())
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
                        for (int i = 0; i < len; i++)
                        {
                            var entry = entriesArr.Get(i.ToString());
                            if (entry.IsObject)
                            {
                                var entryObj = entry.AsObject();
                                var keyVal = entryObj.Get("0");
                                var key = !keyVal.IsUndefined ? keyVal.ToString() : null;
                                var val = entryObj.Get("1");
                                if (key != null) result.Set(key, val);
                            }
                        }
                    }

                    return FenValue.FromObject(result);
                })));

                // Object.getOwnPropertySymbols(obj)
                objStatic.Set("getOwnPropertySymbols", FenValue.FromFunction(new FenFunction("getOwnPropertySymbols",
                    (args, thisVal) =>
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
                        p.SetDirect("__isRevoked__", FenValue.FromBoolean(false)); // Track revocation
                        p.SetDirect("__target__", target);
                        p.SetDirect("__proxyTarget__", target);
                        p.SetDirect("__handler__", handler);

                        // Copy traps
                        if (handler.IsObject)
                        {
                            var hObj = handler.AsObject();
                            {
                                var getTrap = hObj.Get("get");
                                var setTrap = hObj.Get("set");
                                var hasTrap = hObj.Get("has");
                                var deletePropertyTrap = hObj.Get("deleteProperty");
                                var ownKeysTrap = hObj.Get("ownKeys");
                                var definePropertyTrap = hObj.Get("defineProperty");
                                var getOwnPropertyDescriptorTrap = hObj.Get("getOwnPropertyDescriptor");
                                var getPrototypeOfTrap = hObj.Get("getPrototypeOf");
                                var setPrototypeOfTrap = hObj.Get("setPrototypeOf");
                                var applyTrap = hObj.Get("apply");

                                if (getTrap.IsFunction) p.SetDirect("__proxyGet__", getTrap);
                                if (setTrap.IsFunction) p.SetDirect("__proxySet__", setTrap);
                                if (hasTrap.IsFunction) p.SetDirect("__proxyHas__", hasTrap);
                                if (deletePropertyTrap.IsFunction) p.SetDirect("__proxyDelete__", deletePropertyTrap);
                                if (ownKeysTrap.IsFunction) p.SetDirect("__proxyOwnKeys__", ownKeysTrap);
                                if (definePropertyTrap.IsFunction) p.SetDirect("__proxyDefineProperty__", definePropertyTrap);
                                if (getOwnPropertyDescriptorTrap.IsFunction) p.SetDirect("__proxyGetOwnPropertyDescriptor__", getOwnPropertyDescriptorTrap);
                                if (getPrototypeOfTrap.IsFunction) p.SetDirect("__proxyGetPrototypeOf__", getPrototypeOfTrap);
                                if (setPrototypeOfTrap.IsFunction) p.SetDirect("__proxySetPrototypeOf__", setPrototypeOfTrap);
                                if (applyTrap.IsFunction) p.SetDirect("__proxyApply__", applyTrap);
                                p.SetDirect("__isProxy__", FenValue.FromBoolean(true));
                            }
                        }

                        var revoke = new FenFunction("revoke", (rArgs, rThis) =>
                        {
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
            reflect.Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                if (args.Length < 2 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.Undefined; // Should throw TypeError in strict
                var target = args[0].AsObject() as FenObject;
                if (target == null) return FenValue.Undefined;

                var receiver = args.Length > 2 ? args[2] : FenValue.FromObject(target);
                var key = args[1];
                var val = key.IsSymbol
                    ? target.GetWithReceiver(key, receiver, _context)
                    : target.GetWithReceiver(key.AsString(_context), receiver, _context);
                return val != null ? (FenValue)val : FenValue.Undefined;
            })));

            // Reflect.set(target, propertyKey, value[, receiver])
            reflect.Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) =>
            {
                return ReflectSetOperation(args);
            })));

            // Reflect.has(target, propertyKey)
            reflect.Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
            {
                if (args.Length < 2 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.FromBoolean(false);
                var target = args[0].AsObject() as FenObject;
                if (target == null) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(target.Has(args[1], _context));
            })));

            // Reflect.deleteProperty(target, propertyKey)
            reflect.Set("deleteProperty", FenValue.FromFunction(new FenFunction("deleteProperty", (args, thisVal) =>
            {
                if (args.Length < 2 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.FromBoolean(false);
                var target = args[0].AsObject() as FenObject;
                if (target == null) return FenValue.FromBoolean(false);
                target.Delete(args[1], _context);
                return FenValue.FromBoolean(true);
            })));

            // Reflect.ownKeys(target)
            reflect.Set("ownKeys", FenValue.FromFunction(new FenFunction("ownKeys", (args, thisVal) =>
            {
                if (args.Length < 1 || (!args[0].IsObject && !args[0].IsFunction))
                    return FenValue.FromObject(CreateArray(new FenValue[0])); // Should throw
                var target = args[0].AsObject() as FenObject;
                var keys = new List<FenValue>();
                foreach (var k in target.Keys()) keys.Add(FenValue.FromString(k));
                return FenValue.FromObject(CreateArray(keys.ToArray()));
            })));

            // Reflect.apply(target, thisArgument, argumentsList)
            reflect.Set("apply", FenValue.FromFunction(new FenFunction("apply", (args, thisVal) =>
            {
                if (args.Length < 3 || !args[0].IsFunction) return FenValue.Undefined; // TypeError
                var func = args[0].AsFunction();
                var thisArg = args[1];
                var argsListObj = args[2].AsObject() as FenObject;

                var argsList = new List<FenValue>();
                if (argsListObj != null)
                {
                    var lenVal = argsListObj.Get("length");
                    if (lenVal != null && lenVal.IsNumber)
                    {
                        int len = (int)lenVal.ToNumber();
                        for (int i = 0; i < len; i++)
                        {
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
            var existingReflectVal = GetGlobal("Reflect");
            if (!(existingReflectVal.IsObject || existingReflectVal.IsFunction))
            {
                SetGlobal("Reflect", FenValue.FromObject(reflect));
            }

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

            if (!GetGlobal("Promise").IsObject && !GetGlobal("Promise").IsFunction)
            {
                SetGlobal("Promise", FenValue.FromObject(promiseStatic));
            }

            // Delegate to the instance methods on JsWeakMap/JsWeakSet which use FenTypeError.
            void PopulateWeakMapFromConstructorArg(JsWeakMap weakMap, FenValue iterable)
                => weakMap.PopulateFromIterable(iterable, _context);

            void PopulateWeakSetFromConstructorArg(JsWeakSet weakSet, FenValue iterable)
                => weakSet.PopulateFromIterable(iterable, _context);

            // --- COLLECTIONS ---
            if (!GetGlobal("Map").IsObject && !GetGlobal("Map").IsFunction)
            {
                SetGlobal("Map",
                    FenValue.FromFunction(new FenFunction("Map",
                        (args, thisVal) => FenValue.FromObject(new JsMap(_context)))));
            }
            if (!GetGlobal("Set").IsObject && !GetGlobal("Set").IsFunction)
            {
                SetGlobal("Set",
                    FenValue.FromFunction(new FenFunction("Set",
                        (args, thisVal) => FenValue.FromObject(new JsSet(_context)))));
            }
            if (!GetGlobal("WeakMap").IsObject && !GetGlobal("WeakMap").IsFunction)
            {
                SetGlobal("WeakMap",
                    FenValue.FromFunction(new FenFunction("WeakMap",
                        (args, thisVal) =>
                        {
                            var weakMap = new JsWeakMap();
                            if (args != null && args.Length > 0)
                            {
                                PopulateWeakMapFromConstructorArg(weakMap, args[0]);
                            }
                            return FenValue.FromObject(weakMap);
                        })));
            }
            if (!GetGlobal("WeakSet").IsObject && !GetGlobal("WeakSet").IsFunction)
            {
                SetGlobal("WeakSet",
                    FenValue.FromFunction(new FenFunction("WeakSet",
                        (args, thisVal) =>
                        {
                            var weakSet = new JsWeakSet();
                            if (args != null && args.Length > 0)
                            {
                                PopulateWeakSetFromConstructorArg(weakSet, args[0]);
                            }
                            return FenValue.FromObject(weakSet);
                        })));
            }

            // --- TYPED ARRAYS ---
            SetGlobal("ArrayBuffer", FenValue.FromFunction(new FenFunction("ArrayBuffer", (args, thisVal) =>
                FenValue.FromObject(new JsArrayBuffer(args.Length > 0 ? (int)args[0].ToNumber() : 0)))));

            SetGlobal("DataView", FenValue.FromFunction(new FenFunction("DataView", (args, thisVal) =>
            {
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction)) return FenValue.Undefined;
                var buf = args[0].AsObject() as JsArrayBuffer;
                if (buf == null) return FenValue.Undefined; // TypeError
                int offset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                int len = args.Length > 2 ? (int)args[2].ToNumber() : -1;
                return FenValue.FromObject(new JsDataView(buf, offset, len));
            })));

            // Typed array early registrations replaced by full registrations below (§13846+)

            // --- XHR ---
            SetGlobal("XMLHttpRequest", FenValue.FromFunction(new FenFunction("XMLHttpRequest", (args, thisVal) =>
            {
                var xhr = new XMLHttpRequest(_context, SendNetworkRequestAsync);
                xhr.SetPrototype(eventTargetPrototype);
                return FenValue.FromObject(xhr);
            })));

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
                            if (len > 65536) throw new FenResourceError("QuotaExceededError"); // Validation

                            byte[] randomBytes = new byte[len];
                            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                            {
                                rng.GetBytes(randomBytes);
                            }

                            // Write back to object
                            for (int i = 0; i < len; i++)
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
            cryptoObj.Set("randomUUID",
                FenValue.FromFunction(new FenFunction("randomUUID",
                    (args, thisVal) => { return FenValue.FromString(Guid.NewGuid().ToString()); })));

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
                            for (int i = 0; i < len; i++)
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
                    var buffer = CreateArray(new FenValue[0]); // Actually needs to be ArrayBuffer-like
                    // Let's just return a standard Array of numbers for now as ArrayBuffer emulation
                    var byteVals = new IValue[hash.Length];
                    for (int i = 0; i < hash.Length; i++) byteVals[i] = FenValue.FromNumber(hash[i]);

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
                formatObj.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions",
                    (fArgs, fThis) =>
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

            // WebSocket is registered at the full implementation site above (line ~5174).
            // Removed duplicate stub registration that was overwriting the full implementation.

            // IndexedDB - Client-side database API
            SetGlobal("indexedDB", FenValue.FromObject(CreateIndexedDB()));

            // Promise - Full Promise implementation with static methods
            SetGlobal("Promise", FenValue.FromFunction(CreatePromiseConstructorModern()));

            // ============================================
            // TIER-2: WeakRef / FinalizationRegistry
            // ============================================
            SetGlobal("WeakRef", FenValue.FromFunction(new FenFunction("WeakRef", (args, thisVal) =>
            {
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction))
                    throw new FenTypeError("TypeError: WeakRef: Target must be an object");
                var target = args[0].AsObject();
                var weakRef = new WeakReference<IObject>(target);

                var obj = new FenObject();
                obj.Set("deref", FenValue.FromFunction(new FenFunction("deref", (dArgs, dThis) =>
                {
                    if (weakRef.TryGetTarget(out var t)) return FenValue.FromObject(t);
                    return FenValue.Undefined;
                })));
                obj.Set("toString",
                    FenValue.FromFunction(
                        new FenFunction("toString", (a, t) => FenValue.FromString("[object WeakRef]"))));
                return FenValue.FromObject(obj);
            })));

            SetGlobal("FinalizationRegistry", FenValue.FromFunction(new FenFunction("FinalizationRegistry",
                (args, thisVal) =>
                {
                    if (args.Length == 0 || !args[0].IsFunction)
                        throw new FenTypeError("TypeError: Constructor requires a cleanup callback");
                    var callback = args[0].AsFunction();

                    var registry = new FenObject();
                    var state = new FinalizationRegistryState(callback);

                    registry.Set("register", FenValue.FromFunction(new FenFunction("register", (rArgs, rThis) =>
                    {
                        state.DrainPending(_context);
                        if (rArgs.Length < 2)
                        {
                            throw new FenTypeError("TypeError: FinalizationRegistry.prototype.register requires target and holdings");
                        }

                        state.Register(rArgs[0], rArgs[1], rArgs.Length > 2 ? rArgs[2] : FenValue.Undefined);
                        return FenValue.Undefined;
                    })));
                    registry.Set("unregister",
                        FenValue.FromFunction(new FenFunction("unregister",
                            (uArgs, uThis) =>
                            {
                                state.DrainPending(_context);
                                if (uArgs.Length == 0)
                                {
                                    throw new FenTypeError("TypeError: FinalizationRegistry.prototype.unregister requires an unregister token");
                                }

                                return FenValue.FromBoolean(state.Unregister(uArgs[0]));
                            })));
                    registry.Set("cleanupSome",
                        FenValue.FromFunction(new FenFunction("cleanupSome",
                            (cArgs, cThis) =>
                            {
                                if (cArgs.Length > 0 && !cArgs[0].IsUndefined && !cArgs[0].IsFunction)
                                {
                                    throw new FenTypeError("TypeError: FinalizationRegistry.prototype.cleanupSome callback must be callable");
                                }

                                state.DrainPending(cArgs.Length > 0 && cArgs[0].IsFunction ? cArgs[0].AsFunction() : null, _context);
                                return FenValue.Undefined;
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
                sab.Set("slice", FenValue.FromFunction(new FenFunction("slice", (sArgs, sThis) =>
                {
                    // Slice implementation similar to ArrayBuffer
                    return FenValue.Null; // Stub for brevity
                })));
                sab.Set(FenBrowser.FenEngine.Core.Types.JsSymbol.ToStringTag.ToPropertyKey(),
                    FenValue.FromString("SharedArrayBuffer"));
                return FenValue.FromObject(sab);
            })));

            var atomics = new FenObject();
            atomics.Set(FenBrowser.FenEngine.Core.Types.JsSymbol.ToStringTag.ToPropertyKey(),
                FenValue.FromString("Atomics"));

            // Helper for Atomics Validation
            Func<FenValue[], int, (byte[] buffer, int index, bool isInt32)> ValidateAtomic = (vArgs, minArgs) =>
            {
                if (vArgs.Length < minArgs) throw new FenTypeError("TypeError: Missing args");
                if (!vArgs[0].IsObject) throw new FenTypeError("TypeError: Arg 0 must be TypedArray");
                var ta = vArgs[0].AsObject() as FenObject;
                if (ta == null || !(ta.NativeObject is byte[]))
                    throw new FenTypeError("TypeError: Arg 0 must be TypedArray");

                var idx = (int)vArgs[1].ToNumber();
                var buffer = ta.NativeObject as byte[];
                // Verify bounds
                // Assuming Int32Array for now mainly
                bool isInt32 = true; // Simplified assumption for stub
                if (idx < 0 || idx >= buffer.Length / 4) throw new FenRangeError("RangeError: Out of bounds");

                return (buffer, idx, isInt32);
            };

            atomics.Set("add", FenValue.FromFunction(new FenFunction("add", (args, thisVal) =>
            {
                try
                {
                    var (buf, idx, isInt32) = ValidateAtomic(args, 3);
                    int val = (int)args[2].ToNumber();
                    // Basic thread safety wrapper (simulated)
                    lock (buf)
                    {
                        int offset = idx * 4;
                        int current = BitConverter.ToInt32(buf, offset);
                        int result = current + val;
                        var bytes = BitConverter.GetBytes(result);
                        Array.Copy(bytes, 0, buf, offset, 4);
                        return FenValue.FromNumber(current); // Returns OLD value
                    }
                }
                catch
                {
                    return FenValue.FromNumber(0);
                }
            })));

            atomics.Set("sub", FenValue.FromFunction(new FenFunction("sub", (args, thisVal) =>
            {
                try
                {
                    var (buf, idx, isInt32) = ValidateAtomic(args, 3);
                    int val = (int)args[2].ToNumber();
                    lock (buf)
                    {
                        int offset = idx * 4;
                        int current = BitConverter.ToInt32(buf, offset);
                        int result = current - val;
                        var bytes = BitConverter.GetBytes(result);
                        Array.Copy(bytes, 0, buf, offset, 4);
                        return FenValue.FromNumber(current);
                    }
                }
                catch
                {
                    return FenValue.FromNumber(0);
                }
            })));

            atomics.Set("and", FenValue.FromFunction(new FenFunction("and", (args, thisVal) =>
            {
                var (buf, idx, _) = ValidateAtomic(args, 3);
                int val = (int)args[2].ToNumber();
                lock (buf)
                {
                    int offset = idx * 4;
                    int current = BitConverter.ToInt32(buf, offset);
                    int result = current & val;
                    var bytes = BitConverter.GetBytes(result);
                    Array.Copy(bytes, 0, buf, offset, 4);
                    return FenValue.FromNumber(current);
                }
            })));

            atomics.Set("or", FenValue.FromFunction(new FenFunction("or", (args, thisVal) =>
            {
                var (buf, idx, _) = ValidateAtomic(args, 3);
                int val = (int)args[2].ToNumber();
                lock (buf)
                {
                    int offset = idx * 4;
                    int current = BitConverter.ToInt32(buf, offset);
                    int result = current | val;
                    var bytes = BitConverter.GetBytes(result);
                    Array.Copy(bytes, 0, buf, offset, 4);
                    return FenValue.FromNumber(current);
                }
            })));

            atomics.Set("xor", FenValue.FromFunction(new FenFunction("xor", (args, thisVal) =>
            {
                var (buf, idx, _) = ValidateAtomic(args, 3);
                int val = (int)args[2].ToNumber();
                lock (buf)
                {
                    int offset = idx * 4;
                    int current = BitConverter.ToInt32(buf, offset);
                    int result = current ^ val;
                    var bytes = BitConverter.GetBytes(result);
                    Array.Copy(bytes, 0, buf, offset, 4);
                    return FenValue.FromNumber(current);
                }
            })));

            atomics.Set("load", FenValue.FromFunction(new FenFunction("load", (args, thisVal) =>
            {
                try
                {
                    var (buf, idx, isInt32) = ValidateAtomic(args, 2);
                    lock (buf)
                    {
                        int offset = idx * 4;
                        return FenValue.FromNumber(BitConverter.ToInt32(buf, offset));
                    }
                }
                catch
                {
                    return FenValue.FromNumber(0);
                }
            })));

            atomics.Set("store", FenValue.FromFunction(new FenFunction("store", (args, thisVal) =>
            {
                try
                {
                    var (buf, idx, isInt32) = ValidateAtomic(args, 3);
                    int val = (int)args[2].ToNumber();
                    lock (buf)
                    {
                        int offset = idx * 4;
                        var bytes = BitConverter.GetBytes(val);
                        Array.Copy(bytes, 0, buf, offset, 4);
                        return FenValue.FromNumber(val);
                    }
                }
                catch
                {
                    return FenValue.FromNumber(0);
                }
            })));

            // Atomics.wait(typedArray, index, value[, timeout]) Ã¢â‚¬â€ blocks until notified or timeout
            atomics.Set("wait", FenValue.FromFunction(new FenFunction("wait", (args, thisVal) =>
            {
                // In a single-threaded runtime, we cannot actually block
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

            // ES2024: Atomics.waitAsync(typedArray, index, value[, timeout]) Ã¢â‚¬â€ async version of wait
            atomics.Set("waitAsync", FenValue.FromFunction(new FenFunction("waitAsync", (args, thisVal) =>
            {
                var (buf, idx, _) = ValidateAtomic(args, 3);
                int expected = (int)args[2].ToNumber();
                int offset = idx * 4;
                int current = BitConverter.ToInt32(buf, offset);
                var result = new FenObject();
                result.Set("async", FenValue.FromBoolean(false));
                result.Set("value", FenValue.FromString(current == expected ? "timed-out" : "not-equal"));
                return FenValue.FromObject(result);
            })));

            // Atomics.notify(typedArray, index[, count]) Ã¢â‚¬â€ wake waiting agents
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
                catch
                {
                    return FenValue.FromNumber(0);
                }
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
                catch
                {
                    return FenValue.FromNumber(0);
                }
            })));

            // Atomics.isLockFree(size) Ã¢â‚¬â€ returns true for sizes 1,2,4 on most platforms
            atomics.Set("isLockFree", FenValue.FromFunction(new FenFunction("isLockFree", (args, thisVal) =>
            {
                int size = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                return FenValue.FromBoolean(size == 1 || size == 2 || size == 4 || size == 8);
            })));

            SetGlobal("Atomics", FenValue.FromObject(atomics));

            // ============================================
            // WebAssembly API (ES2017+)
            // ============================================
            var webAssembly = new FenObject();
            var emptyExports = new FenObject();

            FenValue CreateWasmModuleFromBytes(byte[] bytes)
            {
                if (!IsLikelyWasmBinary(bytes))
                {
                    throw new FenTypeError("TypeError: WebAssembly.Module: invalid binary");
                }

                var module = new FenObject();
                module.NativeObject = bytes;
                module.Set("__wasmBytes", FenValue.FromString(Convert.ToBase64String(bytes)));
                module.Set("imports", FenValue.FromObject(new FenObject()));
                module.Set("exports", FenValue.FromObject(emptyExports));
                return FenValue.FromObject(module);
            }

            FenValue CreateWasmInstance(FenValue moduleValue, FenValue importObject)
            {
                var instance = new FenObject();
                instance.Set("exports", FenValue.FromObject(new FenObject()));
                instance.Set("__module", moduleValue);
                instance.Set("__imports",
                    importObject.IsUndefined ? FenValue.FromObject(new FenObject()) : importObject);
                return FenValue.FromObject(instance);
            }

            // WebAssembly.compile(bufferSource) - Returns Promise<Module>
            webAssembly.Set("compile", FenValue.FromFunction(new FenFunction("compile", (args, thisVal) =>
            {
                if (args.Length == 0)
                {
                    return FenValue.FromObject(Types.JsPromise.Reject(
                        FenValue.FromError("WebAssembly.compile: missing bufferSource"),
                        _context));
                }

                var bytes = TryExtractByteBuffer(args[0]);
                if (!IsLikelyWasmBinary(bytes))
                {
                    return FenValue.FromObject(Types.JsPromise.Reject(
                        FenValue.FromError("WebAssembly.compile: invalid wasm binary"),
                        _context));
                }

                return FenValue.FromObject(Types.JsPromise.Resolve(CreateWasmModuleFromBytes(bytes), _context));
            })));

            // WebAssembly.instantiate(bufferSource, importObject) - Returns Promise<{module, instance}>
            webAssembly.Set("instantiate", FenValue.FromFunction(new FenFunction("instantiate", (args, thisVal) =>
            {
                if (args.Length == 0)
                {
                    return FenValue.FromObject(Types.JsPromise.Reject(
                        FenValue.FromError("WebAssembly.instantiate: missing bufferSource"),
                        _context));
                }

                var importObject = args.Length > 1 ? args[1] : FenValue.Undefined;
                FenValue moduleValue;

                if (args[0].IsObject && args[0].AsObject() is FenObject moduleObj && moduleObj.Has("__wasmBytes"))
                {
                    moduleValue = FenValue.FromObject(moduleObj);
                }
                else
                {
                    var bytes = TryExtractByteBuffer(args[0]);
                    if (!IsLikelyWasmBinary(bytes))
                    {
                        return FenValue.FromObject(Types.JsPromise.Reject(
                            FenValue.FromError("WebAssembly.instantiate: invalid wasm binary"),
                            _context));
                    }

                    moduleValue = CreateWasmModuleFromBytes(bytes);
                }

                var result = new FenObject();
                result.Set("module", moduleValue);
                result.Set("instance", CreateWasmInstance(moduleValue, importObject));

                return FenValue.FromObject(Types.JsPromise.Resolve(FenValue.FromObject(result), _context));
            })));

            // WebAssembly.validate(bufferSource) - Returns boolean
            webAssembly.Set("validate", FenValue.FromFunction(new FenFunction("validate", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(IsLikelyWasmBinary(TryExtractByteBuffer(args[0])));
            })));

            // WebAssembly.Module constructor
            webAssembly.Set("Module", FenValue.FromFunction(new FenFunction("Module", (args, thisVal) =>
            {
                if (args.Length == 0)
                    throw new FenTypeError("TypeError: WebAssembly.Module: missing bufferSource");

                var bytes = TryExtractByteBuffer(args[0]);
                return CreateWasmModuleFromBytes(bytes);
            })));

            // WebAssembly.Instance constructor
            webAssembly.Set("Instance", FenValue.FromFunction(new FenFunction("Instance", (args, thisVal) =>
            {
                if (args.Length == 0)
                    throw new FenTypeError("TypeError: WebAssembly.Instance: missing module");

                var moduleValue = args[0];
                if (!moduleValue.IsObject || moduleValue.AsObject() is not FenObject moduleObject ||
                    !moduleObject.Has("__wasmBytes"))
                    throw new FenTypeError("TypeError: WebAssembly.Instance: invalid module");

                var importObject = args.Length > 1 ? args[1] : FenValue.Undefined;
                return CreateWasmInstance(moduleValue, importObject);
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
                table.Set("grow",
                    FenValue.FromFunction(new FenFunction("grow",
                        (gArgs, gThis) => { return FenValue.FromNumber(initial); })));

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
            // Temporal API stub - throws TypeError for all uses (not yet implemented)
            // ECMA-262 proposal-temporal: https://tc39.es/proposal-temporal/
            // ============================================
            var temporalObj = new FenObject();
            Action<string> RegisterTemporalStub = (name) => {
                var stub = new FenFunction(name, (args, thisVal) => {
                    throw new FenTypeError($"TypeError: Temporal.{name} is not implemented");
                });
                stub.IsConstructor = true;
                temporalObj.Set(name, FenValue.FromFunction(stub));
            };
            foreach (var name in new[] { "PlainDate", "PlainTime", "PlainDateTime", "ZonedDateTime",
                "Instant", "Duration", "PlainYearMonth", "PlainMonthDay", "Calendar", "TimeZone", "Now" })
            {
                RegisterTemporalStub(name);
            }
            SetGlobal("Temporal", FenValue.FromObject(temporalObj));
            window.Set("Temporal", FenValue.FromObject(temporalObj));

            // ============================================
            // TIER-2: GeneratorFunction (Prototype)
            // ============================================
            var generatorFunctionProto = new FenObject();
            generatorFunctionProto.SetBuiltin(FenBrowser.FenEngine.Core.Types.JsSymbol.ToStringTag.ToPropertyKey(),
                FenValue.FromString("GeneratorFunction"));
            var generatorFunction = FenValue.FromFunction(new FenFunction("GeneratorFunction",
                (args, thisVal) =>
                {
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
                    if (key == null || key == null) return "null";
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
                        callback.Invoke(
                            new FenValue[] { (FenValue)kvp.value, (FenValue)kvp.key, FenValue.FromObject(map) }, null);
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

                    iterator.SetPrototype(mapIteratorProto);
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
                    if (val == null || val == null) return "null";
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

                    iterator.SetPrototype(setIteratorProto);
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
                    foreach (var methodName in new[]
                             {
                                 "add", "has", "delete", "clear", "values", "keys", "entries", "forEach",
                                 "[Symbol.iterator]"
                             })
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
                    foreach (var methodName in new[]
                             {
                                 "add", "has", "delete", "clear", "values", "keys", "entries", "forEach",
                                 "[Symbol.iterator]"
                             })
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
                    foreach (var methodName in new[]
                             {
                                 "add", "has", "delete", "clear", "values", "keys", "entries", "forEach",
                                 "[Symbol.iterator]"
                             })
                    {
                        var method = set.Get(methodName);
                        if (method.IsFunction)
                            result.Set(methodName, method);
                    }

                    return FenValue.FromObject(result);
                })));

                // symmetricDifference(other) - Returns new Set with elements in either but not both
                set.Set("symmetricDifference", FenValue.FromFunction(new FenFunction("symmetricDifference",
                    (symArgs, _) =>
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
                        foreach (var methodName in new[]
                                 {
                                     "add", "has", "delete", "clear", "values", "keys", "entries", "forEach",
                                     "[Symbol.iterator]"
                                 })
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

            // ArrayBuffer - Binary data container (ECMA-262 §25.1)
            var arrayBufferProto = new FenObject();
            arrayBufferProto.Set("byteLength", FenValue.Undefined); // instance getter, placeholder
            arrayBufferProto.Set("slice", FenValue.FromFunction(new FenFunction("slice", (args, thisVal) =>
            {
                if (thisVal.IsObject && thisVal.AsObject() is JsArrayBuffer ab)
                {
                    int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                    int end = args.Length > 1 ? (int)args[1].ToNumber() : ab.Data.Length;
                    if (start < 0) start = Math.Max(0, ab.Data.Length + start);
                    if (end < 0) end = Math.Max(0, ab.Data.Length + end);
                    start = Math.Min(start, ab.Data.Length);
                    end = Math.Min(end, ab.Data.Length);
                    int len = Math.Max(0, end - start);
                    var newBuf = new JsArrayBuffer(len);
                    if (len > 0) Array.Copy(ab.Data, start, newBuf.Data, 0, len);
                    return FenValue.FromObject(newBuf);
                }
                return FenValue.Null;
            })));
            arrayBufferProto.Set("transfer", FenValue.FromFunction(new FenFunction("transfer", (args, thisVal) =>
            {
                if (thisVal.IsObject && thisVal.AsObject() is JsArrayBuffer ab2)
                {
                    int newLen = args.Length > 0 && !args[0].IsUndefined ? (int)args[0].ToNumber() : ab2.Data.Length;
                    var newBuf = new JsArrayBuffer(newLen);
                    Array.Copy(ab2.Data, newBuf.Data, Math.Min(ab2.Data.Length, newLen));
                    return FenValue.FromObject(newBuf);
                }
                return FenValue.Null;
            })));
            var arrayBufferCtor = new FenFunction("ArrayBuffer", (args, thisVal) =>
            {
                var length = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                var abNew = new JsArrayBuffer(length);
                abNew.SetPrototype(arrayBufferProto);
                return FenValue.FromObject(abNew);
            });
            arrayBufferCtor.IsConstructor = true;
            arrayBufferCtor.Set("prototype", FenValue.FromObject(arrayBufferProto));
            arrayBufferCtor.Prototype = arrayBufferProto;
            arrayBufferProto.Set("constructor", FenValue.FromFunction(arrayBufferCtor));
            arrayBufferCtor.Set("isView", FenValue.FromFunction(new FenFunction("isView", (args, thisVal) =>
                FenValue.FromBoolean(args.Length > 0 && args[0].IsObject && args[0].AsObject() is JsTypedArrayView))));
            SetGlobal("ArrayBuffer", FenValue.FromFunction(arrayBufferCtor));
            window.Set("ArrayBuffer", FenValue.FromFunction(arrayBufferCtor));

            // TypedArrays - Views over ArrayBuffer (ECMA-262 §22.2)
            // %TypedArray% abstract superclass — required by testTypedArray.js harness:
            //   var TypedArray = Object.getPrototypeOf(Int8Array);
            // Each concrete ctor's [[Prototype]] must be this abstract ctor.
            var typedArrayAbstractProto = new FenObject(); // %TypedArray%.prototype
            var typedArrayAbstractCtor = new FenFunction("TypedArray", (args, thisVal) =>
            {
                throw new FenTypeError("TypeError: Abstract class TypedArray not directly constructible");
            });
            typedArrayAbstractCtor.IsConstructor = false;
            typedArrayAbstractCtor.NativeLength = 0; // ECMA-262 §22.2.1: %TypedArray%.length = 0
            typedArrayAbstractCtor.DefineOwnProperty("prototype", new PropertyDescriptor {
                Value = FenValue.FromObject(typedArrayAbstractProto), Writable = false, Enumerable = false, Configurable = false
            });
            typedArrayAbstractCtor.Prototype = typedArrayAbstractProto;
            typedArrayAbstractProto.SetBuiltin("constructor", FenValue.FromFunction(typedArrayAbstractCtor));
            // Static methods on %TypedArray%
            typedArrayAbstractCtor.Set("from", FenValue.FromFunction(new FenFunction("from", (args, thisVal) =>
            {
                // Basic implementation: creates array of same ctor from source
                if (args.Length == 0) throw new FenTypeError("TypeError: TypedArray.from requires source");
                return FenValue.Undefined; // subclass ctors override
            })));
            typedArrayAbstractCtor.Set("of", FenValue.FromFunction(new FenFunction("of", (args, thisVal) =>
            {
                return FenValue.Undefined; // subclass ctors override
            })));
            SetGlobal("TypedArray", FenValue.FromFunction(typedArrayAbstractCtor));
            window.Set("TypedArray", FenValue.FromFunction(typedArrayAbstractCtor));

            FenFunction MakeConcreteTypedArrayCtor(string name, int bytesPerElement, Func<IValue, IValue, IValue, JsTypedArray> factory)
            {
                // Create prototype first so the lambda can capture it
                var proto = new FenObject();
                proto.SetBuiltin("BYTES_PER_ELEMENT", FenValue.FromNumber(bytesPerElement));
                // ECMA-262 §22.2.3: concrete proto [[Prototype]] = %TypedArray%.prototype
                proto.SetPrototype(typedArrayAbstractProto);

                var ctor = new FenFunction(name, (args, thisVal) =>
                {
                    IValue a0 = args.Length > 0 ? (IValue)args[0] : null;
                    IValue a1 = args.Length > 1 ? (IValue)args[1] : null;
                    IValue a2 = args.Length > 2 ? (IValue)args[2] : null;
                    var instance = factory(a0, a1, a2);
                    // ECMA-262 §22.2.4.2: instance [[Prototype]] = Constructor.prototype
                    instance.SetPrototype(proto);
                    return FenValue.FromObject(instance);
                });
                ctor.IsConstructor = true;
                ctor.NativeLength = 3;
                // ECMA-262 §22.2.2: concrete ctor [[Prototype]] = %TypedArray%
                ctor.SetPrototype(typedArrayAbstractCtor);
                // BYTES_PER_ELEMENT on constructor (non-writable, non-enumerable, non-configurable)
                ctor.DefineOwnProperty("BYTES_PER_ELEMENT", new PropertyDescriptor {
                    Value = FenValue.FromNumber(bytesPerElement), Writable = false, Enumerable = false, Configurable = false
                });
                // .prototype — non-writable, non-enumerable, non-configurable per ES spec
                proto.SetBuiltin("constructor", FenValue.FromFunction(ctor));
                ctor.DefineOwnProperty("prototype", new PropertyDescriptor {
                    Value = FenValue.FromObject(proto), Writable = false, Enumerable = false, Configurable = false
                });
                ctor.Prototype = proto;
                // Static from/of using the concrete ctor
                ctor.Set("from", FenValue.FromFunction(new FenFunction("from", (args, thisVal) =>
                {
                    if (args.Length == 0) throw new FenTypeError("TypeError: TypedArray.from requires source");
                    IValue a0f = args.Length > 0 ? (IValue)args[0] : null;
                    return FenValue.FromObject(factory(a0f, null, null));
                })));
                ctor.Set("of", FenValue.FromFunction(new FenFunction("of", (args, thisVal) =>
                {
                    var buf = new JsArrayBuffer(args.Length * bytesPerElement);
                    var result = factory(FenValue.FromObject(buf), FenValue.FromNumber(0), FenValue.FromNumber(args.Length));
                    for (int i = 0; i < args.Length; i++)
                        ((JsTypedArray)result).SetIndex(i, args[i].ToNumber());
                    return FenValue.FromObject(result);
                })));
                return ctor;
            }

            var uint8ArrayCtor    = MakeConcreteTypedArrayCtor("Uint8Array",       1, (a,b,c) => new JsUint8Array(a,b,c));
            var int8ArrayCtor     = MakeConcreteTypedArrayCtor("Int8Array",        1, (a,b,c) => new JsInt8Array(a,b,c));
            var uint8ClampedCtor  = MakeConcreteTypedArrayCtor("Uint8ClampedArray",1, (a,b,c) => new JsUint8ClampedArray(a,b,c));
            var uint16ArrayCtor   = MakeConcreteTypedArrayCtor("Uint16Array",      2, (a,b,c) => new JsUint16Array(a,b,c));
            var int16ArrayCtor    = MakeConcreteTypedArrayCtor("Int16Array",       2, (a,b,c) => new JsInt16Array(a,b,c));
            var uint32ArrayCtor   = MakeConcreteTypedArrayCtor("Uint32Array",      4, (a,b,c) => new JsUint32Array(a,b,c));
            var int32ArrayCtor    = MakeConcreteTypedArrayCtor("Int32Array",       4, (a,b,c) => new JsInt32Array(a,b,c));
            var float32ArrayCtor  = MakeConcreteTypedArrayCtor("Float32Array",     4, (a,b,c) => new JsFloat32Array(a,b,c));
            var float64ArrayCtor  = MakeConcreteTypedArrayCtor("Float64Array",     8, (a,b,c) => new JsFloat64Array(a,b,c));

            SetGlobal("Uint8Array",       FenValue.FromFunction(uint8ArrayCtor));
            SetGlobal("Int8Array",        FenValue.FromFunction(int8ArrayCtor));
            SetGlobal("Uint8ClampedArray",FenValue.FromFunction(uint8ClampedCtor));
            SetGlobal("Uint16Array",      FenValue.FromFunction(uint16ArrayCtor));
            SetGlobal("Int16Array",       FenValue.FromFunction(int16ArrayCtor));
            SetGlobal("Uint32Array",      FenValue.FromFunction(uint32ArrayCtor));
            SetGlobal("Int32Array",       FenValue.FromFunction(int32ArrayCtor));
            SetGlobal("Float32Array",     FenValue.FromFunction(float32ArrayCtor));
            SetGlobal("Float64Array",     FenValue.FromFunction(float64ArrayCtor));

            window.Set("Uint8Array",       FenValue.FromFunction(uint8ArrayCtor));
            window.Set("Int8Array",        FenValue.FromFunction(int8ArrayCtor));
            window.Set("Uint8ClampedArray",FenValue.FromFunction(uint8ClampedCtor));
            window.Set("Uint16Array",      FenValue.FromFunction(uint16ArrayCtor));
            window.Set("Int16Array",       FenValue.FromFunction(int16ArrayCtor));
            window.Set("Uint32Array",      FenValue.FromFunction(uint32ArrayCtor));
            window.Set("Int32Array",       FenValue.FromFunction(int32ArrayCtor));
            window.Set("Float32Array",     FenValue.FromFunction(float32ArrayCtor));
            window.Set("Float64Array",     FenValue.FromFunction(float64ArrayCtor));

            // ECMA-262 §22.2: BigInt64Array / BigUint64Array — element type is BigInt, not Number.
            var bigInt64Ctor = MakeConcreteTypedArrayCtor("BigInt64Array", 8, (a,b,c) => new JsBigInt64Array(a,b,c));
            var bigUint64Ctor = MakeConcreteTypedArrayCtor("BigUint64Array", 8, (a,b,c) => new JsBigUint64Array(a,b,c));
            SetGlobal("BigInt64Array", FenValue.FromFunction(bigInt64Ctor));
            SetGlobal("BigUint64Array", FenValue.FromFunction(bigUint64Ctor));
            window.Set("BigInt64Array", FenValue.FromFunction(bigInt64Ctor));
            window.Set("BigUint64Array", FenValue.FromFunction(bigUint64Ctor));
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
                // SECURITY: Function() is equivalent to eval() Ã¢â‚¬â€ require the same permission.
                // Without this check, `new Function("code")()` bypasses CSP unsafe-eval restrictions.
                if (_context != null && !_context.Permissions.Check(FenBrowser.FenEngine.Security.JsPermissions.Eval))
                {
                    _context.Permissions.LogViolation(
                        FenBrowser.FenEngine.Security.JsPermissions.Eval,
                        "Function()",
                        "Function constructor blocked by permission policy");
                    throw new FenTypeError("EvalError: Refused to create a function from string because 'unsafe-eval' is not allowed by the current security policy.");
                }

                try
                {
                    // Last argument is the function body, previous arguments are parameters
                    string body = args.Length > 0 ? args[args.Length - 1].ToString() : "";
                    var paramNames = new List<string>();

                    for (int i = 0; i < args.Length - 1; i++)
                    {
                        paramNames.Add(args[i].ToString());
                    }

                    // Build function source per spec: params and body each on their own lines so
                    // HTML-like comments (-->) inside params or body don't eat the closing braces.
                    string functionSource = $"(function anonymous({string.Join(", ", paramNames)}\n) {{\n{body}\n}})";

                    // Parse the function
                    var lexer = new Lexer(functionSource);
                    var parser = new Parser(lexer);
                    var program = parser.ParseProgram();

                    if (parser.Errors.Count > 0)
                    {
                        throw new FenSyntaxError($"SyntaxError: {string.Join(", ", parser.Errors)}");
                    }

                    // Compile the function expression to bytecode via ExecuteSimple so the
                    // resulting FenFunction has a BytecodeBlock and is callable from the VM.
                    var funcResult = ExecuteSimple(functionSource, inheritStrictFromContext: false);
                    if (funcResult is FenValue fv && fv.IsFunction)
                        return fv;

                    throw new FenSyntaxError("SyntaxError: Invalid function syntax");
                }
                catch (Exception ex)
                {
                    throw new FenSyntaxError($"SyntaxError: {ex.Message}");
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

            var hasInstanceFn = new FenFunction("[Symbol.hasInstance]", (args, thisVal) =>
            {
                if (!thisVal.IsFunction) return FenValue.FromBoolean(false);

                var targetFunction = thisVal.AsFunction() as FenFunction;
                while (targetFunction != null && targetFunction.BoundTargetFunction != null)
                {
                    targetFunction = targetFunction.BoundTargetFunction;
                }

                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction))
                    return FenValue.FromBoolean(false);

                if (targetFunction == null) return FenValue.FromBoolean(false);

                var prototypeValue = targetFunction.Get("prototype", null);
                if (!prototypeValue.IsObject)
                    throw new FenTypeError("TypeError: object prototype property is not an object");

                var prototype = prototypeValue.AsObject();
                IObject obj = args[0].IsObject ? args[0].AsObject() : args[0].AsFunction();
                while (obj != null)
                {
                    if (ReferenceEquals(obj, prototype))
                        return FenValue.FromBoolean(true);

                    obj = obj.GetPrototype();
                }

                return FenValue.FromBoolean(false);
            });
            hasInstanceFn.NativeLength = 1;
            functionPrototype.DefineOwnProperty(FenValue.FromSymbol(JsSymbol.HasInstance), new PropertyDescriptor
            {
                Value = FenValue.FromFunction(hasInstanceFn),
                Writable = false,
                Enumerable = false,
                Configurable = false
            });
            functionPrototype.DefineOwnProperty(JsSymbol.HasInstance.ToPropertyKey(), new PropertyDescriptor
            {
                Value = FenValue.FromFunction(hasInstanceFn),
                Writable = false,
                Enumerable = false,
                Configurable = false
            });

            // Function.prototype.call(thisArg, ...args)
            functionPrototype.Set("call", FenValue.FromFunction(new FenFunction("call", (args, thisVal) =>
            {
                if (!thisVal.IsFunction) throw new FenTypeError("TypeError: Function.prototype.call called on non-callable");
                var fn = thisVal.AsFunction();
                var newThis = args.Length > 0 ? args[0] : FenValue.Undefined;
                var fnArgs = args.Length > 1 ? args.Skip(1).ToArray() : new FenValue[0];
                return fn.Invoke(fnArgs, _context, newThis);
            })), null);

            // Function.prototype.apply(thisArg, argsArray)
            functionPrototype.Set("apply", FenValue.FromFunction(new FenFunction("apply", (args, thisVal) =>
            {
                if (!thisVal.IsFunction) throw new FenTypeError("TypeError: Function.prototype.apply called on non-callable");
                var fn = thisVal.AsFunction();
                var newThis = args.Length > 0 ? args[0] : FenValue.Undefined;
                var fnArgs = new FenValue[0];
                if (args.Length > 1 && !args[1].IsUndefined && !args[1].IsNull && args[1].IsObject)
                {
                    var argsObj = args[1].AsObject();
                    var len = (int)argsObj.Get("length", null).ToNumber();
                    fnArgs = new FenValue[len];
                    for (int i = 0; i < len; i++)
                        fnArgs[i] = argsObj.Get(i.ToString(), null);
                }

                return fn.Invoke(fnArgs, _context, newThis);
            })), null);

            // Function.prototype.bind(thisArg, ...args)
            functionPrototype.Set("bind", FenValue.FromFunction(new FenFunction("bind", (args, thisVal) =>
            {
                if (!thisVal.IsFunction) throw new FenTypeError("TypeError: Function.prototype.bind called on non-callable");
                var fn = thisVal.AsFunction();
                var boundThis = args.Length > 0 ? args[0] : FenValue.Undefined;
                var boundArgs = args.Length > 1 ? args.Skip(1).ToArray() : new FenValue[0];
                var boundFn = new FenFunction("bound", (newArgs, _) =>
                {
                    var allArgs = boundArgs.Concat(newArgs).ToArray();
                    return fn.Invoke(allArgs, _context, boundThis);
                });
                boundFn.BoundTargetFunction = fn;
                return FenValue.FromFunction(boundFn);
            })), null);

            functionPrototype.DefineOwnProperty("name", new PropertyDescriptor
            {
                Value = FenValue.FromString(""),
                Writable = false,
                Enumerable = false,
                Configurable = true
            });

            functionCtor.Prototype = functionPrototype;
            functionCtor.Set("prototype", FenValue.FromObject(functionPrototype), null);
            SetGlobal("Function", FenValue.FromFunction(functionCtor));

            // CRITICAL: Set DefaultFunctionPrototype so all subsequently created FenFunction instances
            // (user-defined functions) inherit .call(), .apply(), .bind(), .length, .name, .toString().
            FenFunction.DefaultFunctionPrototype = functionPrototype;

            // FIX: intrinsic Function.prototype methods created before DefaultFunctionPrototype was set
            // (call/apply/bind/toString) need their own prototype corrected so they behave like
            // ordinary functions in prototype lookups (including `.bind` access on call/apply/bind).
            foreach (var methodName in new[] { "call", "apply", "bind", "toString", JsSymbol.HasInstance.ToPropertyKey() })
            {
                var method = functionPrototype.Get(methodName, null);
                if (method.IsFunction && method.AsFunction() is FenFunction methodFn)
                {
                    methodFn.SetPrototype(functionPrototype);
                }
            }

            // Normalize previously-created intrinsic methods to the active Function.prototype.
            var activeObjectCtor = GetGlobal("Object");
            if ((activeObjectCtor.IsObject || activeObjectCtor.IsFunction) && activeObjectCtor.AsObject() is FenObject activeObjectCtorObj)
            {
                var activeObjectProtoVal = activeObjectCtorObj.Get("prototype", null);
                if (activeObjectProtoVal.IsObject && activeObjectProtoVal.AsObject() is FenObject activeObjectProto)
                {
                    string[] objectProtoMethodNames =
                    {
                        "hasOwnProperty", "isPrototypeOf", "propertyIsEnumerable", "toString", "valueOf",
                        "toLocaleString", "__defineGetter__", "__defineSetter__", "__lookupGetter__", "__lookupSetter__"
                    };
                    foreach (var methodName in objectProtoMethodNames)
                    {
                        var methodVal = activeObjectProto.Get(methodName, null);
                        if (methodVal.IsFunction)
                        {
                            methodVal.AsFunction().SetPrototype(functionPrototype);
                        }
                    }
                }
            }

            // Intrinsic globals and prototype methods created before Function.prototype exists
            // must be normalized here so `.bind/.call/.apply` work on builtins used by
            // production bundle bootstraps such as Array.prototype.push and Object.defineProperty.
            NormalizeGlobalIntrinsicFunctionPrototypes(functionPrototype);

            void EnsureConstructorPrototypeToStringTag(string ctorName, string tag)
            {
                var ctorVal = GetGlobal(ctorName);
                if (!(ctorVal.IsObject || ctorVal.IsFunction)) return;

                var ctorObj = ctorVal.AsObject() as FenObject;
                if (ctorObj == null) return;

                var protoVal = ctorObj.Get("prototype", null);
                FenObject protoObj;
                if (protoVal.IsObject && protoVal.AsObject() is FenObject existingProto)
                {
                    protoObj = existingProto;
                }
                else
                {
                    protoObj = new FenObject();
                    ctorObj.Set("prototype", FenValue.FromObject(protoObj), null);
                }

                protoObj.SetBuiltin(JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString(tag));
            }

            EnsureConstructorPrototypeToStringTag("Map", "Map");
            EnsureConstructorPrototypeToStringTag("Set", "Set");
            EnsureConstructorPrototypeToStringTag("WeakMap", "WeakMap");
            EnsureConstructorPrototypeToStringTag("WeakSet", "WeakSet");
            EnsureConstructorPrototypeToStringTag("Promise", "Promise");
            EnsureConstructorPrototypeToStringTag("BigInt", "BigInt");


            // ============================================================
            // String constructor (ES5.1/ES2015)
            // ============================================================
            var stringConstructor = new FenObject();
            stringConstructor.InternalClass = "Function";
            stringConstructor.Set("__call__", FenValue.FromFunction(new FenFunction("String", (args, thisVal) =>
            {
                var str = args.Length > 0
                    ? (args[0].IsSymbol ? args[0].AsSymbol().ToString() : args[0].ToString())
                    : "";
                if (thisVal.IsObject && thisVal.AsObject() is FenObject obj && obj.InternalClass == "String")
                {
                    // new String() called
                    obj.NativeObject = str;
                    return thisVal; // Constructor returns the new object
                }

                return FenValue.FromString(str); // Called as function
            })), null);

            // String.fromCharCode(...)
            stringConstructor.Set("fromCharCode", FenValue.FromFunction(new FenFunction("fromCharCode",
                (args, thisVal) =>
                {
                    var sb = new StringBuilder();
                    foreach (var arg in args)
                    {
                        sb.Append((char)(ushort)arg.ToNumber());
                    }

                    return FenValue.FromString(sb.ToString());
                })), null);

            // String.fromCodePoint(...) - ES2015
            stringConstructor.Set("fromCodePoint", FenValue.FromFunction(new FenFunction("fromCodePoint",
                (args, thisVal) =>
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
                            throw new FenRangeError("RangeError: Invalid code point " + num);
                        }
                    }

                    return FenValue.FromString(sb.ToString());
                })), null);

            // String.raw(template, ...substitutions) - ES2015
            stringConstructor.Set("raw", FenValue.FromFunction(new FenFunction("raw", (args, thisVal) =>
            {
                if (args.Length == 0)
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");

                var template = args[0];
                if (!template.IsObject) throw new FenTypeError("TypeError: String.raw requires template object");

                var cooked = template.AsObject();
                // Check if raw property exists
                var rawVal = cooked.Get("raw", null);
                if (rawVal.IsUndefined || rawVal.IsNull)
                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");

                if (!rawVal.IsObject) throw new FenTypeError("TypeError: raw property must be an object");

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
                if (args.Length == 0) throw new FenTypeError("TypeError: matchAll requires a regular expression");
                var regexpVal = args[0];
                var str = thisVal.ToString();

                // Per spec: matchAll requires the regexp to have the 'g' flag; throw TypeError if absent
                if (regexpVal.IsObject)
                {
                    var globalFlag = regexpVal.AsObject()?.Get("global");
                    if (globalFlag.HasValue && globalFlag.Value.IsBoolean && !globalFlag.Value.ToBoolean())
                        throw new FenTypeError("TypeError: String.prototype.matchAll called with a non-global RegExp argument");
                }

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
                iterator.Set("[Symbol.iterator]",
                    FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (iArgs, iThis) => iThis)));

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

            // String was already registered earlier in initialization.
            // Merge the newer ES2015+ static methods into the active global constructor.
            var existingString = GetGlobal("String");
            if (existingString.IsFunction || existingString.IsObject)
            {
                var stringTarget = existingString.AsObject() as FenObject;
                if (stringTarget != null)
                {
                    foreach (var key in stringConstructor.Keys())
                    {
                        stringTarget.Set(key, stringConstructor.Get(key, null), null);
                    }
                    var stringProtoVal = stringTarget.Get("prototype", null);
                    var stringProtoObj = stringProtoVal.IsObject ? stringProtoVal.AsObject() as FenObject : null;
                    if (stringProtoObj != null)
                    {
                        stringProtoObj.SetBuiltin("replaceAll", FenValue.FromFunction(new FenFunction("replaceAll", (args, thisVal) =>
                        {
                            if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.replaceAll called on null or undefined");
                            var str = thisVal.AsString(_context);
                            if (args.Length < 2) return FenValue.FromString(str);
                            var searchStr = args[0].AsString(_context);
                            var replaceStr = args[1].AsString(_context);
                            if (string.IsNullOrEmpty(searchStr)) return FenValue.FromString(str);
                            return FenValue.FromString(str.Replace(searchStr, replaceStr));
                        })));

                        stringProtoObj.SetBuiltin("codePointAt", FenValue.FromFunction(new FenFunction("codePointAt", (args, thisVal) =>
                        {
                            if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.codePointAt called on null or undefined");
                            var str = thisVal.AsString(_context);
                            int pos = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                            if (pos < 0 || pos >= str.Length) return FenValue.Undefined;
                            char first = str[pos];
                            if (char.IsHighSurrogate(first) && pos + 1 < str.Length && char.IsLowSurrogate(str[pos + 1]))
                            {
                                return FenValue.FromNumber(char.ConvertToUtf32(first, str[pos + 1]));
                            }
                            return FenValue.FromNumber(first);
                        })));

                        // matchAll — ES2020, belongs on String.prototype
                        var matchAllFn = GetGlobal("String").AsFunction()?.Get("matchAll") ?? FenValue.Undefined;
                        if (matchAllFn.IsFunction)
                        {
                            stringProtoObj.SetBuiltin("matchAll", matchAllFn);
                        }
                        else
                        {
                            stringProtoObj.SetBuiltin("matchAll", FenValue.FromFunction(new FenFunction("matchAll", (args, thisVal) =>
                            {
                                if (thisVal.IsNull || thisVal.IsUndefined) throw new FenTypeError("TypeError: String.prototype.matchAll called on null or undefined");
                                return FenValue.Undefined; // fallback
                            })));
                        }
                    }
                }
            }
            else
            {
                SetGlobal("String", FenValue.FromObject(stringConstructor));
            }
        }

        private FenObject CreateEmptyIterator()
        {
            var iterator = new FenObject();
            iterator.Set("[Symbol.iterator]",
                FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, thisVal) => thisVal)));
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
            if (value.IsUndefined)
                return "undefined"; // JSON.stringify(undefined) is undefined, but for now string representation

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
        private Uri ResolveLocationTarget(string requestedUrl, FenObject location)
        {
            if (string.IsNullOrWhiteSpace(requestedUrl))
            {
                return null;
            }

            var trimmed = requestedUrl.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
            {
                return absolute;
            }

            Uri baseUri = BaseUri;
            if (baseUri == null && location != null)
            {
                var hrefValue = location.Get("href");
                if (!hrefValue.IsUndefined && !hrefValue.IsNull)
                {
                    Uri.TryCreate(hrefValue.ToString(), UriKind.Absolute, out baseUri);
                }
            }

            if (baseUri != null && Uri.TryCreate(baseUri, trimmed, out var relative))
            {
                return relative;
            }

            return null;
        }

        private void EnsureLocalHistoryInitialized()
        {
            if (_historyBridge != null || _localHistoryEntries.Count > 0)
            {
                return;
            }

            _localHistoryEntries.Add(new HistoryEntryState
            {
                Url = BaseUri ?? new Uri("about:blank"),
                State = FenValue.Null,
                Title = string.Empty
            });
            _localHistoryIndex = 0;
        }

        private Uri GetCurrentHistoryUrl()
        {
            if (_historyBridge?.CurrentUrl != null)
            {
                return _historyBridge.CurrentUrl;
            }

            if (_localHistoryIndex >= 0 && _localHistoryIndex < _localHistoryEntries.Count)
            {
                return _localHistoryEntries[_localHistoryIndex].Url;
            }

            return BaseUri;
        }

        private int GetHistoryLength()
        {
            if (_historyBridge != null)
            {
                return Math.Max(1, _historyBridge.Length);
            }

            EnsureLocalHistoryInitialized();
            return Math.Max(1, _localHistoryEntries.Count);
        }

        private FenValue GetHistoryStateValue()
        {
            if (_historyBridge != null)
            {
                return _historyBridge.State != null
                    ? CloneHistoryState(ConvertNativeToFenValue(_historyBridge.State))
                    : FenValue.Null;
            }

            EnsureLocalHistoryInitialized();
            if (_localHistoryIndex >= 0 && _localHistoryIndex < _localHistoryEntries.Count)
            {
                return CloneHistoryState(_localHistoryEntries[_localHistoryIndex].State);
            }

            return FenValue.Null;
        }

        private FenValue CloneHistoryState(FenValue state)
        {
            var structuredClone = _globalEnv.Get("structuredClone");
            if (structuredClone.IsFunction)
            {
                return structuredClone.AsFunction().Invoke(
                    new[] { state },
                    _context,
                    _windowObject != null ? FenValue.FromObject(_windowObject) : FenValue.Undefined);
            }

            return state;
        }

        private void PushLocalHistoryState(FenValue state, string title, Uri target)
        {
            EnsureLocalHistoryInitialized();
            var resolvedUrl = target ?? GetCurrentHistoryUrl() ?? BaseUri ?? new Uri("about:blank");
            if (_localHistoryIndex < _localHistoryEntries.Count - 1)
            {
                _localHistoryEntries.RemoveRange(_localHistoryIndex + 1, _localHistoryEntries.Count - (_localHistoryIndex + 1));
            }

            _localHistoryEntries.Add(new HistoryEntryState
            {
                Url = resolvedUrl,
                State = CloneHistoryState(state),
                Title = title ?? string.Empty
            });
            _localHistoryIndex = _localHistoryEntries.Count - 1;
        }

        private void ReplaceLocalHistoryState(FenValue state, string title, Uri target)
        {
            EnsureLocalHistoryInitialized();
            var resolvedUrl = target ?? GetCurrentHistoryUrl() ?? BaseUri ?? new Uri("about:blank");
            if (_localHistoryIndex < 0 || _localHistoryIndex >= _localHistoryEntries.Count)
            {
                _localHistoryEntries.Add(new HistoryEntryState
                {
                    Url = resolvedUrl,
                    State = CloneHistoryState(state),
                    Title = title ?? string.Empty
                });
                _localHistoryIndex = _localHistoryEntries.Count - 1;
                return;
            }

            var entry = _localHistoryEntries[_localHistoryIndex];
            entry.Url = resolvedUrl;
            entry.State = CloneHistoryState(state);
            entry.Title = title ?? entry.Title;
        }

        private void TraverseLocalHistory(int delta)
        {
            EnsureLocalHistoryInitialized();
            if (delta == 0)
            {
                ReloadWindowLocation(_locationObject);
                return;
            }

            var targetIndex = _localHistoryIndex + delta;
            if (targetIndex < 0 || targetIndex >= _localHistoryEntries.Count)
            {
                return;
            }

            _localHistoryIndex = targetIndex;
            var entry = _localHistoryEntries[_localHistoryIndex];
            SynchronizeHistorySurface(entry.Url);
            NotifyPopState(entry.State);
        }

        private void SynchronizeHistorySurface(Uri explicitUrl = null)
        {
            var currentUrl = explicitUrl ?? GetCurrentHistoryUrl();
            if (currentUrl != null)
            {
                BaseUri = currentUrl;
            }

            if (_locationObject != null)
            {
                UpdateLocationState(_locationObject, currentUrl ?? BaseUri);
            }
        }

        private void DispatchPopStateEvent(FenValue state)
        {
            SynchronizeHistorySurface();

            var eventObj = new DomEvent("popstate", false, false, false, _context);
            eventObj.Set("state", CloneHistoryState(state));
            var popStateArgs = new[] { FenValue.FromObject(eventObj) };

            InvokeWindowObjectListeners("popstate", popStateArgs[0]);

            if (_windowEventListeners.ContainsKey("popstate"))
            {
                var listeners = _windowEventListeners["popstate"].ToList();
                foreach (var listener in listeners)
                {
                    var callback = listener.Callback;
                    if (callback.IsFunction)
                    {
                        ExecuteFunction(callback.AsFunction() as FenFunction, popStateArgs);
                    }
                    else if (callback.IsObject)
                    {
                        var handleEvent = callback.AsObject().Get("handleEvent");
                        if (handleEvent.IsFunction)
                        {
                            _context.ThisBinding = callback;
                            handleEvent.AsFunction().Invoke(popStateArgs, _context, callback);
                        }
                    }

                    if (listener.Once)
                    {
                        _windowEventListeners["popstate"].Remove(listener);
                    }
                }
            }

            var windowVal = _globalEnv.Get("window");
            if (windowVal is FenValue fvWin && fvWin.IsObject)
            {
                var handler = fvWin.AsObject().Get("onpopstate");
                if (handler.IsFunction)
                {
                    handler.AsFunction()?.Invoke(popStateArgs, _context);
                }
            }
        }

        private void InvokeWindowObjectListeners(string eventType, FenValue eventValue)
        {
            if (_windowObject == null)
            {
                return;
            }

            var listenersVal = _windowObject.Get("__fen_listeners__");
            if (!listenersVal.IsObject)
            {
                return;
            }

            var listenersObj = listenersVal.AsObject() as FenObject;
            var arrVal = listenersObj?.Get(eventType) ?? FenValue.Undefined;
            var arr = arrVal.IsObject ? arrVal.AsObject() as FenObject : null;
            if (arr == null)
            {
                return;
            }

            int len = (int)arr.Get("length").ToNumber();
            for (int i = 0; i < len; i++)
            {
                var listenerEntry = arr.Get(i.ToString());
                var callback = listenerEntry;
                var onceListener = false;

                if (listenerEntry.IsObject)
                {
                    var entryObj = listenerEntry.AsObject();
                    var cbVal = entryObj.Get("callback");
                    if (!cbVal.IsUndefined)
                    {
                        callback = cbVal;
                        onceListener = entryObj.Get("once").ToBoolean();
                    }
                }

                FenFunction callbackFn = null;
                var callbackThis = FenValue.FromObject(_windowObject);
                if (callback.IsFunction)
                {
                    callbackFn = callback.AsFunction() as FenFunction;
                }
                else if (callback.IsObject)
                {
                    var handleEvent = callback.AsObject().Get("handleEvent");
                    if (handleEvent.IsFunction)
                    {
                        callbackFn = handleEvent.AsFunction() as FenFunction;
                        callbackThis = callback;
                    }
                }

                if (callbackFn == null)
                {
                    continue;
                }

                _context.ThisBinding = callbackThis;
                callbackFn.Invoke(new[] { eventValue }, _context, callbackThis);

                if (onceListener)
                {
                    if (listenerEntry.IsObject)
                    {
                        DetachAbortSignalRegistration(listenerEntry.AsObject() as FenObject);
                    }

                    var kept = FenObject.CreateArray();
                    int k = 0;
                    for (int j = 0; j < len; j++)
                    {
                        if (j == i) continue;
                        kept.Set(k.ToString(), arr.Get(j.ToString()));
                        k++;
                    }

                    kept.Set("length", FenValue.FromNumber(k));
                    listenersObj.Set(eventType, FenValue.FromObject(kept));
                    arr = kept;
                    len = k;
                    i--;
                }
            }
        }

        private void DetachAbortSignalRegistration(FenObject entryObj)
        {
            if (entryObj == null)
            {
                return;
            }

            var signal = entryObj.Get("__signal");
            var abortCallback = entryObj.Get("__abortCallback");
            if (!signal.IsObject || !abortCallback.IsFunction)
            {
                return;
            }

            var removeAbortListener = signal.AsObject()?.Get("removeEventListener") ?? FenValue.Undefined;
            if (removeAbortListener.IsFunction)
            {
                removeAbortListener.AsFunction()?.Invoke(new[]
                {
                    FenValue.FromString("abort"),
                    abortCallback
                }, _context, signal);
            }

            entryObj.Delete("__abortCallback");
        }

        private static void UpdateLocationState(FenObject location, Uri uri)
        {
            if (location == null)
            {
                return;
            }

            if (uri == null)
            {
                location.Set("href", FenValue.FromString("about:blank"));
                location.Set("origin", FenValue.FromString("null"));
                location.Set("protocol", FenValue.FromString("about:"));
                location.Set("host", FenValue.FromString(string.Empty));
                location.Set("hostname", FenValue.FromString(string.Empty));
                location.Set("port", FenValue.FromString(string.Empty));
                location.Set("pathname", FenValue.FromString("blank"));
                location.Set("search", FenValue.FromString(string.Empty));
                location.Set("hash", FenValue.FromString(string.Empty));
                return;
            }

            var origin = string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase)
                ? "null"
                : uri.GetLeftPart(UriPartial.Authority);

            location.Set("href", FenValue.FromString(uri.AbsoluteUri));
            location.Set("origin", FenValue.FromString(origin));
            location.Set("protocol", FenValue.FromString(uri.Scheme + ":"));
            location.Set("host", FenValue.FromString(uri.Authority));
            location.Set("hostname", FenValue.FromString(uri.Host));
            location.Set("port", FenValue.FromString(uri.IsDefaultPort ? string.Empty : uri.Port.ToString()));
            location.Set("pathname", FenValue.FromString(string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath));
            location.Set("search", FenValue.FromString(uri.Query ?? string.Empty));
            location.Set("hash", FenValue.FromString(uri.Fragment ?? string.Empty));
        }

        private void RequestWindowNavigation(FenObject location, string requestedUrl)
        {
            var target = ResolveLocationTarget(requestedUrl, location);
            if (target == null)
            {
                return;
            }

            UpdateLocationState(location, target);
            BaseUri = target;

            try
            {
                NavigationRequested?.Invoke(target);
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[FenRuntime] NavigationRequested callback failed for '{target}': {ex.Message}", LogCategory.JavaScript);
            }
        }

        private void ReloadWindowLocation(FenObject location)
        {
            if (location == null)
            {
                return;
            }

            var hrefValue = location.Get("href");
            if (hrefValue.IsUndefined || hrefValue.IsNull)
            {
                return;
            }

            RequestWindowNavigation(location, hrefValue.ToString());
        }

        public void SetDom(Node root, Uri baseUri = null)
        {
            if (root == null) return;
            this.BaseUri = baseUri;
            if (_historyBridge == null)
            {
                EnsureLocalHistoryInitialized();
                if (_localHistoryIndex >= 0 && _localHistoryIndex < _localHistoryEntries.Count)
                {
                    _localHistoryEntries[_localHistoryIndex].Url = baseUri ?? new Uri("about:blank");
                }
            }
            SynchronizeHistorySurface(baseUri);

            var documentWrapper = new DocumentWrapper(root, _context, baseUri);
            if (_domDocumentPrototype != null)
            {
                documentWrapper.SetPrototype(_domDocumentPrototype);
            }

            var docValue = FenValue.FromObject(documentWrapper);
            SetGlobal("document", docValue);

            // Update window.document
            var window = GetGlobal("window");
            if (window.IsObject)
            {
                window.AsObject().Set("document", docValue);
            }

            var locationValue = GetGlobal("location");
            if (locationValue.IsObject && locationValue.AsObject() is FenObject locationObject)
            {
                UpdateLocationState(locationObject, baseUri);
            }

            RegisterLegacyNamedGlobals(root);

            var fontLoadingBindings = FontLoadingBindings.CreateForDocument(documentWrapper, _context);
            documentWrapper.AttachFonts(fontLoadingBindings.Fonts);
            SetGlobal("FontFace", fontLoadingBindings.FontFaceConstructor);
            SetGlobal("FontFaceSetLoadEvent", fontLoadingBindings.FontFaceSetLoadEventConstructor);

            var highlightBindings = HighlightApiBindings.Create(_context);
            SetGlobal("StaticRange", highlightBindings.StaticRangeConstructor);
            SetGlobal("Highlight", highlightBindings.HighlightConstructor);
            SetGlobal("HighlightRegistry", highlightBindings.HighlightRegistryConstructor);

            var rangePrototype = new FenObject();
            var rangeConstructor = new FenFunction("Range", (args, _) =>
            {
                var rangeWrapper = new RangeWrapper(new FenBrowser.Core.Dom.V2.Range(root as Document ?? root.OwnerDocument ?? new Document()), _context);
                rangeWrapper.SetPrototype(rangePrototype);
                return FenValue.FromObject(rangeWrapper);
            });
            rangeConstructor.Prototype = rangePrototype;
            rangeConstructor.Set("prototype", FenValue.FromObject(rangePrototype));
            rangePrototype.SetBuiltin("constructor", FenValue.FromFunction(rangeConstructor));
            SetGlobal("Range", FenValue.FromFunction(rangeConstructor));

            var cssValue = GetGlobal("CSS");
            if (cssValue.IsObject)
            {
                cssValue.AsObject().Set("highlights", FenValue.FromObject(highlightBindings.Registry));
            }

            if (window.IsObject)
            {
                var windowObject = window.AsObject();
                windowObject.Set("Range", FenValue.FromFunction(rangeConstructor));
                windowObject.Set("StaticRange", highlightBindings.StaticRangeConstructor);
                windowObject.Set("Highlight", highlightBindings.HighlightConstructor);
                windowObject.Set("HighlightRegistry", highlightBindings.HighlightRegistryConstructor);
            }
        }

        public void DispatchEvent(string type, IObject eventData = null)
        {
            try
            {
                var evt = eventData != null ? FenValue.FromObject(eventData) : FenValue.Null;
                var handlerName = "on" + type;
                var windowObj = _globalEnv.Get("window");
                if (windowObj is FenValue fvWindow && fvWindow.IsObject)
                {
                    var handler = fvWindow.AsObject().Get(handlerName);
                    if (handler.IsFunction)
                    {
                        _context.ThisBinding = fvWindow;
                        handler.AsFunction().Invoke(new[] { evt }, _context);
                    }
                }

                InvokeWindowObjectListeners(type, evt);

                if (_windowEventListeners.ContainsKey(type))
                {
                    var listeners = _windowEventListeners[type].ToList();
                    foreach (var listener in listeners)
                    {
                        var callback = listener.Callback;
                        if (callback.IsFunction)
                        {
                            _context.ThisBinding = FenValue.Undefined;
                            try
                            {
                                callback.AsFunction().Invoke(new[] { evt }, _context);
                            }
                            catch
                            {
                            }
                        }
                        else if (callback.IsObject)
                        {
                            var handleEvent = callback.AsObject().Get("handleEvent");
                            if (handleEvent.IsFunction)
                            {
                                _context.ThisBinding = callback;
                                try
                                {
                                    handleEvent.AsFunction().Invoke(new[] { evt }, _context, callback);
                                }
                                catch
                                {
                                }
                            }
                        }

                        if (listener.Once)
                        {
                            _windowEventListeners[type].Remove(listener);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                /* [PERF-REMOVED] */
            }
        }

        private sealed class WindowEventListener
        {
            public FenValue Callback { get; }
            public bool Capture { get; }
            public bool Once { get; }
            public bool Passive { get; }

            public WindowEventListener(FenValue callback, bool capture, bool once, bool passive)
            {
                Callback = callback;
                Capture = capture;
                Once = once;
                Passive = passive;
            }
        }

        private Dictionary<string, List<WindowEventListener>> _windowEventListeners = new Dictionary<string, List<WindowEventListener>>(StringComparer.OrdinalIgnoreCase);

        private FenValue CreateTimer(FenFunction callback, int delay, bool repeat, FenValue[] args)
        {
            int id;
            lock (_timerLock)
            {
                id = _timerIdCounter++;
            }

            try
            {
                FenLogger.Debug($"[FenRuntime] CreateTimer called. ID: {id}, Delay: {delay}", LogCategory.JavaScript);
            }
            catch
            {
            }

            var cts = new CancellationTokenSource();
            lock (_timerLock)
            {
                _activeTimers[id] = cts;
            }

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
                    lock (_timerLock)
                    {
                        _activeTimers.Remove(id);
                    }
                }
            };

            _context.ScheduleCallback(timerAction, delay);
            return FenValue.FromNumber(id);
        }

        private FenValue CreateAnimationFrame(FenFunction callback)
        {
            int id;
            lock (_timerLock)
            {
                id = _timerIdCounter++;
            }

            var cts = new CancellationTokenSource();
            lock (_timerLock)
            {
                _activeTimers[id] = cts;
            }

            try
            {
                FenLogger.Debug($"[FenRuntime] RequestAnimationFrame. ID: {id}", LogCategory.JavaScript);
            }
            catch
            {
            }

            Action timerAction = () =>
            {
                if (cts.IsCancellationRequested) return;

                lock (_timerLock)
                {
                    _activeTimers.Remove(id);
                } // Autoremove before execution, unlike Interval

                try
                {
                    double now = Convert.ToDouble(DateTime.Now.Ticks) / 10000.0; // Simulated high-res time (ms)
                    _context.ThisBinding = FenValue.Undefined;
                    callback.Invoke(new FenValue[] { FenValue.FromNumber(now) }, _context);
                }
                catch (Exception ex)
                {
                    try
                    {
                        FenLogger.Error($"[RAF Error] {ex.Message}", LogCategory.JavaScript, ex);
                    }
                    catch
                    {
                    }
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

        private void RegisterLegacyNamedGlobals(Node root)
        {
            if (root == null)
            {
                return;
            }

            if (root is Element element)
            {
                var id = element.Id;
                if (string.Equals(id, "iframe", StringComparison.Ordinal))
                {
                }
                if (!string.IsNullOrWhiteSpace(id))
                {
                    var existing = _globalEnv.Get(id);
                    if (existing.IsUndefined || existing.IsNull)
                    {
                        var wrapped = DomWrapperFactory.Wrap(element, _context);
                        if (wrapped.IsObject)
                        {
                            _globalEnv.Set(id, wrapped);
                            var window = _globalEnv.Get("window");
                            if (window.IsObject)
                            {
                                window.AsObject().Set(id, wrapped);
                            }
                        }
                    }
                }
            }

            if (root.ChildNodes == null)
            {
                return;
            }

            foreach (var child in root.ChildNodes)
            {
                RegisterLegacyNamedGlobals(child);
            }
        }
        public void SetGlobal(string name, FenValue value)
        {
            if ((string.Equals(name, "window", StringComparison.Ordinal) || string.Equals(name, "globalThis", StringComparison.Ordinal)) && value.IsObject)
            {
                value.AsObject().Set("__fen_window_named_access__", FenValue.FromBoolean(true));
            }

            _globalEnv.Set(name, value);
        }

        private void NormalizeGlobalIntrinsicFunctionPrototypes(FenObject functionPrototype)
        {
            if (functionPrototype == null)
            {
                return;
            }

            var visited = new HashSet<FenObject>();
            foreach (var bindingName in _globalEnv.GetOwnBindingNames())
            {
                var bindingValue = _globalEnv.Get(bindingName);
                if (bindingValue.IsFunction && bindingValue.AsFunction() is FenFunction function)
                {
                    NormalizeCallableIntrinsic(function, functionPrototype, visited, walkPrototypeObject: true);
                    continue;
                }

                if (bindingValue.IsObject && bindingValue.AsObject() is FenObject targetObject)
                {
                    NormalizeIntrinsicObjectFunctions(targetObject, functionPrototype, visited, walkPrototypeObject: false);
                }
            }
        }

        private static void NormalizeCallableIntrinsic(
            FenFunction function,
            FenObject functionPrototype,
            HashSet<FenObject> visited,
            bool walkPrototypeObject)
        {
            if (function == null)
            {
                return;
            }

            function.SetPrototype(functionPrototype);
            NormalizeIntrinsicObjectFunctions(function, functionPrototype, visited, walkPrototypeObject);
        }

        private static void NormalizeIntrinsicObjectFunctions(
            FenObject target,
            FenObject functionPrototype,
            HashSet<FenObject> visited,
            bool walkPrototypeObject)
        {
            if (target == null || functionPrototype == null || !visited.Add(target))
            {
                return;
            }

            foreach (var propertyName in target.GetOwnPropertyNames())
            {
                if (!target.TryGetDirect(propertyName, out var propertyValue))
                {
                    continue;
                }

                if (propertyValue.IsFunction && propertyValue.AsFunction() is FenFunction method)
                {
                    NormalizeCallableIntrinsic(method, functionPrototype, visited, walkPrototypeObject: true);
                    continue;
                }

                if (!walkPrototypeObject || !string.Equals(propertyName, "prototype", StringComparison.Ordinal))
                {
                    continue;
                }

                if (propertyValue.IsObject && propertyValue.AsObject() is FenObject prototypeObject)
                {
                    NormalizeIntrinsicObjectFunctions(prototypeObject, functionPrototype, visited, walkPrototypeObject: false);
                }
            }
        }

        public IValue GetGlobal(string name)
        {
            var val = _globalEnv.Get(name);
            return val;
        }

        public void SetVariable(string name, FenValue value)
        {
            _globalEnv.Set(name, value);
        }

        public IValue GetVariable(string name)
        {
            return GetGlobal(name);
        }

        private FenValue ReflectSetOperation(FenValue[] args)
        {
            if (args == null || args.Length < 3 || (!args[0].IsObject && !args[0].IsFunction))
            {
                return FenValue.FromBoolean(false);
            }

            var target = args[0].AsObject() as FenObject;
            if (target == null)
            {
                return FenValue.FromBoolean(false);
            }

            var key = args[1];
            var value = args[2];
            var receiverValue = args.Length > 3 ? args[3] : FenValue.FromObject(target);
            var inheritedDesc = GetPropertyDescriptorFromChain(target, key);

            if (inheritedDesc.HasValue && inheritedDesc.Value.IsData && inheritedDesc.Value.Writable == false)
            {
                return FenValue.FromBoolean(false);
            }

            if (inheritedDesc.HasValue && inheritedDesc.Value.IsAccessor)
            {
                if (inheritedDesc.Value.Setter == null)
                {
                    return FenValue.FromBoolean(false);
                }

                target.SetWithReceiver(key, value, receiverValue, _context);
                return FenValue.FromBoolean(true);
            }

            var receiverObject = (receiverValue.IsObject || receiverValue.IsFunction)
                ? receiverValue.AsObject() as FenObject
                : null;
            if (receiverObject == null)
            {
                return FenValue.FromBoolean(false);
            }

            var receiverDesc = receiverObject.GetOwnPropertyDescriptor(key);
            if (receiverDesc.HasValue)
            {
                if (receiverDesc.Value.IsAccessor)
                {
                    if (receiverDesc.Value.Setter == null)
                    {
                        return FenValue.FromBoolean(false);
                    }

                    receiverObject.SetWithReceiver(key, value, receiverValue, _context);
                    return FenValue.FromBoolean(true);
                }

                if (receiverDesc.Value.Writable == false)
                {
                    return FenValue.FromBoolean(false);
                }

                receiverObject.SetWithReceiver(key, value, receiverValue, _context);
                return FenValue.FromBoolean(true);
            }

            if (!receiverObject.IsExtensible)
            {
                return FenValue.FromBoolean(false);
            }

            receiverObject.SetWithReceiver(key, value, receiverValue, _context);
            return FenValue.FromBoolean(true);
        }

        private static PropertyDescriptor? GetPropertyDescriptorFromChain(FenObject target, FenValue key)
        {
            for (var current = target; current != null; current = current.GetPrototype() as FenObject)
            {
                var desc = current.GetOwnPropertyDescriptor(key);
                if (desc.HasValue)
                {
                    return desc;
                }
            }

            return null;
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
        /// Execute JavaScript code using the FenEngine parser with bytecode-only execution.
        /// </summary>
        public IValue ExecuteSimple(string code, System.Threading.CancellationToken cancellationToken)
        {
            return ExecuteSimple(code, "script", false, cancellationToken);
        }
        public IValue ExecuteSimple(string code, string url = "script", bool allowReturn = false,
            System.Threading.CancellationToken cancellationToken = default, bool inheritStrictFromContext = true)
        {
            try
            {
                using var realmScope = EnterRealmActivationScope();

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
                    var parser = new Parser(lexer, allowReturnOutsideFunction: allowReturn, initialStrictMode: inheritStrictFromContext && (_context?.StrictMode ?? false));
                    var program = parser.ParseProgram();

                    if (parser.Errors.Count > 0)
                    {
                        var errMsg = string.Join("\n", parser.Errors);
                        throw new FenSyntaxError(errMsg);
                    }

                    if (program is Program parsedProgram && IsGlobalScriptExecution())
                    {
                        ValidateGlobalScriptDeclarations(parsedProgram);
                    }

                    DevToolsCore.Instance.RegisterSource(url, code);

                    // Bytecode execution Ã¢â‚¬â€ compile and run directly.
                    bool isEval = url == "eval.js";
                    FenBrowser.FenEngine.Core.Bytecode.CodeBlock compiledBlock;
                    try
                    {
                        var compiler = new FenBrowser.FenEngine.Core.Bytecode.Compiler.BytecodeCompiler(isEval);
                        compiledBlock = compiler.Compile(program);
                    }
                    catch (Exception compileEx)
                    {
                        FenLogger.Debug($"[FenRuntime] Compile error in {url}: {compileEx.Message}", LogCategory.JavaScript);
                        throw new FenSyntaxError($"SyntaxError: {compileEx.Message}");
                    }

                    try
                    {
                        var vm = new FenBrowser.FenEngine.Core.Bytecode.VM.VirtualMachine();
                        var bytecodeResult = vm.Execute(compiledBlock, _globalEnv);
                        return bytecodeResult;
                    }
                    catch (Exception vmEx)
                    {
                        FenLogger.Debug($"[FenRuntime] Bytecode runtime error in {url}: {vmEx.Message}", LogCategory.JavaScript);
                        if (TryExtractThrownValue(vmEx, out var thrownValue))
                        {
                            return FenValue.FromThrow(thrownValue);
                        }

                        throw new FenTypeError($"TypeError: {vmEx.GetType().Name}: {vmEx.Message}");
                    }
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
                FenLogger.Debug($"[FenRuntime] Runtime error in {url}: {ex.Message}", LogCategory.JavaScript);
                if (TryExtractThrownValue(ex, out var thrownValue))
                {
                    return FenValue.FromThrow(thrownValue);
                }

                if (ex is FenSyntaxError || ex is FenTypeError || ex is FenReferenceError || ex is FenRangeError)
                {
                    return FenValue.FromError(ex.Message);
                }

                return FenValue.FromError($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private FenValue ConstructForReflect(FenFunction targetFunction, FenValue[] args, FenValue newTargetValue)
        {
            var constructedObject = new FenObject();
            var prototype = ResolveConstructorPrototype(newTargetValue, targetFunction);
            if (prototype != null)
            {
                constructedObject.SetPrototype(prototype);
            }

            var thisValue = FenValue.FromObject(constructedObject);
            var result = targetFunction.Invoke(args ?? Array.Empty<FenValue>(), _context, thisValue);
            return (result.IsObject || result.IsFunction) ? result : thisValue;
        }

        private FenObject ResolveConstructorPrototype(FenValue newTargetValue, FenFunction targetFunction)
        {
            var newTargetObject = (newTargetValue.IsObject || newTargetValue.IsFunction)
                ? newTargetValue.AsObject() as FenObject
                : null;

            if (newTargetObject != null)
            {
                var explicitPrototype = newTargetObject.Get("prototype", _context);
                if (explicitPrototype.IsObject && explicitPrototype.AsObject() is FenObject explicitPrototypeObject)
                {
                    return explicitPrototypeObject;
                }
            }

            var realmRuntime = (newTargetObject as FenFunction)?.OwningRuntime ?? targetFunction?.OwningRuntime ?? this;
            return realmRuntime.GetIntrinsicDefaultPrototypeForConstructor(targetFunction);
        }

        private FenObject GetIntrinsicDefaultPrototypeForConstructor(FenFunction targetFunction)
        {
            string ctorName = targetFunction?.Name;
            if (!string.IsNullOrEmpty(ctorName) && GetGlobal(ctorName) is FenValue ctorValue && (ctorValue.IsObject || ctorValue.IsFunction))
            {
                var ctorObject = ctorValue.AsObject() as FenObject;
                if (ctorObject != null)
                {
                    var prototypeValue = ctorObject.Get("prototype", _context);
                    if (prototypeValue.IsObject && prototypeValue.AsObject() is FenObject prototypeObject)
                    {
                        return prototypeObject;
                    }
                }
            }

            if (GetGlobal("Object") is FenValue objectCtorValue && (objectCtorValue.IsObject || objectCtorValue.IsFunction))
            {
                var objectCtor = objectCtorValue.AsObject() as FenObject;
                if (objectCtor != null)
                {
                    var objectPrototypeValue = objectCtor.Get("prototype", _context);
                    if (objectPrototypeValue.IsObject && objectPrototypeValue.AsObject() is FenObject objectPrototype)
                    {
                        return objectPrototype;
                    }
                }
            }

            return FenObject.DefaultPrototype as FenObject;
        }

        private static bool TryExtractThrownValue(Exception exception, out FenValue thrownValue)
        {
            thrownValue = FenValue.Undefined;
            if (exception == null)
            {
                return false;
            }

            var thrownValueProperty = exception.GetType().GetProperty("ThrownValue");
            if (thrownValueProperty?.PropertyType != typeof(FenValue))
            {
                return false;
            }

            if (thrownValueProperty.GetValue(exception) is FenValue extracted)
            {
                thrownValue = extracted;
                return true;
            }

            return false;
        }

        private bool IsGlobalScriptExecution()
        {
            return _globalEnv != null && _globalEnv.Outer == null;
        }

        private void ValidateGlobalScriptDeclarations(Program program)
        {
            if (program == null)
            {
                return;
            }

            var varNames = new HashSet<string>(StringComparer.Ordinal);
            var lexicalNames = new HashSet<string>(StringComparer.Ordinal);
            CollectTopLevelDeclarationNames(program, varNames, lexicalNames);

            foreach (var lexicalName in lexicalNames)
            {
                if (varNames.Contains(lexicalName) || _globalEnv.HasLocalBinding(lexicalName))
                {
                    throw new FenSyntaxError($"SyntaxError: Identifier '{lexicalName}' has already been declared");
                }
            }
        }

        private void CollectTopLevelDeclarationNames(Program program, ISet<string> varNames, ISet<string> lexicalNames)
        {
            foreach (var statement in program.Statements)
            {
                switch (statement)
                {
                    case LetStatement letStatement when letStatement.Name != null && !string.IsNullOrEmpty(letStatement.Name.Value):
                        if (letStatement.Kind == DeclarationKind.Var)
                        {
                            varNames.Add(letStatement.Name.Value);
                        }
                        else if (!lexicalNames.Add(letStatement.Name.Value))
                        {
                            throw new FenSyntaxError($"SyntaxError: Identifier '{letStatement.Name.Value}' has already been declared");
                        }
                        break;
                    case FunctionDeclarationStatement functionDeclaration when !string.IsNullOrEmpty(functionDeclaration.Function?.Name):
                        varNames.Add(functionDeclaration.Function.Name);
                        break;
                    case ClassStatement classStatement when !string.IsNullOrEmpty(classStatement.Name?.Value):
                        if (!lexicalNames.Add(classStatement.Name.Value))
                        {
                            throw new FenSyntaxError($"SyntaxError: Identifier '{classStatement.Name.Value}' has already been declared");
                        }
                        break;
                    case ForInStatement forInStatement when forInStatement.BindingKind.HasValue && forInStatement.Variable != null && !string.IsNullOrEmpty(forInStatement.Variable.Value):
                        if (forInStatement.BindingKind.Value == DeclarationKind.Var)
                        {
                            varNames.Add(forInStatement.Variable.Value);
                        }
                        else if (!lexicalNames.Add(forInStatement.Variable.Value))
                        {
                            throw new FenSyntaxError($"SyntaxError: Identifier '{forInStatement.Variable.Value}' has already been declared");
                        }
                        break;
                    case ForOfStatement forOfStatement when forOfStatement.BindingKind.HasValue && forOfStatement.Variable != null && !string.IsNullOrEmpty(forOfStatement.Variable.Value):
                        if (forOfStatement.BindingKind.Value == DeclarationKind.Var)
                        {
                            varNames.Add(forOfStatement.Variable.Value);
                        }
                        else if (!lexicalNames.Add(forOfStatement.Variable.Value))
                        {
                            throw new FenSyntaxError($"SyntaxError: Identifier '{forOfStatement.Variable.Value}' has already been declared");
                        }
                        break;
                }
            }
        }




        #region Helper Methods for Browser APIs

        private sealed class FinalizationRegistryState
        {
            private readonly object _syncRoot = new();
            private readonly FenFunction _defaultCleanupCallback;
            private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IObject, FinalizationTargetBucket> _buckets = new();
            private readonly Queue<FenValue> _pendingHeldValues = new();
            private readonly List<FinalizationRegistration> _registrations = new();

            public FinalizationRegistryState(FenFunction defaultCleanupCallback)
            {
                _defaultCleanupCallback = defaultCleanupCallback;
            }

            public void Register(FenValue targetValue, FenValue heldValue, FenValue unregisterTokenValue)
            {
                var target = targetValue.AsObject();
                if (target == null)
                {
                    throw new FenTypeError("TypeError: FinalizationRegistry.prototype.register target must be an object");
                }

                IObject? unregisterToken = null;
                if (!unregisterTokenValue.IsUndefined)
                {
                    unregisterToken = unregisterTokenValue.AsObject();
                    if (unregisterToken == null)
                    {
                        throw new FenTypeError("TypeError: FinalizationRegistry.prototype.register unregisterToken must be an object");
                    }
                }

                lock (_syncRoot)
                {
                    var registration = new FinalizationRegistration(heldValue, unregisterToken);
                    _registrations.Add(registration);
                    var bucket = _buckets.GetValue(target, _ => new FinalizationTargetBucket(this));
                    bucket.Add(registration);
                }
            }

            public bool Unregister(FenValue unregisterTokenValue)
            {
                var unregisterToken = unregisterTokenValue.AsObject();
                if (unregisterToken == null)
                {
                    throw new FenTypeError("TypeError: FinalizationRegistry.prototype.unregister unregisterToken must be an object");
                }

                lock (_syncRoot)
                {
                    var removed = false;
                    foreach (var registration in _registrations)
                    {
                        if (registration.Active && ReferenceEquals(registration.UnregisterToken, unregisterToken))
                        {
                            registration.Active = false;
                            removed = true;
                        }
                    }

                    if (removed)
                    {
                        _registrations.RemoveAll(static registration => !registration.Active);
                    }

                    return removed;
                }
            }

            public void DrainPending(IExecutionContext context)
            {
                DrainPending(null, context);
            }

            public void DrainPending(FenFunction? cleanupCallbackOverride, IExecutionContext context)
            {
                FenValue[] pendingHeldValues;
                lock (_syncRoot)
                {
                    if (_pendingHeldValues.Count == 0)
                    {
                        return;
                    }

                    pendingHeldValues = _pendingHeldValues.ToArray();
                    _pendingHeldValues.Clear();
                    _registrations.RemoveAll(static registration => !registration.Active);
                }

                var callback = cleanupCallbackOverride ?? _defaultCleanupCallback;
                foreach (var heldValue in pendingHeldValues)
                {
                    callback.Invoke(new[] { heldValue }, context);
                }
            }

            private void EnqueueFinalized(IReadOnlyList<FinalizationRegistration> registrations)
            {
                lock (_syncRoot)
                {
                    foreach (var registration in registrations)
                    {
                        if (!registration.Active)
                        {
                            continue;
                        }

                        registration.Active = false;
                        _pendingHeldValues.Enqueue(registration.HeldValue);
                    }
                }
            }

            private sealed class FinalizationTargetBucket
            {
                private readonly FinalizationRegistryState _owner;
                private readonly object _registrationsLock = new();
                private readonly List<FinalizationRegistration> _registrations = new();

                public FinalizationTargetBucket(FinalizationRegistryState owner)
                {
                    _owner = owner;
                }

                public void Add(FinalizationRegistration registration)
                {
                    lock (_registrationsLock)
                    {
                        _registrations.Add(registration);
                    }
                }

                ~FinalizationTargetBucket()
                {
                    List<FinalizationRegistration> snapshot;
                    lock (_registrationsLock)
                    {
                        snapshot = new List<FinalizationRegistration>(_registrations);
                        _registrations.Clear();
                    }

                    _owner.EnqueueFinalized(snapshot);
                }
            }

            private sealed class FinalizationRegistration
            {
                public FinalizationRegistration(FenValue heldValue, IObject? unregisterToken)
                {
                    HeldValue = heldValue;
                    UnregisterToken = unregisterToken;
                }

                public FenValue HeldValue { get; }
                public IObject? UnregisterToken { get; }
                public bool Active { get; set; } = true;
            }
        }

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
                        callback.NativeImplementation(new FenValue[] { FenValue.FromString(errorMessage) },
                            FenValue.Undefined);
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
            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), url);

                    // Add headers
                    foreach (var h in headers)
                    {
                        try
                        {
                            request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                        }
                        catch
                        {
                        }
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
                                        callback.NativeImplementation(new FenValue[] { (FenValue)responseObj },
                                            FenValue.Undefined);
                                    else if (!callback.IsNative)
                                        callback.Invoke(new FenValue[] { (FenValue)responseObj }, _context);
                                }
                                catch (Exception ex)
                                {
                                    try
                                    {
                                        FenLogger.Error($"[fetch] Then callback error: {ex.Message}",
                                            LogCategory.JavaScript);
                                    }
                                    catch
                                    {
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                FenLogger.Error($"[fetch] Resolution error: {ex.Message}", LogCategory.JavaScript);
                            }
                            catch
                            {
                            }
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
                                    callback.NativeImplementation(new FenValue[] { FenValue.FromString(errorMessage) },
                                        FenValue.Undefined);
                                else if (!callback.IsNative)
                                    callback.Invoke(new FenValue[] { FenValue.FromString(errorMessage) }, _context);
                            }
                            catch
                            {
                            }
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
                            cb.NativeImplementation(new FenValue[] { FenValue.FromString(bodyText ?? "") },
                                FenValue.Undefined);
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
                                cb.NativeImplementation(
                                    new FenValue[] { FenValue.FromError($"JSON parse error: {ex.Message}") },
                                    FenValue.Undefined);
                        }
                    }

                    return tThis;
                })));
                jsonPromise.Set("catch",
                    FenValue.FromFunction(new FenFunction("catch", (cArgs, cThis) => { return cThis; })));
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

                _ = RunDetachedAsync(async () =>
                {
                    try
                    {
                        if (clientWs.State == WebSocketState.Open)
                        {
                            var bytes = System.Text.Encoding.UTF8.GetBytes(data);
                            await clientWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                                cts.Token);
                        }
                    }
                    catch
                    {
                    }
                });

                return FenValue.Undefined;
            })));

            // close() method
            ws.Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
            {
                ws.Set("readyState", FenValue.FromNumber(CLOSING));

                _ = RunDetachedAsync(async () =>
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
                                cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) },
                                    FenValue.Undefined);
                            }
                        }
                    }
                    catch
                    {
                    }
                });

                return FenValue.Undefined;
            })));

            // Connect asynchronously
            _ = RunDetachedAsync(async () =>
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
                                    evt.Set("code",
                                        FenValue.FromNumber((int)(result.CloseStatus ??
                                                                  WebSocketCloseStatus.NormalClosure)));
                                    evt.Set("reason", FenValue.FromString(result.CloseStatusDescription ?? ""));
                                    evt.Set("wasClean", FenValue.FromBoolean(true));
                                    cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) },
                                        FenValue.Undefined);
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
                                    cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) },
                                        FenValue.Undefined);
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
                _ = RunDetachedAsync(async () =>
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
                                cb.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) },
                                    FenValue.Undefined);
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
            db.Set("close",
                FenValue.FromFunction(new FenFunction("close", (args, thisVal) => { return FenValue.Undefined; })));

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

                _ = RunDetachedAsync(async () =>
                {
                    await _storageBackend.Add(origin, dbName, storeName, key, StorageUtils.ToSerializable(value));
                });

                var request = new FenObject();
                request.Set("result", FenValue.FromString(key));
                request.Set("onsuccess", FenValue.Null);

                _ = RunDetachedAsync(async () =>
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

                _ = RunDetachedAsync(async () =>
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

                _ = RunDetachedAsync(async () =>
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

                _ = RunDetachedAsync(async () =>
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

                _ = RunDetachedAsync(async () =>
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
        /// Promise constructor wired to JsPromise (microtask-driven semantics).
        /// This is used for global Promise registration.
        /// </summary>
        private FenFunction CreatePromiseConstructorModern()
        {
            FenValue GetIterableOrEmpty(FenValue[] args)
            {
                if (args.Length > 0 && (args[0].IsObject || args[0].IsFunction))
                    return args[0];

                var empty = FenObject.CreateArray();
                empty.Set("length", FenValue.FromNumber(0));
                return FenValue.FromObject(empty);
            }

            FenFunction promiseCtor = null;
            promiseCtor = new FenFunction("Promise", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction)
                    throw new FenTypeError("TypeError: Promise resolver is not a function");

                var promise = new JsPromise(args[0], _context);
                var ctorProtoVal = promiseCtor?.Get("prototype", null) ?? FenValue.Undefined;
                if (ctorProtoVal.IsObject)
                {
                    promise.SetPrototype(ctorProtoVal.AsObject());
                }
                return FenValue.FromObject(promise);
            });

            // Promise.prototype[@@toStringTag] = "Promise"
            var promiseProtoVal = promiseCtor.Get("prototype", null);
            if (promiseProtoVal.IsObject && promiseProtoVal.AsObject() is FenObject promiseProto)
            {
                promiseProto.SetBuiltin(JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("Promise"));
            }
            promiseCtor.Set("resolve", FenValue.FromFunction(new FenFunction("resolve", (args, thisVal) =>
            {
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                return FenValue.FromObject(JsPromise.Resolve(value, _context));
            })));

            promiseCtor.Set("reject", FenValue.FromFunction(new FenFunction("reject", (args, thisVal) =>
            {
                var reason = args.Length > 0 ? args[0] : FenValue.Undefined;
                return FenValue.FromObject(JsPromise.Reject(reason, _context));
            })));

            promiseCtor.Set("all", FenValue.FromFunction(new FenFunction("all", (args, thisVal) =>
            {
                return FenValue.FromObject(JsPromise.All(GetIterableOrEmpty(args), _context));
            })));

            promiseCtor.Set("race", FenValue.FromFunction(new FenFunction("race", (args, thisVal) =>
            {
                return FenValue.FromObject(JsPromise.Race(GetIterableOrEmpty(args), _context));
            })));

            promiseCtor.Set("allSettled", FenValue.FromFunction(new FenFunction("allSettled", (args, thisVal) =>
            {
                return FenValue.FromObject(JsPromise.AllSettled(GetIterableOrEmpty(args), _context));
            })));

            promiseCtor.Set("any", FenValue.FromFunction(new FenFunction("any", (args, thisVal) =>
            {
                return FenValue.FromObject(JsPromise.Any(GetIterableOrEmpty(args), _context));
            })));

            promiseCtor.Set("withResolvers", FenValue.FromFunction(new FenFunction("withResolvers", (args, thisVal) =>
            {
                FenFunction? resolveFn = null;
                FenFunction? rejectFn = null;

                var executor = new FenFunction("withResolversExecutor", (exArgs, _) =>
                {
                    resolveFn = exArgs.Length > 0 && exArgs[0].IsFunction ? exArgs[0].AsFunction() : null;
                    rejectFn = exArgs.Length > 1 && exArgs[1].IsFunction ? exArgs[1].AsFunction() : null;
                    return FenValue.Undefined;
                });

                var promise = CreateExecutorPromise(executor, promiseCtor);
                if (resolveFn == null || rejectFn == null)
                {
                    throw new FenTypeError("TypeError: Promise.withResolvers failed to capture resolve/reject functions");
                }

                var result = new FenObject();
                result.Set("promise", promise);
                result.Set("resolve", FenValue.FromFunction(resolveFn));
                result.Set("reject", FenValue.FromFunction(rejectFn));
                return FenValue.FromObject(result);
            })));

            promiseCtor.Set("try", FenValue.FromFunction(new FenFunction("try", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction)
                {
                    return FenValue.FromObject(JsPromise.Reject(
                        FenValue.FromString("TypeError: Promise.try requires a callable"), _context));
                }

                var fn = args[0].AsFunction();
                var fnArgs = args.Skip(1).ToArray();
                try
                {
                    var result = fn.Invoke(fnArgs, _context);
                    if (result.IsObject && result.AsObject() is JsPromise promiseResult)
                        return FenValue.FromObject(promiseResult);

                    return FenValue.FromObject(JsPromise.Resolve(result, _context));
                }
                catch (Exception ex)
                {
                    return FenValue.FromObject(JsPromise.Reject(FenValue.FromString(ex.Message), _context));
                }
            })));

            return promiseCtor;
        }

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
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction))
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
                        var item = iterable.Get(i.ToString());

                        // Handle thenable/promise
                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod.HasValue && thenMethod.Value.IsFunction)
                            {
                                thenMethod.Value.AsFunction().Invoke(new FenValue[]
                                {
                                    FenValue.FromFunction(new FenFunction("resolve", (a, __) =>
                                    {
                                        lock (lockObj)
                                        {
                                            if (rejected) return FenValue.Undefined;
                                            results.Set(index.ToString(), a.Length > 0 ? a[0] : FenValue.Undefined);
                                            completed++;
                                            if (completed == len)
                                                resolve?.Invoke(new FenValue[] { FenValue.FromObject(results) }, null);
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
                            if (completed == len)
                                resolve?.Invoke(new FenValue[] { FenValue.FromObject(results) }, null);
                        }
                    }

                    return FenValue.Undefined;
                }), promiseCtor);
            })));

            // Promise.race(iterable) - Returns first settled promise
            promiseCtor.Set("race", FenValue.FromFunction(new FenFunction("race", (args, thisVal) =>
            {
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction))
                    return CreateExecutorPromise(new FenFunction("raceExecutor", (_, __) => FenValue.Undefined),
                        promiseCtor);

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
                        var item = iterable.Get(i.ToString());

                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod.HasValue && thenMethod.Value.IsFunction)
                            {
                                thenMethod.Value.AsFunction().Invoke(new FenValue[]
                                {
                                    FenValue.FromFunction(new FenFunction("resolve", (a, __) =>
                                    {
                                        lock (lockObj)
                                        {
                                            if (settled) return FenValue.Undefined;
                                            settled = true;
                                        }

                                        resolve?.Invoke(a, null);
                                        return FenValue.Undefined;
                                    })),
                                    FenValue.FromFunction(new FenFunction("reject", (a, __) =>
                                    {
                                        lock (lockObj)
                                        {
                                            if (settled) return FenValue.Undefined;
                                            settled = true;
                                        }

                                        reject?.Invoke(a, null);
                                        return FenValue.Undefined;
                                    }))
                                }, null);
                                continue;
                            }
                        }

                        // Non-promise settles immediately
                        lock (lockObj)
                        {
                            if (settled) continue;
                            settled = true;
                        }

                        resolve?.Invoke(new FenValue[] { item }, null);
                        break;
                    }

                    return FenValue.Undefined;
                }), promiseCtor);
            })));

            // Promise.allSettled(iterable) - Waits for all to settle (resolve or reject)
            promiseCtor.Set("allSettled", FenValue.FromFunction(new FenFunction("allSettled", (args, thisVal) =>
            {
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction))
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
                        var item = iterable.Get(i.ToString());

                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod.HasValue && thenMethod.Value.IsFunction)
                            {
                                thenMethod.Value.AsFunction().Invoke(new FenValue[]
                                {
                                    FenValue.FromFunction(new FenFunction("resolve", (a, __) =>
                                    {
                                        var result = new FenObject();
                                        result.Set("status", FenValue.FromString("fulfilled"));
                                        result.Set("value", a.Length > 0 ? a[0] : FenValue.Undefined);
                                        lock (lockObj)
                                        {
                                            results.Set(index.ToString(), FenValue.FromObject(result));
                                            completed++;
                                            if (completed == len)
                                                resolve?.Invoke(new FenValue[] { FenValue.FromObject(results) }, null);
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
                                            if (completed == len)
                                                resolve?.Invoke(new FenValue[] { FenValue.FromObject(results) }, null);
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
                            if (completed == len)
                                resolve?.Invoke(new FenValue[] { FenValue.FromObject(results) }, null);
                        }
                    }

                    return FenValue.Undefined;
                }), promiseCtor);
            })));

            // Promise.any(iterable) - Returns first fulfilled or AggregateError if all reject
            promiseCtor.Set("any", FenValue.FromFunction(new FenFunction("any", (args, thisVal) =>
            {
                if (args.Length == 0 || (!args[0].IsObject && !args[0].IsFunction))
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
                        var item = iterable.Get(i.ToString());

                        if (item.IsObject)
                        {
                            var thenMethod = item.AsObject()?.Get("then");
                            if (thenMethod.HasValue && thenMethod.Value.IsFunction)
                            {
                                thenMethod.Value.AsFunction().Invoke(new FenValue[]
                                {
                                    FenValue.FromFunction(new FenFunction("resolve", (a, __) =>
                                    {
                                        lock (lockObj)
                                        {
                                            if (fulfilled) return FenValue.Undefined;
                                            fulfilled = true;
                                        }

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
                                                aggErr.Set("message",
                                                    FenValue.FromString("All promises were rejected"));
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
                        lock (lockObj)
                        {
                            if (fulfilled) continue;
                            fulfilled = true;
                        }

                        resolve?.Invoke(new FenValue[] { item }, null);
                        break;
                    }

                    return FenValue.Undefined;
                }), promiseCtor);
            })));

            // ES2024: Promise.withResolvers() - returns { promise, resolve, reject }
            promiseCtor.Set("withResolvers", FenValue.FromFunction(new FenFunction("withResolvers", (args, thisVal) =>
            {
                FenFunction? resolveFn = null;
                FenFunction? rejectFn = null;
                var promise = CreateExecutorPromise(new FenFunction("withResolversExecutor", (exArgs, _) =>
                {
                    resolveFn = exArgs.Length > 0 ? exArgs[0].AsFunction() : null;
                    rejectFn = exArgs.Length > 1 ? exArgs[1].AsFunction() : null;
                    return FenValue.Undefined;
                }), promiseCtor);
                if (resolveFn == null || rejectFn == null)
                {
                    throw new FenTypeError("TypeError: Promise.withResolvers failed to capture resolve/reject functions");
                }

                var result = new FenObject();
                result.Set("promise", promise);
                result.Set("resolve", FenValue.FromFunction(resolveFn));
                result.Set("reject", FenValue.FromFunction(rejectFn));
                return FenValue.FromObject(result);
            })));

            // ES2025: Promise.try(fn, ...args) - wraps sync/async fn in a promise
            promiseCtor.Set("try", FenValue.FromFunction(new FenFunction("try", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction)
                    return CreateRejectedPromiseValue(
                        FenValue.FromString("TypeError: Promise.try requires a callable"));
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
            var fulfillCallbacks =
                new List<(FenFunction onFulfill, FenFunction onReject, FenFunction chainResolve, FenFunction chainReject
                    )>();
            var rejectCallbacks =
                new List<(FenFunction onFulfill, FenFunction onReject, FenFunction chainResolve, FenFunction chainReject
                    )>();
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
                            thenMethod.Value.AsFunction().Invoke(new FenValue[]
                            {
                                FenValue.FromFunction(new FenFunction("res", (a, __) =>
                                {
                                    settle("fulfilled", a.Length > 0 ? a[0] : FenValue.Undefined);
                                    return FenValue.Undefined;
                                })),
                                FenValue.FromFunction(new FenFunction("rej", (a, __) =>
                                {
                                    settle("rejected", a.Length > 0 ? a[0] : FenValue.Undefined);
                                    return FenValue.Undefined;
                                }))
                            }, null);
                        }
                        catch (Exception ex)
                        {
                            settle("rejected", FenValue.FromString(ex.Message));
                        }

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
                        _ = RunDetached(() =>
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
                    return thenMethod.AsFunction()
                        .Invoke(
                            new FenValue[]
                                { FenValue.Undefined, catchArgs.Length > 0 ? catchArgs[0] : FenValue.Undefined }, null);
                return FenValue.FromObject(promise);
            })));

            // finally(onFinally)
            promise.Set("finally", FenValue.FromFunction(new FenFunction("finally", (finallyArgs, _) =>
            {
                var onFinally = finallyArgs.Length > 0 && finallyArgs[0].IsFunction
                    ? finallyArgs[0].AsFunction()
                    : null;
                if (onFinally == null) return FenValue.FromObject(promise);

                var thenMethod = promise.Get("then");
                if (thenMethod != null && thenMethod.IsFunction)
                {
                    return thenMethod.AsFunction().Invoke(new FenValue[]
                    {
                        FenValue.FromFunction(new FenFunction("onFulfill", (a, __) =>
                        {
                            onFinally.Invoke(new FenValue[0], null);
                            return a.Length > 0 ? a[0] : FenValue.Undefined;
                        })),
                        FenValue.FromFunction(new FenFunction("onReject", (a, __) =>
                        {
                            onFinally.Invoke(new FenValue[0], null);
                            return CreateRejectedPromiseValue(a.Length > 0 ? a[0] : FenValue.Undefined);
                        }))
                    }, null);
                }

                return FenValue.FromObject(promise);
            })));

            // Execute the executor
            try
            {
                executor.Invoke(new FenValue[] { FenValue.FromFunction(resolveFn), FenValue.FromFunction(rejectFn) },
                    null);
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
                _ = RunDetachedAsync(async () =>
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
            worker.Set("terminate",
                FenValue.FromFunction(new FenFunction("terminate", (args, thisVal) => { return FenValue.Undefined; })));

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
            FenValue CreateTypedArrayValue(byte[] buffer)
            {
                buffer ??= Array.Empty<byte>();
                var length = bytesPerElement <= 0 ? 0 : buffer.Length / bytesPerElement;
                var arr = new FenObject();
                arr.NativeObject = buffer;
                arr.Set("length", FenValue.FromNumber(length));
                arr.Set("byteLength", FenValue.FromNumber(buffer.Length));
                arr.Set("BYTES_PER_ELEMENT", FenValue.FromNumber(bytesPerElement));

                for (int i = 0; i < length && i < 1000; i++)
                {
                    if (bytesPerElement == 1)
                    {
                        arr.Set(i.ToString(), FenValue.FromNumber(buffer[i]));
                    }
                    else
                    {
                        arr.Set(i.ToString(), FenValue.FromNumber(0));
                    }
                }

                arr.Set("set", FenValue.FromFunction(new FenFunction("set", (setArgs, setThis) => FenValue.Undefined)));
                arr.Set("subarray", FenValue.FromFunction(new FenFunction("subarray", (subArgs, subThis) => FenValue.FromObject(arr))));
                return FenValue.FromObject(arr);
            }

            var ctor = new FenFunction(name, (args, thisVal) =>
            {
                int length = 0;
                byte[] buffer = null;

                if (args.Length > 0)
                {
                    if (args[0].IsNumber)
                    {
                        length = (int)args[0].ToNumber();
                        buffer = new byte[Math.Max(0, length * bytesPerElement)];
                    }
                    else if (args[0].IsObject)
                    {
                        var obj = args[0].AsObject() as FenObject;
                        if (obj?.NativeObject is byte[] existingBuffer)
                        {
                            int byteOffset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                            int byteLength = args.Length > 2 ? (int)args[2].ToNumber() * bytesPerElement : existingBuffer.Length - byteOffset;
                            byteOffset = Math.Max(0, Math.Min(byteOffset, existingBuffer.Length));
                            byteLength = Math.Max(0, Math.Min(byteLength, existingBuffer.Length - byteOffset));
                            buffer = new byte[byteLength];
                            if (byteLength > 0)
                            {
                                Array.Copy(existingBuffer, byteOffset, buffer, 0, byteLength);
                            }
                            length = byteLength / bytesPerElement;
                        }
                    }
                }

                if (buffer == null)
                    buffer = Array.Empty<byte>();

                return CreateTypedArrayValue(buffer);
            });

            ctor.Set("BYTES_PER_ELEMENT", FenValue.FromNumber(bytesPerElement));
            if (string.Equals(name, "Uint8Array", StringComparison.Ordinal))
            {
                ctor.Set("fromBase64", FenValue.FromFunction(new FenFunction("fromBase64", (args, thisVal) =>
                {
                    if (args.Length == 0)
                    {
                        throw new FenTypeError("TypeError: Uint8Array.fromBase64 requires a source string");
                    }

                    var source = args[0].ToString();
                    var alphabet = "base64";

                    if (args.Length > 1 && args[1].IsObject)
                    {
                        var options = args[1].AsObject();
                        var alphabetValue = options.Get("alphabet", null);
                        if (!alphabetValue.IsUndefined)
                        {
                            alphabet = alphabetValue.ToString();
                        }
                    }

                    if (alphabet != "base64" && alphabet != "base64url")
                    {
                        throw new FenTypeError("TypeError: Invalid base64 alphabet option");
                    }

                    var sanitized = new string(source.Where(ch =>
                        ch != ' ' &&
                        ch != '\t' &&
                        ch != '\r' &&
                        ch != '\n' &&
                        ch != '\f').ToArray());

                    if (alphabet == "base64url")
                    {
                        sanitized = sanitized.Replace('-', '+').Replace('_', '/');
                    }

                    var remainder = sanitized.Length % 4;
                    if (remainder == 1)
                    {
                        throw new FenTypeError("TypeError: Invalid base64 string");
                    }

                    if (remainder > 0)
                    {
                        sanitized = sanitized.PadRight(sanitized.Length + (4 - remainder), '=');
                    }

                    try
                    {
                        return CreateTypedArrayValue(Convert.FromBase64String(sanitized));
                    }
                    catch (FormatException)
                    {
                        throw new FenTypeError("TypeError: Invalid base64 string");
                    }
                })));

                ctor.Set("fromHex", FenValue.FromFunction(new FenFunction("fromHex", (args, thisVal) =>
                {
                    if (args.Length == 0)
                    {
                        throw new FenTypeError("TypeError: Uint8Array.fromHex requires a source string");
                    }

                    var source = args[0].ToString();
                    var compact = new string(source.Where(ch =>
                        ch != ' ' &&
                        ch != '\t' &&
                        ch != '\r' &&
                        ch != '\n' &&
                        ch != '\f').ToArray());

                    if ((compact.Length & 1) != 0)
                    {
                        throw new FenTypeError("TypeError: Invalid hex string");
                    }

                    var bytes = new byte[compact.Length / 2];
                    for (int i = 0; i < compact.Length; i += 2)
                    {
                        int hi = HexDigitToInt(compact[i]);
                        int lo = HexDigitToInt(compact[i + 1]);
                        if (hi < 0 || lo < 0)
                        {
                            throw new FenTypeError("TypeError: Invalid hex string");
                        }

                        bytes[i / 2] = (byte)((hi << 4) | lo);
                    }

                    return CreateTypedArrayValue(bytes);
                })));
            }
            return ctor;
        }

        private static int HexDigitToInt(char ch)
        {
            if (ch >= '0' && ch <= '9') return ch - '0';
            if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
            if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
            return -1;
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
        private string ConvertToJsonStringWithReplacer(FenValue value, FenFunction replacer, string[] replacerArray,
            int spaces, string indent, HashSet<object> seen = null)
        {
            if (value == null || value.IsUndefined) return "undefined";
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
                if (obj == null) return "null";

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
                        var itemStr =
                            ConvertToJsonStringWithReplacer(item, replacer, replacerArray, spaces, newIndent, seen);
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
                            var valStr = ConvertToJsonStringWithReplacer(val, replacer, replacerArray, spaces,
                                newIndent, seen);
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

            var resolve = new Action<IValue>(value =>
            {
                if (promise.Get("__state__").ToString() == "pending")
                {
                    promise.Set("__state__", FenValue.FromString("fulfilled"));
                    promise.Set("__value__", (FenValue)value);
                }
            });

            var reject = new Action<IValue>(reason =>
            {
                if (promise.Get("__state__").ToString() == "pending")
                {
                    promise.Set("__state__", FenValue.FromString("rejected"));
                    promise.Set("__reason__", (FenValue)reason);
                }
            });

            try
            {
                executor(resolve, reject);
            }
            catch (Exception ex)
            {
                reject(FenValue.FromString(ex.Message));
            }

            // then(onFulfilled, onRejected)
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
            {
                var state = promise.Get("__state__").ToString();
                if (state == "fulfilled")
                {
                    if (args.Length > 0 && args[0].IsFunction)
                    {
                        var res = args[0].AsFunction().Invoke(new FenValue[] { promise.Get("__value__") }, null);
                        return res;
                    }

                    return promise.Get("__value__");
                }

                return FenValue.FromObject(promise);
            })));

            // catch(onRejected)
            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
            {
                var state = promise.Get("__state__").ToString();
                if (state == "rejected")
                {
                    if (args.Length > 0 && args[0].IsFunction)
                    {
                        var res = args[0].AsFunction().Invoke(new FenValue[] { promise.Get("__reason__") }, null);
                        return res;
                    }
                }

                return FenValue.FromObject(promise);
            })));

            return FenValue.FromObject(promise);
        }

        private FenValue ConvertNativeToFenValue(object obj)
        {
            if (obj == null) return FenValue.Null;
            if (obj is FenValue fenValue) return fenValue;
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
                throw new InvalidOperationException(
                    "Invalid property descriptor. Cannot both have accessors and value or writable attribute");
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

        private static byte[] TryExtractByteBuffer(FenValue value)
        {
            if (value.IsObject && value.AsObject() is FenObject obj)
            {
                if (obj.NativeObject is byte[] nativeBytes)
                    return nativeBytes;

                var bytesFromLength = ExtractIndexedBytes(obj);
                if (bytesFromLength != null)
                    return bytesFromLength;
            }

            return null;
        }

        private static byte[] ExtractIndexedBytes(FenObject obj)
        {
            if (obj == null)
                return null;

            int? length = null;
            var byteLength = obj.Get("byteLength");
            if (byteLength.IsNumber)
            {
                length = (int)byteLength.ToNumber();
            }
            else
            {
                var len = obj.Get("length");
                if (len.IsNumber)
                    length = (int)len.ToNumber();
            }

            if (length == null || length <= 0)
                return null;

            var bytes = new byte[length.Value];
            for (int i = 0; i < length.Value; i++)
            {
                var v = obj.Get(i.ToString());
                if (!v.IsNumber)
                    return null;
                bytes[i] = (byte)v.ToNumber();
            }

            return bytes;
        }

        private static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

        private static bool IsLikelyWasmBinary(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8)
                return false;

            // Magic: 00 61 73 6d
            if (bytes[0] != 0x00 || bytes[1] != 0x61 || bytes[2] != 0x73 || bytes[3] != 0x6d)
                return false;

            // Version 1: 01 00 00 00
            return bytes[4] == 0x01 && bytes[5] == 0x00 && bytes[6] == 0x00 && bytes[7] == 0x00;
        }

        #endregion
    }
}













































































































































































