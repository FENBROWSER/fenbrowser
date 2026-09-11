using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// Members reCAPTCHA reads while fingerprinting the browser, each of which
/// exists in every mainstream engine. A missing one is a "not a browser" tell.
/// </summary>
public sealed class FenJsCaptchaProbedApisTests
{
    [Theory]
    [InlineData("typeof document.elementFromPoint", "function")]
    [InlineData("typeof document.elementsFromPoint", "function")]
    [InlineData("typeof navigator.mediaDevices", "object")]
    [InlineData("typeof navigator.mediaDevices.enumerateDevices", "function")]
    [InlineData("document.createTextNode('ab').wholeText", "ab")]
    [InlineData("typeof document.body.innerText", "string")]
    [InlineData("'ariaInvalid' in document.createElement('input')", "true")]
    [InlineData("(function(){var i=new Image(); return typeof i.addEventListener;})()", "function")]
    [InlineData("(function(){var i=new Image(10,20); return i.width+'x'+i.height;})()", "10x20")]
    [InlineData("(function(){var i=document.createElement('img'); return i.width+'x'+i.height+':'+i.naturalWidth;})()", "0x0:0")]
    public async Task ProbedMemberIsPresent(string expression, string expected)
    {
        var engine = await CreateEngineAsync("<html><body><div id=\"d\" style=\"width:100px;height:100px\">x</div></body></html>");
        var result = engine.Evaluate($"String({expression});");
        Assert.Equal(expected, result?.ToString());
    }

    [Fact]
    public async Task InnerTextFollowsBlocksBreaksAndHiddenContent()
    {
        var engine = await CreateEngineAsync(
            "<html><body><div id=\"r\"><p>Select all   squares with <strong>buses</strong></p>" +
            "<script>var x = 1;</script><span style=\"display:none\">hidden</span>" +
            "<div>If there are none,<br>click skip</div></div></body></html>");

        var result = engine.Evaluate("document.getElementById('r').innerText.split('\\n').join('|')");
        Assert.Equal("Select all squares with buses|If there are none,|click skip", result?.ToString());
    }

    [Fact]
    public async Task InnerTextSetterTurnsLineBreaksIntoBr()
    {
        var engine = await CreateEngineAsync("<html><body><div id=\"r\"><b>old</b></div></body></html>");

        var result = engine.Evaluate("""
            (function () {
                var r = document.getElementById('r');
                r.innerText = 'one\ntwo';
                return r.querySelectorAll('br').length + ':' + r.childNodes.length + '|' + r.innerText.split('\n').join('/');
            })();
            """);
        Assert.Equal("1:3|one/two", result?.ToString());
    }

    [Fact]
    public async Task AriaReflectionRoundTripsThroughAttributes()
    {
        var engine = await CreateEngineAsync("<html><body><input id=\"i\" aria-invalid=\"true\"></body></html>");

        var result = engine.Evaluate("""
            (function () {
                var i = document.getElementById('i');
                var out = [i.ariaInvalid, i.ariaLabel];
                i.ariaLabel = 'Name'; out.push(i.getAttribute('aria-label'));
                i.ariaValueMax = '10'; out.push(i.getAttribute('aria-valuemax'));
                i.ariaInvalid = null; out.push(i.hasAttribute('aria-invalid'));
                return JSON.stringify(out);
            })();
            """);
        Assert.Equal("[\"true\",null,\"Name\",\"10\",false]", result?.ToString());
    }

    [Fact]
    public async Task WholeTextSpansContiguousTextSiblings()
    {
        var engine = await CreateEngineAsync("<html><body><p id=\"p\"></p></body></html>");

        var result = engine.Evaluate("""
            (function () {
                var p = document.getElementById('p');
                p.appendChild(document.createTextNode('a'));
                p.appendChild(document.createTextNode('b'));
                p.appendChild(document.createElement('span'));
                p.appendChild(document.createTextNode('c'));
                return p.firstChild.wholeText + '|' + p.lastChild.wholeText;
            })();
            """);
        Assert.Equal("ab|c", result?.ToString());
    }

    [Fact]
    public async Task ElementFromPointWithoutAHostHitTesterFallsBackToTheRoot()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");
        Assert.Equal("HTML", engine.Evaluate("document.elementFromPoint(10, 10).tagName")?.ToString());
        Assert.Equal("null", engine.Evaluate("String(document.elementFromPoint(-1, 10))")?.ToString());
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string html)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
