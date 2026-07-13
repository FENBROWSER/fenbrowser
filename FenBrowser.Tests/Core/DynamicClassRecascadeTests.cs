using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;

namespace FenBrowser.Tests.Core;

public sealed class DynamicClassRecascadeTests
{
    [Fact]
    public async Task BodyClassMutation_ReevaluatesDocumentAuthorSelectors()
    {
        const string html = """
<!doctype html>
<html>
<head>
  <style>
    .if-js-enabled { display: block; }
    body.js-enabled .if-js-enabled { display: none; }
  </style>
</head>
<body>
  <div id="message" class="if-js-enabled">connected</div>
</body>
</html>
""";

        using var host = new BrowserHost();
        host.EnableJavaScript = true;

        await host.Engine.RenderAsync(
            html,
            new Uri("https://fen.test/dynamic-class"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth: 320,
            viewportHeight: 200,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var message = root.Descendants().OfType<Element>()
            .Single(element => element.Id == "message");
        var body = root.DescendantsAndSelf().OfType<Element>()
            .Single(element => string.Equals(element.TagName, "body", StringComparison.OrdinalIgnoreCase));

        Assert.True(host.ComputedStyles.TryGetValue(message, out var initialStyle));
        Assert.Equal("block", initialStyle.Display);

        body.ClassName = "js-enabled";
        Assert.True(SelectorMatcher.Matches(message, "body.js-enabled .if-js-enabled"));

        await WaitForAsync(
            () => host.ComputedStyles.TryGetValue(message, out var style) && style.Display == "none",
            "document author selector to match after the body class mutation");
    }

    private static async Task WaitForAsync(Func<bool> predicate, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(predicate(), $"Timed out waiting for {description}.");
    }
}
