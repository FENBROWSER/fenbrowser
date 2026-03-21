using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineImagePrewarmTests
    {
        [Fact]
        public async Task PrewarmImageAsync_CachesBitmapForImmediateFirstPaint()
        {
            ImageLoader.ClearCache();
            try
            {
                using var stream = CreatePngStream(4, 3, SKColors.CornflowerBlue);

                var ok = await ImageLoader.PrewarmImageAsync("https://example.test/assets/logo.png", stream);

                Assert.True(ok);
                Assert.True(ImageLoader.ContainsCachedImage("https://example.test/assets/logo.png"));

                var bitmap = ImageLoader.GetImage("https://example.test/assets/logo.png");
                Assert.NotNull(bitmap);
                Assert.True(bitmap.Width > 0);
                Assert.True(bitmap.Height > 0);
                Assert.Equal(0, ImageLoader.PendingLoadCount);
            }
            finally
            {
                ImageLoader.ClearCache();
            }
        }

        [Fact]
        public async Task CustomHtmlEngine_PrewarmImages_PopulatesImageLoaderCache()
        {
            ImageLoader.ClearCache();
            try
            {
                const string html = @"
<!doctype html>
<html>
<body>
  <img src='/images/hero.png' width='4' height='3'>
</body>
</html>";

                var parser = new HtmlParser(html, new Uri("https://example.test/page"));
                var document = parser.Parse();
                var root = document.DocumentElement;
                Assert.NotNull(root);

                Func<Uri, Task<Stream>> loader = _ =>
                    Task.FromResult<Stream>(CreatePngStream(4, 3, SKColors.OrangeRed));

                await InvokePrewarmImagesAsync(root, new Uri("https://example.test/page"), loader, 1280d);

                var cached = SpinWait.SpinUntil(
                    () => ImageLoader.ContainsCachedImage("https://example.test/images/hero.png"),
                    TimeSpan.FromSeconds(2));

                Assert.True(cached);

                var bitmap = ImageLoader.GetImage("https://example.test/images/hero.png");
                Assert.NotNull(bitmap);
                Assert.True(bitmap.Width > 0);
                Assert.True(bitmap.Height > 0);
            }
            finally
            {
                ImageLoader.ClearCache();
            }
        }

        [Fact]
        public async Task CustomHtmlEngine_PrewarmImages_AwaitsEagerBatchBeforeReturning()
        {
            ImageLoader.ClearCache();
            var backgroundRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var html = BuildImagePage(7);
                var parser = new HtmlParser(html, new Uri("https://example.test/page"));
                var document = parser.Parse();
                var root = document.DocumentElement;
                Assert.NotNull(root);

                Func<Uri, Task<Stream>> loader = async uri =>
                {
                    if (uri.AbsoluteUri.EndsWith("img7.png", StringComparison.Ordinal))
                    {
                        await backgroundRelease.Task.ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(40).ConfigureAwait(false);
                    }

                    return CreatePngStream(4, 3, SKColors.ForestGreen);
                };

                await InvokePrewarmImagesAsync(root, new Uri("https://example.test/page"), loader, 1280d);

                for (int i = 1; i <= 6; i++)
                {
                    Assert.True(ImageLoader.ContainsCachedImage($"https://example.test/images/img{i}.png"));
                }

                Assert.False(ImageLoader.ContainsCachedImage("https://example.test/images/img7.png"));

                backgroundRelease.TrySetResult(true);
                var backgroundCached = SpinWait.SpinUntil(
                    () => ImageLoader.ContainsCachedImage("https://example.test/images/img7.png"),
                    TimeSpan.FromSeconds(2));

                Assert.True(backgroundCached);
            }
            finally
            {
                backgroundRelease.TrySetResult(true);
                ImageLoader.ClearCache();
            }
        }

        [Fact]
        public async Task CustomHtmlEngine_PrewarmImages_DoesNotBlockOnBackgroundTail()
        {
            ImageLoader.ClearCache();
            var backgroundRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var html = BuildImagePage(7);
                var parser = new HtmlParser(html, new Uri("https://example.test/page"));
                var document = parser.Parse();
                var root = document.DocumentElement;
                Assert.NotNull(root);

                Func<Uri, Task<Stream>> loader = async uri =>
                {
                    if (uri.AbsoluteUri.EndsWith("img7.png", StringComparison.Ordinal))
                    {
                        await backgroundRelease.Task.ConfigureAwait(false);
                    }

                    return CreatePngStream(4, 3, SKColors.MediumPurple);
                };

                var prewarmTask = InvokePrewarmImagesAsync(root, new Uri("https://example.test/page"), loader, 1280d);
                var completed = await Task.WhenAny(prewarmTask, Task.Delay(TimeSpan.FromSeconds(1)));

                Assert.Same(prewarmTask, completed);
                await prewarmTask;
                Assert.False(ImageLoader.ContainsCachedImage("https://example.test/images/img7.png"));

                backgroundRelease.TrySetResult(true);
                var backgroundCached = SpinWait.SpinUntil(
                    () => ImageLoader.ContainsCachedImage("https://example.test/images/img7.png"),
                    TimeSpan.FromSeconds(2));

                Assert.True(backgroundCached);
            }
            finally
            {
                backgroundRelease.TrySetResult(true);
                ImageLoader.ClearCache();
            }
        }

        [Fact]
        public async Task CustomHtmlEngine_PrewarmImages_UsesImageLoaderByteFetcherBeforeStreamFallback()
        {
            ImageLoader.ClearCache();
            var previousByteFetcher = ImageLoader.FetchBytesAsync;
            try
            {
                const string html = @"
<!doctype html>
<html>
<body>
  <img src='/images/hero.png' width='4' height='3'>
</body>
</html>";

                var parser = new HtmlParser(html, new Uri("https://example.test/page"));
                var document = parser.Parse();
                var root = document.DocumentElement;
                Assert.NotNull(root);

                var streamCalls = 0;
                ImageLoader.FetchBytesAsync = _ => Task.FromResult(CreatePngBytes(4, 3, SKColors.DeepSkyBlue));

                Func<Uri, Task<Stream>> loader = _ =>
                {
                    Interlocked.Increment(ref streamCalls);
                    return Task.FromResult<Stream>(CreatePngStream(4, 3, SKColors.OrangeRed));
                };

                await InvokePrewarmImagesAsync(root, new Uri("https://example.test/page"), loader, 1280d);

                Assert.True(ImageLoader.ContainsCachedImage("https://example.test/images/hero.png"));
                Assert.Equal(0, streamCalls);
            }
            finally
            {
                ImageLoader.FetchBytesAsync = previousByteFetcher;
                ImageLoader.ClearCache();
            }
        }

        private static Task InvokePrewarmImagesAsync(Element root, Uri baseUri, Func<Uri, Task<Stream>> loader, double viewportWidth)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                "PrewarmImagesAsync",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var task = method.Invoke(null, new object[] { root, baseUri, loader, viewportWidth }) as Task;
            Assert.NotNull(task);
            return task;
        }

        private static string BuildImagePage(int count)
        {
            var parts = new List<string>();
            for (int i = 1; i <= count; i++)
            {
                parts.Add($"<img src='/images/img{i}.png' width='4' height='3'>");
            }

            return $@"
<!doctype html>
<html>
<body>
  {string.Join(Environment.NewLine + "  ", parts)}
</body>
</html>";
        }

        private static MemoryStream CreatePngStream(int width, int height, SKColor color)
        {
            return new MemoryStream(CreatePngBytes(width, height, color), writable: false);
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
