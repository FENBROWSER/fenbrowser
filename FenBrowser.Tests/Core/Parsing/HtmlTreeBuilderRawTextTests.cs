using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;

namespace FenBrowser.Tests.Core.Parsing
{
    public class HtmlTreeBuilderRawTextTests
    {
        [Fact]
        public void Build_GoogleLikeInlineScript_KeepsScriptBodyOutOfMarkup()
        {
            const string html = """
<!doctype html>
<html>
  <head>
    <script nonce="abc">(function(){var w=["Google Sans",[400,500,700]];(function(){for(var a=0;a<w.length;a+=2)for(var d=w[a],e=w[a+1],b=0,c=void 0;c=e[b];++b)document.fonts.load(c+" 10pt "+d).catch(function(){})})();})();</script>
    <title>Google</title>
  </head>
  <body>
    <div id="ok">ready</div>
  </body>
</html>
""";

            var builder = new HtmlTreeBuilder(html);
            var document = builder.Build();

            var bogusNode = document
                .Descendants()
                .OfType<Element>()
                .FirstOrDefault(e => e.TagName.Contains("W.LENGTH", System.StringComparison.OrdinalIgnoreCase));

            Assert.Null(bogusNode);

            var scripts = document.Descendants().OfType<Element>().Where(e => e.TagName == "SCRIPT").ToList();
            Assert.Single(scripts);
            Assert.Contains("a<w.length", scripts[0].TextContent);

            var title = document.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "TITLE");
            Assert.NotNull(title);
            Assert.Equal("Google", title!.TextContent);

            var marker = document.Descendants().OfType<Element>().FirstOrDefault(e => e.GetAttribute("id") == "ok");
            Assert.NotNull(marker);
            Assert.Equal("ready", marker!.TextContent);
        }

        [Fact]
        public void Build_Acid3HeadCommentWithMarkupLikeText_DoesNotTruncateDocument()
        {
            const string html = """
<!doctype html>
<html>
  <head>
    <link rel="stylesheet" href="empty.css"><!-- text/html file (should be ignored, <h1> will go red if it isn't) -->
    <script>var marker = 1;</script>
  </head>
  <body>
    <p id="score">JS</p>
  </body>
</html>
""";

            var builder = new HtmlTreeBuilder(html);
            var document = builder.Build();

            var script = document.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "SCRIPT");
            Assert.NotNull(script);
            Assert.Contains("marker = 1", script!.TextContent);

            var score = document.Descendants().OfType<Element>().FirstOrDefault(e => e.GetAttribute("id") == "score");
            Assert.NotNull(score);
            Assert.Equal("JS", score!.TextContent);
        }
    }
}
