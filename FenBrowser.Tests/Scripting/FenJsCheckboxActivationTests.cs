using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class FenJsCheckboxActivationTests
{
    [Fact]
    public async Task TypeProperty_ReflectsAttributeAndExposesUncheckedState()
    {
        var result = await EvaluateAsync(
            "var input=document.createElement('input');" +
            "input.type='checkbox';" +
            "[input.type,input.getAttribute('type'),String(input.checked)].join('|');");

        Assert.Equal("checkbox|checkbox|false", result);
    }

    [Fact]
    public async Task Click_TogglesBeforeListenerAndRollsBackWhenCanceled()
    {
        var result = await EvaluateAsync(
            "var input=document.createElement('input');input.type='checkbox';var values=[];" +
            "input.addEventListener('click',function(e){values.push(String(input.checked));" +
            "e.preventDefault();values.push(String(input.checked));});" +
            "input.click();values.push(String(input.checked));values.join('|');");

        Assert.Equal("true|true|false", result);
    }

    [Fact]
    public async Task Click_DispatchesClickInputChangeAfterSuccessfulActivation()
    {
        var result = await EvaluateAsync(
            "var input=document.createElement('input');input.type='checkbox';document.body.appendChild(input);" +
            "var events=[];['change','click','input'].forEach(function(type){" +
            "input.addEventListener(type,function(){events.push(type);});});" +
            "input.click();[String(input.checked),events.join(',')].join('|');");

        Assert.Equal("true|click,input,change", result);
    }

    [Fact]
    public async Task DispatchEvent_ClickRunsCheckboxActivationBehavior()
    {
        var result = await EvaluateAsync(
            "var input=document.createElement('input');input.type='checkbox';document.body.appendChild(input);" +
            "var events=[];['change','click','input'].forEach(function(type){" +
            "input.addEventListener(type,function(){events.push(type);});});" +
            "var event=new MouseEvent('click',{bubbles:true,cancelable:true});input.dispatchEvent(event);" +
            "[String(input.checked),events.join(',')].join('|');");

        Assert.Equal("true|click,input,change", result);
    }

    private static async Task<string> EvaluateAsync(string script)
    {
        var baseUri = new Uri("https://fixture.test/checkbox-activation.html");
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            return engine.Evaluate(script)?.ToString();
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
