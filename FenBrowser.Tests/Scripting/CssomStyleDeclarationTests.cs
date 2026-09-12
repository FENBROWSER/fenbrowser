using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// CSSOM §6.7.3: every supported property is a camel-cased accessor on
/// CSSStyleDeclaration (plus cssFloat), on the inline style object and on the
/// computed style; §4.3: computed colors serialise as rgb()/rgba(); CSS Syntax 3
/// §8: a declaration with an invalid value is dropped from the cascade.
/// </summary>
[Collection("Engine Tests")]
public sealed class CssomStyleDeclarationTests
{
    private static async Task<string> RunAsync(string html, string script)
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync("data:text/html," + Uri.EscapeDataString(html)));
            var result = await browser.ExecuteScriptAsync("(function(){" + script + "})();");
            return result?.ToString() ?? string.Empty;
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task InlineStyleExposesCssFloatAndArbitraryCamelCasedProperties()
    {
        var result = await RunAsync(
            "<div id='d'></div>",
            @"var d = document.getElementById('d');
              var before = d.style.cssFloat;
              d.setAttribute('style', 'float: right; text-indent: 3px');
              var a = d.style.cssFloat + '|' + d.style.textIndent + '|' + d.style['text-indent'];
              d.style.cssFloat = 'none';
              d.style.letterSpacing = '2px';
              return [before, a, d.style.cssFloat, d.getAttribute('style').indexOf('letter-spacing:2px') >= 0].join('/');");

        Assert.Equal("/right|3px|3px/none/true", result);
    }

    [Fact]
    public async Task ComputedStyleMirrorsDashedAndCamelCasedNamesAndSerialisesColors()
    {
        var result = await RunAsync(
            "<style>#p { white-space: pre-wrap; color: gray; float: left }</style><p id='p'>x</p>",
            @"var cs = getComputedStyle(document.getElementById('p'));
              return [cs.whiteSpace, cs['white-space'], cs.getPropertyValue('white-space'), cs.color, cs.cssFloat].join('|');");

        Assert.Equal("pre-wrap|pre-wrap|pre-wrap|rgb(128, 128, 128)|left", result);
    }

    [Fact]
    public async Task InvalidKeywordDeclarationDoesNotOverrideAnEarlierValidOne()
    {
        var result = await RunAsync(
            "<style>#p { white-space: pre-wrap; white-space: x-bogus; cursor: help; cursor: -acid3-bogus; color: gray; color: -acid3-bogus }</style><p id='p'>x</p>",
            @"var cs = getComputedStyle(document.getElementById('p'));
              return [cs.whiteSpace, cs.cursor, cs.color].join('|');");

        Assert.Equal("pre-wrap|help|rgb(128, 128, 128)", result);
    }
}
