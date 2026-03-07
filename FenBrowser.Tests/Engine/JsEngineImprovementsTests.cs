using System;
using System.Linq;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class JsEngineImprovementsTests
    {
        private FenRuntime CreateRuntime()
        {
            return new FenRuntime();
        }

        private Parser CreateParser(string input)
        {
            var lexer = new Lexer(input);
            return new Parser(lexer);
        }

        private void AssertNoParseErrors(string code)
        {
            var parser = CreateParser(code);
            parser.ParseProgram();
            Assert.Empty(parser.Errors);
        }

        // ========== Phase 1: Hex, Octal, Binary Number Literals ==========

        [Fact]
        public void Lexer_HexLiteral_ParsesCorrectly()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var result = 0xFF;");
            var result = runtime.GetGlobal("result");
            Assert.Equal(255.0, result.ToNumber());
        }

        [Fact]
        public void Lexer_HexLiteral_UpperCase()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var result = 0XAB;");
            var result = runtime.GetGlobal("result");
            Assert.Equal(171.0, result.ToNumber());
        }

        [Fact]
        public void Lexer_OctalLiteral_ParsesCorrectly()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var result = 0o77;");
            var result = runtime.GetGlobal("result");
            Assert.Equal(63.0, result.ToNumber());
        }

        [Fact]
        public void Lexer_BinaryLiteral_ParsesCorrectly()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var result = 0b1010;");
            var result = runtime.GetGlobal("result");
            Assert.Equal(10.0, result.ToNumber());
        }

        [Fact]
        public void Lexer_HexLiteral_WithUnderscoreSeparator()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var result = 0xFF_FF;");
            var result = runtime.GetGlobal("result");
            Assert.Equal(65535.0, result.ToNumber());
        }

        [Fact]
        public void Lexer_BinaryLiteral_Zero()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var result = 0b0;");
            var result = runtime.GetGlobal("result");
            Assert.Equal(0.0, result.ToNumber());
        }

        [Fact]
        public void Parse_HexOctalBinary_NoErrors()
        {
            AssertNoParseErrors("var a = 0xFF; var b = 0o77; var c = 0b1010;");
        }

        [Fact]
        public void NavigatorSerial_GetPorts_ShouldResolveEmptyArray()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple(@"
                var result = false;
                navigator.serial.getPorts().then(function(ports) {
                    result = Array.isArray(ports) && ports.length === 0;
                });
            ");

            var result = runtime.GetGlobal("result");
            Assert.True(result.ToBoolean());
        }

        // ========== Phase 2: Template Literal Interpolation ==========

        [Fact]
        public void TemplateLiteral_SimpleInterpolation()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var name = 'World';");
            runtime.ExecuteSimple("var result = `Hello ${name}`;");
            var result = runtime.GetGlobal("result");
            Assert.Equal("Hello World", result.ToString());
        }

        [Fact]
        public void TemplateLiteral_ExpressionInterpolation()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var result = `2 + 3 = ${2 + 3}`;");
            var result = runtime.GetGlobal("result");
            Assert.Equal("2 + 3 = 5", result.ToString());
        }

        [Fact]
        public void TemplateLiteral_MultipleInterpolations()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var a = 'foo'; var b = 'bar';");
            runtime.ExecuteSimple("var result = `${a} and ${b}`;");
            var result = runtime.GetGlobal("result");
            Assert.Equal("foo and bar", result.ToString());
        }

        [Fact]
        public void TemplateLiteral_NoInterpolation()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var result = `plain string`;");
            var result = runtime.GetGlobal("result");
            Assert.Equal("plain string", result.ToString());
        }

        [Fact]
        public void TemplateLiteral_NestedExpression()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var arr = [1,2,3];");
            runtime.ExecuteSimple("var result = `length is ${arr.length}`;");
            var result = runtime.GetGlobal("result");
            Assert.Equal("length is 3", result.ToString());
        }

        [Fact]
        public void Parse_TemplateLiteral_NoErrors()
        {
            AssertNoParseErrors("`hello ${x + y} world`");
        }

        // ========== Phase 3: Labeled Statements ==========

        [Fact]
        public void Parse_LabeledStatement_NoErrors()
        {
            AssertNoParseErrors("outer: for (var i = 0; i < 10; i++) { break outer; }");
        }

        [Fact]
        public void LabeledBreak_ExitsOuterLoop()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple(@"
                var count = 0;
                outer: for (var i = 0; i < 5; i++) {
                    for (var j = 0; j < 5; j++) {
                        count++;
                        if (j === 2) break outer;
                    }
                }
            ");
            var count = runtime.GetGlobal("count");
            Assert.Equal(3.0, count.ToNumber()); // i=0, j=0,1,2 then break outer
        }

        [Fact]
        public void LabeledContinue_ContinuesOuterLoop()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple(@"
                var count = 0;
                outer: for (var i = 0; i < 3; i++) {
                    for (var j = 0; j < 3; j++) {
                        if (j === 1) continue outer;
                        count++;
                    }
                }
            ");
            var count = runtime.GetGlobal("count");
            // i=0: j=0 (count=1), j=1 continue outer
            // i=1: j=0 (count=2), j=1 continue outer
            // i=2: j=0 (count=3), j=1 continue outer
            Assert.Equal(3.0, count.ToNumber());
        }

        // ========== Phase 4: for...of/in Destructuring ==========

        [Fact]
        public void ForOf_ArrayDestructuring()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple(@"
                var pairs = [[1, 'a'], [2, 'b'], [3, 'c']];
                var keys = [];
                var vals = [];
                for (var [k, v] of pairs) {
                    keys.push(k);
                    vals.push(v);
                }
            ");
            var keys = runtime.GetGlobal("keys")?.ToObject();
            var vals = runtime.GetGlobal("vals")?.ToObject();
            Assert.NotNull(keys);
            Assert.NotNull(vals);
            Assert.Equal(3.0, keys.Get("length").ToNumber());
            Assert.Equal(1.0, keys.Get("0").ToNumber());
            Assert.Equal(2.0, keys.Get("1").ToNumber());
            Assert.Equal(3.0, keys.Get("2").ToNumber());
            Assert.Equal("a", vals.Get("0").ToString());
            Assert.Equal("b", vals.Get("1").ToString());
            Assert.Equal("c", vals.Get("2").ToString());
        }

        [Fact]
        public void Parse_ForOf_Destructuring_NoErrors()
        {
            AssertNoParseErrors("for (const [key, value] of entries) { }");
        }

        // ========== Phase 5: Generator Protocol ==========

        [Fact]
        public void Generator_BasicYield()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple(@"
                function* gen() {
                    yield 1;
                    yield 2;
                    yield 3;
                }
                var g = gen();
                var r1 = g.next();
                var r2 = g.next();
                var r3 = g.next();
                var r4 = g.next();
            ");
            var r1 = runtime.GetGlobal("r1")?.ToObject();
            var r2 = runtime.GetGlobal("r2")?.ToObject();
            var r3 = runtime.GetGlobal("r3")?.ToObject();
            var r4 = runtime.GetGlobal("r4")?.ToObject();

            Assert.NotNull(r1);
            Assert.Equal(1.0, r1.Get("value").ToNumber());
            Assert.False(r1.Get("done").ToBoolean());

            Assert.NotNull(r2);
            Assert.Equal(2.0, r2.Get("value").ToNumber());
            Assert.False(r2.Get("done").ToBoolean());

            Assert.NotNull(r3);
            Assert.Equal(3.0, r3.Get("value").ToNumber());
            Assert.False(r3.Get("done").ToBoolean());

            Assert.NotNull(r4);
            Assert.True(r4.Get("done").ToBoolean());
        }

        [Fact]
        public void Generator_ReturnValue()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple(@"
                function* gen() {
                    yield 1;
                    return 42;
                }
                var g = gen();
                var r1 = g.next();
                var r2 = g.next();
                var r3 = g.next();
            ");
            var r1 = runtime.GetGlobal("r1")?.ToObject();
            var r2 = runtime.GetGlobal("r2")?.ToObject();
            var r3 = runtime.GetGlobal("r3")?.ToObject();

            Assert.Equal(1.0, r1.Get("value").ToNumber());
            Assert.False(r1.Get("done").ToBoolean());

            Assert.Equal(42.0, r2.Get("value").ToNumber());
            Assert.True(r2.Get("done").ToBoolean());

            Assert.True(r3.Get("done").ToBoolean());
        }

        [Fact]
        public void Parse_GeneratorFunction_NoErrors()
        {
            AssertNoParseErrors("function* gen() { yield 1; yield 2; }");
        }

        // ========== Phase 6: Computed Property Keys ==========

        [Fact]
        public void ObjectLiteral_ComputedKey()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple(@"
                var key = 'foo';
                var obj = { [key]: 42 };
                var result = obj.foo;
            ");
            var result = runtime.GetGlobal("result");
            Assert.Equal(42.0, result.ToNumber());
        }

        [Fact]
        public void ObjectLiteral_ComputedKey_Expression()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple(@"
                var obj = { ['a' + 'b']: 99 };
                var result = obj.ab;
            ");
            var result = runtime.GetGlobal("result");
            Assert.Equal(99.0, result.ToNumber());
        }

        [Fact]
        public void Parse_ObjectLiteral_ComputedKey_NoErrors()
        {
            AssertNoParseErrors("var obj = { [key]: value, ['str']: 1 };");
        }

        // ========== Combined / Integration ==========

        [Fact]
        public void HexInTemplateLiteral()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple("var result = `hex: ${0xFF}`;");
            var result = runtime.GetGlobal("result");
            Assert.Equal("hex: 255", result.ToString());
        }

        [Fact]
        public void ComputedKey_WithTemplateLiteral()
        {
            var runtime = CreateRuntime();
            runtime.ExecuteSimple(@"
                var prefix = 'item';
                var obj = { [`${prefix}_1`]: 'a', [`${prefix}_2`]: 'b' };
                var r1 = obj.item_1;
                var r2 = obj.item_2;
            ");
            Assert.Equal("a", runtime.GetGlobal("r1").ToString());
            Assert.Equal("b", runtime.GetGlobal("r2").ToString());
        }
    }
}
