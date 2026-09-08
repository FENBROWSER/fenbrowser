using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Symbol-keyed properties are stored in a small array beside the string-keyed
// ones rather than a dictionary, with accessor halves in the same side table.
// What that has to preserve: attributes, both descriptor forms, creation order,
// deletion, and the collector reaching what a symbol slot holds.
public class SymbolPropertyStorageTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void ASymbolDataProperty_KeepsItsAttributes()
    {
        var v = Run(@"
            var s = Symbol('k');
            var o = {};
            Object.defineProperty(o, s, { value: 5, writable: false, enumerable: true, configurable: false });
            var d = Object.getOwnPropertyDescriptor(o, s);
            [d.value, d.writable, d.enumerable, d.configurable].join(',');
        ");
        Assert.Equal("5,false,true,false", v);
    }

    [Fact]
    public void ASymbolAccessor_KeepsBothHalves()
    {
        var v = Run(@"
            var s = Symbol('k');
            var o = {};
            function g() { return 'got'; }
            function st(v) { }
            Object.defineProperty(o, s, { get: g, set: st, configurable: true });
            var d = Object.getOwnPropertyDescriptor(o, s);
            [d.get === g, d.set === st, o[s]].join(',');
        ");
        Assert.Equal("true,true,got", v);
    }

    [Fact]
    public void SymbolKeys_EnumerateInCreationOrder()
    {
        var v = Run(@"
            var a = Symbol('a'), b = Symbol('b'), c = Symbol('c');
            var o = {};
            o[b] = 1; o[a] = 2; o[c] = 3;
            var keys = Object.getOwnPropertySymbols(o);
            [keys.length, keys[0] === b, keys[1] === a, keys[2] === c].join(',');
        ");
        Assert.Equal("3,true,true,true", v);
    }

    [Fact]
    public void RedefiningASymbolAcrossTheAccessorBoundary_WorksBothWays()
    {
        var v = Run(@"
            var s = Symbol('k');
            var o = {};
            Object.defineProperty(o, s, { get: function () { return 'via-getter'; }, configurable: true });
            var first = o[s];
            Object.defineProperty(o, s, { value: 'plain', writable: true, configurable: true });
            var second = o[s] + ',' + ('get' in Object.getOwnPropertyDescriptor(o, s));
            Object.defineProperty(o, s, { get: function () { return 'again'; }, configurable: true });
            first + ',' + second + ',' + o[s];
        ");
        Assert.Equal("via-getter,plain,false,again", v);
    }

    [Fact]
    public void DeletingASymbol_RemovesItAndLeavesItsNeighboursAlone()
    {
        var v = Run(@"
            var a = Symbol('a'), b = Symbol('b'), c = Symbol('c');
            var o = {};
            o[a] = 1; o[b] = 2; o[c] = 3;
            delete o[b];
            var keys = Object.getOwnPropertySymbols(o);
            [keys.length, keys[0] === a, keys[1] === c, o[a], o[c], o[b] === undefined].join(',');
        ");
        Assert.Equal("2,true,true,1,3,true", v);
    }

    [Fact]
    public void ANonConfigurableSymbol_RefusesDeletion()
    {
        var v = Run(@"
            var s = Symbol('k');
            var o = {};
            Object.defineProperty(o, s, { value: 1, configurable: false });
            (delete o[s]) + ',' + o[s];
        ");
        Assert.Equal("false,1", v);
    }

    [Fact]
    public void ManySymbols_GrowTheTableWithoutLosingAny()
    {
        var v = Run(@"
            var o = {};
            var syms = [];
            for (var i = 0; i < 40; i++) { syms.push(Symbol('s' + i)); o[syms[i]] = i; }
            var total = 0;
            for (var j = 0; j < syms.length; j++) total += o[syms[j]];
            total + ',' + Object.getOwnPropertySymbols(o).length;
        ");
        Assert.Equal("780,40", v);
    }

    [Fact]
    public void WhatASymbolSlotHolds_SurvivesCollection()
    {
        var v = Run(@"
            var s = Symbol('k');
            var root = {};
            root[s] = { n: 11 };
            var churn = null;
            for (var i = 0; i < 20000; i++) churn = { pad: i, more: 'x' + i };
            String(root[s].n + (churn.pad > 0 ? 0 : 0));
        ");
        Assert.Equal("11", v);
    }

    [Fact]
    public void AnObjectsIteratorSymbol_StillDrivesForOf()
    {
        var v = Run(@"
            var o = {};
            o[Symbol.iterator] = function () {
                var i = 0;
                return { next: function () { return i < 3 ? { value: i++, done: false } : { value: undefined, done: true }; } };
            };
            var seen = [];
            for (var x of o) seen.push(x);
            seen.join('-');
        ");
        Assert.Equal("0-1-2", v);
    }
}
