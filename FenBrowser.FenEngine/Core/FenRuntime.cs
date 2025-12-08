using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.DOM;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// FenEngine JavaScript runtime - manages global scope and execution context
    /// </summary>
    public class FenRuntime
    {
        private readonly FenEnvironment _globalEnv;
        private readonly IExecutionContext _context;
        private readonly Dictionary<int, CancellationTokenSource> _activeTimers = new Dictionary<int, CancellationTokenSource>();
        private int _timerIdCounter = 1;
        private readonly object _timerLock = new object();

        public FenRuntime(IExecutionContext context = null)
        {
            _context = context ?? new ExecutionContext();
            _globalEnv = new FenEnvironment();
            
            // Initialize module loader
            _context.ModuleLoader = new ModuleLoader(_globalEnv, _context);
            
            InitializeBuiltins();
        }

        public Action RequestRender
        {
            get => _context.RequestRender;
            set => _context.SetRequestRender(value);
        }

        public IValue ExecuteFunction(FenFunction func, IValue[] args)
        {
            if (_context.ExecuteFunction != null)
            {
                return _context.ExecuteFunction(FenValue.FromFunction(func), args);
            }
            return func.Invoke(args, _context);
        }

        private void InitializeBuiltins()
        {
            // console object
            var console = new FenObject();
            console.Set("log", FenValue.FromFunction(new FenFunction("log", (args, thisVal) =>
            {
                var messages = new List<string>();
                foreach (var arg in args)
                {
                    messages.Add(arg.ToString());
                }
                var msg = string.Join(" ", messages);
                Console.WriteLine(msg);
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Console] {msg}\r\n"); } catch { }
                return FenValue.Undefined;
            })));
            console.Set("error", FenValue.FromFunction(new FenFunction("error", (args, thisVal) =>
            {
                var msg = string.Join(" ", args.Select(a => a.ToString()));
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR: {msg}");
                Console.ResetColor();
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Console Error] {msg}\r\n"); } catch { }
                return FenValue.Undefined;
            })));
            console.Set("warn", FenValue.FromFunction(new FenFunction("warn", (args, thisVal) =>
            {
                var msg = string.Join(" ", args.Select(a => a.ToString()));
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"WARN: {msg}");
                Console.ResetColor();
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Console Warn] {msg}\r\n"); } catch { }
                return FenValue.Undefined;
            })));
            console.Set("info", FenValue.FromFunction(new FenFunction("info", (args, thisVal) =>
            {
                var msg = string.Join(" ", args.Select(a => a.ToString()));
                Console.WriteLine($"INFO: {msg}");
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Console Info] {msg}\r\n"); } catch { }
                return FenValue.Undefined;
            })));
            console.Set("clear", FenValue.FromFunction(new FenFunction("clear", (args, thisVal) =>
            {
                Console.Clear();
                return FenValue.Undefined;
            })));

            SetGlobal("console", FenValue.FromObject(console));

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

            // undefined and null
            SetGlobal("undefined", FenValue.Undefined);
            SetGlobal("null", FenValue.Null);

            // navigator object - Privacy-focused (generic values to prevent fingerprinting)
            var navigator = new FenObject();
            navigator.Set("userAgent", FenValue.FromString("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:146.0) Gecko/20100101 Firefox/146.0 FenBrowser/1.0"));
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
            
            // Network Information Spoofing
            var connection = new FenObject();
            connection.Set("effectiveType", FenValue.FromString("4g"));
            connection.Set("rtt", FenValue.FromNumber(50));
            connection.Set("downlink", FenValue.FromNumber(10));
            connection.Set("saveData", FenValue.FromBoolean(false));
            navigator.Set("connection", FenValue.FromObject(connection));

            SetGlobal("navigator", FenValue.FromObject(navigator));

            // location object (basic)
            var location = new FenObject();
            location.Set("href", FenValue.FromString("http://localhost:8000/"));
            location.Set("protocol", FenValue.FromString("http:"));
            location.Set("host", FenValue.FromString("localhost:8000"));
            location.Set("hostname", FenValue.FromString("localhost"));
            location.Set("pathname", FenValue.FromString("/"));
            SetGlobal("location", FenValue.FromObject(location));

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

            // localStorage - Modular storage implementation
            var localStorage = CreateStorageObject("localStorage");
            SetGlobal("localStorage", FenValue.FromObject(localStorage));

            // sessionStorage - Modular storage implementation
            var sessionStorage = CreateStorageObject("sessionStorage");
            SetGlobal("sessionStorage", FenValue.FromObject(sessionStorage));

            // window object - Comprehensive with all standard properties
            var window = new FenObject();
            window.Set("console", FenValue.FromObject(console));
            window.Set("navigator", FenValue.FromObject(navigator));
            window.Set("location", FenValue.FromObject(location));
            window.Set("screen", FenValue.FromObject(screen));
            window.Set("localStorage", FenValue.FromObject(localStorage));
            window.Set("sessionStorage", FenValue.FromObject(sessionStorage));
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

            // RegExp constructor
            SetGlobal("RegExp", FenValue.FromFunction(new FenFunction("RegExp", (args, thisVal) =>
            {
                var pattern = args.Length > 0 ? args[0].ToString() : "";
                var flags = args.Length > 1 ? args[1].ToString() : "";
                
                try
                {
                    var options = RegexOptions.None;
                    if (flags.Contains("i")) options |= RegexOptions.IgnoreCase;
                    if (flags.Contains("m")) options |= RegexOptions.Multiline;
                    
                    var r = new Regex(pattern, options);
                    var obj = new FenObject();
                    obj.NativeObject = r;
                    obj.Set("source", FenValue.FromString(pattern));
                    obj.Set("flags", FenValue.FromString(flags));
                    obj.Set("lastIndex", FenValue.FromNumber(0));
                    
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
                FenValue.FromNumber(new Random().NextDouble()))));
            math.Set("sin", FenValue.FromFunction(new FenFunction("sin", (args, thisVal) => 
                FenValue.FromNumber(Math.Sin(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("cos", FenValue.FromFunction(new FenFunction("cos", (args, thisVal) => 
                FenValue.FromNumber(Math.Cos(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            math.Set("tan", FenValue.FromFunction(new FenFunction("tan", (args, thisVal) => 
                FenValue.FromNumber(Math.Tan(args.Length > 0 ? args[0].ToNumber() : double.NaN)))));
            
            SetGlobal("Math", FenValue.FromObject(math));

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

            SetGlobal("Date", FenValue.FromObject(dateObj));

            // JSON object
            var json = new FenObject();
            json.Set("parse", FenValue.FromFunction(new FenFunction("parse", (args, thisVal) => {
                if (args.Length == 0) return new ErrorValue("JSON.parse: no argument");
                try
                {
                    var jsonString = args[0].ToString();
                    using var doc = JsonDocument.Parse(jsonString);
                    return ConvertJsonElement(doc.RootElement);
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
                    return FenValue.FromString(ConvertToJsonString(args[0]));
                }
                catch (Exception ex)
                {
                    return new ErrorValue($"JSON.stringify error: {ex.Message}");
                }
            })));
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
            SetGlobal("Object", FenValue.FromObject(objectConstructor));

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
        public void SetDom(LiteElement root)
        {
            if (root == null) return;

            var documentWrapper = new DocumentWrapper(root, _context);
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
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[DispatchEvent Error] {ex.Message}\r\n"); } catch { }
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
                   try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Timer Error] {ex.Message}\r\n"); } catch { }
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
        public IValue ExecuteSimple(string code)
        {
            try
            {
                var lexer = new Lexer(code);
                var parser = new Parser(lexer);
                var program = parser.ParseProgram();

                if (parser.Errors.Count > 0)
                {
                    return new ErrorValue(string.Join("\n", parser.Errors));
                }
                
                var interpreter = new Interpreter();
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
        private readonly Dictionary<string, Dictionary<string, string>> _storageData = new Dictionary<string, Dictionary<string, string>>()
        {
            { "localStorage", new Dictionary<string, string>() },
            { "sessionStorage", new Dictionary<string, string>() }
        };

        /// <summary>
        /// Create Storage object (localStorage/sessionStorage) - Secure: in-memory only
        /// </summary>
        private FenObject CreateStorageObject(string storageType)
        {
            var storage = new FenObject();
            
            // getItem(key) - Returns value or null
            storage.Set("getItem", FenValue.FromFunction(new FenFunction("getItem", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Null;
                var key = args[0].ToString();
                if (_storageData[storageType].TryGetValue(key, out var value))
                    return FenValue.FromString(value);
                return FenValue.Null;
            })));

            // setItem(key, value) - Stores value
            storage.Set("setItem", FenValue.FromFunction(new FenFunction("setItem", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var key = args[0].ToString();
                var value = args[1].ToString();
                _storageData[storageType][key] = value;
                return FenValue.Undefined;
            })));

            // removeItem(key) - Removes item
            storage.Set("removeItem", FenValue.FromFunction(new FenFunction("removeItem", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                var key = args[0].ToString();
                _storageData[storageType].Remove(key);
                return FenValue.Undefined;
            })));

            // clear() - Clears all items
            storage.Set("clear", FenValue.FromFunction(new FenFunction("clear", (args, thisVal) =>
            {
                _storageData[storageType].Clear();
                return FenValue.Undefined;
            })));

            // key(index) - Returns key at index
            storage.Set("key", FenValue.FromFunction(new FenFunction("key", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Null;
                var index = (int)args[0].ToNumber();
                var keys = _storageData[storageType].Keys.ToList();
                if (index >= 0 && index < keys.Count)
                    return FenValue.FromString(keys[index]);
                return FenValue.Null;
            })));

            // length property (getter-like behavior through initial value)
            storage.Set("length", FenValue.FromNumber(_storageData[storageType].Count));

            return storage;
        }

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
                    
                    // Create Response object
                    var responseObj = CreateResponse(url, (int)response.StatusCode, response.ReasonPhrase, responseText);
                    
                    // Call all then callbacks
                    foreach (var callback in thenCallbacks)
                    {
                        try
                        {
                            if (callback.IsNative && callback.NativeImplementation != null)
                                callback.NativeImplementation(new IValue[] { responseObj }, FenValue.Undefined);
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    // Call all catch callbacks
                    foreach (var callback in catchCallbacks)
                    {
                        try
                        {
                            if (callback.IsNative && callback.NativeImplementation != null)
                                callback.NativeImplementation(new IValue[] { FenValue.FromString(ex.Message) }, FenValue.Undefined);
                        }
                        catch { }
                    }
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

        // In-memory storage for IndexedDB databases
        private static readonly Dictionary<string, Dictionary<string, Dictionary<string, IValue>>> _idbDatabases = 
            new Dictionary<string, Dictionary<string, Dictionary<string, IValue>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Creates the indexedDB global object (IDBFactory)
        /// </summary>
        private FenObject CreateIndexedDB()
        {
            var idb = new FenObject();
            
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
                    
                    bool isNew = !_idbDatabases.ContainsKey(dbName);
                    if (isNew)
                    {
                        _idbDatabases[dbName] = new Dictionary<string, Dictionary<string, IValue>>(StringComparer.OrdinalIgnoreCase);
                    }
                    
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
                                evt.Set("oldVersion", FenValue.FromNumber(0));
                                evt.Set("newVersion", FenValue.FromNumber(version));
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
                _idbDatabases.Remove(dbName);
                
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
                
                if (!_idbDatabases.ContainsKey(name))
                    _idbDatabases[name] = new Dictionary<string, Dictionary<string, IValue>>(StringComparer.OrdinalIgnoreCase);
                
                if (!_idbDatabases[name].ContainsKey(storeName))
                    _idbDatabases[name][storeName] = new Dictionary<string, IValue>(StringComparer.OrdinalIgnoreCase);
                
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
            
            // Ensure store exists
            if (!_idbDatabases.ContainsKey(dbName))
                _idbDatabases[dbName] = new Dictionary<string, Dictionary<string, IValue>>(StringComparer.OrdinalIgnoreCase);
            if (!_idbDatabases[dbName].ContainsKey(storeName))
                _idbDatabases[dbName][storeName] = new Dictionary<string, IValue>(StringComparer.OrdinalIgnoreCase);
            
            var storeData = _idbDatabases[dbName][storeName];
            
            // add(value, key) - Adds a value
            store.Set("add", FenValue.FromFunction(new FenFunction("add", (args, thisVal) =>
            {
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                var key = args.Length > 1 ? args[1].ToString() : Guid.NewGuid().ToString();
                
                storeData[key] = value;
                
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
                storeData.TryGetValue(key, out var value);
                request.Set("result", value ?? FenValue.Undefined);
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
            
            // put(value, key) - Updates or adds a value
            store.Set("put", FenValue.FromFunction(new FenFunction("put", (args, thisVal) =>
            {
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                var key = args.Length > 1 ? args[1].ToString() : Guid.NewGuid().ToString();
                
                storeData[key] = value;
                
                var request = new FenObject();
                request.Set("result", FenValue.FromString(key));
                request.Set("onsuccess", FenValue.Null);
                
                return FenValue.FromObject(request);
            })));
            
            // delete(key) - Deletes a value
            store.Set("delete", FenValue.FromFunction(new FenFunction("delete", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0].ToString() : "";
                storeData.Remove(key);
                
                var request = new FenObject();
                request.Set("onsuccess", FenValue.Null);
                return FenValue.FromObject(request);
            })));
            
            // clear() - Clears all values
            store.Set("clear", FenValue.FromFunction(new FenFunction("clear", (args, thisVal) =>
            {
                storeData.Clear();
                
                var request = new FenObject();
                request.Set("onsuccess", FenValue.Null);
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
            
            // Promise.resolve(value) - Creates a resolved promise
            promiseCtor.Set("resolve", FenValue.FromFunction(new FenFunction("resolve", (args, thisVal) =>
            {
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                return CreateResolvedPromise(value);
            })));
            
            // Promise.reject(reason) - Creates a rejected promise  
            promiseCtor.Set("reject", FenValue.FromFunction(new FenFunction("reject", (args, thisVal) =>
            {
                var reason = args.Length > 0 ? args[0].ToString() : "";
                return CreateRejectedPromise(reason);
            })));
            
            // Promise.all(iterable) - Waits for all promises
            promiseCtor.Set("all", FenValue.FromFunction(new FenFunction("all", (args, thisVal) =>
            {
                // Simplified: immediately resolve with the array
                var arr = args.Length > 0 ? args[0] : FenValue.Undefined;
                return CreateResolvedPromise(arr);
            })));
            
            // Promise.race(iterable) - Returns first settled promise
            promiseCtor.Set("race", FenValue.FromFunction(new FenFunction("race", (args, thisVal) =>
            {
                // Simplified: immediately resolve with first element
                if (args.Length > 0 && args[0].IsObject)
                {
                    var arrObj = args[0].AsObject() as FenObject;
                    var first = arrObj?.Get("0");
                    if (first != null && !first.IsUndefined)
                        return CreateResolvedPromise(first);
                }
                return CreateResolvedPromise(FenValue.Undefined);
            })));

            // Promise.allSettled(iterable)
            promiseCtor.Set("allSettled", FenValue.FromFunction(new FenFunction("allSettled", (args, thisVal) =>
            {
                var arr = args.Length > 0 ? args[0] : FenValue.Undefined;
                return CreateResolvedPromise(arr);
            })));
            
            return promiseCtor;
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

        #endregion
    }
}
