using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class FenJsDocumentBootstrapTests : IDisposable
{
    private const string RuntimeSelectorVariable = "FEN_BROWSER_SCRIPT_ENGINE";
    private readonly string _originalMode = Environment.GetEnvironmentVariable(RuntimeSelectorVariable);

    public FenJsDocumentBootstrapTests()
    {
        Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
        BrowserScriptEngineRuntime.Reset();
    }

    [Fact]
    public async Task InlineBootstrapCanReplaceDocumentElementClassName()
    {
        var baseUri = new Uri("https://example.com/app/index.html");
        var document = new HtmlParser(
            "<html class='client-nojs'><head><script>" +
            "(function(){globalThis.__trace='start';var className='client-js appearance-pinned';" +
            "var cookie=document.cookie.match(/(?:^|; )preferences=([^;]+)/);" +
            "globalThis.__trace+='|matched:' + String(cookie);" +
            "if(cookie){cookie[1].split('%2C').forEach(function(pref){className += ' ' + pref;});}" +
            "globalThis.__trace+='|assigning';document.documentElement.className=className;" +
            "globalThis.__trace+='|assigned:' + document.documentElement.className;}());" +
            "</script></head><body></body></html>",
            baseUri).Parse();
        var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
        engine.Sandbox = SandboxPolicy.AllowAll;

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var snapshot = engine.GetScriptLoadingSnapshot();
        Assert.True(
            snapshot.ExecutionFailed == 0 && snapshot.ExecutionCompleted == 1,
            $"started={snapshot.ExecutionStarted}; completed={snapshot.ExecutionCompleted}; " +
            $"failed={snapshot.ExecutionFailed}; eligible={snapshot.EligibleScripts}; " +
            $"skipped={snapshot.SkippedScripts}; scripts={snapshot.ScriptElements}; " +
            $"status={snapshot.Status}; infrastructure={snapshot.InfrastructureError}; " +
            $"records={string.Join(" | ", snapshot.Scripts.ConvertAll(script => $"{script.Status}:{script.Failure}"))}");
        Assert.Equal(
            "start|matched:null|assigning|assigned:client-js appearance-pinned",
            engine.Evaluate("globalThis.__trace")?.ToString());
        Assert.Equal("client-js appearance-pinned", document.DocumentElement.ClassName);
        Assert.Equal(
            "client-js appearance-pinned",
            engine.Evaluate("document.documentElement.className")?.ToString());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeSelectorVariable, _originalMode);
        BrowserScriptEngineRuntime.Reset();
    }

    private static JsHostAdapter CreateHost() => new(
        navigate: _ => { },
        post: (_, _) => { },
        status: _ => { },
        log: _ => { });
}
