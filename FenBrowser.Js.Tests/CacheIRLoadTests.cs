using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Behaviour of the property-load cache. A cache is only as good as the cases
/// where it must refuse to answer, so most of these drive it to a state where a
/// stale hit would be observable and check that it is not.
/// </summary>
public sealed class CacheIRLoadTests
{
    private static JsValue Run(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function);
    }

    private static string RunString(string source) => Run(source).AsString();

    private static double RunNumber(string source) => Run(source).AsNumber();

    [Fact]
    public void ReadsAnOwnDataPropertyRepeatedly()
    {
        Assert.Equal(
            40d,
            RunNumber(
                "function read(o) { return o.x; }" +
                "var a = { x: 10 };" +
                "read(a) + read(a) + read(a) + read(a);"));
    }

    [Fact]
    public void SeesAWriteThroughTheCachedSlot()
    {
        Assert.Equal(
            "1|2|3",
            RunString(
                "function read(o) { return o.x; }" +
                "var a = { x: 1 };" +
                "var out = [];" +
                "out.push(read(a)); a.x = 2;" +
                "out.push(read(a)); a.x = 3;" +
                "out.push(read(a));" +
                "out.join('|');"));
    }

    [Fact]
    public void DistinguishesObjectsOfDifferentShape()
    {
        Assert.Equal(
            "1|2|3|4",
            RunString(
                "function read(o) { return o.x; }" +
                "var a = { x: 1 };" +
                "var b = { y: 0, x: 2 };" +
                "var c = { z: 0, w: 0, x: 3 };" +
                "var d = { p: 0, q: 0, r: 0, x: 4 };" +
                "[read(a), read(b), read(c), read(d)].join('|');"));
    }

    [Fact]
    public void KeepsAnsweringPastTheSiteCapacity()
    {
        // A fifth shape turns the site megamorphic; every read must still be
        // correct, just answered by the general path.
        Assert.Equal(
            "1|2|3|4|5|6|1",
            RunString(
                "function read(o) { return o.x; }" +
                "var os = [{x:1},{a:0,x:2},{b:0,c:0,x:3},{d:0,e:0,f:0,x:4}," +
                "          {g:0,h:0,i:0,j:0,x:5},{k:0,l:0,m:0,n:0,o:0,x:6}];" +
                "var out = [];" +
                "for (var i = 0; i < os.length; i++) out.push(read(os[i]));" +
                "out.push(read(os[0]));" +
                "out.join('|');"));
    }

    [Fact]
    public void HonoursAnAccessorInstalledAfterTheCacheWarmed()
    {
        // The shape is unchanged by redefining an existing data property as an
        // accessor, so only the slot re-check can catch this.
        Assert.Equal(
            "plain|plain|plain|from getter",
            RunString(
                "function read(o) { return o.x; }" +
                "var a = { x: 'plain' };" +
                "var out = [read(a), read(a), read(a)];" +
                "Object.defineProperty(a, 'x', { get: function () { return 'from getter'; } });" +
                "out.push(read(a));" +
                "out.join('|');"));
    }

    [Fact]
    public void RunsTheGetterEveryTimeAfterInvalidation()
    {
        Assert.Equal(
            3d,
            RunNumber(
                "function read(o) { return o.x; }" +
                "var calls = 0;" +
                "var a = { x: 0 };" +
                "read(a); read(a);" +
                "Object.defineProperty(a, 'x', { get: function () { calls++; return calls; } });" +
                "read(a); read(a); read(a);" +
                "calls;"));
    }

    [Fact]
    public void FallsBackToThePrototypeAfterAnOwnPropertyIsDeleted()
    {
        Assert.Equal(
            "own|own|inherited",
            RunString(
                "function read(o) { return o.x; }" +
                "var proto = { x: 'inherited' };" +
                "var a = Object.create(proto);" +
                "a.x = 'own';" +
                "var out = [read(a), read(a)];" +
                "delete a.x;" +
                "out.push(read(a));" +
                "out.join('|');"));
    }

    [Fact]
    public void NeverCachesAnInheritedPropertyAsOwn()
    {
        Assert.Equal(
            "base|base|shadow",
            RunString(
                "function read(o) { return o.x; }" +
                "var proto = { x: 'base' };" +
                "var a = Object.create(proto);" +
                "var out = [read(a), read(a)];" +
                "a.x = 'shadow';" +
                "out.push(read(a));" +
                "out.join('|');"));
    }

    [Fact]
    public void RoutesEveryReadOfAProxyThroughItsTrap()
    {
        Assert.Equal(
            5d,
            RunNumber(
                "function read(o) { return o.x; }" +
                "var hits = 0;" +
                "var p = new Proxy({ x: 1 }, { get: function (t, k) { hits++; return t[k]; } });" +
                "read(p); read(p); read(p); read(p); read(p);" +
                "hits;"));
    }

    [Fact]
    public void DoesNotLeakOneObjectsValueToAnotherOfTheSameShape()
    {
        Assert.Equal(
            "1|2|1|2",
            RunString(
                "function read(o) { return o.x; }" +
                "var a = { x: 1 };" +
                "var b = { x: 2 };" +
                "[read(a), read(b), read(a), read(b)].join('|');"));
    }

    [Fact]
    public void GuardsTheKeyAtSitesWhoseKeyVaries()
    {
        // obj[k] reuses one site for several keys; without a key guard the
        // second read would answer with the first key's slot.
        Assert.Equal(
            "1|2|1|2",
            RunString(
                "function read(o, k) { return o[k]; }" +
                "var a = { x: 1, y: 2 };" +
                "[read(a,'x'), read(a,'y'), read(a,'x'), read(a,'y')].join('|');"));
    }

    [Fact]
    public void KeyGuardSurvivesRepetitionAndShapeChange()
    {
        Assert.Equal(
            "1|2|1|2|9",
            RunString(
                "function read(o, k) { return o[k]; }" +
                "var a = { x: 1, y: 2 };" +
                "var out = [];" +
                "for (var i = 0; i < 2; i++) { out.push(read(a,'x')); out.push(read(a,'y')); }" +
                "a.z = 9;" +
                "out.push(read(a,'z'));" +
                "out.join('|');"));
    }

    [Fact]
    public void ReadsAMissingPropertyAsUndefined()
    {
        Assert.Equal(
            "undefined|undefined|7",
            RunString(
                "function read(o) { return o.x; }" +
                "var a = {};" +
                "var out = [String(read(a)), String(read(a))];" +
                "a.x = 7;" +
                "out.push(String(read(a)));" +
                "out.join('|');"));
    }

    [Fact]
    public void NonWritablePropertyStillReadsAndRejectsWrites()
    {
        Assert.Equal(
            "5|5|5",
            RunString(
                "function read(o) { return o.x; }" +
                "var a = {};" +
                "Object.defineProperty(a, 'x', { value: 5, writable: false, configurable: true });" +
                "var out = [read(a), read(a)];" +
                "try { a.x = 9; } catch (e) {}" +
                "out.push(read(a));" +
                "out.join('|');"));
    }

    [Fact]
    public void SurvivesAPrototypeSwap()
    {
        Assert.Equal(
            "own|own|own",
            RunString(
                "function read(o) { return o.x; }" +
                "var a = { x: 'own' };" +
                "var out = [read(a), read(a)];" +
                "Object.setPrototypeOf(a, { x: 'other' });" +
                "out.push(read(a));" +
                "out.join('|');"));
    }

    [Fact]
    public void PolymorphicSiteStaysCorrectOnceTheFunctionIsCompiled()
    {
        // Compiled code inlines the guard for the first attached program only,
        // so a site rotating between shapes reaches the miss path on most reads.
        // If that path re-attached a shape the site already held, the site would
        // exhaust its capacity and every read would go generic.
        Assert.Equal(
            "ok",
            RunString(
                "function read(o) { return o.x; }" +
                "var a = { x: 1 };" +
                "var b = { p: 0, x: 2 };" +
                "var c = { q: 0, r: 0, x: 3 };" +
                "var want = [1, 2, 3];" +
                "var os = [a, b, c];" +
                "for (var i = 0; i < 900; i++) {" +
                "  var k = i % 3;" +
                "  if (read(os[k]) !== want[k]) { throw new Error('wrong at ' + i); }" +
                "}" +
                "'ok';"));
    }

    [Fact]
    public void MonomorphicSiteStaysCorrectAcrossManyCompiledCalls()
    {
        Assert.Equal(
            900d,
            RunNumber(
                "function read(o) { return o.x; }" +
                "var a = { x: 1 };" +
                "var total = 0;" +
                "for (var i = 0; i < 900; i++) total += read(a);" +
                "total;"));
    }

    [Fact]
    public void CompiledSiteHonoursAnAccessorInstalledLate()
    {
        // The same invalidation, but after the function has tiered up.
        Assert.Equal(
            "1|getter",
            RunString(
                "function read(o) { return o.x; }" +
                "var a = { x: 1 };" +
                "for (var i = 0; i < 900; i++) read(a);" +
                "var before = read(a);" +
                "Object.defineProperty(a, 'x', { get: function () { return 'getter'; } });" +
                "before + '|' + read(a);"));
    }

    [Fact]
    public void CompiledSiteStillRoutesProxiesThroughTheirTrap()
    {
        Assert.Equal(
            300d,
            RunNumber(
                "function read(o) { return o.x; }" +
                "var plain = { x: 1 };" +
                "for (var i = 0; i < 900; i++) read(plain);" +
                "var hits = 0;" +
                "var p = new Proxy({ x: 1 }, { get: function (t, k) { hits++; return t[k]; } });" +
                "for (var i = 0; i < 300; i++) read(p);" +
                "hits;"));
    }

    [Fact]
    public void ADenseArrayLengthProgramAnswersOnlyLength()
    {
        // A site reading `o[k]` sees many names through one offset. The length
        // program is the one that guards no shape, so without a key guard it
        // answered the element count for every one of them: `a["0"]` came back
        // as 3.
        Assert.Equal(
            "3|10|20|undefined",
            RunString(
                "function get(o, k) { return o[k]; }" +
                "var a = [10, 20, 30];" +
                "[String(get(a, 'length'))," +
                " String(get(a, '0'))," +
                " String(get(a, '1'))," +
                " String(get(a, 'foo'))].join('|');"));
    }

    [Fact]
    public void CachedAndUncachedSitesAgreeOnTheSameObject()
    {
        Assert.Equal(
            "true",
            RunString(
                "function warmed(o) { return o.x; }" +
                "var a = { p: 0, x: 'v' };" +
                "warmed(a); warmed(a); warmed(a);" +
                "String(warmed(a) === a.x && warmed(a) === a['x']);"));
    }
}
