using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Symbol-keyed operations must reach the Proxy handler and honour the same
/// [[GetOwnProperty]] invariants as string keys (audit JSRT-010), and one
/// [[Delete]] must ask the target for its descriptor exactly once (JSRT-009).
/// </summary>
public sealed class ProxySymbolKeyTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    private static bool RunBool(string source) => Run(source).AsBoolean();

    private static string RunString(string source) => Run(source).AsString();

    private const string Sym = "var s = Symbol('s'); ";

    [Theory]
    // Each trap returns a result its own invariant accepts on an empty target:
    // getOwnPropertyDescriptor must return an object or undefined, the rest a
    // boolean.
    [InlineData("get", "return 1;", "p[s];")]
    [InlineData("set", "return true;", "p[s] = 1;")]
    [InlineData("has", "return true;", "s in p;")]
    [InlineData("deleteProperty", "return true;", "delete p[s];")]
    [InlineData("getOwnPropertyDescriptor", "return undefined;", "Object.getOwnPropertyDescriptor(p, s);")]
    [InlineData("defineProperty", "return true;", "Object.defineProperty(p, s, { value: 1 });")]
    public void SymbolKeyedOperationReachesTheTrap(string trap, string trapBody, string operation)
    {
        Assert.True(RunBool(Sym +
            "var hit = false; " +
            "var p = new Proxy({}, { " + trap + "() { hit = true; " + trapBody + " } }); " +
            operation + " hit;"));
    }

    [Theory]
    [InlineData("Reflect.set(p, s, 1);", "set")]
    [InlineData("Reflect.deleteProperty(p, s);", "deleteProperty")]
    [InlineData("Reflect.has(p, s);", "has")]
    [InlineData("Reflect.get(p, s);", "get")]
    public void ReflectRoutesSymbolKeysThroughTheTrap(string operation, string trap)
    {
        Assert.True(RunBool(Sym +
            "var hit = false; " +
            "var p = new Proxy({}, { " + trap + "() { hit = true; return true; } }); " +
            operation + " hit;"));
    }

    // A Proxy trap's invariant TypeError used to escape the interpreter past
    // any enclosing JS try/catch, because the `in` opcode and the symbol-keyed
    // element store ran outside a handler.
    [Theory]
    [InlineData("s in p")]
    [InlineData("p[s] = 2")]
    public void TrapInvariantViolationIsCatchableFromJavaScript(string operation)
    {
        Assert.Equal("TypeError", RunString(Sym +
            "var t = {}; Object.defineProperty(t, s, { value: 1, writable: false, configurable: false }); " +
            "var p = new Proxy(t, { has() { return false; }, set() { return true; } }); " +
            "try { " + operation + "; 'no-throw'; } catch (e) { e.name; }"));
    }

    [Fact]
    public void GetTrapMustReturnTheTargetValueForNonWritableNonConfigurableSymbolProperty()
    {
        Assert.Equal("TypeError", RunString(Sym +
            "var t = {}; Object.defineProperty(t, s, { value: 1, writable: false, configurable: false }); " +
            "var p = new Proxy(t, { get() { return 99; } }); " +
            "try { p[s]; 'no-throw'; } catch (e) { e.name; }"));
    }

    [Fact]
    public void SetTrapCannotReportSuccessForNonWritableNonConfigurableSymbolProperty()
    {
        Assert.Equal("TypeError", RunString(Sym +
            "var t = {}; Object.defineProperty(t, s, { value: 1, writable: false, configurable: false }); " +
            "var p = new Proxy(t, { set() { return true; } }); " +
            "try { p[s] = 2; 'no-throw'; } catch (e) { e.name; }"));
    }

    [Fact]
    public void GetOwnPropertyDescriptorTrapCannotHideANonConfigurableSymbolProperty()
    {
        Assert.Equal("TypeError", RunString(Sym +
            "var t = {}; Object.defineProperty(t, s, { value: 1, writable: false, configurable: false }); " +
            "var p = new Proxy(t, { getOwnPropertyDescriptor() { return undefined; } }); " +
            "try { Object.getOwnPropertyDescriptor(p, s); 'no-throw'; } catch (e) { e.name; }"));
    }

    [Fact]
    public void HasTrapCannotHideANonConfigurableSymbolProperty()
    {
        Assert.Equal("TypeError", RunString(Sym +
            "var t = {}; Object.defineProperty(t, s, { value: 1, writable: false, configurable: false }); " +
            "var p = new Proxy(t, { has() { return false; } }); " +
            "try { s in p; 'no-throw'; } catch (e) { e.name; }"));
    }

    [Fact]
    public void SymbolKeyedDefinePropertyWithoutATrapReachesTheTarget()
    {
        Assert.True(RunBool(Sym +
            "var t = {}; var p = new Proxy(t, {}); " +
            "Object.defineProperty(p, s, { value: 7, configurable: true }); t[s] === 7;"));
    }

    [Fact]
    public void SymbolKeyedSetWithoutATrapReachesTheTarget()
    {
        Assert.True(RunBool(Sym + "var t = {}; var p = new Proxy(t, {}); p[s] = 7; t[s] === 7;"));
    }

    [Fact]
    public void DeleteAsksTheTargetForItsDescriptorExactlyOnce()
    {
        // The invariant checks used to call target.[[GetOwnProperty]] twice,
        // which fired a nested proxy's trap twice for a single delete.
        Assert.Equal("gopd:a", RunString(
            "var calls = []; " +
            "var inner = new Proxy({ a: 1 }, { getOwnPropertyDescriptor(t, k) { " +
            "  calls.push('gopd:' + String(k)); return Reflect.getOwnPropertyDescriptor(t, k); } }); " +
            "var p = new Proxy(inner, { deleteProperty() { return true; } }); " +
            "delete p.a; calls.join(',');"));
    }
}
