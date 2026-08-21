using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class FenJsCharacterClassRangeTests
{
    [Fact]
    public async Task NullEscapeRange_IsValidAndMatchesControlCharacters()
    {
        var result = await EvaluateAsync(
            "var r = new RegExp('([\\\\0-\\\\x1f\\\\x7f]|^-?\\\\d)|^-$|[^\\\\x80-\\\\uFFFF\\\\w-]');" +
            "[r.test('\\u0005'), r.test('\\u007f'), r.test('-'), r.test('a')].join('|');");

        Assert.Equal("true|true|true|false", result);
    }

    [Fact]
    public async Task NullEscapeRange_LiteralSourceCompilesAndReplaces()
    {
        var result = await EvaluateAsync(
            "var rcssescape = /([\\0-\\x1f\\x7f]|^-?\\d)|^-$|[^\\x80-\\uFFFF\\w-]/g;" +
            "['a\\u0005b\\u007f-'.replace(rcssescape, '-'), typeof rcssescape.exec].join('|');");

        Assert.Equal("a-b--|function", result);
    }

    [Fact]
    public async Task NullEscape_MatchesOnlyNulCharacter()
    {
        var result = await EvaluateAsync(
            "[/[\\0]/.test('\\u0000'), /[\\0]/.test('0'), 'a\\u0000b'.replace(/[\\0]/g, 'N')].join('|');");

        Assert.Equal("true|false|aNb", result);
    }

    [Fact]
    public async Task OutOfOrderRangeAgainstNullEscape_StillThrows()
    {
        var result = await EvaluateAsync(
            "var outcomes = [];" +
            "try { new RegExp('[\\\\x1f-\\\\0]'); outcomes.push('compiled'); }" +
            "catch (error) { outcomes.push(error.name); }" +
            "try { new RegExp('[b-\\\\0]'); outcomes.push('compiled'); }" +
            "catch (error) { outcomes.push(error.name); }" +
            "outcomes.join('|');");

        Assert.Equal("SyntaxError|SyntaxError", result);
    }

    private static async Task<string> EvaluateAsync(string script)
    {
        var baseUri = new Uri("https://fixture.test/character-class-range.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
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
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
