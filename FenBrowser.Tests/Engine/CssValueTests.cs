using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssValueTests
    {
        [Fact]
        public void ParseLength_Px()
        {
            var val = CssValueParser.Parse("10px");
            Assert.IsType<CssLength>(val);
            var len = (CssLength)val;
            Assert.Equal(10, len.Value);
            Assert.Equal(CssUnit.Px, len.Unit);
        }

        [Fact]
        public void ParseLength_Percent()
        {
            var val = CssValueParser.Parse("50%");
            Assert.IsType<CssLength>(val);
            var len = (CssLength)val;
            Assert.Equal(50, len.Value);
            Assert.Equal(CssUnit.Percent, len.Unit); // Percent handled as unit in our parser
            Assert.Equal(CssValueType.Percentage, val.Type); // Type check
        }

        [Fact]
        public void ParseLength_Decimal()
        {
            var val = CssValueParser.Parse("1.5em");
            Assert.IsType<CssLength>(val);
            var len = (CssLength)val;
            Assert.Equal(1.5, len.Value);
            Assert.Equal(CssUnit.Em, len.Unit);
        }

        [Fact]
        public void ParseNumber_NoUnit()
        {
            var val = CssValueParser.Parse("42");
            Assert.IsType<CssNumber>(val);
            var num = (CssNumber)val;
            Assert.Equal(42, num.Value);
        }

        [Fact]
        public void ParseColor_Hex()
        {
            var val = CssValueParser.Parse("#ff0000");
            Assert.IsType<CssColor>(val);
            var col = (CssColor)val;
            Assert.Equal(SKColors.Red, col.Color);
        }

        [Fact]
        public void ParseColor_Named()
        {
            var val = CssValueParser.Parse("blue");
            Assert.IsType<CssColor>(val);
            var col = (CssColor)val;
            Assert.Equal(SKColors.Blue, col.Color);
        }

        [Fact]
        public void ParseKeyword_Auto()
        {
            var val = CssValueParser.Parse("auto");
            Assert.IsType<CssKeyword>(val);
            var kw = (CssKeyword)val;
            Assert.True(kw.IsAuto);
            Assert.Equal("auto", kw.Keyword);
        }

        [Fact]
        public void ParseUnknown_String()
        {
            var val = CssValueParser.Parse("something-weird");
            Assert.IsType<CssKeyword>(val);
        }

        [Fact]
        public void ParseString_Quoted()
        {
            var val = CssValueParser.Parse("\"quoted string\"");
            Assert.IsType<CssString>(val);
            Assert.Equal("quoted string", ((CssString)val).Value);
        }
    }
}
