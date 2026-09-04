using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class MicrotaskTracingTests
{
    [Fact]
    public void QueueMicrotaskTraceNamesCheckpointAndJobStages()
    {
        var events = new List<FenJsMicrotaskTraceEvent>();
        var interpreter = new BytecodeInterpreter
        {
            MicrotaskTraceSink = events.Add
        };
        var function = new BytecodeCompiler().CompileScript(new SourceText(
            "queueMicrotask(function tracedMicrotask(){}); " +
            "queueMicrotask(function secondMicrotask(){});"));
        new BytecodeVerifier().Verify(function);

        _ = interpreter.Execute(function);

        Assert.Contains(events, entry => entry.Stage == "checkpoint-enter");
        Assert.Contains(events, entry =>
            entry.Stage == "job-enter" &&
            entry.JobKind == "queue-microtask" &&
            entry.Detail.Contains("tracedMicrotask", StringComparison.Ordinal));
        Assert.Contains(events, entry =>
            entry.Stage == "job-complete" && entry.JobKind == "queue-microtask");
        Assert.Equal(
            new[] { 1, 2 },
            events.Where(entry => entry.Stage == "job-enter").Select(entry => entry.JobIndex));
        Assert.Contains(events, entry => entry.Stage == "checkpoint-complete");
    }

    [Fact]
    public void PromiseTraceSeparatesHandlerFromCapabilityResolution()
    {
        var events = new List<FenJsMicrotaskTraceEvent>();
        var interpreter = new BytecodeInterpreter
        {
            MicrotaskTraceSink = events.Add
        };
        var function = new BytecodeCompiler().CompileScript(new SourceText(
            "Promise.resolve(1).then(function tracedReaction(value){ return value + 1; });"));
        new BytecodeVerifier().Verify(function);

        _ = interpreter.Execute(function);

        Assert.Contains(events, entry => entry.Stage == "promise-reaction-handler-enter");
        Assert.Contains(events, entry => entry.Stage == "promise-reaction-handler-complete");
        Assert.Contains(events, entry => entry.Stage == "promise-reaction-resolve-enter");
        Assert.Contains(events, entry => entry.Stage == "promise-reaction-resolve-complete");
    }
}
