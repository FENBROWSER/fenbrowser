using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// Per-element-kind members real pages read that the bridge did not answer.
/// Every expected value here was taken from Chrome 152 run against this exact
/// fixture markup, which is how the option.selected setter was caught: Chrome
/// leaves the selected content attribute alone (that is defaultSelected) and
/// only changes live state.
/// </summary>
public sealed class FenJsTypedElementMembersTests
{
    [Fact]
    public async Task AnchorHash_IsTheFragmentIncludingTheHash()
    {
        Assert.Equal("#frag", await EvaluateAsync("document.getElementById('anchor').hash;"));
    }

    [Fact]
    public async Task AnchorHash_IsEmptyWithoutAFragment()
    {
        Assert.Equal(
            "\"\"",
            await EvaluateAsync("JSON.stringify(document.getElementById('plain').hash);"));
    }

    [Fact]
    public async Task AnchorHash_SetterAddsTheFragmentToHref()
    {
        Assert.Equal(
            "https://fixture.test/page.html#z|#z",
            await EvaluateAsync(
                "var a = document.getElementById('plain');" +
                "a.hash = 'z';" +
                "a.getAttribute('href') + '|' + a.hash;"));
    }

    [Fact]
    public async Task AnchorHash_SetterStripsALeadingHash()
    {
        Assert.Equal(
            "https://fixture.test/page.html#w|#w",
            await EvaluateAsync(
                "var a = document.getElementById('plain');" +
                "a.hash = '#w';" +
                "a.getAttribute('href') + '|' + a.hash;"));
    }

    [Fact]
    public async Task AnchorType_ReflectsTheAttribute()
    {
        Assert.Equal("text/html", await EvaluateAsync("document.getElementById('anchor').type;"));
    }

    [Fact]
    public async Task RelList_IsTokenizedOnAnAnchor()
    {
        Assert.Equal(
            "noopener,noreferrer",
            await EvaluateAsync(
                "Array.prototype.join.call(document.getElementById('anchor').relList, ',');"));
    }

    [Fact]
    public async Task RelList_IsTokenizedOnALinkElement()
    {
        Assert.Equal(
            "stylesheet,alternate|2",
            await EvaluateAsync(
                "var r = document.getElementById('sheet').relList;" +
                "Array.prototype.join.call(r, ',') + '|' + r.length;"));
    }

    [Fact]
    public async Task ScriptText_IsTheChildTextContent()
    {
        Assert.Equal("var q = 1;", await EvaluateAsync("document.getElementById('scr').text;"));
    }

    [Fact]
    public async Task TextAreaDefaultValue_IsTheChildTextContent()
    {
        Assert.Equal(
            "default text",
            await EvaluateAsync("document.getElementById('ta').defaultValue;"));
    }

    [Fact]
    public async Task OptionSelected_ReflectsTheAttribute()
    {
        Assert.Equal(
            "true|false",
            await EvaluateAsync(
                "document.getElementById('opt1').selected + '|' + document.getElementById('opt2').selected;"));
    }

    [Fact]
    public async Task OptionSelected_SetterDoesNotTouchTheDefaultSelectedAttribute()
    {
        // Chrome: `true | attr=false` - the content attribute is defaultSelected.
        Assert.Equal(
            "true|attr=false",
            await EvaluateAsync(
                "var o = document.getElementById('opt2');" +
                "o.selected = true;" +
                "o.selected + '|attr=' + o.hasAttribute('selected');"));
    }

    [Fact]
    public async Task FieldSetDisabled_ReflectsTheAttribute()
    {
        Assert.Equal(
            "true|false",
            await EvaluateAsync(
                "document.getElementById('fs').disabled + '|' + document.getElementById('fs2').disabled;"));
    }

    [Fact]
    public async Task FieldSetDisabled_SetterWritesTheAttribute()
    {
        Assert.Equal(
            "true|true",
            await EvaluateAsync(
                "var f = document.getElementById('fs2');" +
                "f.disabled = true;" +
                "f.hasAttribute('disabled') + '|' + f.disabled;"));
    }

    [Fact]
    public async Task StyleType_ReflectsTheAttribute()
    {
        Assert.Equal(
            "text/css|",
            await EvaluateAsync(
                "document.getElementById('st').type + '|' + document.getElementById('st2').type;"));
    }

    [Fact]
    public async Task StyleType_SetterWritesTheAttribute()
    {
        Assert.Equal(
            "text/css",
            await EvaluateAsync(
                "var s = document.getElementById('st2');" +
                "s.type = 'text/css';" +
                "s.getAttribute('type');"));
    }

    // HTML dom-button-type: limited-value reflection whose missing/invalid
    // default is "submit", so a bare <button> reports "submit", not "".
    [Fact]
    public async Task ButtonType_DefaultsToSubmitWhenAbsent()
    {
        Assert.Equal("submit", await EvaluateAsync("document.getElementById('btn-bare').type;"));
    }

    [Fact]
    public async Task ButtonType_ReflectsAValidKeyword()
    {
        Assert.Equal(
            "reset|button",
            await EvaluateAsync(
                "document.getElementById('btn-reset').type + '|' +" +
                "document.getElementById('btn-button').type;"));
    }

    [Fact]
    public async Task ButtonType_FallsBackToSubmitForAnInvalidKeyword()
    {
        Assert.Equal("submit", await EvaluateAsync("document.getElementById('btn-bogus').type;"));
    }

    // HTML dom-select-type: derived from the multiple attribute, not a type one.
    [Fact]
    public async Task SelectType_IsSelectOneWithoutMultiple()
    {
        Assert.Equal("select-one", await EvaluateAsync("document.getElementById('sel').type;"));
    }

    [Fact]
    public async Task SelectType_IsSelectMultipleWithMultiple()
    {
        Assert.Equal("select-multiple", await EvaluateAsync("document.getElementById('sel-multi').type;"));
    }

    // relList.supports must answer rather than throw: Next.js probes it before
    // using a resource hint, and a throw reads as "no rel support at all".
    // Sets verified against Chrome 152.
    [Fact]
    public async Task LinkRelListSupports_AnswersForResourceHints()
    {
        Assert.Equal(
            "true|true|true|false",
            await EvaluateAsync(
                "var r = document.getElementById('sheet').relList;" +
                "r.supports('preload') + '|' + r.supports('stylesheet') + '|' +" +
                "r.supports('modulepreload') + '|' + r.supports('bogus');"));
    }

    [Fact]
    public async Task AnchorRelListSupports_AnswersForOpenerKeywords()
    {
        Assert.Equal(
            "true|true|false",
            await EvaluateAsync(
                "var r = document.getElementById('anchor').relList;" +
                "r.supports('noopener') + '|' + r.supports('opener') + '|' + r.supports('preload');"));
    }

    [Fact]
    public async Task ClassListSupports_StillThrows()
    {
        // Chrome throws here; only rel and sandbox define supported tokens.
        Assert.Equal(
            "threw",
            await EvaluateAsync(
                "try { document.getElementById('anchor').classList.supports('x'); 'no-throw'; }" +
                "catch (e) { 'threw'; }"));
    }

    private static async Task<string> EvaluateAsync(string script)
    {
        var baseUri = new Uri("https://fixture.test/typed-members.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body>" +
                "<a id='anchor' href='https://fixture.test/page.html#frag' type='text/html' rel='noopener noreferrer'>x</a>" +
                "<a id='plain' href='https://fixture.test/page.html'>y</a>" +
                "<link id='sheet' rel='stylesheet alternate' href='/a.css'>" +
                "<script id='scr'>var q = 1;</script>" +
                "<textarea id='ta'>default text</textarea>" +
                "<select><option id='opt1' selected>a</option><option id='opt2'>b</option></select>" +
                "<button id='btn-bare'>b</button>" +
                "<button id='btn-reset' type='reset'>r</button>" +
                "<button id='btn-button' type='button'>u</button>" +
                "<button id='btn-bogus' type='nonsense'>n</button>" +
                "<select id='sel'><option>a</option></select>" +
                "<select id='sel-multi' multiple><option>a</option></select>" +
                "<fieldset id='fs' disabled></fieldset>" +
                "<fieldset id='fs2'></fieldset>" +
                "<style id='st' type='text/css'></style>" +
                "<style id='st2'></style>" +
                "</body></html>",
                baseUri).Parse();
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
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
