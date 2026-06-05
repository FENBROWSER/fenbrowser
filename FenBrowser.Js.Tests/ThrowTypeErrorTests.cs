using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ThrowTypeErrorTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void StrictArgumentsCalleeGetterExistsAndIsCallable()
    {
        Assert.True(Run("""
            var thrower = Object.getOwnPropertyDescriptor((function() {
              "use strict";
              return arguments;
            })(), "callee").get;
            typeof thrower === "function";
            """).AsBoolean());
    }

    [Fact]
    public void StrictArgumentsCalleeGetterThrowsTypeError()
    {
        Assert.True(Run("""
            var thrower = Object.getOwnPropertyDescriptor((function() {
              "use strict";
              return arguments;
            })(), "callee").get;
            var ok = false;
            try { thrower(); } catch (e) { ok = e instanceof TypeError; }
            ok;
            """).AsBoolean());
    }

    [Fact]
    public void StrictArgumentsCalleeGetterHasThrowTypeErrorShape()
    {
        Assert.True(Run("""
            var thrower = Object.getOwnPropertyDescriptor((function() {
              "use strict";
              return arguments;
            })(), "callee").get;
            var names = Object.getOwnPropertyNames(thrower);
            var lengthIndex = names.indexOf("length");
            var nameIndex = names.indexOf("name");
            var lengthDesc = Object.getOwnPropertyDescriptor(thrower, "length");
            var nameDesc = Object.getOwnPropertyDescriptor(thrower, "name");
            Object.getPrototypeOf(thrower) === Function.prototype &&
              Object.isFrozen(thrower) &&
              lengthIndex >= 0 &&
              nameIndex === lengthIndex + 1 &&
              lengthDesc.value === 0 &&
              lengthDesc.writable === false &&
              lengthDesc.enumerable === false &&
              lengthDesc.configurable === false &&
              nameDesc.value === "" &&
              nameDesc.writable === false &&
              nameDesc.enumerable === false &&
              nameDesc.configurable === false &&
              !Object.prototype.hasOwnProperty.call(thrower, "caller") &&
              !Object.prototype.hasOwnProperty.call(thrower, "arguments");
            """).AsBoolean());
    }
}
