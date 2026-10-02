using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// DOM 4.3 mutation observers: characterData records are queued for the text node and
/// its observed ancestors, and callbacks are delivered as a microtask of the realm.
/// </summary>
public sealed class MutationObserverDeliveryTests
{
    [Fact]
    public async Task CharacterDataRecords_AreQueued()
    {
        var engine = await StartAsync("<div id='host'>a</div>");

        try
        {
            var result = engine.Evaluate(
                """
                (function () {
                    var out = [];
                    var detached = document.createTextNode('');
                    var o1 = new MutationObserver(function () {});
                    o1.observe(detached, { characterData: true });
                    detached.textContent = 1;
                    out.push('detached:' + o1.takeRecords().length);

                    var attached = document.getElementById('host').firstChild;
                    var o2 = new MutationObserver(function () {});
                    o2.observe(attached, { characterData: true, characterDataOldValue: true });
                    attached.data = 'b';
                    var r2 = o2.takeRecords();
                    out.push('attached:' + r2.length + ':' + (r2[0] && r2[0].type) + ':' + (r2[0] && r2[0].oldValue));

                    var o3 = new MutationObserver(function () {});
                    o3.observe(document.getElementById('host'), { characterData: true, subtree: true });
                    attached.nodeValue = 'c';
                    out.push('subtree:' + o3.takeRecords().length);
                    return out.join(',');
                })();
                """);

            Assert.Equal("detached:1,attached:1:characterData:a,subtree:1", result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task Callbacks_RunAsAMicrotask_BeforeLaterPromiseReactions()
    {
        // DOM 4.3.2: the first mutation queues the mutation observer microtask, so the
        // callback runs ahead of a promise reaction queued after it. Polymer's microTask
        // is exactly this: a characterData observer on a detached text node.
        var engine = await StartAsync(
            """
            <script>
            var log = [];
            var node = document.createTextNode('');
            new MutationObserver(function (r) { log.push('mo:' + r[0].type); }).observe(node, { characterData: true });
            new MutationObserver(function (r) { log.push('list:' + r.length); }).observe(document.body, { childList: true });
            node.textContent = 1;
            document.body.appendChild(document.createElement('span'));
            Promise.resolve().then(function () { log.push('promise'); globalThis.__r = log.join(','); });
            </script>
            """);

        try
        {
            Assert.Equal("mo:characterData,list:1,promise", engine.Evaluate("String(globalThis.__r)")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task MicrotasksDoNotRun_InsideAnEngineEvaluationNestedInScript()
    {
        // HTML 8.1.4.4: the checkpoint waits for the execution context stack to empty.
        // The first classList read compiles a prototype helper through a nested
        // evaluation, which used to drain the queue mid-script.
        var engine = await StartAsync(
            """
            <script>
            var log = [];
            Promise.resolve().then(function () { log.push('promise'); });
            var node = document.createTextNode('');
            new MutationObserver(function () { log.push('observer'); }).observe(node, { characterData: true });
            node.data = 'x';
            var list = document.createElement('div').classList;
            log.push('sync:' + typeof list);
            globalThis.__during = log.join(',');
            </script>
            """);

        try
        {
            Assert.Equal("sync:object", engine.Evaluate("String(globalThis.__during)")?.ToString());
            Assert.Equal("sync:object,promise,observer", engine.Evaluate("String(log.join(','))")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static async Task<FenJsBrowserScriptEngine> StartAsync(string body)
    {
        var baseUri = new Uri("https://fixture.test/index.html");
        var document = new HtmlParser("<html><body>" + body + "</body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }
}
