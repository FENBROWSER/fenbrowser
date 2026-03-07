using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Tests.Core.Parsing
{
    public class StreamingHtmlParserTests
    {
        [Fact]
        public async Task ParseAsync_GoogleLikeInlineScript_DoesNotTurnScriptBodyIntoMarkup()
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

            using var parser = new StreamingHtmlParser(html);
            var document = await parser.ParseAsync();

            Assert.NotNull(document.DocumentElement);
            Assert.False(document.Descendants().OfType<Element>().Any(e => e.TagName.Contains("w.length", System.StringComparison.OrdinalIgnoreCase)));

            var title = document.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "title");
            Assert.NotNull(title);
            Assert.Equal("Google", title!.TextContent);

            var marker = document.Descendants().OfType<Element>().FirstOrDefault(e => e.GetAttribute("id") == "ok");
            Assert.NotNull(marker);
            Assert.Equal("ready", marker!.TextContent);
        }

        [Fact]
        public async Task ParseAsync_ScriptEndTagSplitAcrossChunks_KeepsScriptAsSingleElement()
        {
            const string html = """
<html><head><script>if (a < b) { window.x = 1; }</script><title>x</title></head><body><p>done</p></body></html>
""";

            using var stream = new ChunkedMemoryStream(html, 11);
            using var parser = new StreamingHtmlParser(stream);
            var document = await parser.ParseAsync();

            var scripts = document.Descendants().OfType<Element>().Where(e => e.TagName == "script").ToList();
            Assert.Single(scripts);
            Assert.Contains("if (a < b)", scripts[0].TextContent);
            Assert.False(document.Descendants().OfType<Element>().Any(e => e.TagName.Contains("window.x", System.StringComparison.OrdinalIgnoreCase)));

            var paragraph = document.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "p");
            Assert.NotNull(paragraph);
            Assert.Equal("done", paragraph!.TextContent);
        }

        private sealed class ChunkedMemoryStream : MemoryStream
        {
            private readonly int _chunkSize;

            public ChunkedMemoryStream(string content, int chunkSize)
                : base(System.Text.Encoding.UTF8.GetBytes(content))
            {
                _chunkSize = chunkSize;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return base.Read(buffer, offset, System.Math.Min(count, _chunkSize));
            }
        }
    }
}
