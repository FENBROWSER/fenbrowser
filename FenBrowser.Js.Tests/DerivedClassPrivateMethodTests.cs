using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 13.3.7.1 SuperCall step 10: InitializeInstanceElements brands the
// object super() returned with the class's private methods and accessors
// (PrivateMethodOrAccessorAdd). A subclass whose only private members were
// methods or accessors left its instances unbranded, so `this.#m()` threw
// "Cannot read private field from an object whose class did not declare it".
public sealed class DerivedClassPrivateMethodTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void ASubclassWithOnlyPrivateMethodsCanCallThem()
    {
        Assert.Equal("ok", Run(@"
            class Base {}
            class K extends Base { #m() { return 'ok'; } r() { return this.#m(); } }
            new K().r();"));
    }

    [Fact]
    public void ASubclassWithAnExplicitConstructorCanCallAPrivateMethodInIt()
    {
        Assert.Equal("ok", Run(@"
            class Base {}
            class K extends Base { #m() { return 'ok'; } constructor() { super(); this.r = this.#m(); } }
            new K().r;"));
    }

    [Fact]
    public void ASubclassWithOnlyAPrivateGetterCanReadIt()
    {
        Assert.Equal("ok", Run(@"
            class Base {}
            class K extends Base { get #g() { return 'ok'; } r() { return this.#g; } }
            new K().r();"));
    }

    [Fact]
    public void AnObjectNotConstructedByTheClassStillFailsTheBrandCheck()
    {
        Assert.Equal("TypeError", Run(@"
            class Base {}
            class K extends Base { #m() { return 'ok'; } static call(o) { return o.#m(); } }
            var name;
            try { K.call({}); } catch (e) { name = e.constructor.name; }
            name;"));
    }
}
