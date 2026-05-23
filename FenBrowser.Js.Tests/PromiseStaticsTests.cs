using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class PromiseStaticsTests
{
    private static JsValue RunThenRead(string side, string read)
    {
        var interpreter = new BytecodeInterpreter();
        var sideFn = new BytecodeCompiler().CompileScript(new SourceText(side));
        new BytecodeVerifier().Verify(sideFn);
        _ = interpreter.Execute(sideFn);

        var readFn = new BytecodeCompiler().CompileScript(new SourceText(read));
        new BytecodeVerifier().Verify(readFn);
        return interpreter.Execute(readFn);
    }

    // Promise.all

    [Fact]
    public void AllResolvesWhenAllInputsResolve()
    {
        Assert.Equal(6d, RunThenRead(
            "var sum = 0; Promise.all([Promise.resolve(1), Promise.resolve(2), Promise.resolve(3)]).then(function(arr){ sum = arr[0]+arr[1]+arr[2]; });",
            "sum;").AsNumber());
    }

    [Fact]
    public void AllRejectsOnFirstRejection()
    {
        Assert.Equal("bad", RunThenRead(
            "var observed; Promise.all([Promise.resolve(1), Promise.reject('bad')]).catch(function(r){ observed = r; });",
            "observed;").AsString());
    }

    [Fact]
    public void AllOnEmptyResolvesWithEmptyArray()
    {
        Assert.Equal(0d, RunThenRead(
            "var len = -1; Promise.all([]).then(function(arr){ len = arr.length; });",
            "len;").AsNumber());
    }

    [Fact]
    public void AllPreservesOrderRegardlessOfFulfillmentTime()
    {
        Assert.Equal("a,b,c", RunThenRead(
            "var observed; Promise.all([Promise.resolve('a'), Promise.resolve('b'), Promise.resolve('c')]).then(function(arr){ observed = arr.join(','); });",
            "observed;").AsString());
    }

    [Fact]
    public void AllWrapsNonPromiseValues()
    {
        Assert.Equal(5d, RunThenRead(
            "var observed; Promise.all([1, 2, Promise.resolve(2)]).then(function(arr){ observed = arr[0] + arr[1] + arr[2]; });",
            "observed;").AsNumber());
    }

    // Promise.race

    [Fact]
    public void RaceSettlesWithFirstFulfillment()
    {
        Assert.Equal("first", RunThenRead(
            "var observed; Promise.race([Promise.resolve('first'), Promise.resolve('second')]).then(function(v){ observed = v; });",
            "observed;").AsString());
    }

    [Fact]
    public void RaceSettlesWithFirstRejection()
    {
        Assert.Equal("err", RunThenRead(
            "var observed; Promise.race([Promise.reject('err'), Promise.resolve('ok')]).catch(function(r){ observed = r; });",
            "observed;").AsString());
    }

    // Promise.allSettled

    [Fact]
    public void AllSettledReportsBothOutcomes()
    {
        Assert.Equal("fulfilled,rejected", RunThenRead(
            "var observed; Promise.allSettled([Promise.resolve(1), Promise.reject('e')]).then(function(arr){ observed = arr[0].status + ',' + arr[1].status; });",
            "observed;").AsString());
    }

    [Fact]
    public void AllSettledExposesValueAndReason()
    {
        Assert.Equal("42:e", RunThenRead(
            "var observed; Promise.allSettled([Promise.resolve(42), Promise.reject('e')]).then(function(arr){ observed = arr[0].value + ':' + arr[1].reason; });",
            "observed;").AsString());
    }

    // Promise.any

    [Fact]
    public void AnyFulfillsWithFirstFulfillment()
    {
        Assert.Equal(2d, RunThenRead(
            "var observed; Promise.any([Promise.reject('a'), Promise.resolve(2), Promise.reject('b')]).then(function(v){ observed = v; });",
            "observed;").AsNumber());
    }

    [Fact]
    public void AnyRejectsWithAggregateErrorWhenAllReject()
    {
        Assert.Equal("AggregateError", RunThenRead(
            "var observed; Promise.any([Promise.reject('a'), Promise.reject('b')]).catch(function(e){ observed = e.errors[0] + e.errors[1]; });",
            "typeof observed === 'string' ? 'AggregateError' : 'other';").AsString());
    }
}
