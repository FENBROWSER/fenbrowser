using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssContainerQueryTests
    {
        private static bool Evaluate(string query, float width, float height)
        {
            var method = typeof(CssLoader).GetMethod("IsContainerConditionMet", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var result = method.Invoke(null, new object[] { query, width, height });
            return result is bool b && b;
        }

        [Fact]
        public void ContainerCondition_RangeAndLogicalOperators_AreEvaluated()
        {
            Assert.True(Evaluate("(min-width: 40em) and (max-width: 80rem)", 900, 600));
            Assert.True(Evaluate("not (max-width: 600px)", 900, 600));
            Assert.False(Evaluate("(max-width: 600px) and (min-height: 800px)", 900, 700));
        }

        [Fact]
        public void ContainerCondition_RangeSyntax_WorksForValueAndFeatureForms()
        {
            Assert.True(Evaluate("(width >= 640px)", 640, 500));
            Assert.True(Evaluate("(1200px > width)", 1100, 500));
            Assert.True(Evaluate("(400px <= width <= 900px)", 700, 500));
            Assert.False(Evaluate("(400px <= width <= 900px)", 1000, 500));
        }

        [Fact]
        public void ContainerCondition_HeightAndPercentUnits_AreSupported()
        {
            Assert.True(Evaluate("(min-height: 50%)", 1000, 600));
            Assert.False(Evaluate("(max-height: 25%)", 1000, 600));
            Assert.True(Evaluate("(block-size: 600px)", 1000, 600));
        }

        [Fact]
        public async Task ComputeAsync_ContainerQueryUsesViewportHeight()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    .probe { color: blue; }
    @container (min-height: 700px) {
      .probe { color: red; }
    }
  </style>
</head>
<body>
  <div class='probe'>x</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var probe = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("probe"));

            var tall = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null, viewportWidth: 900, viewportHeight: 800);
            var shortViewport = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null, viewportWidth: 900, viewportHeight: 600);

            Assert.Equal("red", tall[probe].Map["color"]);
            Assert.Equal("blue", shortViewport[probe].Map["color"]);
        }
    }
}
