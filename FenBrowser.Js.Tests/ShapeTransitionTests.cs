using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// A shape remembers the transition it was asked for last, so adding a property
// to a fresh object is usually a reference compare. A wrong answer there would
// hand an object another shape's slot numbering, which shows up as properties
// reading each other's values. These drive the cases that would.
public class ShapeTransitionTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void TwoKeySequencesFromTheSameBase_KeepTheirOwnSlots()
    {
        var v = Run(@"
            function first() { var o = {}; o.a = 1; o.b = 2; return o; }
            function second() { var o = {}; o.a = 10; o.c = 20; return o; }
            var x = first(), y = second();
            [x.a, x.b, x.c, y.a, y.b, y.c].join(',');
        ");
        Assert.Equal("1,2,,10,,20", v);
    }

    [Fact]
    public void AlternatingBetweenTwoShapes_AnswersEachCorrectly()
    {
        // Each turn asks the same parent shape for a different transition, so the
        // remembered one never matches twice running.
        var v = Run(@"
            function build(useB) { var o = {}; o.head = 1; if (useB) o.b = 2; else o.c = 3; return o; }
            var total = '';
            for (var i = 0; i < 200; i++) {
                var o = build(i % 2 === 0);
                total = '' + o.head + (o.b === undefined ? 'x' : o.b) + (o.c === undefined ? 'x' : o.c);
            }
            total;
        ");
        Assert.Equal("1x3", v);
    }

    [Fact]
    public void TheSameNameFromDifferentSites_ReachesTheSameShape()
    {
        var v = Run(@"
            function siteOne() { var o = {}; o.shared = 'one'; return o; }
            function siteTwo() { var o = {}; o.shared = 'two'; return o; }
            var a = siteOne(), b = siteTwo();
            a.shared + ',' + b.shared + ',' + Object.keys(a).join('') + ',' + Object.keys(b).join('');
        ");
        Assert.Equal("one,two,shared,shared", v);
    }

    [Fact]
    public void AKeyDeletedAndReadded_StillReadsItsOwnValue()
    {
        var v = Run(@"
            var results = [];
            for (var i = 0; i < 50; i++) {
                var o = { a: i, b: i + 1 };
                delete o.a;
                o.a = i + 100;
                results.push(o.a + ':' + o.b + ':' + Object.keys(o).join(''));
            }
            results[49];
        ");
        Assert.Equal("149:50:ba", v);
    }

    [Fact]
    public void ManyDistinctShapes_EachKeepsItsOwnLayout()
    {
        var v = Run(@"
            var total = 0;
            for (var i = 0; i < 400; i++) {
                var o = {};
                o['k' + (i % 7)] = i;
                o['j' + (i % 11)] = i * 2;
                total += o['k' + (i % 7)] + o['j' + (i % 11)];
            }
            String(total);
        ");

        var expected = 0;
        for (var i = 0; i < 400; i++) expected += i + (i * 2);
        Assert.Equal(expected.ToString(), v);
    }

    [Fact]
    public void ALongChainOfProperties_NumbersEverySlot()
    {
        var v = Run(@"
            function build(n) {
                var o = {};
                for (var i = 0; i < n; i++) o['p' + i] = i;
                return o;
            }
            var o = build(64);
            var total = 0;
            for (var k in o) total += o[k];
            total + ',' + Object.keys(o).length + ',' + o.p0 + ',' + o.p63;
        ");
        Assert.Equal("2016,64,0,63", v);
    }
}
