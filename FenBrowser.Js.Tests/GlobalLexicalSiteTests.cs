using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// A top-level let, const or class lives in the global record's lexical half
// (ECMA-262 9.1.1.4), which numbers its names in declaration order so a
// free-variable site can reach one by slot. Each case runs twice: once in a
// function the register-window loop takes, once in one it declines (a block
// binding captured by a closure), which puts it on the dispatch loop. The loops
// run long enough to be handed to compiled code as well.
public sealed class GlobalLexicalSiteTests
{
    private const string KeepOnDispatchLoop = "{ let pin = 0; var keep = function () { return pin; }; }";

    public static TheoryData<string> Loops => new() { "", KeepOnDispatchLoop };

    private static string Run(params string[] scripts)
    {
        var interpreter = new BytecodeInterpreter();
        JsValue result = default;
        foreach (var script in scripts)
        {
            var fn = new BytecodeCompiler().CompileScript(new SourceText(script));
            new BytecodeVerifier().Verify(fn);
            result = interpreter.Execute(fn);
        }

        return result.AsString();
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void ReadsAndWritesGoThroughTheSameBinding(string pin)
    {
        Assert.Equal("300,300,4950", Run($@"
            let total = 0;
            let last = 0;
            function add(n) {{ {pin} for (var i = 0; i < n; i++) {{ total = total + 1; last = i; }} }}
            function sum(n) {{ {pin} var s = 0; for (var i = 0; i < n; i++) s += i; return s; }}
            function read() {{ {pin} var t = 0; for (var i = 0; i < 100; i++) t = total; return t; }}
            add(100); add(100); total++;
            total--; add(100);
            total + ',' + read() + ',' + sum(last + 1);"));
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void AReadBeforeInitializationStillThrows(string pin)
    {
        Assert.Equal("ReferenceError,ReferenceError,7", Run($@"
            function read() {{ {pin} var v; for (var i = 0; i < 50; i++) v = late; return v; }}
            function write() {{ {pin} for (var i = 0; i < 50; i++) late = i; }}
            var a = '', b = '';
            try {{ read(); }} catch (e) {{ a = e.constructor.name; }}
            try {{ write(); }} catch (e) {{ b = e.constructor.name; }}
            let late = 7;
            a + ',' + b + ',' + read();"));
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void AConstRefusesAWriteAfterCachedReads(string pin)
    {
        Assert.Equal("150,TypeError,3,TypeError,3", Run($@"
            const k = 3;
            function read() {{ {pin} var t = 0; for (var i = 0; i < 50; i++) t += k; return t; }}
            function write() {{ {pin} for (var i = 0; i < 50; i++) k = i; }}
            function bump() {{ {pin} for (var i = 0; i < 50; i++) k++; }}
            var r = read(), w = '', b = '';
            try {{ write(); }} catch (e) {{ w = e.constructor.name; }}
            var afterWrite = k;
            try {{ bump(); }} catch (e) {{ b = e.constructor.name; }}
            r + ',' + w + ',' + afterWrite + ',' + b + ',' + k;"));
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void BindingsDeclaredByLaterScriptsKeepEarlierSitesValid(string pin)
    {
        // Twenty more names grow the slot arrays past their first size; the
        // sites recorded for `first` must still land on it afterwards.
        var more = string.Concat(Enumerable.Range(0, 20).Select(i => $"let extra{i} = {i};"));
        Assert.Equal("10,30,19", Run(
            $@"let first = 1;
               function readFirst() {{ {pin} var t = 0; for (var i = 0; i < 10; i++) t += first; return t; }}
               function bumpFirst() {{ {pin} for (var i = 0; i < 2; i++) first++; }}
               readFirst();",
            more,
            $@"function readLast() {{ {pin} var t = 0; for (var i = 0; i < 50; i++) t = extra19; return t; }}
               var before = readFirst();
               bumpFirst();
               before + ',' + readFirst() + ',' + readLast();"));
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void ALetDeclaredLaterShadowsAGlobalPropertyAlreadyCached(string pin)
    {
        Assert.Equal("property|lexical|property", Run(
            $@"globalThis.shadowed = 'property';
               function read() {{ {pin} var v; for (var i = 0; i < 50; i++) v = shadowed; return v; }}
               read();",
            @"let shadowed = 'lexical';",
            @"var before = globalThis.shadowed;
              'property|' + read() + '|' + before;"));
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void ObjectsWrittenThroughTheSiteSurviveCollection(string pin)
    {
        Assert.Equal("499", Run($@"
            let holder = null;
            function fill() {{ {pin} for (var i = 0; i < 500; i++) holder = {{ index: i, pad: [i, i, i] }}; }}
            fill();
            var junk = [];
            for (var j = 0; j < 20000; j++) junk.push({{ j: j }});
            junk = null;
            for (var j = 0; j < 20000; j++) ({{ j: j }});
            String(holder.index + holder.pad.length - 3);"));
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void AClassDeclarationIsReadThroughItsSlot(string pin)
    {
        Assert.Equal("7", Run($@"
            class Point {{ constructor(x) {{ this.x = x; }} }}
            function make() {{ {pin} var p; for (var i = 0; i < 50; i++) p = new Point(7); return p.x; }}
            String(make());"));
    }
}
