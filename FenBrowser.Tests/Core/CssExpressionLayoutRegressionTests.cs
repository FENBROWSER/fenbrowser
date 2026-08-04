using System;
using System.Collections.Concurrent;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Tests.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

public class CssExpressionLayoutRegressionTests
{
    [Fact]
    public void EvaluateCssExpression_MultipliesEmByUnitlessValue()
    {
        float result = LayoutHelper.EvaluateCssExpression(
            "calc(3em * 1.2)",
            parentSize: 800f,
            viewportWidth: 1280f,
            viewportHeight: 800f,
            fontSize: 12.8f);

        Assert.Equal(46.08f, result, 2);
    }

    [Fact]
    public void EvaluateCssExpression_DoesNotIgnoreInvalidOperands()
    {
        float result = LayoutHelper.EvaluateCssExpression(
            "calc(3unsupported * 1.2)",
            parentSize: 800f,
            viewportWidth: 1280f,
            viewportHeight: 800f,
            fontSize: 12.8f);

        Assert.Equal(-1f, result);
    }

    [Fact]
    public void EvaluateCssExpression_UsesMultiplicationPrecedence()
    {
        float result = LayoutHelper.EvaluateCssExpression(
            "calc(100% - 2px * 2)",
            parentSize: 1280f,
            viewportWidth: 1280f,
            viewportHeight: 800f);

        Assert.Equal(1276f, result, 2);
    }

    [Fact]
    public async System.Threading.Tasks.Task ComputedCalcInset_WithLocalCustomProperty_PositionsOffscreen()
    {
        const string html = @"
<!doctype html>
<html>
<head>
  <style>
    body { display: grid; margin: 0; }
    #skip {
      --offset: 20em;
      left: 2px;
      position: absolute;
      right: 2px;
      top: calc(var(--offset) * -1);
      width: calc(100% - 2px * 2);
    }
  </style>
</head>
<body><div id='skip'>Skip to main content</div></body>
</html>";

        var doc = new HtmlParser(html).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
        var skip = doc.GetElementById("skip");

        Assert.True(styles.TryGetValue(skip, out var skipStyle));
        Assert.True(skipStyle.Top.HasValue);
        Assert.Equal(-320d, skipStyle.Top.Value, 2);

        var computer = new LayoutEngineComputer(styles, 1280, 800);
        computer.Measure(doc, new SKSize(1280, 800));
        computer.Arrange(doc, new SKRect(0, 0, 1280, 800));

        var boxes = new ConcurrentDictionary<Node, BoxModel>(computer.GetAllBoxes());
        Assert.True(boxes.TryGetValue(skip, out var skipBox));
        Assert.Equal(-320f, skipBox.MarginBox.Top, 1f);
        Assert.Equal(1276f, skipBox.MarginBox.Width, 1f);
    }
}
