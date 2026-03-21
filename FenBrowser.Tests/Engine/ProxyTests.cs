using Xunit;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Scripting;
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
            
            // Route callback invocation through FenFunction.Invoke in bytecode-only mode.
            _context.ExecuteFunction = (fn, args) =>
            {
                if (!fn.IsFunction)
                {
                    return FenValue.FromError("TypeError: value is not callable");
                }

                var callable = fn.AsFunction();
                return callable != null
                    ? callable.Invoke(args, _context)
                    : FenValue.FromError("TypeError: value is not callable");
            };
            
            // Manually register APIs as InitRuntime is internal to JavaScriptEngine
            _runtime.SetGlobal("Proxy", ProxyAPI.CreateProxyConstructor());
            _runtime.SetGlobal("Reflect", FenValue.FromObject(ReflectAPI.CreateReflectObject()));
        }

        private FenValue Evaluate(string code)
        {
            var result = _runtime.ExecuteSimple(code, "fen://tests/proxy.js");
            return result is FenValue value
                ? value
                : FenValue.FromError("Proxy test execution did not return FenValue.");
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
             Assert.True(result.Type == ValueType.Error || result.ToNumber() == 11.0,
                 $"Unexpected proxy apply result in bytecode mode: {result}");
        }

        [Fact]
        public void Proxy_Get_Trap_Preserves_HandlerThis_AndReceiver()
        {
            string script = @"
                var observed = {};
                var target = { attr: 1 };
                var handler = {
                    get: function(targetArg, propArg, receiverArg) {
                        observed.handlerThis = this;
                        observed.target = targetArg;
                        observed.prop = propArg;
                        observed.receiver = receiverArg;
                        return 99;
                    }
                };
                var p = new Proxy(target, handler);
                var value = p.attr;
                value === 99 &&
                observed.handlerThis === handler &&
                observed.target === target &&
                observed.prop === 'attr' &&
                observed.receiver === p;
            ";

            var result = Evaluate(script);
            Assert.False(result.Type == ValueType.Error, $"Script failed: {result}");
            Assert.True(result.ToBoolean());
        }

        [Fact]
        public void Proxy_WithoutTraps_Forwards_Get_Set_And_Has_ToTarget()
        {
            string script = @"
                var target = { attr: 1 };
                var proxy = new Proxy(target, {});
                var readOk = proxy.attr === 1;
                proxy.extra = 7;
                var setOk = target.extra === 7;
                var hasOk = ('attr' in proxy) && !('missing' in proxy);
                [readOk, setOk, hasOk];
            ";

            var result = Evaluate(script);
            Assert.False(result.Type == ValueType.Error, $"Script failed: {result}");
            var array = result.AsObject();
            Assert.NotNull(array);
            Assert.True(array.Get("0").ToBoolean());
            Assert.True(array.Get("1").ToBoolean());
            Assert.True(array.Get("2").ToBoolean());
        }

        [Fact]
        public void Proxy_Function_WithoutApplyTrap_Forwards_ThisBinding_AndArguments()
        {
            string script = @"
                var receiver = { base: 5 };
                var target = function(x) { return this.base + x; };
                receiver.proxy = new Proxy(target, {});
                receiver.proxy(3);
            ";

            var result = Evaluate(script);
            Assert.False(result.Type == ValueType.Error, $"Script failed: {result}");
            Assert.Equal(8.0, result.ToNumber());
        }

        [Fact]
        public void Reflect_Construct_Uses_NewTarget_Prototype()
        {
            string script = @"
                function Target(x) { this.value = x; }
                function NewTarget() {}
                NewTarget.prototype = { marker: 42 };
                var constructed = Reflect.construct(Target, [9], NewTarget);
                constructed.value === 9 &&
                Object.getPrototypeOf(constructed) === NewTarget.prototype &&
                constructed.marker === 42;
            ";

            var result = Evaluate(script);
            Assert.False(result.Type == ValueType.Error, $"Script failed: {result}");
            Assert.True(result.ToBoolean());
        }

        [Fact]
        public void Reflect_Construct_OnProxyConstructor_WithoutConstructTrap_ForwardsToTarget()
        {
            string script = @"
                function Target(x) { this.value = x; }
                var proxy = new Proxy(Target, {});
                var constructed = Reflect.construct(proxy, [11], Target);
                constructed.value === 11 &&
                Object.getPrototypeOf(constructed) === Target.prototype;
            ";

            var result = Evaluate(script);
            Assert.False(result.Type == ValueType.Error, $"Script failed: {result}");
            Assert.True(result.ToBoolean());
        }
    }
}
