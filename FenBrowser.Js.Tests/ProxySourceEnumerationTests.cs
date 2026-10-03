using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Object.assign (ECMA-262 20.1.2.1), object spread's CopyDataProperties (7.3.25),
/// JSON.stringify's SerializeJSONObject (25.5.2.5) and for-in's
/// EnumerateObjectProperties (14.7.5.9) read a Proxy source through its ownKeys,
/// getOwnPropertyDescriptor, get and getPrototypeOf traps. Each used to enumerate the
/// proxy's own storage, which threw a CLR "must be dispatched through the owning
/// interpreter" error that script could not catch - a framework spreading a
/// Proxy-backed props object lost the whole task.
/// </summary>
public sealed class ProxySourceEnumerationTests
{
    private static string RunString(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private const string TracedProxy =
        "var log = []; var sym = Symbol('s'); " +
        "var p = new Proxy({}, { " +
        "  ownKeys: function () { log.push('keys'); return ['a', sym, 'b', 'hidden']; }, " +
        "  getOwnPropertyDescriptor: function (t, k) { log.push('desc:' + String(k)); return { value: 1, enumerable: k !== 'hidden', configurable: true }; }, " +
        "  get: function (t, k) { log.push('get:' + String(k)); return 'v-' + String(k); }, " +
        "  getPrototypeOf: function () { log.push('proto'); return null; } " +
        "}); ";

    [Fact]
    public void ObjectAssign_ReadsEachKeyThroughTheTrapsInTrapOrder()
    {
        var result = RunString(TracedProxy +
            "var o = Object.assign({}, p); JSON.stringify(o) + '|' + o[sym] + '|' + log.join(',');");

        Assert.Equal(
            "{\"a\":\"v-a\",\"b\":\"v-b\"}|v-Symbol(s)|keys,desc:a,get:a,desc:Symbol(s),get:Symbol(s),desc:b,get:b,desc:hidden",
            result);
    }

    [Fact]
    public void ObjectSpread_ReadsEachKeyThroughTheTrapsInTrapOrder()
    {
        var result = RunString(TracedProxy +
            "var o = { ...p }; JSON.stringify(o) + '|' + o[sym] + '|' + log.join(',');");

        Assert.Equal(
            "{\"a\":\"v-a\",\"b\":\"v-b\"}|v-Symbol(s)|keys,desc:a,get:a,desc:Symbol(s),get:Symbol(s),desc:b,get:b,desc:hidden",
            result);
    }

    [Fact]
    public void JsonStringify_SerializesTheProxysEnumerableStringKeys()
    {
        var result = RunString(TracedProxy + "JSON.stringify(p) + '|' + log.join(',');");

        Assert.Equal(
            "{\"a\":\"v-a\",\"b\":\"v-b\"}|get:toJSON,keys,desc:a,desc:b,desc:hidden,get:a,get:b",
            result);
    }

    [Fact]
    public void ForIn_EnumeratesTheProxysEnumerableKeysAndAsksForItsPrototype()
    {
        var result = RunString(TracedProxy + "var seen = []; for (var k in p) seen.push(k); seen.join(',') + '|' + log.join(',');");

        Assert.Equal("a,b|keys,desc:a,desc:b,desc:hidden,proto", result);
    }

    [Fact]
    public void ThrowingTrap_IsCatchableByScript()
    {
        var result = RunString(
            "var p = new Proxy({}, { ownKeys: function () { throw new RangeError('nope'); } }); " +
            "var caught = []; " +
            "try { Object.assign({}, p); } catch (e) { caught.push(e.name); } " +
            "try { ({ ...p }); } catch (e) { caught.push(e.name); } " +
            "try { JSON.stringify(p); } catch (e) { caught.push(e.name); } " +
            "try { for (var k in p) {} } catch (e) { caught.push(e.name); } " +
            "caught.join(',');");

        Assert.Equal("RangeError,RangeError,RangeError,RangeError", result);
    }
}
