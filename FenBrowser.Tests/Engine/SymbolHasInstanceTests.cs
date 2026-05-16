using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    // Spec: ECMA-262 §7.3.21 InstanceofOperator — before falling back to the
    // ordinary prototype walk, instanceof must look up @@hasInstance on the
    // right-hand operand and invoke it with the left operand. Without this,
    // class { static [Symbol.hasInstance](v) { … } } is silently ignored.
    [Collection("Engine Tests")]
    public class SymbolHasInstanceTests
    {
        [Fact]
        public void Instanceof_ConsultsSymbolHasInstance_WhenPresent()
        {
            var rt = new FenRuntime();
            rt.ExecuteSimple(@"
                function Matcher() {}
                Matcher[Symbol.hasInstance] = function(v) { return v === 42; };
                var a = 42 instanceof Matcher;
                var b = 41 instanceof Matcher;
            ");

            Assert.True(rt.GetGlobal("a").ToBoolean(),
                "@@hasInstance returning true must make instanceof return true");
            Assert.False(rt.GetGlobal("b").ToBoolean());
        }

        [Fact]
        public void Instanceof_FallsBackToOrdinary_WhenNoSymbolHasInstance()
        {
            var rt = new FenRuntime();
            rt.ExecuteSimple(@"
                function Animal() {}
                var a = new Animal();
                var matches = a instanceof Animal;
                var noMatch = ({}) instanceof Animal;
            ");

            Assert.True(rt.GetGlobal("matches").ToBoolean());
            Assert.False(rt.GetGlobal("noMatch").ToBoolean());
        }

        [Fact]
        public void Instanceof_SymbolHasInstance_ReturnsBooleanByToBoolean()
        {
            // The spec calls ToBoolean on the handler's return value, so any
            // truthy/falsy non-bool should still coerce correctly.
            var rt = new FenRuntime();
            rt.ExecuteSimple(@"
                function M() {}
                M[Symbol.hasInstance] = function() { return 'yes'; };
                var truthy = 0 instanceof M;
                M[Symbol.hasInstance] = function() { return 0; };
                var falsy = 0 instanceof M;
            ");

            Assert.True(rt.GetGlobal("truthy").ToBoolean());
            Assert.False(rt.GetGlobal("falsy").ToBoolean());
        }
    }
}
