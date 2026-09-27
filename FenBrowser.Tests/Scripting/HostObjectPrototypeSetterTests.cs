using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

// ECMA-262 10.1.9.2 OrdinarySetWithOwnDescriptor on a platform object.
[Collection("Engine Tests")]
public sealed class HostObjectPrototypeSetterTests : IDisposable
{
    public HostObjectPrototypeSetterTests()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    public void Dispose()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    private static FenJsBrowserScriptEngine Load()
    {
        var baseUri = new Uri("https://setter.test/page");
        var document = new HtmlParser("<!doctype html><html><body><video></video></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    [Fact]
    public void AnInheritedSetterRunsInsteadOfMakingAnOwnProperty()
    {
        var engine = Load();

        Assert.Equal("set:7|false|stored", engine.Evaluate(@"
            var seen = '';
            Object.defineProperty(HTMLElement.prototype, 'polyfilled', {
                get: function () { return 'stored'; },
                set: function (v) { seen = 'set:' + v; },
                configurable: true
            });
            var e = document.createElement('div');
            e.polyfilled = 7;
            [seen, Object.prototype.hasOwnProperty.call(e, 'polyfilled'), e.polyfilled].join('|')")?.ToString());
    }

    [Fact]
    public void InheritedReadOnlyAndGetterOnlyPropertiesRefuseTheWrite()
    {
        var engine = Load();

        Assert.Equal("TypeError|TypeError|plain", engine.Evaluate(@"
            'use strict';
            Object.defineProperty(HTMLElement.prototype, 'fixed', { value: 1, writable: false, configurable: true });
            Object.defineProperty(HTMLElement.prototype, 'readOnly', { get: function () { return 1; }, configurable: true });
            var e = document.createElement('div');
            function err(f) { try { f(); return 'none'; } catch (x) { return x.name; } }
            e.plain = 'plain';
            [err(function () { e.fixed = 2; }), err(function () { e.readOnly = 2; }), e.plain].join('|')")?.ToString());
    }

    [Fact]
    public void SrcObjectTakesAMediaStreamAndEmptiesCurrentSrc()
    {
        var engine = Load();

        Assert.Equal("true|false|TypeError", engine.Evaluate(@"
            var v = document.querySelector('video');
            var s = new MediaStream();
            v.srcObject = s;
            var err;
            try { v.srcObject = {}; err = 'none'; } catch (x) { err = x.name; }
            [v.srcObject === s, Object.prototype.hasOwnProperty.call(v, 'srcObject'), err].join('|')")?.ToString());
    }
}
