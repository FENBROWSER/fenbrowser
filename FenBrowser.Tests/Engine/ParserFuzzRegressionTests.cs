using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class ParserFuzzRegressionTests
    {
        [Fact]
        public void HtmlFuzzCorpus_DoesNotThrow()
        {
            foreach (var html in BuildHtmlCorpus())
            {
                var ex = Record.Exception(() =>
                {
                    var parser = new HtmlParser(html, securityPolicy: new ParserSecurityPolicy
                    {
                        HtmlMaxTokenEmissions = 100000,
                        HtmlMaxOpenElementsDepth = 1024
                    });
                    var doc = parser.Parse();
                    Assert.NotNull(doc.DocumentElement);
                });

                Assert.Null(ex);
            }
        }

        [Fact]
        public void CssFuzzCorpus_DoesNotThrow()
        {
            foreach (var css in BuildCssCorpus())
            {
                var ex = Record.Exception(() =>
                {
                    var parser = new CssSyntaxParser(new CssTokenizer(css))
                    {
                        MaxRules = 50000,
                        MaxDeclarationsPerBlock = 4096
                    };
                    var sheet = parser.ParseStylesheet();
                    Assert.NotNull(sheet);
                });

                Assert.Null(ex);
            }
        }

        [Fact]
        public async Task EndToEndFuzzCorpus_DoesNotThrow()
        {
            foreach (var html in BuildHtmlCorpus().Take(8))
            {
                var parser = new HtmlParser(html);
                var doc = parser.Parse();
                var root = doc.Children.OfType<Element>().FirstOrDefault(e => e.TagName == "HTML");
                if (root == null) continue;

                var computed = await CssLoader.ComputeAsync(root, new Uri("https://fuzz.local"), null);

                using var bitmap = new SKBitmap(256, 256);
                using var canvas = new SKCanvas(bitmap);
                var renderer = new SkiaDomRenderer();
                var ex = Record.Exception(() =>
                    renderer.Render(
                        root,
                        canvas,
                        computed,
                        new SKRect(0, 0, 256, 256),
                        "https://fuzz.local",
                        (size, overlays) => { }));

                Assert.Null(ex);
            }
        }

        private static IEnumerable<string> BuildHtmlCorpus()
        {
            var seeds = new[]
            {
                "<!doctype html><html><body><div>ok</div></body></html>",
                "<!doctype html><html><head><style>div{color:red}</style></head><body><div a='&copy=1'>&#x;</div></body></html>",
                "<html><body><script><!--x--></script><style>@media screen{.a{b:c}}</style></body></html>",
                "<html><body><svg><g><g><path d='M0 0 L1 1'/></g></g></svg></body></html>",
                "<html><body><div style=\"background:url('data:image/svg+xml;utf8,<svg></svg>')\">x</div></body></html>"
            };

            foreach (var s in seeds) yield return s;

            var rng = new Random(1337);
            for (int i = 0; i < 20; i++)
            {
                var body = RandomTokenSoup(rng, 220);
                var style = RandomCssSoup(rng, 180);
                yield return $"<!doctype html><html><head><style>{style}</style></head><body>{body}</body></html>";
            }
        }

        private static IEnumerable<string> BuildCssCorpus()
        {
            var seeds = new[]
            {
                "div{color:red}",
                "@media (min-width:1px){.x{background:url(data:image/svg+xml;utf8,<svg/>);}}",
                "@font-face{font-family:'A';src:url(a.woff2) format('woff2');}",
                ":root{--MyVar:1px;--myvar:2px} #t{width:calc(var(--MyVar) + var(--myvar));}",
                "a{content:'x:y;z';transform:translate(1px,2px)}"
            };

            foreach (var s in seeds) yield return s;

            var rng = new Random(4242);
            for (int i = 0; i < 30; i++)
            {
                yield return RandomCssSoup(rng, 320);
            }
        }

        private static string RandomTokenSoup(Random rng, int length)
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789<>&\"'=/;:()[]{}!-_ \t\n\r";
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                sb.Append(alphabet[rng.Next(alphabet.Length)]);
            }
            return sb.ToString();
        }

        private static string RandomCssSoup(Random rng, int length)
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#.@_:-;!(){}[],'\"/\\ \t\n\r";
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                sb.Append(alphabet[rng.Next(alphabet.Length)]);
            }
            return sb.ToString();
        }
    }
}
