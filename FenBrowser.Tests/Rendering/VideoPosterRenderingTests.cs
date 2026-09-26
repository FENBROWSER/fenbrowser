using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Media;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Interaction;
using FenBrowser.FenEngine.Rendering.UserAgent;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    /// <summary>
    /// HTML §4.8.9: while the show-poster flag is set a video represents its poster frame,
    /// whose size is the playback area's intrinsic size, fitted with object-fit (UA default
    /// "contain").
    /// </summary>
    [Collection("Engine Tests")]
    public class VideoPosterRenderingTests
    {
        [Fact]
        public void PosterFrameSizesAndPaintsTheVideoUntilItIsHidden()
        {
            ImageLoader.ClearCache();
            var previousFetcher = ImageLoader.FetchBytesAsync;
            try
            {
                const string posterUrl = "https://example.test/poster.png";
                ImageLoader.FetchBytesAsync = _ => System.Threading.Tasks.Task.FromResult(CreatePngBytes(64, 48, SKColors.Blue));

                var video = new Element("video");
                video.SetAttribute("poster", posterUrl);

                // The first request starts the fetch; sizing settles once it lands.
                ReplacedElementSizing.TryResolveIntrinsicSizeFromElement("VIDEO", video, out _, out _);
                Assert.True(SpinWait.SpinUntil(() => ImageLoader.ContainsCachedImage(posterUrl), System.TimeSpan.FromSeconds(2)));

                Assert.True(ReplacedElementSizing.TryResolveIntrinsicSizeFromElement("VIDEO", video, out var width, out var height));
                Assert.Equal(64f, width);
                Assert.Equal(48f, height);

                CssComputed style = new CssComputed { Display = "block" };
                UAStyleProvider.Apply(video, ref style);
                Assert.Equal("contain", style.ObjectFit);

                var box = BoxModel.FromContentBox(0, 0, 320, 240);
                var node = BuildVideoNode(video, box, style);
                var poster = Assert.IsType<ImagePaintNode>(node);
                Assert.Equal(64, poster.Bitmap.Width);
                Assert.Equal("contain", poster.ObjectFit);

                // Once playback has hidden the poster, neither sizing nor paint use it.
                MediaPresentation.Update(video, new MediaPresentationState(ShowPoster: false, 0, 0));
                Assert.False(ReplacedElementSizing.TryResolveIntrinsicSizeFromElement("VIDEO", video, out _, out _));
                Assert.IsNotType<ImagePaintNode>(BuildVideoNode(video, box, style));

                // Video dimensions, when there are any, win over the poster.
                MediaPresentation.Update(video, new MediaPresentationState(ShowPoster: true, 1280, 720));
                Assert.True(ReplacedElementSizing.TryResolveIntrinsicSizeFromElement("VIDEO", video, out width, out height));
                Assert.Equal((1280f, 720f), (width, height));
            }
            finally
            {
                ImageLoader.FetchBytesAsync = previousFetcher;
                ImageLoader.ClearCache();
            }
        }

        [Fact]
        public void AudioWithoutControlsIsNotRendered()
        {
            var audio = new Element("audio");
            CssComputed style = new CssComputed { Display = "inline" };
            UAStyleProvider.Apply(audio, ref style);
            Assert.Equal("none", style.Display);

            var withControls = new Element("audio");
            withControls.SetAttribute("controls", "");
            CssComputed controlsStyle = new CssComputed { Display = "inline" };
            UAStyleProvider.Apply(withControls, ref controlsStyle);
            Assert.Equal("inline", controlsStyle.Display);
        }

        private static PaintNodeBase BuildVideoNode(Element video, BoxModel box, CssComputed style)
        {
            var builderType = typeof(SkiaDomRenderer).Assembly.GetType("FenBrowser.FenEngine.Rendering.NewPaintTreeBuilder");
            Assert.NotNull(builderType);
            var ctor = builderType!.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                new[]
                {
                    typeof(IReadOnlyDictionary<Node, BoxModel>),
                    typeof(IReadOnlyDictionary<Node, CssComputed>),
                    typeof(float),
                    typeof(float),
                    typeof(ScrollManager),
                    typeof(string)
                },
                modifiers: null);
            Assert.NotNull(ctor);
            var builder = ctor!.Invoke(new object[]
            {
                new Dictionary<Node, BoxModel> { [video] = box },
                new Dictionary<Node, CssComputed> { [video] = style },
                16f,
                16f,
                new ScrollManager(),
                "https://example.test/page"
            });
            var method = builderType.GetMethod("BuildVideoPlaceholder", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method!.Invoke(builder, new object[] { video, box, style }) as PaintNodeBase;
        }

        private static byte[] CreatePngBytes(int width, int height, SKColor color)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
