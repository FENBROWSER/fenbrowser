using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 10.4.4.7 CreateMappedArgumentsObject: in a sloppy function with a
// simple parameter list, an argument index and its parameter are one binding
// until the index leaves the map.
public sealed class MappedArgumentsObjectTests
{
    [Theory]
    // A parameter written, then read through arguments; and the reverse.
    [InlineData("function f(n) { n = 5; var a = arguments[0]; arguments[0] = 9; return a + ',' + n; } f(1);", "5,9")]
    // Only indices that were passed are mapped.
    [InlineData("function f(a, b) { arguments[1] = 3; return String(b); } f(1);", "undefined")]
    // Of two parameters with the same name, the later one is mapped.
    [InlineData("function f(a, a) { arguments[0] = 1; arguments[1] = 2; return a; } f(10, 20);", "2")]
    // A closure writing the parameter is seen through arguments.
    [InlineData("function f(a) { (() => { a = 11; })(); return arguments[0]; } f(1);", "11")]
    // The object keeps aliasing after the call has returned.
    [InlineData("function f(a) { var x = arguments; return function () { x[0] = 42; return a; }; } f(1)();", "42")]
    // A sloppy generator's arguments too.
    [InlineData("function* g(a) { arguments[0] = 5; yield a; } g(1).next().value;", "5")]
    // A direct eval assigning the parameter.
    [InlineData("function f(a) { eval('a = 12'); return arguments[0]; } f(1);", "12")]
    // A loop long enough to be handed to compiled code.
    [InlineData("function f(a) { for (var i = 0; i < 100000; i++) a = (a + 1) | 0; return arguments[0]; } f(0);", "100000")]
    public void IndexAndParameterAreOneBinding(string source, string expected)
        => Assert.Equal(expected, Run(source));

    [Theory]
    [InlineData("function f(a) { 'use strict'; a = 2; return arguments[0]; } f(1);", "1")]
    [InlineData("function f(a, b = 1) { a = 2; return arguments[0]; } f(1);", "1")]
    [InlineData("function f(...r) { r[0] = 2; return arguments[0]; } f(1);", "1")]
    public void StrictAndNonSimpleParameterListsAreNotMapped(string source, string expected)
        => Assert.Equal(expected, Run(source));

    [Theory]
    // 10.4.4.5 [[Delete]] removes the mapping.
    [InlineData("function f(a) { delete arguments[0]; arguments[0] = 7; return a; } f(1);", "1")]
    // 10.4.4.2: made read-only, the index keeps its value and stops following.
    [InlineData("function f(a) { Object.defineProperty(arguments, '0', { writable: false }); a = 2; return arguments[0] + ',' + a; } f(1);", "1,2")]
    // ... a value defined on it still reaches the parameter first.
    [InlineData("function f(a) { Object.defineProperty(arguments, '0', { value: 4 }); return a; } f(1);", "4")]
    // An accessor ends the mapping.
    [InlineData("function f(a) { Object.defineProperty(arguments, '0', { get() { return 8; } }); a = 3; return arguments[0] + ',' + a; } f(1);", "8,3")]
    // Enumerability changes without unmapping.
    [InlineData("function f(a) { Object.defineProperty(arguments, '0', { enumerable: false }); a = 6; return arguments[0] + ',' + Object.keys(arguments).length; } f(1, 2);", "6,1")]
    // Freezing a non-extensible object still freezes its mapped indices.
    [InlineData("function f(a) { Object.freeze(arguments); a = 5; return arguments[0] + ',' + Object.isFrozen(arguments); } f(1);", "1,true")]
    [InlineData("function f(a) { Object.preventExtensions(arguments); Object.defineProperty(arguments, '0', { writable: false }); a = 9; return arguments[0] + ',' + Object.getOwnPropertyNames(arguments).join(); } f(1);", "1,0,length,callee")]
    public void TheMappingEndsWhereTheSpecificationSays(string source, string expected)
        => Assert.Equal(expected, Run(source));

    [Theory]
    [InlineData("function f(a, b) { return Reflect.ownKeys(arguments).map(String).join(); } f(1, 2, 3);",
                "0,1,2,length,callee,Symbol(Symbol.iterator)")]
    [InlineData("function f(a) { return JSON.stringify(Object.getOwnPropertyDescriptor(arguments, '0')); } f(1);",
                "{\"value\":1,\"writable\":true,\"enumerable\":true,\"configurable\":true}")]
    [InlineData("function f(a) { var s = 0; for (var k in arguments) s += arguments[k]; a = 100; for (var k in arguments) s += arguments[k]; return s; } f(1, 2);",
                "105")]
    public void MappedIndicesAreOrdinaryOwnProperties(string source, string expected)
        => Assert.Equal(expected, Run(source));

    [Fact]
    public void OnlySloppySimpleBodiesThatUseArgumentsAreMapped()
    {
        Assert.True(Nested("function f(a) { return arguments; }").UsesMappedArgumentsObject);
        Assert.False(Nested("function f() { return arguments; }").UsesMappedArgumentsObject);
        Assert.False(Nested("function f(a) { return a; }").UsesMappedArgumentsObject);
        Assert.False(Nested("function f(a) { 'use strict'; return arguments; }").UsesMappedArgumentsObject);
        Assert.False(Nested("function f({ a }) { return arguments; }").UsesMappedArgumentsObject);
    }

    private static BytecodeFunction Nested(string source)
        => Assert.Single(new BytecodeCompiler().CompileScript(new SourceText(source)).NestedFunctions);

    private static string Run(string source)
    {
        // Each source ends in the call, whose value is the script's completion.
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        return result.Tag == JsValueTag.String
            ? result.AsString()
            : result.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
