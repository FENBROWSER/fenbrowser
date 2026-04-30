using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;
using NewCss = FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssLoaderIsolationRegressionTests
    {
        [Fact]
        public async Task ParseCache_RespectsBaseUri_ForRelativeBackgroundImageUrls()
        {
            const string html = @"<!doctype html><html><head></head><body><div id='hero'>hero</div></body></html>";
            const string css = "#hero { background-image: url('assets/bg.png'); }";

            var firstBaseUri = new Uri("https://first.example.test/path-a/");
            var secondBaseUri = new Uri("https://second.example.test/path-b/");

            var firstDoc = new HtmlParser(html, firstBaseUri).Parse();
            var firstRoot = firstDoc.DocumentElement ?? firstDoc.Children.OfType<Element>().First();
            var firstHero = firstRoot.Descendants().OfType<Element>().First(e => string.Equals(e.Id, "hero", StringComparison.Ordinal));
            var firstSources = new System.Collections.Generic.List<CssLoader.CssSource>
            {
                new CssLoader.CssSource
                {
                    CssText = css,
                    Origin = CssLoader.CssOrigin.Inline,
                    SourceOrder = 0,
                    BaseUri = firstBaseUri
                }
            };
            var firstMatch = CssLoader.GetMatchedRules(firstHero, firstSources)
                .Select(m => m.Rule)
                .OfType<NewCss.CssStyleRule>()
                .First();
            Assert.Equal(firstBaseUri, firstMatch.BaseUri);

            var secondDoc = new HtmlParser(html, secondBaseUri).Parse();
            var secondRoot = secondDoc.DocumentElement ?? secondDoc.Children.OfType<Element>().First();
            var secondHero = secondRoot.Descendants().OfType<Element>().First(e => string.Equals(e.Id, "hero", StringComparison.Ordinal));
            var secondSources = new System.Collections.Generic.List<CssLoader.CssSource>
            {
                new CssLoader.CssSource
                {
                    CssText = css,
                    Origin = CssLoader.CssOrigin.Inline,
                    SourceOrder = 0,
                    BaseUri = secondBaseUri
                }
            };
            var secondMatch = CssLoader.GetMatchedRules(secondHero, secondSources)
                .Select(m => m.Rule)
                .OfType<NewCss.CssStyleRule>()
                .First();
            Assert.Equal(secondBaseUri, secondMatch.BaseUri);
        }

        [Fact]
        public async Task HeroTileImageRules_AfterHasSelector_AreApplied()
        {
            const string css = @"
:root{--hero-content-height:580px}
.tile-wrapper{display:flex;justify-content:center;position:relative;width:100%;min-height:var(--hero-content-height);box-sizing:border-box;overflow:clip;padding:48px 0 56px}
.tile-ctas:has(:nth-child(2)){grid-template-columns:min-content min-content;column-gap:17px}
@media(max-width:734px){.tile-ctas:has(:nth-child(2)){column-gap:14px}}
.button{z-index:4}
.tile-image-wrapper{position:absolute;top:0;width:100%;height:100%}
.tile-image-wrapper picture{display:block;width:100%;height:100%}
.tile-image-wrapper img{inset-inline-start:50%;transform:translate(-50%);position:absolute;bottom:0;width:auto;height:100%}
";

            var html = $@"<!doctype html>
<html>
<head><style>{css}</style></head>
<body>
  <div class='tile-wrapper'>
    <div class='tile-content'>
      <div class='tile-ctas'><a>one</a><a>two</a></div>
    </div>
    <div class='tile-image-wrapper'>
      <picture class='static'>
        <source srcset='hero-small.jpg' media='(max-width:734px)' />
        <img id='hero-img' src='hero-large.jpg' alt='hero' />
      </picture>
    </div>
  </div>
</body>
</html>";

            var doc = new HtmlParser(html, new Uri("https://apple.test/")).Parse();
            var root = doc.DocumentElement ?? doc.Children.OfType<Element>().First();

            var computed = await CssLoader.ComputeAsync(
                root,
                new Uri("https://apple.test/"),
                fetchExternalCssAsync: null,
                viewportWidth: 1920,
                viewportHeight: 1080);

            var img = root.Descendants()
                .OfType<Element>()
                .First(e => string.Equals(e.GetAttribute("id"), "hero-img", StringComparison.Ordinal));
            var tileWrapper = root.Descendants()
                .OfType<Element>()
                .First(e => string.Equals(e.GetAttribute("class"), "tile-wrapper", StringComparison.Ordinal));
            var picture = root.Descendants()
                .OfType<Element>()
                .First(e => string.Equals(e.TagName, "PICTURE", StringComparison.OrdinalIgnoreCase));

            Assert.True(computed.TryGetValue(img, out var imgStyle));
            Assert.Equal("absolute", imgStyle.Position);
            Assert.Equal(100d, imgStyle.HeightPercent);
            Assert.Equal("50%", imgStyle.Map["inset-inline-start"]);
            Assert.True(computed.TryGetValue(tileWrapper, out var tileWrapperStyle));
            Assert.Equal(580d, tileWrapperStyle.MinHeight);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(1920, 1080);
            using var canvas = new SKCanvas(bitmap);
            renderer.Render(
                root,
                canvas,
                computed,
                new SKRect(0, 0, 1920, 1080),
                "https://apple.test/",
                (_, _) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(tileWrapper, out var wrapperRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(picture, out var pictureRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(img, out var heroRect));
            Assert.True(wrapperRect.Height >= 580f, $"Expected wrapper min-height >= 580px, got {wrapperRect.Height}");
            Assert.True(pictureRect.Height >= 500f, $"Expected picture 100% height chain to resolve, got {pictureRect.Height}");
            Assert.True(heroRect.Height >= 500f, $"Expected hero image height >= 500px, got {heroRect.Height}");
        }

        [Fact]
        public async Task TileCopyLogoImage_DoesNotInheritTileImageWrapperAbsoluteSizingRule()
        {
            const string css = @"
.tile-copy-wrapper img{inset-inline-start:50%;transform:translate(-50%);position:relative}
.tile-image-wrapper img{inset-inline-start:50%;transform:translate(-50%);position:absolute;bottom:0;width:auto;height:100%}
";

            var html = $@"<!doctype html>
<html>
<head><style>{css}</style></head>
<body>
  <div class='tile-wrapper'>
    <div class='tile-content'>
      <div class='tile-copy-wrapper'>
        <picture class='headline'>
          <source srcset='data:image/gif;base64,R0lGODlhAQABAHAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==' media='(min-width:0px)' />
          <img id='logo-img' src='logo.png' alt='' />
        </picture>
      </div>
    </div>
    <div class='tile-image-wrapper'>
      <picture class='static'>
        <img id='hero-img' src='hero-large.jpg' alt='hero' />
      </picture>
    </div>
  </div>
</body>
</html>";

            var doc = new HtmlParser(html, new Uri("https://apple.test/")).Parse();
            var root = doc.DocumentElement ?? doc.Children.OfType<Element>().First();

            var computed = await CssLoader.ComputeAsync(
                root,
                new Uri("https://apple.test/"),
                fetchExternalCssAsync: null,
                viewportWidth: 1920,
                viewportHeight: 1080);

            var logoImg = root.Descendants()
                .OfType<Element>()
                .First(e => string.Equals(e.GetAttribute("id"), "logo-img", StringComparison.Ordinal));
            var heroImg = root.Descendants()
                .OfType<Element>()
                .First(e => string.Equals(e.GetAttribute("id"), "hero-img", StringComparison.Ordinal));

            Assert.True(computed.TryGetValue(logoImg, out var logoStyle));
            Assert.Equal("relative", logoStyle.Position);
            Assert.False(logoStyle.HeightPercent.HasValue, "Logo image should not pick up tile-image-wrapper height:100%");

            Assert.True(computed.TryGetValue(heroImg, out var heroStyle));
            Assert.Equal("absolute", heroStyle.Position);
            Assert.Equal(100d, heroStyle.HeightPercent);
        }

        [Fact]
        public async Task TransformLonghands_AndObjectFitPosition_AreMappedToTypedComputedStyle()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    #hero {
      translate: -50% -50%;
      rotate: 10deg;
      scale: 1.2;
      transform: translateX(12px);
      object-fit: cover;
      object-position: 50% 30%;
    }
  </style>
</head>
<body><img id='hero' src='hero.png' /></body>
</html>";

            var doc = new HtmlParser(html, new Uri("https://apple.test/")).Parse();
            var root = doc.DocumentElement ?? doc.Children.OfType<Element>().First();

            var computed = await CssLoader.ComputeAsync(
                root,
                new Uri("https://apple.test/"),
                fetchExternalCssAsync: null,
                viewportWidth: 1920,
                viewportHeight: 1080);

            var hero = root.Descendants()
                .OfType<Element>()
                .First(e => string.Equals(e.GetAttribute("id"), "hero", StringComparison.Ordinal));

            Assert.True(computed.TryGetValue(hero, out var heroStyle));
            Assert.Equal(
                "translate(-50% -50%) rotate(10deg) scale(1.2) translateX(12px)",
                heroStyle.Transform);
            Assert.Equal("cover", heroStyle.ObjectFit);
            Assert.Equal("50% 30%", heroStyle.ObjectPosition);
        }

    }
}
