using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class AnnexBRegExpTests
{
    private static JsValue Run(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function);
    }

    [Fact]
    public void LegacyContextAliasesHaveAccessorsAndRejectWrongReceiver()
    {
        Assert.True(Run(@"
            var left = Object.getOwnPropertyDescriptor(RegExp, '$`');
            var right = Object.getOwnPropertyDescriptor(RegExp, ""$'"");
            var threw = false;
            try { left.get.call({}); } catch (error) { threw = error instanceof TypeError; }
            typeof left.get === 'function' && left.set === undefined &&
            typeof right.get === 'function' && right.set === undefined && threw;
        ").AsBoolean());
    }

    [Fact]
    public void CompileUpdatesMatcherThenThrowsWhenLastIndexIsReadOnly()
    {
        Assert.True(Run(@"
            var value = /initial/;
            Object.defineProperty(value, 'lastIndex', { value: 45, writable: false });
            var threw = false;
            try { value.compile(/updated/gi); } catch (error) { threw = error instanceof TypeError; }
            threw && value.toString() === '/updated/gi' && value.lastIndex === 45;
        ").AsBoolean());
    }

    [Fact]
    public void CompileRejectsSubclassInstances()
    {
        Assert.True(Run(@"
            class DerivedRegExp extends RegExp {}
            var value = new DerivedRegExp('');
            var threw = false;
            try { value.compile(); } catch (error) { threw = error instanceof TypeError; }
            threw;
        ").AsBoolean());
    }

    [Fact]
    public void NonUnicodePatternsUseAnnexBIdentityAndClassRangeSemantics()
    {
        Assert.True(Run(@"
            var invalidControl = new RegExp('\\cА');
            /\x/.test('x') &&
            /O\PQ/.test('OPQ') &&
            /[--\d]+/.exec('.-0123456789-.')[0] === '-0123456789-' &&
            invalidControl.test('\\cА');
        ").AsBoolean());
    }

    [Fact]
    public void NonUnicodeBareClassCloseIsLiteralForWebCompat()
    {
        Assert.True(Run(@"
            var value = /\s*] */y;
            value.test(']  ');
        ").AsBoolean());
    }

    [Fact]
    public void NonUnicodeKIsIdentityEscapeWithoutNamedCaptures()
    {
        Assert.Equal(511, Run(@"
            ( /\k<a>/.test('k<a>') ? 1 : 0 ) +
            ( /\k<a/.test('k<a') ? 2 : 0 ) +
            ( /\k<a>(?<=>)a/.test('k<a>a') ? 4 : 0 ) +
            ( /(?<!a>)\k<a>/.test('k<a>') ? 8 : 0 ) +
            ( 'xxxk<a>xxx'.match(/\k<a>/)[0] === 'k<a>' ? 16 : 0 ) +
            ( 'xxxk<a>xxx'.match(/\k<a/)[0] === 'k<a' ? 32 : 0 ) +
            ( /\k<a>(<a>x)/.test('k<a><a>x') ? 64 : 0 ) +
            ( /\k<a>\1/.test('k<a>\x01') ? 128 : 0 ) +
            ( /\1(b)\k<a>/.test('bk<a>') ? 256 : 0 );
        ").AsNumber());
    }

    [Fact]
    public void SourcePreservesEscapedBackslashBeforeIdentityEscape()
    {
        Assert.Equal(@"\\N", Run(@"/\\N/.source;").AsString());
    }

    [Fact]
    public void StickyTokenizerSkipsEscapedBackslashBeforeIdentityEscape()
    {
        Assert.True(Run(@"
            var token = /\\([ABCE-RTUVXYZaeg-mopqyz]|c(?![A-Za-z])|u(?![\dA-Fa-f]{4}|{[\dA-Fa-f]+})|x(?![\dA-Fa-f]{2}))/gy;
            var fallback = /\\(?:0(?:[0-3][0-7]{0,2}|[4-7][0-7]?)?|[1-9]\d*|x[\dA-Fa-f]{2}|u(?:[\dA-Fa-f]{4}|{[\dA-Fa-f]+})|c[A-Za-z]|[\s\S])|\(\?(?:[:=!]|<[=!])|[?*+]\?|{\d+(?:,\d*)?}\??|[\s\S]/gy;
            var input = '\\\\N';
            token.lastIndex = 0;
            var firstToken = token.exec(input);
            fallback.lastIndex = 0;
            var firstFallback = fallback.exec(input);
            token = new RegExp(token.source, 'gy');
            token.lastIndex = firstFallback[0].length;
            var secondToken = token.exec(input);
            firstToken === null &&
                firstFallback[0] === '\\\\' &&
                firstFallback.index === 0 &&
                fallback.lastIndex === 2 &&
                secondToken === null &&
                token.lastIndex === 0;
        ").AsBoolean());
    }

    [Fact]
    public void ReplacementTokenEscapesBackslashForXRegExpEscapePattern()
    {
        Assert.Equal(@"\\N", Run(@"
            String('\\N').replace(/[-\[\]{}()*+?.,\\^$|#\s]/g, '\\$&');
        ").AsString());
    }

    [Fact]
    public void ReplacementTokenEscapesMdnSearchHighlightSyntax()
    {
        Assert.Equal(@"a\+b\[c\]", Run(@"
            'a+b[c]'.replaceAll(/[.*+?^${}()|[\]\\]/g, String.raw`\$&`);
        ").AsString());
    }

    [Fact]
    public void RegexLiteralAllowsEscapedSlashInPattern()
    {
        Assert.True(Run(@"
            /\/api\/v1/.test('/api/v1/users');
        ").AsBoolean());
    }

    [Fact]
    public void SplitClonesAfterSymbolMatchGetterAndBeforeLimitCoercion()
    {
        Assert.Equal("a||a;|bb|", Run(@"
            var first = /a/;
            Object.defineProperty(first, Symbol.match, { get: function () { first.compile('b'); } });
            var firstResult = first[Symbol.split]('abba');

            var second = /a/;
            var limit = { valueOf: function () { second.compile('b'); return -1; } };
            var secondResult = second[Symbol.split]('abba', limit);

            firstResult.join('|') + ';' + secondResult.join('|');
        ").AsString());
    }
}
