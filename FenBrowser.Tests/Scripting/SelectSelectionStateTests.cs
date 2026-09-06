using System;
using System.Collections.Generic;
using System.Linq;
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
/// HTML 4.10.10: an option's selectedness is internal state and the "selected"
/// content attribute is only its default. A select is more than a bag of
/// booleans -- a single-selection list keeps exactly one option selected, and
/// "ask for a reset" gives a dropdown an implicit selection.
/// </summary>
[Collection("Engine Tests")]
public sealed class SelectSelectionStateTests
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

    private static string Submit(Document doc)
    {
        var form = doc.GetElementById("f");
        var entries = BrowserHost.CollectFormSubmissionEntries(form, submitter: null);
        var (body, _) = BrowserHost.EncodeFormSubmissionBody(form, entries);
        return Encoding.UTF8.GetString(body);
    }

    [Fact]
    public async Task AssigningSelected_DoesNotWriteTheSelectedAttribute()
    {
        var doc = await RunAsync(
            "<select id='s'><option id='a' value='a' selected>A</option>" +
            "<option id='b' value='b'>B</option></select>" +
            "<script>document.getElementById('b').selected = true;</script>");

        // The attribute is defaultSelected and must stay where the markup put it.
        Assert.True(doc.GetElementById("a").HasAttribute("selected"));
        Assert.False(doc.GetElementById("b").HasAttribute("selected"));

        Assert.False(SelectSelection.IsSelected(doc.GetElementById("a")));
        Assert.True(SelectSelection.IsSelected(doc.GetElementById("b")));
    }

    [Fact]
    public async Task SelectingOne_DeselectsTheOthersInASingleSelectionList()
    {
        var doc = await RunAsync(
            "<select id='s'><option value='a' selected>A</option>" +
            "<option value='b'>B</option><option value='c'>C</option></select>" +
            "<script>var s=document.getElementById('s');" +
            "s.options ? 0 : 0;" +
            "document.querySelectorAll('option')[2].selected = true;" +
            "document.body.setAttribute('data-value', s.value);" +
            "document.body.setAttribute('data-index', String(s.selectedIndex));</script>");

        Assert.Equal("c", doc.Body.GetAttribute("data-value"));
        Assert.Equal("2", doc.Body.GetAttribute("data-index"));

        var options = doc.QuerySelectorAll("option").OfType<Element>().ToList();
        Assert.False(SelectSelection.IsSelected(options[0]));
        Assert.True(SelectSelection.IsSelected(options[2]));
    }

    [Fact]
    public async Task MultipleSelect_KeepsEveryAssignedSelection()
    {
        var doc = await RunAsync(
            "<select id='s' multiple><option value='a'>A</option>" +
            "<option value='b'>B</option><option value='c'>C</option></select>" +
            "<script>var o=document.querySelectorAll('option');" +
            "o[0].selected=true;o[2].selected=true;</script>");

        var options = doc.QuerySelectorAll("option").OfType<Element>().ToList();

        Assert.True(SelectSelection.IsSelected(options[0]));
        Assert.False(SelectSelection.IsSelected(options[1]));
        Assert.True(SelectSelection.IsSelected(options[2]));
    }

    [Fact]
    public async Task Dropdown_SelectsItsFirstOptionWhenTheMarkupSelectsNone()
    {
        // "Ask for a reset": a dropdown always shows something.
        var doc = await RunAsync(
            "<form id='f'><select name='s'><option value='a'>A</option>" +
            "<option value='b'>B</option></select></form>");

        Assert.Equal("s=a", Submit(doc));
    }

    [Fact]
    public async Task MultipleSelect_SelectsNothingByDefault()
    {
        var doc = await RunAsync(
            "<form id='f'><select name='s' multiple><option value='a'>A</option>" +
            "<option value='b'>B</option></select></form>");

        Assert.Equal(string.Empty, Submit(doc));
    }

    [Fact]
    public async Task MultipleSelect_SubmitsOneEntryPerSelectedOption()
    {
        var doc = await RunAsync(
            "<form id='f'><select name='s' multiple><option value='a'>A</option>" +
            "<option value='b'>B</option><option value='c'>C</option></select></form>" +
            "<script>var o=document.querySelectorAll('option');" +
            "o[0].selected=true;o[2].selected=true;</script>");

        Assert.Equal("s=a&s=c", Submit(doc));
    }

    [Fact]
    public async Task SubmissionFollowsLiveSelection_NotTheMarkup()
    {
        var doc = await RunAsync(
            "<form id='f'><select name='s'><option value='a' selected>A</option>" +
            "<option value='b'>B</option></select></form>" +
            "<script>document.querySelectorAll('option')[1].selected=true;</script>");

        Assert.Equal("s=b", Submit(doc));
    }

    [Fact]
    public async Task SelectValueSetter_SelectsTheMatchingOption()
    {
        var doc = await RunAsync(
            "<form id='f'><select name='s'><option value='a'>A</option>" +
            "<option value='b'>B</option></select></form>" +
            "<script>document.querySelector('select').value='b';</script>");

        Assert.Equal("s=b", Submit(doc));
    }

    [Fact]
    public async Task SelectValueSetter_WithNoMatchFallsBackToTheFirstOption()
    {
        var doc = await RunAsync(
            "<form id='f'><select name='s'><option value='a'>A</option>" +
            "<option value='b'>B</option></select></form>" +
            "<script>document.querySelector('select').value='nope';</script>");

        // No match deselects everything; a dropdown then re-picks its first option.
        Assert.Equal("s=a", Submit(doc));
    }

    [Fact]
    public async Task SelectedIndexSetter_MovesTheSelection()
    {
        var doc = await RunAsync(
            "<form id='f'><select name='s'><option value='a'>A</option>" +
            "<option value='b'>B</option><option value='c'>C</option></select></form>" +
            "<script>document.querySelector('select').selectedIndex=2;</script>");

        Assert.Equal("s=c", Submit(doc));
    }

    [Fact]
    public async Task OptionWithoutValueAttribute_SubmitsItsText()
    {
        var doc = await RunAsync(
            "<form id='f'><select name='s'><option>Plain Text</option></select></form>");

        Assert.Equal("s=Plain+Text", Submit(doc));
    }

    [Fact]
    public async Task DisabledOption_IsNotSuccessful()
    {
        var doc = await RunAsync(
            "<form id='f'><select name='s' multiple>" +
            "<option value='a' selected disabled>A</option>" +
            "<option value='b' selected>B</option></select></form>");

        Assert.Equal("s=b", Submit(doc));
    }

    [Fact]
    public async Task OptionsInsideOptgroup_AreFound()
    {
        var doc = await RunAsync(
            "<form id='f'><select name='s'><optgroup label='g'>" +
            "<option value='a'>A</option><option value='b' selected>B</option>" +
            "</optgroup></select></form>");

        Assert.Equal("s=b", Submit(doc));
    }

    [Fact]
    public async Task DefaultSelected_ReflectsTheAttributeWhileSelectedStaysLive()
    {
        var doc = await RunAsync(
            "<select id='s'><option id='a' value='a' selected>A</option>" +
            "<option id='b' value='b'>B</option></select>" +
            "<script>var a=document.getElementById('a');" +
            "document.getElementById('b').selected=true;" +
            "document.body.setAttribute('data-sel', String(a.selected));" +
            "document.body.setAttribute('data-def', String(a.defaultSelected));</script>");

        Assert.Equal("false", doc.Body.GetAttribute("data-sel"));
        Assert.Equal("true", doc.Body.GetAttribute("data-def"));
    }

    [Fact]
    public async Task FormReset_RestoresTheMarkupSelection()
    {
        var doc = await RunAsync(
            "<form id='f'><select name='s'><option value='a' selected>A</option>" +
            "<option value='b'>B</option></select></form>" +
            "<script>var f=document.getElementById('f');" +
            "document.querySelectorAll('option')[1].selected=true;" +
            "f.reset();</script>");

        Assert.Equal("s=a", Submit(doc));
    }

    [Fact]
    public async Task DisabledSelect_ContributesNothing()
    {
        var doc = await RunAsync(
            "<form id='f'><select name='s' disabled><option value='a' selected>A</option>" +
            "</select><input name='q' value='1'></form>");

        Assert.Equal("q=1", Submit(doc));
    }
}
