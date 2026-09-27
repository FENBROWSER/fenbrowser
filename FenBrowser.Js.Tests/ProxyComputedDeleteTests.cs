using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>ECMA-262 10.5.10 [[Delete]] on a Proxy reached through a computed key.</summary>
public class ProxyComputedDeleteTests
{
    private static string Run(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void ComputedKeyDeleteRunsTheTrap()
    {
        Assert.Equal("true:undefined:0", Run("""
            var seen = [];
            var t = {}; Object.defineProperty(t, '0', { value: 1, configurable: true });
            var p = new Proxy(t, { deleteProperty: function (target, key) { seen.push(key); return Reflect.deleteProperty(target, key); } });
            var k = '0';
            String(delete p[k]) + ':' + String(p[0]) + ':' + seen.join();
            """));
    }

    [Fact]
    public void ComputedKeyDeleteWithoutATrapDeletesOnTheTarget()
    {
        Assert.Equal("true:undefined", Run("""
            var t = { 0: 1 };
            var p = new Proxy(t, {});
            var k = 0;
            String(delete p[k]) + ':' + String(t[0]);
            """));
    }

    [Fact]
    public void ComputedKeyDeleteRefusedByTheTrapThrowsInStrictCode()
    {
        Assert.Equal("TypeError", Run("""
            'use strict';
            var p = new Proxy({ 0: 1 }, { deleteProperty: function () { return false; } });
            var k = '0';
            var r; try { delete p[k]; r = 'no-throw'; } catch (e) { r = e.name; }
            r;
            """));
    }
}
