using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    // Spec: ECMA-262 §7.1.6 ToInt32 / §7.1.7 ToUint32. NaN, ±0, ±Infinity must
    // all become 0 before a bitwise op; doubles outside the int range must wrap
    // modulo 2^32. The previous implementation used a naked (int) cast on the
    // ToNumber result, which is implementation-defined in C# for out-of-range
    // doubles and gave wrong answers for both common idioms (`x | 0` truncate)
    // and uncommon ones (huge multiplies followed by bit ops).
    [Collection("Engine Tests")]
    public class BitwiseToInt32Tests
    {
        private static double Eval(string expr)
        {
            var rt = new FenRuntime();
            rt.ExecuteSimple($"var result = ({expr});");
            return rt.GetGlobal("result").ToNumber();
        }

        [Fact]
        public void NaN_ZerosInBitwiseOps()
        {
            Assert.Equal(0d, Eval("NaN | 0"));
            Assert.Equal(0d, Eval("NaN & 0xFF"));
        }

        [Fact]
        public void Infinity_ZerosInBitwiseOps()
        {
            Assert.Equal(0d, Eval("Infinity | 0"));
            Assert.Equal(0d, Eval("-Infinity | 0"));
        }

        [Fact]
        public void LargeDouble_WrapsModulo2Pow32()
        {
            // 2^32 + 5 → 5 after ToInt32 (per spec).
            Assert.Equal(5d, Eval("(Math.pow(2, 32) + 5) | 0"));
            // 2^31 → -2^31 (sign-extends as the high bit is set).
            Assert.Equal(-2147483648d, Eval("Math.pow(2, 31) | 0"));
        }

        [Fact]
        public void NegativeFraction_TruncatesTowardZero()
        {
            Assert.Equal(-3d, Eval("-3.9 | 0"));
            Assert.Equal(3d, Eval("3.9 | 0"));
        }

        [Fact]
        public void UnsignedRightShift_TreatsLeftAsUint32()
        {
            // -1 ToUint32 = 0xFFFFFFFF, so (-1 >>> 0) === 4294967295.
            Assert.Equal(4294967295d, Eval("-1 >>> 0"));
        }

        [Fact]
        public void LeftShift_MasksRightTo5Bits()
        {
            // (1 << 33) is (1 << 1) because the shift count is masked.
            Assert.Equal(2d, Eval("1 << 33"));
        }

        [Fact]
        public void BitwiseNot_OfNaN_IsMinusOne()
        {
            // ~NaN === ~0 === -1.
            Assert.Equal(-1d, Eval("~NaN"));
        }
    }
}
