using System;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public class DynamicImportRuntimeTests
{
    private static void Execute(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        _ = new BytecodeInterpreter().Execute(function);
    }

    private static JsValue Execute(BytecodeInterpreter interpreter, string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        return interpreter.Execute(function);
    }

    [Fact]
    public void OptionsExpressionIsEvaluatedAfterSpecifier()
    {
        Execute(@"
            var log = [];
            import(log.push('specifier'), log.push('options'));
            if (log.length !== 2 || log[0] !== 'specifier' || log[1] !== 'options') {
                throw new Error('wrong evaluation order');
            }
        ");
    }

    [Fact]
    public void AbruptOptionsExpressionPropagatesBeforePromiseCreation()
    {
        Execute(@"
            var caught = false;
            try {
                import('', (function () { throw new Error('options'); })());
            } catch (error) {
                caught = error.message === 'options';
            }
            if (!caught) throw new Error('options throw was not propagated');
        ");
    }

    [Fact]
    public void EnumerableImportAttributesInvokeProxyTraps()
    {
        Execute(@"
            var log = [];
            var attributes = new Proxy({}, {
                ownKeys() { return ['type']; },
                getOwnPropertyDescriptor() {
                    return { configurable: true, enumerable: true, value: 'json' };
                },
                get(target, key) { log.push(key); return 'json'; }
            });
            import('', { with: attributes });
            if (log.length !== 1 || log[0] !== 'type') {
                throw new Error('attributes were not enumerated');
            }
        ");
    }

    // The point of the loader hook: a host that has to go to the network answers
    // nothing at call time, and import() still returns. A loader that blocked here
    // would hold the thread that has to drain the host's event queue.
    [Fact]
    public void LoaderThatDoesNotAnswerInlineLeavesTheImportPending()
    {
        var interpreter = new BytecodeInterpreter();
        var calls = 0;
        interpreter.DynamicImportLoader = (_, _, _) => calls++;

        var state = Execute(interpreter, @"
            var state = 'pending';
            import('./m.js').then(function () { state = 'settled'; });
            state;
        ");

        Assert.Equal(1, calls);
        Assert.Equal("pending", state.AsString());

        interpreter.PumpMicrotasks();
        Assert.Equal("pending", Execute(interpreter, "state;").AsString());
    }

    [Fact]
    public void SettlingTheCapabilityLaterFulfilsTheImportPromise()
    {
        var interpreter = new BytecodeInterpreter();
        var capability = PromiseCapability.Empty;
        interpreter.DynamicImportLoader = (_, _, cap) => capability = cap;

        Execute(interpreter, @"
            var seen = 'none';
            import('./m.js').then(function (value) { seen = value; });
        ");
        Assert.True(capability.IsComplete);
        Assert.Equal("none", Execute(interpreter, "seen;").AsString());

        interpreter.InvokeFunction(
            capability.Resolve,
            new[] { JsValue.FromString("loaded") },
            JsValue.Undefined);
        interpreter.PumpMicrotasks();

        Assert.Equal("loaded", Execute(interpreter, "seen;").AsString());
    }

    [Fact]
    public void RejectingTheCapabilityRejectsTheImportPromise()
    {
        var interpreter = new BytecodeInterpreter();
        var capability = PromiseCapability.Empty;
        interpreter.DynamicImportLoader = (_, _, cap) => capability = cap;

        Execute(interpreter, @"
            var reason = 'none';
            import('./missing.js').catch(function (error) { reason = error; });
        ");

        interpreter.InvokeFunction(
            capability.Reject,
            new[] { JsValue.FromString("404") },
            JsValue.Undefined);
        interpreter.PumpMicrotasks();

        Assert.Equal("404", Execute(interpreter, "reason;").AsString());
    }

    [Fact]
    public void LoaderReceivesTheSpecifierAndTakesPrecedenceOverTheResolver()
    {
        var interpreter = new BytecodeInterpreter();
        string? seenSpecifier = null;
        interpreter.DynamicImportLoader = (specifier, _, _) => seenSpecifier = specifier;
        interpreter.DynamicImportResolver = (_, _) =>
            throw new InvalidOperationException("the synchronous resolver must not run");

        Execute(interpreter, "import('./m.js');");

        Assert.Equal("./m.js", seenSpecifier);
    }

    // With no hook there is nothing that could load a module; resolving to an
    // empty namespace would make a missing module look like one with no exports.
    [Theory]
    [InlineData("import('./m.js')")]
    [InlineData("import.defer('./m.js')")]
    [InlineData("import.source('./m.js')")]
    public void WithoutAHostLoaderEveryImportFormRejectsWithTypeError(string importExpression)
    {
        var interpreter = new BytecodeInterpreter();
        Execute(interpreter, "var outcome = 'pending'; " + importExpression +
            ".then(function () { outcome = 'fulfilled'; }, function (e) { outcome = e.constructor.name; });");

        Assert.Equal("TypeError", Execute(interpreter, "outcome;").AsString());
    }

    [Fact]
    public void ImportDeferGoesThroughTheHostLoader()
    {
        var interpreter = new BytecodeInterpreter();
        string? seenSpecifier = null;
        interpreter.DynamicImportResolver = (specifier, _) =>
        {
            seenSpecifier = specifier;
            return JsValue.FromString("namespace");
        };

        Execute(interpreter, "var got; import.defer('./lazy.js').then(function (ns) { got = ns; });");

        Assert.Equal("./lazy.js", seenSpecifier);
        Assert.Equal("namespace", Execute(interpreter, "got;").AsString());
    }

    // ECMA-262 GetModuleSource: a JavaScript module has no source phase object.
    [Fact]
    public void ImportSourceOfALoadedJavaScriptModuleRejectsWithSyntaxError()
    {
        var interpreter = new BytecodeInterpreter();
        interpreter.DynamicImportResolver = (_, _) => JsValue.FromString("namespace");

        Execute(interpreter, @"
            var outcome = 'pending';
            import.source('./m.js').then(function () { outcome = 'fulfilled'; }, function (e) { outcome = e.constructor.name; });");

        Assert.Equal("SyntaxError", Execute(interpreter, "outcome;").AsString());
    }

    [Fact]
    public void ImportSourceOfAModuleThatFailsToLoadRejectsWithTheLoadError()
    {
        var interpreter = new BytecodeInterpreter();
        var capability = PromiseCapability.Empty;
        interpreter.DynamicImportLoader = (_, _, cap) => capability = cap;

        Execute(interpreter, "var reason = 'none'; import.source('./missing.js').catch(function (e) { reason = e; });");
        interpreter.InvokeFunction(capability.Reject, new[] { JsValue.FromString("404") }, JsValue.Undefined);
        interpreter.PumpMicrotasks();

        Assert.Equal("404", Execute(interpreter, "reason;").AsString());
    }
}
