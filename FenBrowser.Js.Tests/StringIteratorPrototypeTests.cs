using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StringIteratorPrototypeTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void StringIteratorPrototypeInheritsFromIteratorPrototype()
    {
        Assert.True(Run("""
            var strItrProto = Object.getPrototypeOf(''[Symbol.iterator]());
            var itrProto = Object.getPrototypeOf(Object.getPrototypeOf([][Symbol.iterator]()));
            Object.getPrototypeOf(strItrProto) === itrProto;
            """).AsBoolean());
    }

    [Fact]
    public void StringIteratorRespectsSurrogatePairs()
    {
        Assert.True(Run("""
            var lo = '\uD834';
            var hi = '\uDF06';
            var pair = lo + hi;
            var iterator = ('a' + pair + 'b')[Symbol.iterator]();
            var r1 = iterator.next();
            var r2 = iterator.next();
            var r3 = iterator.next();
            var r4 = iterator.next();
            r1.value === 'a' && r1.done === false &&
              r2.value === pair && r2.done === false &&
              r3.value === 'b' && r3.done === false &&
              r4.value === undefined && r4.done === true;
            """).AsBoolean());
    }

    [Fact]
    public void StringIteratorNextRejectsObjectsMissingInternalSlots()
    {
        Assert.True(Run("""
            var iterator = ''[Symbol.iterator]();
            var object = Object.create(iterator);
            var threw = false;
            try { object.next(); } catch (e) { threw = e instanceof TypeError; }
            threw;
            """).AsBoolean());
    }
}
