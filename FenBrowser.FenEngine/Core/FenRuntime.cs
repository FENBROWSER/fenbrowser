using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.DOM;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Net.Http;
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
                try { System.IO.File.AppendAllText("debug_log.txt", $"[Console] {msg}\r\n"); } catch { }
                return FenValue.Undefined;
            })));

            SetGlobal("console", FenValue.FromObject(console));

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

        /// <summary>
        /// Execute JavaScript code using the FenEngine Parser and Interpreter
        /// </summary>
        public IValue ExecuteSimple(string code)
        {
            try
            {
                try { System.IO.File.AppendAllText("debug_log.txt", $"[FenRuntime] ExecuteSimple called with {code?.Length ?? 0} chars\r\n"); } catch { }
                Console.WriteLine($"[FenRuntime] ExecuteSimple called with {code?.Length ?? 0} chars");
                
                var lexer = new Lexer(code);
                var parser = new Parser(lexer);
                var program = parser.ParseProgram();

                if (parser.Errors.Count > 0)
                {
                    var errorMsg = string.Join("\n", parser.Errors);
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[FenRuntime] Parse Errors:\r\n{errorMsg}\r\n"); } catch { }
                    Console.WriteLine($"[FenRuntime] Parse Errors:\n{errorMsg}");
                    return new ErrorValue(errorMsg);
                }

                try { System.IO.File.AppendAllText("debug_log.txt", $"[FenRuntime] Parse succeeded. Statements: {program.Statements.Count}\r\n"); } catch { }
                Console.WriteLine($"[FenRuntime] Parse succeeded. Statements: {program.Statements.Count}");
                
                var interpreter = new Interpreter();
                var result = interpreter.Eval(program, _globalEnv, _context);

                if (result != null && result.Type == JsValueType.Error)
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[FenRuntime] Execution Error: {result}\r\n"); } catch { }
                    Console.WriteLine($"[FenRuntime] Execution Error: {result}");
                    return result;
                }

                try { System.IO.File.AppendAllText("debug_log.txt", $"[FenRuntime] Execution completed. Result type: {result?.Type}\r\n"); } catch { }
                Console.WriteLine($"[FenRuntime] Execution completed. Result type: {result?.Type}");
                return result ?? FenValue.Undefined;
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("debug_log.txt", $"[FenRuntime] Exception: {ex.Message}\r\n[FenRuntime] Stack trace: {ex.StackTrace}\r\n"); } catch { }
                Console.WriteLine($"[FenRuntime] Exception: {ex.Message}");
                Console.WriteLine($"[FenRuntime] Stack trace: {ex.StackTrace}");
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
    }
}
