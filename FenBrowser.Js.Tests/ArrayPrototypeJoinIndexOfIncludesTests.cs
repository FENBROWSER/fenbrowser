using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayPrototypeJoinIndexOfIncludesTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Theory]
    [InlineData("[1,2,3].join();", "1,2,3")]
    [InlineData("[1,2,3].join('-');", "1-2-3")]
    [InlineData("[].join('-');", "")]
    [InlineData("['a',null,'c'].join(',');", "a,,c")]   // null stringifies to empty
    [InlineData("['a',undefined,'c'].join(',');", "a,,c")] // undefined stringifies to empty
    [InlineData("[1].join('-');", "1")]
    public void Join(string source, string expected)
    {
        Assert.Equal(expected, RunStr(source));
    }

    [Fact]
    public void JoinCapturesLengthBeforeSeparatorCoercion()
    {
        Assert.Equal("0.0.0.0", RunStr("""
            var rab = new ArrayBuffer(4, { maxByteLength: 6 });
            var sample = new Uint8Array(rab);
            var sep = { toString: function() { rab.resize(6); return '.'; } };
            Array.prototype.join.call(sample, sep);
            """));
    }

    [Fact]
    public void JoinUsesLiveTypedArrayReadsForResizableBuffers()
    {
        Assert.Equal("0,2", RunStr("""
            var rab = new ArrayBuffer(4, { maxByteLength: 8 });
            var write = new Uint8Array(rab);
            write[0] = 0; write[1] = 2; write[2] = 4; write[3] = 6;
            var tracking = new Uint8Array(rab, 0);
            rab.resize(2);
            Array.prototype.join.call(tracking);
            """));
    }

    [Theory]
    [InlineData("var sample=[];sample.push(sample);sample.join();", "")]
    [InlineData("var sample=[1];sample.push(sample);sample.toString();", "1,")]
    [InlineData("var left=[],right=[];left.push(right);right.push(left);left.join();", "")]
    public void JoinTreatsCircularArrayReferencesAsEmptyElements(string source, string expected)
    {
        Assert.Equal(expected, RunStr(source));
    }

    [Theory]
    [InlineData("[10, 20, 30].indexOf(20);", 1)]
    [InlineData("[10, 20, 30].indexOf(99);", -1)]
    [InlineData("[10, 20, 30].indexOf(10, 1);", -1)]
    [InlineData("[10, 20, 30].indexOf(30, -1);", 2)]
    [InlineData("[].indexOf(1);", -1)]
    [InlineData("[NaN].indexOf(NaN);", -1)]   // strict-eq; NaN !== NaN
    public void IndexOf(string source, double expected)
    {
        Assert.Equal(expected, RunNum(source));
    }

    [Theory]
    [InlineData("[10, 20, 30].includes(20);", true)]
    [InlineData("[10, 20, 30].includes(99);", false)]
    [InlineData("[10, 20, 30].includes(10, 1);", false)]
    [InlineData("[NaN].includes(NaN);", true)]   // SameValueZero catches NaN
    [InlineData("[0].includes(-0);", true)]      // SameValueZero treats +0 == -0
    [InlineData("[].includes(1);", false)]
    public void Includes(string source, bool expected)
    {
        Assert.Equal(expected, RunBool(source));
    }

    [Fact]
    public void IncludesTreatsSparseSlotsAsUndefined()
    {
        Assert.True(RunBool("""
            var sample = [, , , 42, , ];
            [ , , , ].includes(undefined) &&
            ![, , , 42, ].includes(undefined, 4) &&
            sample.includes(undefined) &&
            sample.includes(undefined, 4) &&
            sample.includes(42, 3);
            """));
    }

    [Fact]
    public void IncludesUsesToLengthBoundaryInsteadOfIntClamp()
    {
        Assert.True(RunBool("""
            var obj = {
              "0": "a",
              "1": "b",
              "9007199254740990": "c",
              "9007199254740991": "d",
              "9007199254740992": "e"
            };
            var fromIndex = 9007199254740990;
            obj.length = 9007199254740991;
            [].includes.call(obj, "c", fromIndex) &&
            ![].includes.call(obj, "d", fromIndex) &&
            (obj.length = Infinity, [].includes.call(obj, "c", fromIndex)) &&
            ![].includes.call(obj, "d", fromIndex);
            """));
    }

    [Fact]
    public void IncludesUsesOriginalLengthAndLiveReadsForResizableTypedArrays()
    {
        Assert.True(RunBool("""
            var rab = new ArrayBuffer(4, { maxByteLength: 8 });
            var fixed = new Uint8Array(rab, 0, 4);
            fixed[0] = 0; fixed[1] = 2; fixed[2] = 4; fixed[3] = 6;
            var tracking = new Uint8Array(rab);
            var shrink = { valueOf: function() { rab.resize(2); return 0; } };
            var grow = { valueOf: function() { rab.resize(6); return -4; } };

            var fixedSeesUndefinedAfterShrink = Array.prototype.includes.call(fixed, undefined, shrink);
            rab.resize(4);
            fixed[0] = 1;
            var trackingKeepsOriginalLengthOnGrowth = Array.prototype.includes.call(tracking, 1, grow);

            fixedSeesUndefinedAfterShrink && trackingKeepsOriginalLengthOnGrowth;
            """));
    }
}
