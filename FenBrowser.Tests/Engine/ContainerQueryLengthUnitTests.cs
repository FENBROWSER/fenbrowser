using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class ContainerQueryLengthUnitTests
    {
        [Theory]
        [InlineData("cqw", CssUnit.Cqw)]
        [InlineData("cqh", CssUnit.Cqh)]
        [InlineData("cqi", CssUnit.Cqi)]
        [InlineData("cqb", CssUnit.Cqb)]
        [InlineData("cqmin", CssUnit.Cqmin)]
        [InlineData("cqmax", CssUnit.Cqmax)]
        public void ParseUnit_RecognizesContainerQueryUnits(string unitText, CssUnit expected)
        {
            Assert.Equal(expected, CssValueParser.ParseUnit(unitText));
            Assert.True(CssValueParser.IsContainerUnit(expected));
            Assert.True(CssValueParser.IsLengthUnit(expected));
        }

        [Fact]
        public void ToPx_ContainerQueryUnits_UseContainerDimensions()
        {
            const double containerWidth = 400;
            const double containerHeight = 200;

            Assert.Equal(40, CssValueParser.ToPx(10, CssUnit.Cqw, containerWidth: containerWidth, containerHeight: containerHeight), 6);
            Assert.Equal(20, CssValueParser.ToPx(10, CssUnit.Cqh, containerWidth: containerWidth, containerHeight: containerHeight), 6);
            Assert.Equal(40, CssValueParser.ToPx(10, CssUnit.Cqi, containerWidth: containerWidth, containerHeight: containerHeight), 6);
            Assert.Equal(20, CssValueParser.ToPx(10, CssUnit.Cqb, containerWidth: containerWidth, containerHeight: containerHeight), 6);
            Assert.Equal(20, CssValueParser.ToPx(10, CssUnit.Cqmin, containerWidth: containerWidth, containerHeight: containerHeight), 6);
            Assert.Equal(40, CssValueParser.ToPx(10, CssUnit.Cqmax, containerWidth: containerWidth, containerHeight: containerHeight), 6);
        }

        [Fact]
        public void ToPx_ContainerQueryUnits_FallbackToViewportWhenContainerNotProvided()
        {
            const double viewportWidth = 1200;
            const double viewportHeight = 800;

            Assert.Equal(120, CssValueParser.ToPx(10, CssUnit.Cqw, viewportWidth: viewportWidth, viewportHeight: viewportHeight), 6);
            Assert.Equal(80, CssValueParser.ToPx(10, CssUnit.Cqh, viewportWidth: viewportWidth, viewportHeight: viewportHeight), 6);
            Assert.Equal(80, CssValueParser.ToPx(10, CssUnit.Cqmin, viewportWidth: viewportWidth, viewportHeight: viewportHeight), 6);
            Assert.Equal(120, CssValueParser.ToPx(10, CssUnit.Cqmax, viewportWidth: viewportWidth, viewportHeight: viewportHeight), 6);
        }
    }
}
