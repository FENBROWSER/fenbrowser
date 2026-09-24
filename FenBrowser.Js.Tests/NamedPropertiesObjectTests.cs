using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NamedPropertiesObjectTests
{
    // A Window-shaped chain: global -> WindowPrototype -> [named] -> Object.prototype,
    // with "target" supported until the host removes it.
    private static (BytecodeInterpreter Interpreter, Func<bool> Supported, Action<bool> SetSupported) CreateWindowLikeGlobal()
    {
        var interpreter = new BytecodeInterpreter(new JsHeap());
        var supported = true;
        var windowPrototype = Run(interpreter, @"
            var WindowPrototype = Object.create(Object.getPrototypeOf(globalThis));
            WindowPrototype.onWindowPrototype = 'interface member';
            Object.setPrototypeOf(globalThis, WindowPrototype);
            WindowPrototype;");
        interpreter.InsertNamedPropertiesObject(
            windowPrototype,
            "WindowProperties",
            name => supported && (name == "target" || name == "onWindowPrototype")
                ? JsValue.FromString("named " + name)
                : null);
        return (interpreter, () => supported, value => supported = value);
    }

    private static JsValue Run(BytecodeInterpreter interpreter, string code)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(code, "named-properties.js"));
        new BytecodeVerifier().Verify(function);
        return interpreter.Execute(function);
    }

    [Fact]
    public void NamedPropertiesResolveAsPropertiesAndAsGlobalIdentifiers()
    {
        var (interpreter, _, _) = CreateWindowLikeGlobal();

        Assert.Equal("named target|named target|true|true",
            Run(interpreter, "[globalThis.target, target, 'target' in globalThis, typeof target === 'string'].join('|')").AsString());
    }

    [Fact]
    public void InterfaceMembersAndOwnPropertiesShadowNamedProperties()
    {
        var (interpreter, _, _) = CreateWindowLikeGlobal();

        Assert.Equal("interface member|own",
            Run(interpreter, "var target = 'own'; [onWindowPrototype, target].join('|')").AsString());
    }

    [Fact]
    public void PropertiesAboveTheNamedPropertiesObjectHideTheName()
    {
        var (interpreter, _, _) = CreateWindowLikeGlobal();

        Assert.Equal("from Object.prototype|undefined",
            Run(interpreter, @"
                Object.prototype.target = 'from Object.prototype';
                var named = Object.getPrototypeOf(Object.getPrototypeOf(globalThis));
                [target, String(Object.getOwnPropertyDescriptor(named, 'target'))].join('|');").AsString());
    }

    [Fact]
    public void NamedPropertiesFollowTheHostOnEveryLookup()
    {
        var (interpreter, _, setSupported) = CreateWindowLikeGlobal();
        const string read = "function readTarget() { return globalThis.target; } readTarget(); readTarget();";
        Assert.Equal("named target", Run(interpreter, read).AsString());

        setSupported(false);

        Assert.Equal("undefined|undefined|threw",
            Run(interpreter, @"
                var direct = String(readTarget());
                var typed = typeof target;
                var bare;
                try { target; bare = 'read'; } catch (e) { bare = e instanceof ReferenceError ? 'threw' : 'wrong'; }
                [direct, typed, bare].join('|');").AsString());
    }

    [Fact]
    public void NamedPropertiesObjectFollowsWebIdlLegacyPlatformObjectRules()
    {
        var (interpreter, _, _) = CreateWindowLikeGlobal();

        Assert.Equal("WindowProperties|false|false|true|false|named target|false",
            Run(interpreter, @"
                var named = Object.getPrototypeOf(Object.getPrototypeOf(globalThis));
                var d = Object.getOwnPropertyDescriptor(named, 'target');
                var defined = Reflect.defineProperty(named, 'other', { value: 1 });
                [
                    Object.prototype.toString.call(named).slice(8, -1),
                    Object.keys(named).indexOf('target') >= 0,
                    d.enumerable,
                    d.writable && d.configurable,
                    defined,
                    Reflect.deleteProperty(named, 'target') ? 'deleted' : named.target,
                    Object.keys(globalThis).indexOf('target') >= 0
                ].join('|');").AsString());
    }

    [Fact]
    public void AssigningANamedPropertyCreatesAnOwnGlobalProperty()
    {
        var (interpreter, _, _) = CreateWindowLikeGlobal();

        Assert.Equal("replaced|true|named target",
            Run(interpreter, @"
                globalThis.target = 'replaced';
                var named = Object.getPrototypeOf(Object.getPrototypeOf(globalThis));
                [target, Object.prototype.hasOwnProperty.call(globalThis, 'target'), named.target].join('|');").AsString());
    }
}
