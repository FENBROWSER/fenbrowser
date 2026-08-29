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

    [Fact]
    public async Task ComputeAsync_NonRootCustomProperty_DoesNotLeakToUnrelatedSubtree()
    {
        CssLoader.ClearCaches();

        const string html = @"
<!doctype html>
<html>
<head>
  <style>
    div.scope { --accent: rgb(1, 2, 3); }
    #inside { color: var(--accent); }
    #outside { color: var(--accent, rgb(9, 9, 9)); background-color: var(--accent); }
  </style>
</head>
<body>
  <div class='scope'><p id='inside'>a</p></div>
  <p id='outside'>b</p>
</body>
</html>";

        var parser = new HtmlParser(html);
        var doc = parser.Parse();
        var root = doc.Children.OfType<Element>().First(element => element.TagName == "HTML");
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
        var inside = doc.Descendants().OfType<Element>().First(element => element.Id == "inside");
        var outside = doc.Descendants().OfType<Element>().First(element => element.Id == "outside");

        // Custom properties inherit within the subtree: div.scope's declaration
        // reaches #inside even though the selector does not match it directly.
        Assert.Equal("rgb(1, 2, 3)", computed[inside].Map["color"]);
        Assert.Equal("rgb(9, 9, 9)", computed[outside].Map["color"]);

        // Without a fallback var(--accent) is guaranteed-invalid at computed-value
        // time outside the scoped subtree, so background-color becomes unset.
        var hasBg = computed[outside].Map.TryGetValue("background-color", out var bg);
        Assert.True(!hasBg || string.IsNullOrEmpty(bg) || bg == "rgba(0, 0, 0, 0)" || bg == "transparent",
            $"Expected unset background-color outside the scoped subtree, got '{bg}'");
    }

    [Fact]
    public async Task ComputeAsync_UniversalSegmentRule_DoesNotLeakIntoDocumentMap()
    {
        CssLoader.ClearCaches();

        const string html = @"
<!doctype html>
<html>
<head>
  <style>
    div * { --x: rgb(1, 2, 3); }
    #outside { color: var(--x, rgb(9, 9, 9)); }
  </style>
</head>
<body>
  <div><span>a</span></div>
  <p id='outside'>b</p>
</body>
</html>";

        var parser = new HtmlParser(html);
        var doc = parser.Parse();
        var root = doc.Children.OfType<Element>().First(element => element.TagName == "HTML");
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
        var outside = doc.Descendants().OfType<Element>().First(element => element.Id == "outside");

        // A universal selector inside a descendant chain is not a document-global
        // declaration; var(--x) outside the div subtree must take the fallback.
        Assert.Equal("rgb(9, 9, 9)", computed[outside].Map["color"]);
    }

    [Fact]
    public async Task ComputeAsync_RootSelectorRule_OnlyCountsWhenItActuallyMatches()
    {
        CssLoader.ClearCaches();

        const string html = @"
<!doctype html>
<html>
<head>
  <style>
    html.dark { --x: rgb(1, 2, 3); }
    #outside { color: var(--x, rgb(9, 9, 9)); }
  </style>
</head>
<body><p id='outside'>a</p></body>
</html>";

        var parser = new HtmlParser(html);
        var doc = parser.Parse();
        var root = doc.Children.OfType<Element>().First(element => element.TagName == "HTML");
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
        var outside = doc.Descendants().OfType<Element>().First(element => element.Id == "outside");

        // html.dark does not match (no class attribute): the declaration must not
        // reach the document map.
        Assert.Equal("rgb(9, 9, 9)", computed[outside].Map["color"]);
    }

    [Fact]
    public async Task ComputeAsync_RootRuleCascade_UsesWinningDeclaration()
    {
        CssLoader.ClearCaches();

        const string html = @"
<!doctype html>
<html>
<head>
  <style>
    html { --x: rgb(1, 1, 1); }
    :root { --x: rgb(2, 2, 2); }
    #outside { color: var(--x); }
  </style>
</head>
<body><p id='outside'>a</p></body>
</html>";

        var parser = new HtmlParser(html);
        var doc = parser.Parse();
        var root = doc.Children.OfType<Element>().First(element => element.TagName == "HTML");
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
        var outside = doc.Descendants().OfType<Element>().First(element => element.Id == "outside");

        // :root (specificity 0,1,0) beats html (0,0,1) regardless of source order.
        Assert.Equal("rgb(2, 2, 2)", computed[outside].Map["color"]);
    }

    [Fact]
    public async Task ComputeAsync_BodyCustomProperty_DoesNotReachHeadElements()
    {
        CssLoader.ClearCaches();

        const string html = @"
<!doctype html>
<html>
<head>
  <style id='hs'></style>
  <style>
    body { --x: rgb(1, 2, 3); }
    #hs { color: var(--x, rgb(9, 9, 9)); }
  </style>
</head>
<body><p>a</p></body>
</html>";

        var parser = new HtmlParser(html);
        var doc = parser.Parse();
        var root = doc.Children.OfType<Element>().First(element => element.TagName == "HTML");
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
        var headStyle = doc.Descendants().OfType<Element>().First(element => element.Id == "hs");

        // Body-scoped declarations inherit into body's subtree, not <head>.
        Assert.Equal("rgb(9, 9, 9)", computed[headStyle].Map["color"]);
    }

    [Fact]
    public async Task ComputeAsync_ScopedCustomProperty_InheritsWithinSubtree()
    {
        CssLoader.ClearCaches();

        const string html = @"
<!doctype html>
<html>
<head>
  <style>
    div.scope { --accent: rgb(1, 2, 3); color: var(--accent); }
  </style>
</head>
<body>
  <div class='scope'><span id='deep'>a</span></div>
</body>
</html>";

        var parser = new HtmlParser(html);
        var doc = parser.Parse();
        var root = doc.Children.OfType<Element>().First(element => element.TagName == "HTML");
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
        var deep = doc.Descendants().OfType<Element>().First(element => element.Id == "deep");

        Assert.Equal("rgb(1, 2, 3)", computed[deep].Map["color"]);
    }
}
