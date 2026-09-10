using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// An array whose length is above the number of elements it holds. Every index
/// between the two is a hole, and a hole is absent rather than undefined - so
/// most of these check the places that difference shows: the prototype chain,
/// `in`, `delete`, enumeration, and the methods that skip what is not there.
/// </summary>
public sealed class ArrayTrailingHoleTests
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
    public void APreSizedArrayHasItsLengthAndNoElements()
    {
        Assert.Equal(
            "5|[]|false|undefined",
            RunString(
                "var a = new Array(5);" +
                "[a.length, JSON.stringify(Object.keys(a)), 0 in a, String(a[0])].join('|');"));
    }

    [Fact]
    public void AHoleReadsThroughToThePrototype()
    {
        // The whole reason a hole cannot be stored as undefined: it is absent, so
        // the read continues up the chain.
        Assert.Equal(
            "fromProto|undefined",
            RunString(
                "Array.prototype[1] = 'fromProto';" +
                "var a = new Array(3);" +
                "var viaHole = String(a[1]);" +
                "delete Array.prototype[1];" +
                "[viaHole, String(a[1])].join('|');"));
    }

    [Fact]
    public void FillingInOrderKeepsTheLength()
    {
        Assert.Equal(
            "4|0,1,2,3|4",
            RunString(
                "var a = new Array(4);" +
                "for (var i = 0; i < 4; i++) a[i] = i;" +
                "[a.length, a.join(','), Object.keys(a).length].join('|');"));
    }

    [Fact]
    public void GrowingTheLengthLeavesHolesBehind()
    {
        Assert.Equal(
            "6|false|undefined|0,1,2",
            RunString(
                "var a = [1, 2, 3];" +
                "a.length = 6;" +
                "[a.length, 4 in a, String(a[4]), Object.keys(a).join(',')].join('|');"));
    }

    [Fact]
    public void ShrinkingTheLengthDropsElements()
    {
        Assert.Equal(
            "2|1,2|false",
            RunString(
                "var a = [1, 2, 3, 4];" +
                "a.length = 2;" +
                "[a.length, a.join(','), 2 in a].join('|');"));
    }

    [Fact]
    public void DeletingTheLastElementKeepsTheLength()
    {
        // ECMA-262 10.4.2: delete removes the property; it never shortens the
        // array. The element it removes becomes a hole inside the array.
        Assert.Equal(
            "3|false|undefined|0,1",
            RunString(
                "var a = [1, 2, 3];" +
                "delete a[2];" +
                "[a.length, 2 in a, String(a[2]), Object.keys(a).join(',')].join('|');"));
    }

    [Fact]
    public void DeletingFromTheMiddleKeepsTheLength()
    {
        Assert.Equal(
            "3|false|0,2",
            RunString(
                "var a = [1, 2, 3];" +
                "delete a[1];" +
                "[a.length, 1 in a, Object.keys(a).join(',')].join('|');"));
    }

    [Fact]
    public void WritingOutOfOrderStillReadsBack()
    {
        Assert.Equal(
            "5|c|a|undefined|false",
            RunString(
                "var a = new Array(5);" +
                "a[2] = 'c'; a[0] = 'a';" +
                "[a.length, a[2], a[0], String(a[1]), 1 in a].join('|');"));
    }

    [Fact]
    public void EnumerationSkipsHoles()
    {
        Assert.Equal(
            "0,2|2",
            RunString(
                "var a = new Array(4);" +
                "a[0] = 'x'; a[2] = 'z';" +
                "var seen = [];" +
                "for (var k in a) seen.push(k);" +
                "var visited = 0;" +
                "a.forEach(function () { visited++; });" +
                "[seen.join(','), visited].join('|');"));
    }

    [Fact]
    public void GetOwnPropertyNamesReportsLengthAndPresentIndicesOnly()
    {
        Assert.Equal(
            "0,length",
            RunString(
                "var a = new Array(3);" +
                "a[0] = 'x';" +
                "Object.getOwnPropertyNames(a).join(',');"));
    }

    [Fact]
    public void StringifyWritesHolesAsNull()
    {
        Assert.Equal("[null,null,null]", RunString("JSON.stringify(new Array(3));"));
    }

    [Fact]
    public void JoinTreatsAHoleAsEmpty()
    {
        Assert.Equal("--", RunString("new Array(3).join('-');"));
    }

    [Fact]
    public void IndexOfSkipsHolesButIncludesFindsThem()
    {
        Assert.Equal(
            "-1|true",
            RunString(
                "var a = new Array(3);" +
                "[a.indexOf(undefined), a.includes(undefined)].join('|');"));
    }

    [Fact]
    public void PushAppendsPastTheHoles()
    {
        Assert.Equal(
            "4|3|true|false",
            RunString(
                "var a = new Array(3);" +
                "a.push('end');" +
                "[a.length, a.indexOf('end'), 3 in a, 1 in a].join('|');"));
    }

    [Fact]
    public void PopReadsAHoleAndShortensTheArray()
    {
        Assert.Equal(
            "undefined|2",
            RunString(
                "var a = new Array(3);" +
                "[String(a.pop()), a.length].join('|');"));
    }

    [Fact]
    public void ConcatCountsTheHoles()
    {
        Assert.Equal(4d, RunNumber("new Array(3).concat(['x']).length;"));
    }

    [Fact]
    public void AFrozenLengthCannotGrow()
    {
        Assert.Equal(
            "3|3",
            RunString(
                "var a = [1, 2, 3];" +
                "Object.defineProperty(a, 'length', { writable: false });" +
                "var before = a.length;" +
                "try { a.length = 9; } catch (e) {}" +
                "[before, a.length].join('|');"));
    }

    [Fact]
    public void AnUnrepresentableElementKeepsTheGrownLength()
    {
        // Defining a non-writable element makes the array give the vector up;
        // the length it was carrying has to survive that.
        Assert.Equal(
            "8|x|false",
            RunString(
                "var a = [];" +
                "a.length = 8;" +
                "Object.defineProperty(a, 0, { value: 'x', writable: false, enumerable: true, configurable: false });" +
                "[a.length, a[0], 5 in a].join('|');"));
    }

    [Fact]
    public void LengthIsReadTheSameWayTwice()
    {
        // The length is cached at its read site, and a pre-sized array is the
        // case where it is not the element count.
        Assert.True(
            RunBool(
                "function len(a) { return a.length; }" +
                "var pre = new Array(7), lit = [1, 2];" +
                "len(pre); len(lit);" +
                "len(pre) === 7 && len(lit) === 2;"));
    }
}
