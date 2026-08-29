using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class FenJsPerformanceObserverTests
{
    [Fact]
    public async Task PerformanceObserver_ValidatesConstructionAndObserveOptions()
    {
        var engine = await CreateEngineAsync();

        var result = engine.Evaluate(
            "var errors=[];" +
            "try{PerformanceObserver(function(){})}catch(e){errors.push(e.name)}" +
            "try{new PerformanceObserver(1)}catch(e){errors.push(e.name)}" +
            "var observer=new PerformanceObserver(function(){});" +
            "try{observer.observe(null)}catch(e){errors.push(e.name)}" +
            "try{observer.observe({})}catch(e){errors.push(e.name)}" +
            "try{observer.observe({type:'mark',entryTypes:['mark']})}catch(e){errors.push(e.name)}" +
            "[typeof PerformanceObserver,PerformanceObserver.supportedEntryTypes.join(','),errors.join(',')].join('|')");

        Assert.Equal("function|mark,measure|TypeError,TypeError,TypeError,TypeError,TypeError", result?.ToString());
    }

    [Fact]
    public async Task PerformanceObserver_DeliversMatchingEntriesAtTheMicrotaskCheckpoint()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate(
            "globalThis.__observed=[];" +
            "var observer=new PerformanceObserver(function(entries,current){" +
            "globalThis.__sameObserver=current===observer;" +
            "globalThis.__observed=entries.map(function(entry){return entry.name+':'+entry.entryType;});});" +
            "observer.observe({entryTypes:['mark','measure']});" +
            "performance.mark('start');performance.measure('span','start');");

        Assert.Equal(
            "true|start:mark,span:measure",
            engine.Evaluate("[String(globalThis.__sameObserver),globalThis.__observed.join(',')].join('|')")?.ToString());
    }

    [Fact]
    public async Task PerformanceObserver_TakeRecordsAndDisconnectPreventDelivery()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate(
            "globalThis.__callbackCount=0;" +
            "var observer=new PerformanceObserver(function(){globalThis.__callbackCount++;});" +
            "observer.observe({type:'mark'});" +
            "performance.mark('queued');" +
            "globalThis.__taken=observer.takeRecords().map(function(entry){return entry.name;}).join(',');" +
            "observer.disconnect();performance.mark('after-disconnect');");

        Assert.Equal(
            "queued|0",
            engine.Evaluate("[globalThis.__taken,String(globalThis.__callbackCount)].join('|')")?.ToString());
    }

    [Fact]
    public async Task PerformanceObserver_BufferedTypeDeliversExistingEntries()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate(
            "performance.mark('before-observe');globalThis.__buffered=[];" +
            "var observer=new PerformanceObserver(function(entries){" +
            "globalThis.__buffered=entries.map(function(entry){return entry.name;});});" +
            "observer.observe({type:'mark',buffered:true});");

        Assert.Equal(
            "before-observe",
            engine.Evaluate("globalThis.__buffered.join(',')")?.ToString());
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }));
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }
}
