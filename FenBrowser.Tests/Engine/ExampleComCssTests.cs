using FenBrowser.Core.Dom;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;
using System;
using System.Threading.Tasks;
using System.Linq;

namespace FenBrowser.Tests.Engine
{
    public class ExampleComCssTests
    {
        [Fact]
        public async Task TestExampleComBodyStyles()
        {
            // Simplified example.com HTML structure with problem case
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", "\n\n[TEST] STARTING TEST RUN CHECKSUM 12345\n"); } catch {}
            // Note: example.com has <style type="text/css">
            string html = @"
<!doctype html>
<html>
<head>
    <style type=""text/css"">
    body {
        background-color: #f0f0f2;
        margin: 0;
        width: 60em;
    }
    div {
        width: 600px;
        margin: 5em auto;
        padding: 2em;
        background-color: #fdfdff;
        border-radius: 0.5em;
        box-shadow: 2px 3px 7px 2px rgba(0,0,0,0.02);
    }
    </style>    
</head>
<body>
    <div>
        <h1>Example Domain</h1>
        <p>This domain is for use in illustrative examples in documents.</p>
    </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            
            // Find html element
            var root = doc.Children.OfType<Element>().FirstOrDefault(e => e.TagName.Equals("HTML", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(root);

            // Compute styles
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://example.com"), null);
            
            // Find body
            var body = root.Children.OfType<Element>().FirstOrDefault(e => e.TagName.Equals("BODY", StringComparison.OrdinalIgnoreCase));
            if (body == null)
            {
               // Try identifying if parser put body inside head? (Unlikely but possible)
               body = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName.Equals("BODY", StringComparison.OrdinalIgnoreCase));
            }

            Assert.NotNull(body);
            
            if (computed.TryGetValue(body, out var style))
            {
                // Access styles via Map property
                var w = style.Map.ContainsKey("width") ? style.Map["width"] : "auto";
                // Debug output (won't be seen unless failed, but good for local)
                // Console.WriteLine($"Body Width: {w}");
                Assert.Equal("60em", w);
                
                var bg = style.Map.ContainsKey("background-color") ? style.Map["background-color"] : "transparent";
                Assert.Equal("#f0f0f2", bg);
            }
            else
            {
                Assert.Fail("No computed style for body");
            }
        }
    }
}
