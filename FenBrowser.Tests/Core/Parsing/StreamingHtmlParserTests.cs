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

            var title = document.Descendants().OfType<Element>().FirstOrDefault(e => string.Equals(e.TagName, "title", System.StringComparison.OrdinalIgnoreCase));
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

            var scripts = document.Descendants().OfType<Element>().Where(e => string.Equals(e.TagName, "script", System.StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Single(scripts);
            Assert.Contains("if (a < b)", scripts[0].TextContent);
            Assert.False(document.Descendants().OfType<Element>().Any(e => e.TagName.Contains("window.x", System.StringComparison.OrdinalIgnoreCase)));

            var paragraph = document.Descendants().OfType<Element>().FirstOrDefault(e => string.Equals(e.TagName, "p", System.StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(paragraph);
            Assert.Equal("done", paragraph!.TextContent);
        }

        [Fact]
        public async Task ParseIncrementallyAsync_ReportsProgressAcrossChunks()
        {
            const string html = "<html><body><div id='a'>one</div><div id='b'>two</div></body></html>";
            using var stream = new ChunkedMemoryStream(html, 8);
            using var parser = new StreamingHtmlParser(stream);

            var progressCallbacks = 0;
            parser.OnDocumentComplete += _ => progressCallbacks++;

            await parser.ParseIncrementallyAsync(_ => progressCallbacks++);

            Assert.True(progressCallbacks >= 3);
        }

        [Fact]
        public async Task ParseIncrementallyAsync_BuildsExpectedDocument()
        {
            const string html = "<html><body><p>hello</p><p>world</p></body></html>";
            using var stream = new ChunkedMemoryStream(html, 7);
            using var parser = new StreamingHtmlParser(stream);

            Document? snapshot = null;
            await parser.ParseIncrementallyAsync(doc => snapshot = doc);

            Assert.NotNull(snapshot);
            var paragraphs = snapshot!.Descendants().OfType<Element>()
                .Where(e => string.Equals(e.TagName, "p", System.StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Equal(2, paragraphs.Count);
            Assert.Equal("hello", paragraphs[0].TextContent);
            Assert.Equal("world", paragraphs[1].TextContent);
        }

        [Fact]
        public async Task ParseAsync_WhenReaderFails_ThrowsInvalidOperationException()
        {
            using var parser = new StreamingHtmlParser(new ThrowingTextReader());
            await Assert.ThrowsAsync<InvalidOperationException>(() => parser.ParseAsync());
        }

        [Fact]
        public async Task ParseAsync_UnquotedAttributeValueWithSlash_PreservesFullValue()
        {
            const string html = "<html><body><img src=https://example.com/a/b.png alt=test></body></html>";
            using var parser = new StreamingHtmlParser(html);
            var document = await parser.ParseAsync();

            var img = document.Descendants().OfType<Element>()
                .FirstOrDefault(e => string.Equals(e.TagName, "img", System.StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(img);
            Assert.Equal("https://example.com/a/b.png", img!.GetAttribute("src"));
            Assert.Equal("test", img.GetAttribute("alt"));
        }

        [Fact]
        public async Task ParseAsync_UnquotedAttributeValueBeforeSelfClosing_DoesNotIncludeSlashClose()
        {
            const string html = "<html><body><img src=https://example.com/a/b.png/></body></html>";
            using var parser = new StreamingHtmlParser(html);
            var document = await parser.ParseAsync();

            var img = document.Descendants().OfType<Element>()
                .FirstOrDefault(e => string.Equals(e.TagName, "img", System.StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(img);
            Assert.Equal("https://example.com/a/b.png/", img!.GetAttribute("src"));
        }

        [Fact]
        public async Task ParseAsync_UnquotedAttributeValueWithWhitespaceBeforeSelfClosing_ExcludesSlashClose()
        {
            const string html = "<html><body><img src=https://example.com/a/b.png /></body></html>";
            using var parser = new StreamingHtmlParser(html);
            var document = await parser.ParseAsync();

            var img = document.Descendants().OfType<Element>()
                .FirstOrDefault(e => string.Equals(e.TagName, "img", System.StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(img);
            Assert.Equal("https://example.com/a/b.png", img!.GetAttribute("src"));
        }

        [Fact]
        public async Task ParseAsync_DuplicateAttributes_PreservesFirstValue()
        {
            const string html = "<html><body><img alt=first alt=second></body></html>";
            using var parser = new StreamingHtmlParser(html);
            var document = await parser.ParseAsync();

            var img = document.Descendants().OfType<Element>()
                .FirstOrDefault(e => string.Equals(e.TagName, "img", System.StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(img);
            Assert.Equal("first", img!.GetAttribute("alt"));
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

        private sealed class ThrowingTextReader : StringReader
        {
            public ThrowingTextReader() : base(string.Empty)
            {
            }

            public override Task<string> ReadToEndAsync()
            {
                throw new IOException("synthetic read failure");
            }
        }
    }
}
