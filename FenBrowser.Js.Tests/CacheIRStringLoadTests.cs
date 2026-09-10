using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Reads off a string primitive, which the load cache answers without any
/// receiver shape to guard - a string has none. What keeps that sound is that a
/// string's own properties are exactly `length` and its integer indices, so
/// these tests spend most of their effort on the cases the cache must refuse:
/// an index, a name that moves on String.prototype, and an accessor.
/// </summary>
public sealed class CacheIRStringLoadTests
{
    private static JsValue Run(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function);
    }

    private static string RunString(string source) => Run(source).AsString();

    private static double RunNumber(string source) => Run(source).AsNumber();

    private static bool RunBool(string source) => Run(source).AsBoolean();

    [Fact]
    public void ReadsAMethodOffTheStringPrototypeRepeatedly()
    {
        Assert.Equal(
            "97|98|99|100",
            RunString(
                "function code(s) { return s.charCodeAt(0); }" +
                "[code('a'), code('b'), code('c'), code('d')].join('|');"));
    }

    [Fact]
    public void TheCachedMethodIsTheOneOnTheStringPrototype()
    {
        Assert.True(
            RunBool(
                "function read(s) { return s.charCodeAt; }" +
                "read('a'); read('b');" +
                "read('c') === String.prototype.charCodeAt;"));
    }

    [Fact]
    public void ReadsALengthThatChangesWithTheReceiver()
    {
        Assert.Equal(
            "0|1|3|6",
            RunString(
                "function len(s) { return s.length; }" +
                "[len(''), len('a'), len('abc'), len('abcdef')].join('|');"));
    }

    [Fact]
    public void ReadsTheLengthOfAStringBuiltByConcatenation()
    {
        // The length form reads the value's length rather than a flattened copy
        // of it, which is what keeps this loop linear.
        Assert.Equal(
            2000d,
            RunNumber(
                "function len(s) { return s.length; }" +
                "var s = ''; var total = 0;" +
                "for (var i = 0; i < 1000; i++) { s = s + 'xy'; total = len(s); }" +
                "total;"));
    }

    [Fact]
    public void SeesAMethodAddedToTheStringPrototypeAfterTheSiteIsWarm()
    {
        Assert.Equal(
            "undefined|undefined|added",
            RunString(
                "function read(s) { return s.tag; }" +
                "var out = [String(read('a')), String(read('b'))];" +
                "String.prototype.tag = 'added';" +
                "out.push(String(read('c')));" +
                "out.join('|');"));
    }

    [Fact]
    public void SeesAMethodReplacedOnTheStringPrototype()
    {
        Assert.Equal(
            "one|one|two",
            RunString(
                "String.prototype.tag = 'one';" +
                "function read(s) { return s.tag; }" +
                "var out = [read('a'), read('b')];" +
                "String.prototype.tag = 'two';" +
                "out.push(read('c'));" +
                "out.join('|');"));
    }

    [Fact]
    public void SeesAMethodDeletedFromTheStringPrototype()
    {
        Assert.Equal(
            "one|one|undefined",
            RunString(
                "String.prototype.tag = 'one';" +
                "function read(s) { return s.tag; }" +
                "var out = [String(read('a')), String(read('b'))];" +
                "delete String.prototype.tag;" +
                "out.push(String(read('c')));" +
                "out.join('|');"));
    }

    [Fact]
    public void RefusesAnAccessorOnTheStringPrototype()
    {
        // An accessor has to be called on every read; a cached slot would answer
        // once and then stop running it.
        Assert.Equal(
            "1|2|3",
            RunString(
                "var calls = 0;" +
                "Object.defineProperty(String.prototype, 'ticks', { get: function () { return ++calls; } });" +
                "function read(s) { return s.ticks; }" +
                "[read('a'), read('b'), read('c')].join('|');"));
    }

    [Fact]
    public void ReadsAnIndexOffTheReceiverRatherThanTheCache()
    {
        Assert.Equal(
            "a|b|c|undefined",
            RunString(
                "function at(s) { return s[0]; }" +
                "[String(at('ax')), String(at('bx')), String(at('cx')), String(at(''))].join('|');"));
    }

    [Fact]
    public void ReadsAVaryingKeyOffAString()
    {
        Assert.Equal(
            "3|a|97|undefined",
            RunString(
                "function get(s, k) { return s[k]; }" +
                "var s = 'abc';" +
                "[String(get(s, 'length'))," +
                " String(get(s, '0'))," +
                " String(get(s, 'charCodeAt').call(s, 0))," +
                " String(get(s, 'nope'))].join('|');"));
    }

    [Fact]
    public void AnswersBothStringAndObjectReceiversAtOneSite()
    {
        Assert.Equal(
            "3|9|3|9",
            RunString(
                "function len(v) { return v.length; }" +
                "var o = { length: 9 };" +
                "[len('abc'), len(o), len('xyz'), len(o)].join('|');"));
    }
}
