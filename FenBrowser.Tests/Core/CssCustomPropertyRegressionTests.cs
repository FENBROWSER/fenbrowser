using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

public class CssCustomPropertyRegressionTests
{
    [Fact]
    public async Task ComputeAsync_CustomPropertyInitialValue_UsesVarFallback()
    {
        CssLoader.ClearCaches();

        const string html = @"
<!doctype html>
<html>
<head>
  <style>
    :root {
      --light-mode-toggle: initial;
      --black: rgb(0, 0, 0);
      --white: rgb(255, 255, 255);
      --text-toggle: var(--light-mode-toggle) var(--white);
      --text-primary: var(--text-toggle, var(--black));
    }
    a { color: var(--text-primary); }
  </style>
</head>
<body><a id='target'>Visible text</a></body>
</html>";

        var parser = new HtmlParser(html);
        var doc = parser.Parse();
        var root = doc.Children.OfType<Element>().First(element => element.TagName == "HTML");
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
        var target = doc.Descendants().OfType<Element>().First(element => element.Id == "target");

        Assert.Equal("rgb(0, 0, 0)", computed[target].Map["color"]);
    }
}
