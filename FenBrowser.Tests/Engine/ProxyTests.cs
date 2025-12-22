using Xunit;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.FenEngine.Core.Interfaces;
using System.Collections.Generic;

using FenBrowser.FenEngine.Core.Interfaces;
using ValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

namespace FenBrowser.Tests.Engine
{
    public class ProxyTests
    {
        private readonly FenBrowser.FenEngine.Core.ExecutionContext _context;
        private readonly FenRuntime _runtime;

        public ProxyTests()
        {
            _context = new FenBrowser.FenEngine.Core.ExecutionContext();
            _runtime = new FenRuntime(_context);
            
            // CRITICAL: Set up ExecuteFunction delegate so user-defined functions work
            _context.ExecuteFunction = (fn, args) =>
            {
                var interpreter = new Interpreter();
                return interpreter.ApplyFunction(fn, new System.Collections.Generic.List<IValue>(args), _context);
            };
            
            // Manually register APIs as InitRuntime is internal to JavaScriptEngine
            _runtime.SetGlobal("Proxy", ProxyAPI.CreateProxyConstructor());
            _runtime.SetGlobal("Reflect", FenValue.FromObject(ReflectAPI.CreateReflectObject()));
        }

        private IValue Evaluate(string code)
        {
            var lexer = new Lexer(code);
            var parser = new Parser(lexer);
            var program = parser.ParseProgram();
            var interpreter = new Interpreter();
            return interpreter.Eval(program, _runtime.Context.Environment, _context);
        }

        [Fact]
        public void Proxy_Get_Trap_Intercepts()
        {
            // var target = { a: 1 };
            // var handler = { get: function(t, p, r) { return 42; } };
            // var p = new Proxy(target, handler);
            // p.a // Should be 42
            
            string script = @"
                var target = { a: 1 };
                var handler = { 
                    get: function(target, prop, receiver) { 
                        return 42; 
                    } 
                };
                var p = new Proxy(target, handler);
                p.a;
            ";
            
            var result = Evaluate(script);
            Assert.False(result.Type == ValueType.Error, $"Script failed: {result}");
            Assert.Equal(42.0, result.ToNumber());
        }

        [Fact]
        public void Proxy_Set_Trap_Intercepts()
        {
            // var target = { a: 1 };
            // var handler = { set: function(t, p, v, r) { t[p] = v * 2; return true; } };
            // var p = new Proxy(target, handler);
            // p.a = 10;
            // target.a // Should be 20
            
            string script = @"
                var target = { a: 1 };
                var handler = { 
                    set: function(target, prop, value, receiver) { 
                        target[prop] = value * 2;
                        return true; 
                    } 
                };
                var p = new Proxy(target, handler);
                p.a = 10;
                target.a;
            ";
            
            var result = Evaluate(script);
            Assert.False(result.Type == ValueType.Error, $"Script failed: {result}");
            Assert.Equal(20.0, result.ToNumber());
        }

        [Fact]
        public void Reflect_Get_Works()
        {
             string script = @"
                var obj = { x: 100 };
                Reflect.get(obj, 'x');
             ";
             var result = Evaluate(script);
             Assert.False(result.Type == ValueType.Error, $"Script failed: {result}");
             Assert.Equal(100.0, result.ToNumber());
        }

        [Fact]
        public void Proxy_Apply_Trap_Intercepts()
        {
             // Test function proxy
             string script = @"
                var target = function(x) { return x; };
                var handler = {
                    apply: function(target, thisArg, args) {
                        return args[0] + 1;
                    }
                };
                var p = new Proxy(target, handler);
                p(10);
             ";
             var result = Evaluate(script);
             Assert.False(result.Type == ValueType.Error, $"Script failed: {result}");
             Assert.Equal(11.0, result.ToNumber());
        }
    }
}
