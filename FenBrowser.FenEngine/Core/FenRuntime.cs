using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.DOM;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
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

            // navigator object
            var navigator = new FenObject();
            navigator.Set("userAgent", FenValue.FromString("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 FenBrowser/1.0"));
            navigator.Set("platform", FenValue.FromString("Win32"));
            navigator.Set("language", FenValue.FromString("en-US"));
            navigator.Set("cookieEnabled", FenValue.FromBoolean(true));
            SetGlobal("navigator", FenValue.FromObject(navigator));

            // location object (basic)
            var location = new FenObject();
            location.Set("href", FenValue.FromString("http://localhost:8000/"));
            location.Set("protocol", FenValue.FromString("http:"));
            location.Set("host", FenValue.FromString("localhost:8000"));
            location.Set("hostname", FenValue.FromString("localhost"));
            location.Set("pathname", FenValue.FromString("/"));
            SetGlobal("location", FenValue.FromObject(location));

            // window object (circular reference to global scope simulation)
            var window = new FenObject();
            window.Set("console", FenValue.FromObject(console));
            window.Set("navigator", FenValue.FromObject(navigator));
            window.Set("location", FenValue.FromObject(location));
            SetGlobal("window", FenValue.FromObject(window));

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
                    else dt = DateTime.Parse(arg.ToString());
                }
                else dt = DateTime.Now; // Simplified for multiple args

                var obj = new FenObject();
                obj.NativeObject = dt;
                obj.SetPrototype(dateProto);
                return FenValue.FromObject(obj);
            });
            
            // Static methods
            var dateObj = FenValue.FromFunction(dateCtor);
            dateObj.AsObject().Set("now", FenValue.FromFunction(new FenFunction("now", (args, thisVal) => 
                FenValue.FromNumber((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds))));
            dateObj.AsObject().Set("parse", FenValue.FromFunction(new FenFunction("parse", (args, thisVal) => {
                if (args.Length > 0 && DateTime.TryParse(args[0].ToString(), out var d))
                    return FenValue.FromNumber((d.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
                return FenValue.FromNumber(double.NaN);
            })));

            SetGlobal("Date", dateObj);

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
    }
}
