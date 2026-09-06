using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML 4.10.5.1: a form control's value is internal state, and the "value"
/// content attribute is only its default. The two diverge once a script assigns,
/// which the dirty value flag records.
/// </summary>
[Collection("Engine Tests")]
public sealed class FormControlValueStateTests
{
    private static FenJsBrowserScriptEngine CreateEngine() =>
        new(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };

    private static async Task<Document> RunAsync(string body)
    {
        ElementStateManager.Instance.ClearAll();
        var uri = new Uri("https://forms.test/page");
        var document = new HtmlParser(body, uri).Parse();
        var engine = CreateEngine();
        await engine.SetDomAsync(document.DocumentElement, uri);
        return document;
    }

    [Fact]
    public async Task AssigningValue_DoesNotWriteTheContentAttribute()
    {
        var doc = await RunAsync(
            "<input id='i' value='default'>" +
            "<script>document.getElementById('i').value = 'typed';</script>");

        var input = doc.GetElementById("i");

        // The attribute is the default value and must not move.
        Assert.Equal("default", input.GetAttribute("value"));
        Assert.Equal("typed", FormControlValue.Read(input));
    }

    [Fact]
    public async Task AssignedValue_IsReadBackFromTheValueIdlAttribute()
    {
        var doc = await RunAsync(
            "<input id='i' value='default'>" +
            "<script>var i=document.getElementById('i');i.value='typed';" +
            "document.body.setAttribute('data-v', i.value);" +
            "document.body.setAttribute('data-d', i.defaultValue);" +
            "document.body.setAttribute('data-attr', i.getAttribute('value'));</script>");

        Assert.Equal("typed", doc.Body.GetAttribute("data-v"));
        Assert.Equal("default", doc.Body.GetAttribute("data-d"));
        Assert.Equal("default", doc.Body.GetAttribute("data-attr"));
    }

    [Fact]
    public async Task PristineControl_TracksItsContentAttribute()
    {
        // Before the value is dirtied, changing the attribute changes the value.
        var doc = await RunAsync(
            "<input id='i' value='first'>" +
            "<script>var i=document.getElementById('i');" +
            "i.setAttribute('value','second');" +
            "document.body.setAttribute('data-before', i.value);" +
            "i.value='typed';" +
            "i.setAttribute('value','third');" +
            "document.body.setAttribute('data-after', i.value);</script>");

        Assert.Equal("second", doc.Body.GetAttribute("data-before"));
        // Once dirty it stops tracking the attribute.
        Assert.Equal("typed", doc.Body.GetAttribute("data-after"));
    }

    [Fact]
    public async Task TextareaValue_IsStateAndItsChildTextIsTheDefault()
    {
        var doc = await RunAsync(
            "<textarea id='t'>markup</textarea>" +
            "<script>var t=document.getElementById('t');t.value='typed';" +
            "document.body.setAttribute('data-v', t.value);" +
            "document.body.setAttribute('data-d', t.defaultValue);</script>");

        var textarea = doc.GetElementById("t");

        Assert.Equal("typed", doc.Body.GetAttribute("data-v"));
        Assert.Equal("markup", doc.Body.GetAttribute("data-d"));
        // The child text is the default value and must survive the assignment.
        Assert.Equal("markup", textarea.TextContent);
        Assert.Null(textarea.GetAttribute("value"));
    }

    [Fact]
    public async Task HiddenInput_StaysInDefaultModeAndReflectsTheAttribute()
    {
        // "default" mode: the IDL attribute really is the content attribute.
        var doc = await RunAsync(
            "<input type='hidden' id='h' value='a'>" +
            "<script>document.getElementById('h').value='b';</script>");

        Assert.Equal("b", doc.GetElementById("h").GetAttribute("value"));
    }

    [Fact]
    public async Task SubmissionEntries_CarryTheAssignedValueNotTheMarkup()
    {
        var doc = await RunAsync(
            "<form id='f'><input name='q' value='markup'>" +
            "<textarea name='t'>markup</textarea></form>" +
            "<script>var f=document.getElementById('f');" +
            "f.querySelector('input').value='typed';" +
            "f.querySelector('textarea').value='typed-area';</script>");

        var form = doc.GetElementById("f");
        var entries = BrowserHost.CollectFormSubmissionEntries(form, submitter: null);
        var (body, _) = BrowserHost.EncodeFormSubmissionBody(form, entries);

        Assert.Equal("q=typed&t=typed-area", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task CheckboxSuccessfulness_FollowsCheckednessNotTheAttribute()
    {
        var doc = await RunAsync(
            "<form id='f'>" +
            "<input type='checkbox' name='on' value='1' checked>" +
            "<input type='checkbox' name='off' value='1'></form>" +
            "<script>var f=document.getElementById('f');" +
            "f.querySelectorAll('input')[0].checked=false;" +
            "f.querySelectorAll('input')[1].checked=true;</script>");

        var entries = BrowserHost.CollectFormSubmissionEntries(doc.GetElementById("f"), submitter: null);

        // The markup says the opposite of the live state; the live state wins.
        Assert.DoesNotContain(entries, e => e.Key == "on");
        Assert.Contains(entries, e => e.Key == "off" && e.Value == "1");
    }

    [Fact]
    public async Task CheckboxWithoutValueAttribute_SubmitsOn()
    {
        var doc = await RunAsync(
            "<form id='f'><input type='checkbox' name='box' checked></form>");

        var entries = BrowserHost.CollectFormSubmissionEntries(doc.GetElementById("f"), submitter: null);

        Assert.Contains(entries, e => e.Key == "box" && e.Value == "on");
    }

    [Fact]
    public async Task FormReset_RestoresDefaultValueAndCheckedness()
    {
        var doc = await RunAsync(
            "<form id='f'><input name='q' value='markup'>" +
            "<input type='checkbox' name='box' value='1' checked></form>" +
            "<script>var f=document.getElementById('f');" +
            "f.querySelector('input').value='typed';" +
            "f.querySelectorAll('input')[1].checked=false;" +
            "f.reset();</script>");

        var entries = BrowserHost.CollectFormSubmissionEntries(doc.GetElementById("f"), submitter: null);

        Assert.Contains(entries, e => e.Key == "q" && e.Value == "markup");
        Assert.Contains(entries, e => e.Key == "box" && e.Value == "1");
    }

    [Fact]
    public async Task DefaultValue_IsWritableAndMovesAPristineValue()
    {
        var doc = await RunAsync(
            "<input id='i' value='a'>" +
            "<script>var i=document.getElementById('i');i.defaultValue='b';" +
            "document.body.setAttribute('data-v', i.value);" +
            "document.body.setAttribute('data-attr', i.getAttribute('value'));</script>");

        // defaultValue reflects the content attribute, and a pristine value follows it.
        Assert.Equal("b", doc.Body.GetAttribute("data-attr"));
        Assert.Equal("b", doc.Body.GetAttribute("data-v"));
    }

    [Fact]
    public async Task DefaultChecked_ReflectsTheAttributeAndCheckedStaysLive()
    {
        var doc = await RunAsync(
            "<input type='checkbox' id='c' checked>" +
            "<script>var c=document.getElementById('c');c.checked=false;" +
            "document.body.setAttribute('data-checked', String(c.checked));" +
            "document.body.setAttribute('data-default', String(c.defaultChecked));</script>");

        Assert.Equal("false", doc.Body.GetAttribute("data-checked"));
        Assert.Equal("true", doc.Body.GetAttribute("data-default"));
        Assert.True(doc.GetElementById("c").HasAttribute("checked"));
    }
}
