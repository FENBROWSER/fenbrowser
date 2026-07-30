using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Regex;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RegExpLiteralTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void RegexLiteralIsRegExpObject()
    {
        var result = Run("var r = /abc/; r instanceof RegExp;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralTestMethodReturnsBoolean()
    {
        var result = Run("/abc/.test('abc');");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralTestMethodMatches()
    {
        var result = Run("/abc/.test('hello abc world');");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralTestMethodNonMatch()
    {
        var result = Run("/abc/.test('hello world');");
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralWithFlagsReadable()
    {
        var result = Run("var r = /abc/gi; r.global && r.ignoreCase;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralIgnoringCase()
    {
        var result = Run("/abc/i.test('ABC');");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralGlobalFlag()
    {
        var result = Run("var r = /abc/g; r.global;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralMultilineFlag()
    {
        var result = Run("var r = /abc/m; r.multiline;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralDotAllFlag()
    {
        var result = Run("var r = /a.b/s; r.dotAll;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralToString()
    {
        var result = Run("/abc/gi.toString();");
        Assert.Equal("/abc/gi", result.AsString());
    }

    [Fact]
    public void RegexLiteralSource()
    {
        var result = Run("/abc/gi.source;");
        Assert.Equal("abc", result.AsString());
    }

    [Fact]
    public void RegexLiteralLastIndex()
    {
        var result = Run("/abc/g.lastIndex;");
        Assert.Equal(0.0, result.AsNumber(), 4);
    }

    [Fact]
    public void RegexLiteralAllowsEscapedPunctuationClassRanges()
    {
        var result = Run(@"var r = /[\x00- \x22\x27-\x29\x3c\x3e\\\x7b\x7d\x7f\x85\xa0\u2028\u2029\uff01\uff03\uff04\uff06-\uff0c\uff0f\uff1a\uff1b\uff1d\uff1f\uff20\uff3b\uff3d]/g;
            r.test(' ') &&
            (r.lastIndex = 0, r.test(String.fromCharCode(0x22))) &&
            (r.lastIndex = 0, !r.test('A'));");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegExpCompilerKeepsMatcherForEscapedPunctuationClassRanges()
    {
        const string pattern = @"[\x00- \x22\x27-\x29\x3c\x3e\\\x7b\x7d\x7f\x85\xa0\u2028\u2029\uff01\uff03\uff04\uff06-\uff0c\uff0f\uff1a\uff1b\uff1d\uff1f\uff20\uff3b\uff3d]";
        var compiled = RegExpCompiler.Compile(pattern, "g");
        var vm = new RegexVM(compiled.Program);
        Assert.True(vm.Execute(" ").Success);
        Assert.True(vm.Execute("\"").Success);
        Assert.False(vm.Execute("A").Success);
    }

    [Fact]
    public void RegExpConstructorAllowsEscapedPunctuationClassRanges()
    {
        var result = Run(@"var r = new RegExp('[\\x00- \\x22\\x27-\\x29\\x3c\\x3e\\\\\\x7b\\x7d\\x7f\\x85\\xa0\\u2028\\u2029\\uff01\\uff03\\uff04\\uff06-\\uff0c\\uff0f\\uff1a\\uff1b\\uff1d\\uff1f\\uff20\\uff3b\\uff3d]', 'g');
            r.test(' ') &&
            (r.lastIndex = 0, r.test(String.fromCharCode(0x22))) &&
            (r.lastIndex = 0, !r.test('A'));");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegExpConstructorRejectsReversedCharacterClassRange()
    {
        Assert.Throws<JsThrownException>(() => Run(@"new RegExp('^[z-a]$');"));
    }

    [Fact]
    public void NativeRegexMatchesNamedBackreferenceInLegacyModeWhenPatternHasNamedCaptures()
    {
        var compiled = RegExpCompiler.Compile(@"(?<a>.)(?<b>.)(?<c>.)\k<c>\k<b>\k<a>", "d");
        var match = new RegexVM(compiled.Program).Execute("abccba");

        Assert.True(match.Success, string.Join("\n", compiled.Program.Instructions.Select((ins, i) => $"{i}: {ins.OpCode} A={ins.A} B={ins.B} C={ins.C}")));
        Assert.Equal("abccba", match.GetGroup(0));
        Assert.Equal("a", match.GetGroup(1));
        Assert.Equal("b", match.GetGroup(2));
        Assert.Equal("c", match.GetGroup(3));
    }

    [Fact]
    public void RegexLiteralReturnsNamedBackreferenceIndicesInLegacyMode()
    {
        var result = Run(@"var r = /(?<a>.)(?<b>.)(?<c>.)\k<c>\k<b>\k<a>/d.exec('abccba');
            r !== null &&
            r.groups.a === 'a' &&
            r.groups.b === 'b' &&
            r.groups.c === 'c' &&
            r.indices.groups.a[0] === 0 &&
            r.indices.groups.a[1] === 1 &&
            r.indices.groups.b[0] === 1 &&
            r.indices.groups.b[1] === 2 &&
            r.indices.groups.c[0] === 2 &&
            r.indices.groups.c[1] === 3;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralCanonicalizesEscapedUnicodeGroupNames()
    {
        var result = Run(@"var pi = /(?<\u{03C0}>a)/du.exec('bab');
            var brown = /(?<\ud835\udcd1\ud835\udcfb\ud835\udcf8\ud835\udd00\ud835\udcf7>brown)/u.exec('brown');
            pi.indices.groups.π[0] === 1 &&
            pi.indices.groups.π[1] === 2 &&
            brown.groups.𝓑𝓻𝓸𝔀𝓷 === 'brown';");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralResolvesDuplicateNamedCapturesByParticipatingGroup()
    {
        var result = Run(@"var alt = /(?<x>a)|(?<x>b)/.exec('bab');
            var backref = /(?:(?<x>a)|(?<x>b))\k<x>/.exec('bb');
            var repeated = /(?:(?:(?<x>a)|(?<x>b))\k<x>){2}/.exec('aabb');
            var stale = /^(?:(?<a>x)|(?<a>y)|z){2}\k<a>$/.exec('xzx');
            var indices = '..ba'.match(/(?<x>a)|(?<x>b)/d).indices;
            alt[0] === 'b' &&
            alt[1] === undefined &&
            alt[2] === 'b' &&
            alt.groups.x === 'b' &&
            backref[0] === 'bb' &&
            backref[1] === undefined &&
            backref[2] === 'b' &&
            backref.groups.x === 'b' &&
            repeated[0] === 'aabb' &&
            repeated[1] === undefined &&
            repeated[2] === 'b' &&
            repeated.groups.x === 'b' &&
            stale === null &&
            indices.groups.x[0] === 2 &&
            indices.groups.x[1] === 3;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralPreservesLookbehindCaptureSemantics()
    {
        var result = Run(@"var fixed = 'abcdef'.match(/(?<=(?<a>\w){3})f/u);
            var greedy = 'abcdef'.match(/(?<=(?<a>\w)+)f/u);
            var empty = 'abcdef'.match(/(?<a>(?<=\w{3}))f/u);
            var impossible = 'abcdef'.match(/(?<=$abc)def/);
            var boundary = 'ab cdef'.match(/(?<=\B)\w{3}/);
            var nested = 'abcdef'.match(/(?<=a(?=([^a]{2})d)\w{3})\w\w/);
            fixed[1] === 'c' &&
            fixed.groups.a === 'c' &&
            greedy.groups.a === 'a' &&
            empty[1] === '' &&
            empty.groups.a === '' &&
            impossible === null &&
            boundary[0] === 'def' &&
            nested[0] === 'ef' &&
            nested[1] === 'bc';");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralRejectsReversedCharacterClassRange()
    {
        Assert.Throws<JsParserException>(() => Run(@"/^[z-a]$/;"));
    }

    [Fact]
    public void RegExpCallWithRegExpAndUndefinedFlagsReturnsSameObject()
    {
        var result = Run("var r = /x/i; RegExp(r) === r;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegExpCallWithSymbolMatchAndSameConstructorReturnsInput()
    {
        var result = Run("var o = { constructor: RegExp }; o[Symbol.match] = true; RegExp(o) === o;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegExpConstructorInheritsFromFunctionPrototype()
    {
        var result = Run("Function.prototype.isPrototypeOf(RegExp);");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void UnicodeDotMatchesSingleSurrogatePairCodePoint()
    {
        var result = Run("/^.$/u.test('\\ud800\\udc00');");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void UnicodeSimpleClassMatchesSingleSurrogatePairCodePoint()
    {
        var result = Run("/^[\\ud800\\udc00]$/u.test('\\ud800\\udc00');");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void UnicodeCaseFoldKelvinRequiresUAndI()
    {
        var result = Run("/\\u212a/i.test('k') === false && /\\u212a/iu.test('k') === true;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void UnicodeBackreferenceMatchesByCodePoint()
    {
        var result = Run("/(.+).*\\1/u.test('\\ud800\\udc00\\ud800') === false;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void UnicodeAstralQuantifierCountsCodePoints()
    {
        var result = Run("/\\ud834\\udf06{2}/u.test('\\ud834\\udf06\\ud834\\udf06');");
        Assert.True(result.AsBoolean());
    }
}
