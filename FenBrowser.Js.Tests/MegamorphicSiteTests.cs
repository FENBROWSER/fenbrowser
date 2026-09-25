using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// A site that has seen more shapes than it can hold programs for reads and
// writes own data properties straight through the receiver's shape. Anything
// else - an accessor, an inherited name, an array's length - must still take
// the general [[Get]]/[[Set]].
public sealed class MegamorphicSiteTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private const string Shapes = @"
        var shapes = [{ k: 1 }, { a: 0, k: 2 }, { b: 0, k: 3 }, { c: 0, k: 4 }, { d: 0, k: 5 }, { e: 0, k: 6 }, { f: 0, k: 7 }, { g: 0, k: 8 }];
        function read(o) { return o.k; }
        function write(o, v) { o.k = v; }
        for (var r = 0; r < 20; r++) for (var i = 0; i < 8; i++) { read(shapes[i]); write(shapes[i], shapes[i].k); }";

    [Fact]
    public void ReadsAndWritesLandOnTheRightObject()
    {
        Assert.Equal("1,2,3,4,5,6,7,8|10,20,30,40,50,60,70,80", Run(Shapes + @"
            var before = shapes.map(read).join();
            for (var i = 0; i < 8; i++) write(shapes[i], (i + 1) * 10);
            before + '|' + shapes.map(read).join();"));
    }

    [Fact]
    public void AccessorsAndInheritedNamesStillTakeTheGeneralPath()
    {
        Assert.Equal("getter,inherited,set:5,undefined", Run(Shapes + @"
            var seen = '';
            var acc = { get k() { return 'getter'; }, set k(v) { seen = 'set:' + v; } };
            var inherits = Object.create({ k: 'inherited' });
            var r1 = read(acc), r2 = read(inherits);
            write(acc, 5);
            [r1, r2, seen, String(read({}))].join();"));
    }

    [Fact]
    public void AnArraysLengthStoreStillTruncates()
    {
        Assert.Equal("2,true", Run(@"
            function setLen(o, v) { o.length = v; }
            var objs = [{ length: 0 }, { a: 1, length: 0 }, { b: 1, length: 0 }, { c: 1, length: 0 }, { d: 1, length: 0 }, { e: 1, length: 0 }];
            for (var r = 0; r < 20; r++) for (var i = 0; i < objs.length; i++) setLen(objs[i], r);
            var arr = [1, 2, 3, 4, 5];
            setLen(arr, 2);
            arr.length + ',' + (arr[3] === undefined);"));
    }

    [Fact]
    public void ANonWritablePropertyIsNotOverwritten()
    {
        Assert.Equal("1,TypeError", Run(Shapes + @"
            var frozen = Object.freeze({ z: 0, k: 1 });
            write(frozen, 99);
            function strictWrite(o, v) { 'use strict'; o.k = v; }
            for (var r = 0; r < 20; r++) for (var i = 0; i < 8; i++) strictWrite(shapes[i], i);
            var name;
            try { strictWrite(frozen, 2); } catch (e) { name = e.constructor.name; }
            frozen.k + ',' + name;"));
    }
}
