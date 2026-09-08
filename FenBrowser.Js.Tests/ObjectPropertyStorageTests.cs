using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Properties are stored as a value and a byte of attributes, with accessor
// halves in a side table. Everything observable about a property has to survive
// that: its attributes, the two directions of data/accessor redefinition,
// enumeration order after a delete, and the collector reaching what a slot
// holds.
public class ObjectPropertyStorageTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void ADataProperty_ReportsTheAttributesItWasDefinedWith()
    {
        var v = Run(@"
            var o = {};
            Object.defineProperty(o, 'x', { value: 7, writable: false, enumerable: true, configurable: false });
            var d = Object.getOwnPropertyDescriptor(o, 'x');
            [d.value, d.writable, d.enumerable, d.configurable, 'get' in d].join(',');
        ");
        Assert.Equal("7,false,true,false,false", v);
    }

    [Fact]
    public void AnAccessorProperty_KeepsBothHalves()
    {
        var v = Run(@"
            var o = {};
            function g() { return 1; }
            function s(v) { }
            Object.defineProperty(o, 'x', { get: g, set: s, enumerable: false, configurable: true });
            var d = Object.getOwnPropertyDescriptor(o, 'x');
            [d.get === g, d.set === s, d.enumerable, d.configurable, 'value' in d].join(',');
        ");
        Assert.Equal("true,true,false,true,false", v);
    }

    [Fact]
    public void RedefiningAcrossTheDataAccessorBoundary_WorksBothWays()
    {
        var v = Run(@"
            var o = {};
            Object.defineProperty(o, 'x', { get: function () { return 'from-getter'; }, configurable: true });
            var first = o.x;
            Object.defineProperty(o, 'x', { value: 'plain', writable: true, configurable: true });
            var second = o.x + ',' + ('get' in Object.getOwnPropertyDescriptor(o, 'x'));
            Object.defineProperty(o, 'x', { get: function () { return 'again'; }, configurable: true });
            first + ',' + second + ',' + o.x;
        ");
        Assert.Equal("from-getter,plain,false,again", v);
    }

    [Fact]
    public void ANonWritableProperty_RefusesAssignment()
    {
        var v = Run(@"
            'use strict';
            var o = {};
            Object.defineProperty(o, 'x', { value: 1, writable: false, configurable: true });
            var threw = false;
            try { o.x = 2; } catch (e) { threw = e instanceof TypeError; }
            threw + ',' + o.x;
        ");
        Assert.Equal("true,1", v);
    }

    [Fact]
    public void ANonConfigurableProperty_RefusesDeletion()
    {
        var v = Run(@"
            var o = {};
            Object.defineProperty(o, 'x', { value: 1, configurable: false });
            var deleted = delete o.x;
            deleted + ',' + ('x' in o);
        ");
        Assert.Equal("false,true", v);
    }

    [Fact]
    public void DeletingAndReaddingAKey_MovesItToTheEnd()
    {
        var v = Run(@"
            var o = { a: 1, b: 2, c: 3 };
            delete o.a;
            o.a = 4;
            Object.keys(o).join('');
        ");
        Assert.Equal("bca", v);
    }

    [Fact]
    public void IndexKeys_EnumerateBeforeStringKeys()
    {
        var v = Run(@"
            var o = {};
            o.zed = 1;
            o[2] = 1;
            o.alpha = 1;
            o[0] = 1;
            Object.keys(o).join(',');
        ");
        Assert.Equal("0,2,zed,alpha", v);
    }

    [Fact]
    public void ADeletedSlot_ReadsAsAbsentNotAsUndefinedValue()
    {
        var v = Run(@"
            var o = { a: 1 };
            delete o.a;
            [('a' in o), Object.keys(o).length, o.a === undefined].join(',');
        ");
        Assert.Equal("false,0,true", v);
    }

    [Fact]
    public void WhatASlotHolds_SurvivesCollection()
    {
        // Only the property slots keep these reachable, so a slot the collector
        // cannot see would leave a stale handle behind.
        var v = Run(@"
            var root = {};
            root.held = { n: 11 };
            Object.defineProperty(root, 'viaGetter', { get: function () { return this.held.n + 1; } });
            var churn = null;
            for (var i = 0; i < 20000; i++) { churn = { pad: i, more: 'x' + i }; }
            String(root.held.n + root.viaGetter + (churn.pad > 0 ? 0 : 0));
        ");
        Assert.Equal("23", v);
    }

    [Fact]
    public void ManyProperties_GrowTheSlotTableWithoutLosingAny()
    {
        var v = Run(@"
            var o = {};
            for (var i = 0; i < 200; i++) o['k' + i] = i;
            var sum = 0;
            for (var k in o) sum += o[k];
            sum + ',' + Object.keys(o).length;
        ");
        Assert.Equal("19900,200", v);
    }
}
