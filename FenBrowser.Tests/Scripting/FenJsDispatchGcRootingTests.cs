using System;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using Xunit;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class FenJsDispatchGcRootingTests
{
    [Fact]
    public async Task ScriptCreatedEvent_SurvivesGcAcrossHostDispatch()
    {
        var result = await EvaluateAsync(
            new[]
            {
                "try { document.addEventListener('ping', function () { window.__log = 'fired'; }); 'ok'; } catch (e) { 'threw:' + ((e && e.name) || '?') + ':' + ((e && e.message) || String(e)); }",
                "window.__ev = new Event('ping'); 'created:' + window.__ev.type;",
                "document.dispatchEvent(window.__ev); String(window.__log);"
            },
            stressHeap: true);

        Assert.Equal("ok|created:ping|fired", result);
    }

    [Fact]
    public async Task ScriptCreatedEvent_ListenerStillFiresWithoutStress()
    {
        var result = await EvaluateAsync(
            new[]
            {
                "document.addEventListener('ping', function () { window.__log = 'fired'; }); 'ok';",
                "window.__ev = new Event('ping'); 'created:' + window.__ev.type;",
                "document.dispatchEvent(window.__ev); String(window.__log);"
            },
            stressHeap: false);

        Assert.Equal("ok|created:ping|fired", result);
    }

    // Regression for the real-site stale-handle class (Wikipedia DOMContentLoaded
    // dispatch): host-built event facades live only in C# locals between listener
    // invocations. Under BeforeEveryAlloc stress every allocation collects, so any
    // unrooted window sweeps the facade and the next listener throws
    // JsEngineFatalException at the RegisterGlobalValue barrier.
    [Fact]
    public async Task BootLifecycleEvents_AllListenersShareFacadeUnderGcStress()
    {
        var result = await BootWithStressAsync(
            "<html><body><script>" +
            "window.__seq = [];" +
            "document.addEventListener('readystatechange', function () { window.__seq.push('rsc:' + document.readyState); });" +
            "document.addEventListener('DOMContentLoaded', function () {" +
            "  for (var i = 0; i < 64; i++) { ({ marker: i }); }" +
            "  window.__seq.push('dcl-a'); " +
            "});" +
            "document.addEventListener('DOMContentLoaded', function () { window.__seq.push('dcl-b'); });" +
            "window.addEventListener('load', function () { window.__seq.push('load'); });" +
            "</script></body></html>",
            postBootProbe: "window.__seq.join(',')");

        Assert.Contains("rsc:interactive", result);
        Assert.Contains("dcl-a", result);
        Assert.Contains("dcl-b", result);
        Assert.Contains("rsc:complete", result);
    }

    // Regression: MutationObserver record facades are freshly allocated C#-side
    // and marshaled through InvokeFenJsCallbackSafely; the worker hop previously
    // left them unrooted. Under BeforeEveryAlloc stress, every intermediate cell
    // (record objects, node arrays, bound item() functions) must survive until
    // the top-level value reaches JS, and appendChild with a live observer must
    // not corrupt the heap.
    [Fact]
    public async Task MutationObserver_RecordsArray_SurvivesStressMarshal()
    {
        var baseUri = new Uri("https://fixture.test/mutation-gc-rooting.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><div id=\"target\"></div></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            GetHeap(engine).SetStressModeForDiagnostics(GcStressMode.BeforeEveryAlloc);

            engine.Evaluate("window.__seen = -1;" +
                "window.__obs = new MutationObserver(function (records) { window.__seen = records.length; });" +
                "window.__obs.observe(document.body, { childList: true });");

            // Mutating with a live observer exercises the record-construction path
            // synchronously inside host property access and native calls.
            engine.Evaluate("document.body.appendChild(document.createElement('span')); 'queued';");

            // takeRecords() builds the same record facades on demand; under stress
            // this proves the construction window keeps partially built graphs alive.
            var taken = Convert.ToInt32(engine.Evaluate("window.__obs.takeRecords().length") ?? "-1");
            Assert.True(taken >= 0, $"takeRecords() returned {taken}.");
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task NativeRangeConstructor_RemainsRootedAfterMajorGc()
    {
        var baseUri = new Uri("https://fixture.test/range-gc-rooting.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            GetHeap(engine).CollectGarbage();

            var result = engine.Evaluate(
                "Range.name + '|' + globalThis.Range.name + '|' + " +
                "Object.getOwnPropertyDescriptor(globalThis, 'Range').value.name");
            Assert.Equal("Range|Range|Range", result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static async Task<string> BootWithStressAsync(string html, string postBootProbe)
    {
        var baseUri = new Uri("https://fixture.test/boot-gc-rooting.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(html, baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            GetHeap(engine).SetStressModeForDiagnostics(GcStressMode.BeforeEveryAlloc);

            Exception bootFailure = null;
            try
            {
                await engine.SetDomAsync(document.DocumentElement, baseUri);
            }
            catch (Exception ex)
            {
                bootFailure = ex;
            }

            var probe = engine.Evaluate(postBootProbe)?.ToString() ?? "null";
            Assert.Null(bootFailure);
            return probe;
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static async Task<string> EvaluateAsync(string[] scripts, bool stressHeap)
    {
        var baseUri = new Uri("https://fixture.test/dispatch-gc-rooting.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            if (stressHeap)
            {
                GetHeap(engine).SetStressModeForDiagnostics(GcStressMode.BeforeEveryAlloc);
            }

            var outputs = new string[scripts.Length];
            for (var i = 0; i < scripts.Length; i++)
            {
                try
                {
                    outputs[i] = engine.Evaluate(scripts[i])?.ToString() ?? "null";
                }
                catch (Exception ex)
                {
                    outputs[i] = $"step{i}:THREW:{ex.GetType().Name}:{ex.Message}";
                }
            }

            return string.Join("|", outputs);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static JsHeap GetHeap(FenJsBrowserScriptEngine engine)
    {
        var field = typeof(FenJsBrowserScriptEngine).GetField(
            "_interpreter",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var interpreter = Assert.IsType<BytecodeInterpreter>(field?.GetValue(engine));
        return interpreter.Heap;
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
