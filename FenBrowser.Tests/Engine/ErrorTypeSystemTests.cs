using System;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// JS-3: Error Type System + instanceof chain regression tests.
    /// Validates Error prototype chain, error names, messages, and toString output.
    /// </summary>
    [Collection("Engine Tests")]
    public class ErrorTypeSystemTests
    {
        public ErrorTypeSystemTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        private FenRuntime CreateRuntime() => new FenRuntime();

        [Fact]
        public void Error_BasicCreation_HasNameAndMessage()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var e = new Error('test error');");
            var e = rt.GetGlobal("e").AsObject();
            Assert.NotNull(e);
            Assert.Equal("Error", e.Get("name").ToString());
            Assert.Equal("test error", e.Get("message").ToString());
        }

        [Fact]
        public void TypeError_HasCorrectName()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var e = new TypeError('type is wrong');");
            var e = rt.GetGlobal("e").AsObject();
            Assert.NotNull(e);
            Assert.Equal("TypeError", e.Get("name").ToString());
            Assert.Equal("type is wrong", e.Get("message").ToString());
        }

        [Fact]
        public void RangeError_HasCorrectName()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var e = new RangeError('out of range');");
            var e = rt.GetGlobal("e").AsObject();
            Assert.NotNull(e);
            Assert.Equal("RangeError", e.Get("name").ToString());
        }

        [Fact]
        public void SyntaxError_HasCorrectName()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var e = new SyntaxError('bad syntax');");
            var e = rt.GetGlobal("e").AsObject();
            Assert.NotNull(e);
            Assert.Equal("SyntaxError", e.Get("name").ToString());
        }

        [Fact]
        public void ReferenceError_HasCorrectName()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var e = new ReferenceError('undefined ref');");
            var e = rt.GetGlobal("e").AsObject();
            Assert.NotNull(e);
            Assert.Equal("ReferenceError", e.Get("name").ToString());
        }

        [Fact]
        public void Error_ToString_ReturnsNameColonMessage()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var e = new Error('oops'); var s = e.toString();");
            var s = rt.GetGlobal("s");
            Assert.Equal("Error: oops", s.ToString());
        }

        [Fact]
        public void TypeError_ToString_IncludesTypeName()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var e = new TypeError('bad type'); var s = e.toString();");
            var s = rt.GetGlobal("s");
            Assert.Equal("TypeError: bad type", s.ToString());
        }

        [Fact]
        public void Error_CatchInTryCatch_WorksCorrectly()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var caught;
                try {
                    throw new TypeError('thrown type error');
                } catch (e) {
                    caught = e.name + ':' + e.message;
                }
            ");
            var v = rt.GetGlobal("caught");
            Assert.Equal("TypeError:thrown type error", v.ToString());
        }

        [Fact]
        public void Error_Instanceof_ErrorIsTrue()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var result = (new Error('x')) instanceof Error;");
            var v = rt.GetGlobal("result");
            Assert.True(v.ToBoolean());
        }

        [Fact]
        public void TypeError_Instanceof_TypeErrorIsTrue()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var result = (new TypeError('x')) instanceof TypeError;");
            var v = rt.GetGlobal("result");
            Assert.True(v.ToBoolean());
        }

        [Fact]
        public void TypeError_Instanceof_Error_IsTrue()
        {
            var rt = CreateRuntime();
            // TypeError should inherit from Error via prototype chain
            rt.ExecuteSimple("var result = (new TypeError('x')) instanceof Error;");
            var v = rt.GetGlobal("result");
            Assert.True(v.ToBoolean(), "TypeError instanceof Error should be true via prototype chain");
        }

        [Fact]
        public void AggregateError_HasErrors_Array()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var e = new AggregateError([new Error('a'), new Error('b')], 'multiple');");
            var e = rt.GetGlobal("e").AsObject();
            Assert.NotNull(e);
            Assert.Equal("AggregateError", e.Get("name").ToString());
            Assert.Equal("multiple", e.Get("message").ToString());
            var errors = e.Get("errors");
            Assert.True(errors.IsObject);
        }

        [Fact]
        public void Error_WithCause_ES2022_HasCause()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var original = new Error('original');
                var wrapped = new Error('wrapper', { cause: original });
                var cause = wrapped.cause;
            ");
            var cause = rt.GetGlobal("cause");
            Assert.True(cause.IsObject, "cause should be the original error object");
        }
    }
}
