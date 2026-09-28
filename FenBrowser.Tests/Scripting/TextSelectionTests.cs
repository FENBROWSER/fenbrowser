using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML §4.10.5.2.10 text control selection APIs. selectionStart was missing
/// entirely on x.com's login input, which React reads to restore the caret.
/// </summary>
public sealed class TextSelectionTests
{
    [Fact]
    public async Task SetSelectionRangeIsReadBackAndClampedToTheValue()
    {
        var engine = await CreateEngineAsync("<input id=i value=hello>");
        var result = engine.Evaluate(
            "(function () {" +
            "  var i = document.getElementById('i'), out = [];" +
            "  i.setSelectionRange(1, 3, 'backward'); out.push(i.selectionStart, i.selectionEnd, i.selectionDirection);" +
            "  i.setSelectionRange(4, 99); out.push(i.selectionStart, i.selectionEnd, i.selectionDirection);" +
            "  i.setSelectionRange(4, 2); out.push(i.selectionStart, i.selectionEnd);" +
            "  return out.join(',');" +
            "})()")?.ToString();

        Assert.Equal("1,3,backward,4,5,none,2,2", result);
    }

    [Fact]
    public async Task SettingTheValueMovesTheSelectionToTheEnd()
    {
        var engine = await CreateEngineAsync("<textarea id=t></textarea>");
        var result = engine.Evaluate(
            "(function () {" +
            "  var t = document.getElementById('t');" +
            "  t.value = 'aukr1';" +
            "  var out = [t.selectionStart, t.selectionEnd];" +
            "  t.select(); out.push(t.selectionStart, t.selectionEnd);" +
            "  t.selectionStart = 2; out.push(t.selectionStart, t.selectionEnd);" +
            "  t.selectionEnd = 1; out.push(t.selectionStart, t.selectionEnd);" +
            "  return out.join(',');" +
            "})()")?.ToString();

        Assert.Equal("5,5,0,5,2,5,1,1", result);
    }

    [Fact]
    public async Task SetRangeTextReplacesAndPlacesTheSelectionByMode()
    {
        var engine = await CreateEngineAsync("<input id=i value=abcdef>");
        var result = engine.Evaluate(
            "(function () {" +
            "  var i = document.getElementById('i'), out = [];" +
            "  i.setRangeText('XY', 1, 3, 'select'); out.push(i.value, i.selectionStart, i.selectionEnd);" +
            "  i.setRangeText('Z', 0, 1, 'end'); out.push(i.value, i.selectionStart, i.selectionEnd);" +
            "  i.setSelectionRange(4, 6); i.setRangeText('--', 0, 2); out.push(i.value, i.selectionStart, i.selectionEnd);" +
            "  return out.join(',');" +
            "})()")?.ToString();

        Assert.Equal("aXYdef,1,3,ZXYdef,1,1,--Ydef,4,6", result);
    }

    [Fact]
    public async Task ControlsWithoutSelectionReturnNullAndThrowOnWrite()
    {
        var engine = await CreateEngineAsync("<input id=n type=number value=5><input id=e type=email>");
        var result = engine.Evaluate(
            "(function () {" +
            "  var n = document.getElementById('n'), e = document.getElementById('e'), out = [n.selectionStart, e.selectionEnd, n.selectionDirection];" +
            "  try { n.setSelectionRange(0, 1); out.push('no throw'); } catch (x) { out.push(x.name); }" +
            "  try { e.selectionStart = 0; out.push('no throw'); } catch (x) { out.push(x.name); }" +
            "  n.select(); out.push('select ok');" +
            "  return out.join(',');" +
            "})()")?.ToString();

        Assert.Equal(",,,InvalidStateError,InvalidStateError,select ok", result);
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string body)
    {
        var baseUri = new Uri("https://example.test/");
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
