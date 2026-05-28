using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Annex B B.2.4.1 RegExp.prototype.compile (audit gap §4.1).
public class AnnexBRegExpCompileTests
{
    private static string RunStr(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private static bool RunBool(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void Compile_RewritesPatternAndFlags()
    {
        Assert.Equal("foo", RunStr(@"
            var r = /bar/;
            r.compile('foo', 'i');
            r.source;
        "));
    }

    [Fact]
    public void Compile_UpdatesFlags()
    {
        Assert.Equal("gi", RunStr(@"
            var r = /x/;
            r.compile('y', 'gi');
            r.flags;
        "));
    }

    [Fact]
    public void Compile_TestUsesNewPattern()
        => Assert.True(RunBool("var r = /a/; r.compile('b'); r.test('b');"));

    [Fact]
    public void Compile_ReturnsSameInstance()
        => Assert.True(RunBool("var r = /a/; (r.compile('b') === r);"));

    [Fact]
    public void Compile_AcceptsRegExpInput()
        => Assert.Equal("hello", RunStr("var r = /x/; r.compile(/hello/); r.source;"));

    [Fact]
    public void Compile_ThrowsWhenRegExpAndFlagsProvided()
    {
        Assert.Equal("TypeError", RunStr(@"
            var caught = '';
            try { /x/.compile(/y/, 'i'); }
            catch (e) { caught = (e && e.constructor && e.constructor.name) || String(e); }
            caught;
        "));
    }
}
