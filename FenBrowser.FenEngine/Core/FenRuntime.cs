using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.DOM;
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
            InitializeBuiltins();
        }

        public Action RequestRender
        {
            get => _context.RequestRender;
            set => _context.RequestRender = value;
        }

        private void InitializeBuiltins()
        {
            // console object
            var console = new FenObject();
            console.Set("log", FenValue.FromFunction(new FenFunction("log", args =>
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
