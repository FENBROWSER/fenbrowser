using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// A realm facade marks the functions it hands out with __realmGlobal__, and a
// call to one resolves its free names through that object. Answering it from
// the function's own shape means the answer is cached, so what these pin is
// that the cache still notices a function acquiring the marker.
public class RealmGlobalMarkerTests
{
    private const string Mark =
        "Object.defineProperty(f, '__realmGlobal__', " +
        "{ value: realmGlobal, writable: false, enumerable: false, configurable: true });";

    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void AMarkedFunction_ResolvesNamesThroughItsRealmGlobal()
    {
        var v = Run($@"
            var realmGlobal = {{ marker: 42 }};
            function f() {{ return typeof marker === 'undefined' ? -1 : marker; }}
            {Mark}
            String(f());
        ");
        Assert.Equal("42", v);
    }

    [Fact]
    public void MarkingAFunctionAfterItHasRun_TakesEffect()
    {
        // The first call caches "this shape carries no marker"; defining the
        // marker changes the shape, so the second call has to look again.
        var v = Run($@"
            var realmGlobal = {{ marker: 42 }};
            function f() {{ return typeof marker === 'undefined' ? -1 : marker; }}
            var before = f();
            {Mark}
            var after = f();
            before + ',' + after;
        ");
        Assert.Equal("-1,42", v);
    }

    [Fact]
    public void AnUnmarkedFunction_KeepsItsOwnOuterScope()
    {
        var v = Run($@"
            var realmGlobal = {{ marker: 42 }};
            function f() {{ return typeof marker === 'undefined' ? -1 : marker; }}
            {Mark}
            f();
            function g() {{ return typeof marker === 'undefined' ? -1 : marker; }}
            String(g());
        ");
        Assert.Equal("-1", v);
    }

    [Fact]
    public void TheMarkerSurvivesTierUp()
    {
        var v = Run($@"
            var realmGlobal = {{ marker: 42 }};
            function f() {{ return typeof marker === 'undefined' ? -1 : marker; }}
            {Mark}
            var total = 0;
            for (var i = 0; i < 2000; i++) total += f();
            String(total);
        ");
        Assert.Equal("84000", v);
    }
}
