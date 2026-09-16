using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// CSSOM 6.2 document.styleSheets and 6.4 CSSStyleSheet.cssRules, backed by the
/// engine's own CSS parser.
/// </summary>
[Collection("Engine Tests")]
public sealed class DocumentStyleSheetsTests : IDisposable
{
    public DocumentStyleSheetsTests()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    public void Dispose()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });

    private static FenJsBrowserScriptEngine CreateEngine(string html)
    {
        var baseUri = new Uri("https://cssom.test/page");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    [Fact]
    public void StyleSheets_ListsStyleAndStylesheetLinksInTreeOrder()
    {
        var engine = CreateEngine(
            "<html><head>" +
            "<style id='a'>.x{color:red}</style>" +
            "<link rel='stylesheet' href='/site.css'>" +
            "<link rel='preload' href='/not-a-sheet.css'>" +
            "</head><body><style id='b'>.y{margin:1px}</style></body></html>");

        Assert.Equal("3", engine.Evaluate("document.styleSheets.length")?.ToString());
        Assert.Equal(
            "STYLE#a|LINK|STYLE#b",
            engine.Evaluate(
                "(function () {" +
                "  var out = [];" +
                "  for (var i = 0; i < document.styleSheets.length; i++) {" +
                "    var owner = document.styleSheets[i].ownerNode;" +
                "    out.push(owner.tagName + (owner.id ? '#' + owner.id : ''));" +
                "  }" +
                "  return out.join('|');" +
                "})()")?.ToString());
    }

    [Fact]
    public void StyleSheets_IsTheSameLiveObjectEachTime()
    {
        // A browser hands back one live StyleSheetList, so identity holds.
        var engine = CreateEngine("<html><head><style>.x{color:red}</style></head><body></body></html>");

        Assert.Equal("True", engine.Evaluate("document.styleSheets === document.styleSheets")?.ToString());

        // ...and it is live: a sheet added afterwards shows up.
        Assert.Equal(
            "1->2",
            engine.Evaluate(
                "(function () {" +
                "  var before = document.styleSheets.length;" +
                "  var added = document.createElement('style');" +
                "  added.textContent = '.z{color:blue}';" +
                "  document.head.appendChild(added);" +
                "  return before + '->' + document.styleSheets.length;" +
                "})()")?.ToString());
    }

    [Fact]
    public void StyleSheet_ReportsHrefOnlyForAnExternalSheet()
    {
        var engine = CreateEngine(
            "<html><head><style>.x{color:red}</style>" +
            "<link rel='stylesheet' href='/site.css'></head><body></body></html>");

        Assert.Equal("null", engine.Evaluate("String(document.styleSheets[0].href)")?.ToString());
        Assert.Equal(
            "https://cssom.test/site.css",
            engine.Evaluate("document.styleSheets[1].href")?.ToString());
    }

    [Fact]
    public void CssRules_ComeFromTheEngineParser()
    {
        var engine = CreateEngine(
            "<html><head><style>" +
            ".a{color:red;background:blue !important}" +
            "@media (min-width:100px){.b{color:green}.c{color:teal}}" +
            "@font-face{font-family:X;src:url(x.woff2)}" +
            "</style></head><body></body></html>");

        Assert.Equal("3", engine.Evaluate("document.styleSheets[0].cssRules.length")?.ToString());

        // CSSRule.STYLE_RULE, with !important carried through.
        Assert.Equal("1", engine.Evaluate("document.styleSheets[0].cssRules[0].type")?.ToString());
        Assert.Equal(".a", engine.Evaluate("document.styleSheets[0].cssRules[0].selectorText")?.ToString());
        Assert.Equal(
            "color: red; background: blue !important;",
            engine.Evaluate("document.styleSheets[0].cssRules[0].style.cssText")?.ToString());

        // CSSRule.MEDIA_RULE, nesting its own rules.
        Assert.Equal("4", engine.Evaluate("document.styleSheets[0].cssRules[1].type")?.ToString());
        Assert.Equal(
            "(min-width:100px)",
            engine.Evaluate("document.styleSheets[0].cssRules[1].conditionText")?.ToString());
        Assert.Equal("2", engine.Evaluate("document.styleSheets[0].cssRules[1].cssRules.length")?.ToString());
        Assert.Equal(".c", engine.Evaluate("document.styleSheets[0].cssRules[1].cssRules[1].selectorText")?.ToString());

        // CSSRule.FONT_FACE_RULE.
        Assert.Equal("5", engine.Evaluate("document.styleSheets[0].cssRules[2].type")?.ToString());
    }

    [Fact]
    public void CssRules_SurviveABraceInsideAString()
    {
        // The reason the rule list has to come from the real parser: the previous
        // shim split the text on "}" and this rule cut in half.
        var engine = CreateEngine(
            "<html><head><style>.d{content:\"}\";color:gold}</style></head><body></body></html>");

        Assert.Equal("1", engine.Evaluate("document.styleSheets[0].cssRules.length")?.ToString());
        Assert.Equal(".d", engine.Evaluate("document.styleSheets[0].cssRules[0].selectorText")?.ToString());
        Assert.Equal(
            "True",
            engine.Evaluate(
                "document.styleSheets[0].cssRules[0].style.cssText.indexOf('gold') >= 0")?.ToString());
    }

    [Fact]
    public void StyleElementSheet_IsTheSameObjectAsTheOneInTheList()
    {
        var engine = CreateEngine("<html><head><style id='a'>.x{color:red}</style></head><body></body></html>");

        Assert.Equal(
            "True",
            engine.Evaluate("document.getElementById('a').sheet === document.styleSheets[0]")?.ToString());
    }

    [Fact]
    public void InsertRuleAndDeleteRule_ActOnTheParsedRules()
    {
        var engine = CreateEngine("<html><head><style>.a{color:red}</style></head><body></body></html>");

        Assert.Equal(
            "2|.b|1",
            engine.Evaluate(
                "(function () {" +
                "  var sheet = document.styleSheets[0];" +
                "  sheet.insertRule('.b { color: blue }', 0);" +
                "  var after = sheet.cssRules.length;" +
                "  var first = sheet.cssRules[0].selectorText;" +
                "  sheet.deleteRule(0);" +
                "  return [after, first, sheet.cssRules.length].join('|');" +
                "})()")?.ToString());
    }
}
