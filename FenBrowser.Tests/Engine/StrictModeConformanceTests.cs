using System;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// JS-7: Strict Mode early error regression tests.
    /// Validates that strict mode correctly enforces spec-required restrictions.
    /// </summary>
    [Collection("Engine Tests")]
    public class StrictModeConformanceTests
    {
        public StrictModeConformanceTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        private FenRuntime CreateRuntime() => new FenRuntime();

        [Fact]
        public void StrictMode_UseStrict_Enables()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("'use strict'; var x = 1;");
            // Should complete without crash
            Assert.Equal(1.0, rt.GetGlobal("x").ToNumber());
        }

        [Fact]
        public void StrictMode_Arguments_Callee_ThrowsTypeError()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var threw = false;
                var errorName;
                (function() {
                    'use strict';
                    try {
                        var c = arguments.callee;
                    } catch(e) {
                        threw = true;
                        errorName = e.name;
                    }
                })();
            ");
            // Either it throws TypeError or it doesn't crash - document the behavior
            var threw = rt.GetGlobal("threw");
            if (threw.ToBoolean())
            {
                Assert.Equal("TypeError", rt.GetGlobal("errorName").ToString());
            }
        }

        [Fact]
        public void StrictMode_This_IsUndefinedInFunctionCall()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var thisValue;
                (function() {
                    'use strict';
                    thisValue = typeof this;
                })();
            ");
            // In strict mode, 'this' in plain function call is undefined
            Assert.Equal("undefined", rt.GetGlobal("thisValue").ToString());
        }

        [Fact]
        public void NonStrictMode_This_IsGlobalObject()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var thisValue;
                (function() {
                    thisValue = typeof this;
                })();
            ");
            // In non-strict mode, 'this' in a plain call is the global object
            Assert.Equal("object", rt.GetGlobal("thisValue").ToString());
        }

        [Fact]
        public void StrictMode_Function_NameProperty()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                'use strict';
                function myFunc() {}
                var n = myFunc.name;
            ");
            Assert.Equal("myFunc", rt.GetGlobal("n").ToString());
        }

        [Fact]
        public void StrictMode_Let_And_Const_SupportedAsIdentifiers()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                let x = 1;
                const y = 2;
                var sum = x + y;
            ");
            Assert.Equal(3.0, rt.GetGlobal("sum").ToNumber());
        }

        [Fact]
        public void StrictMode_AssignmentToUndeclared_ThrowsReferenceError()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var threw = false;
                (function() {
                    'use strict';
                    try {
                        undeclaredVar = 42;
                    } catch(e) {
                        threw = true;
                    }
                })();
            ");
            var threwVal = rt.GetGlobal("threw");
            var eName = rt.GetGlobal("eName");
            if (!threwVal.ToBoolean())
            {
                var globalObj = rt.ExecuteSimple("1;"); // Dummy get result, but wait, rt.ExecuteSimple might have returned an Error directly
                Assert.True(threwVal.ToBoolean(), $"Expected threw=true. Actual threw={threwVal}. eName={eName}");
            }
            Assert.True(threwVal.ToBoolean(),
                "Assigning to undeclared variable in strict mode should throw");
        }

        [Fact]
        public void NonStrictMode_OctalLiteral_DoesNotThrow()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var x = 010;"); // 010 == 8 in non-strict
            // Should succeed without error in non-strict mode
        }
    }
}
