using System.Collections.Generic;
using System.Reflection;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.Host.ProcessIsolation;
using FenBrowser.Host.Tabs;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.ProcessIsolation;

public sealed class BrokeredInputRoutingTests
{
    [Fact]
    public async Task BrowserIntegration_HandleKeyPress_RoutesTextToRendererOnlyInBrokeredMode()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();

            await tab.Browser.HandleKeyPress("t");
            await tab.Browser.HandleKeyPress("Enter");

            await WaitForAsync(
                () => coordinator.Inputs.Count >= 2,
                "queued keyboard input to reach the renderer coordinator");

            Assert.Collection(
                coordinator.Inputs,
                input =>
                {
                    Assert.Equal(RendererInputEventType.TextInput, input.Type);
                    Assert.Equal("t", input.Text);
                },
                input =>
                {
                    Assert.Equal(RendererInputEventType.KeyDown, input.Type);
                    Assert.Equal("Enter", input.Key);
                });
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    [Fact]
    public async Task BrowserIntegration_HandleMouseUp_DoesNotMirrorActivationInUiProcess()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();
            var browserHost = GetBrowserHost(tab);
            SetCurrentUri(browserHost, new System.Uri("about:blank"));

            var form = new Element("form");
            form.SetAttribute("action", "#submitted");
            form.SetAttribute("method", "GET");

            var button = new Element("button");
            button.SetAttribute("type", "submit");
            var span = new Element("span");
            button.AppendChild(span);

            form.AppendChild(button);

            var hit = new HitTestResult(
                TagName: "span",
                Href: null,
                Cursor: CursorType.Pointer,
                IsClickable: true,
                IsFocusable: false,
                IsEditable: false,
                NativeElement: span);

            SetHitTestCache(tab, hit);

            tab.Browser.HandleMouseUp(10, 10, button: 0, emitClick: true);

            Assert.Contains(coordinator.Inputs, inputEvent =>
                inputEvent.Type == RendererInputEventType.MouseUp &&
                inputEvent.Button == 0 &&
                inputEvent.EmitClick);

            await Task.Delay(50);
            Assert.Equal("about:blank", browserHost.CurrentUri?.AbsoluteUri);
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    [Fact]
    public async Task Program_DispatchRendererInputAsync_MouseUpRunsDefaultActivationInRenderer()
    {
        const int viewportWidth = 320;
        const int viewportHeight = 200;
        const string html = """
            <!doctype html>
            <html><body style="margin:0">
              <input id="target" type="checkbox" style="width:24px;height:24px;margin:20px">
            </body></html>
            """;

        using var host = new BrowserHost();
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);
        await host.Engine.RenderAsync(
            html,
            new Uri("https://fen.test/renderer-input"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth,
            viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var target = Assert.IsType<Element>(root.OwnerDocument?.GetElementById("target"));
        using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
        using var canvas = new SKCanvas(bitmap);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = host.ComputedStyles,
            Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
            BaseUrl = "https://fen.test/renderer-input",
            InvalidationReason = RenderFrameInvalidationReason.Input,
            RequestedBy = nameof(Program_DispatchRendererInputAsync_MouseUpRunsDefaultActivationInRenderer),
            EmitVerificationReport = false
        });
        Assert.True(renderer.LastLayout.TryGetElementRect(target, out var rect));

        await FenBrowser.Host.Program.DispatchRendererInputAsync(
            host,
            new RendererInputEvent
            {
                Type = RendererInputEventType.MouseUp,
                X = rect.Left + (rect.Width / 2f),
                Y = rect.Top + (rect.Height / 2f),
                Button = 0,
                EmitClick = true
            });

        Assert.True(ElementStateManager.Instance.IsChecked(target));
    }

    [Fact]
    public void BrowserIntegration_HandleMouseMove_RoutesToRendererWithoutLocalInputFrameInBrokeredMode()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();

            tab.Browser.HandleMouseMove(12, 34);

            Assert.Contains(coordinator.Inputs, inputEvent =>
                inputEvent.Type == RendererInputEventType.MouseMove &&
                inputEvent.X == 12 &&
                inputEvent.Y == 34);

            var pendingReasons = GetPendingInvalidationReasons(tab);
            Assert.True(
                (pendingReasons & RenderFrameInvalidationReason.Input) == 0,
                $"Expected brokered hover to wait for the renderer child frame instead of queuing a local input repaint; got {pendingReasons}.");
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    [Fact]
    public void BrowserIntegration_HandleRightClick_RoutesContextMenuToRendererInBrokeredMode()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();

            tab.Browser.HandleRightClick(12, 34);

            Assert.Contains(coordinator.Inputs, inputEvent =>
                inputEvent.Type == RendererInputEventType.MouseDown &&
                inputEvent.Button == 2);
            Assert.Contains(coordinator.Inputs, inputEvent =>
                inputEvent.Type == RendererInputEventType.MouseUp &&
                inputEvent.Button == 2);
            Assert.Contains(coordinator.Inputs, inputEvent =>
                inputEvent.Type == RendererInputEventType.ContextMenu &&
                inputEvent.Button == 2);
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    [Fact]
    public void BrowserIntegration_HandleMouseUp_SecondClickRoutesDblClickInBrokeredMode()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();

            tab.Browser.HandleMouseUp(18, 22, button: 0, emitClick: true);
            tab.Browser.HandleMouseUp(18, 22, button: 0, emitClick: true);

            Assert.Contains(coordinator.Inputs, inputEvent =>
                inputEvent.Type == RendererInputEventType.DblClick &&
                inputEvent.Button == 0);
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    [Fact]
    public async Task BrowserIntegration_HandleMouseWheel_RoutesThroughIntegrationAndStartsSmoothScrollInBrokeredMode()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();
            SetScrollableContent(tab, viewportHeight: 200, contentHeight: 1000);
            var before = tab.Browser.EffectiveScrollY;
            var expectedTarget = before + 80f;

            tab.Browser.HandleMouseWheel(20, 30, deltaX: 1, deltaY: -2, viewportOffsetX: 5, viewportOffsetY: 7);

            await WaitForAsync(
                () => coordinator.Inputs.Any(inputEvent => inputEvent.Type == RendererInputEventType.MouseWheel),
                "queued wheel input to reach the renderer coordinator");
            Assert.Contains(coordinator.Inputs, inputEvent =>
                inputEvent.Type == RendererInputEventType.MouseWheel &&
                inputEvent.X == 15 &&
                inputEvent.Y == 23 &&
                inputEvent.DeltaX == 1 &&
                inputEvent.DeltaY == -2);

            var firstStep = tab.Browser.EffectiveScrollY;
            Assert.InRange(firstStep, before + 0.1f, expectedTarget - 0.1f);
            Assert.True(
                firstStep <= before + 20f,
                $"Expected smooth wheel scroll to start with a small first step, got {firstStep - before:F1}px of an {expectedTarget - before:F1}px target.");

            tab.Browser.UpdateScrollPhysics(1d / 60d);
            await WaitForAsync(
                () => tab.Browser.EffectiveScrollY > firstStep + 0.1f,
                "queued scroll animation tick to advance the smooth scroll");
            Assert.InRange(tab.Browser.EffectiveScrollY, firstStep + 0.1f, expectedTarget);

            for (int i = 0; i < 90; i++)
            {
                tab.Browser.UpdateScrollPhysics(1d / 60d);
            }
            await WaitForAsync(
                () => tab.Browser.EffectiveScrollY >= expectedTarget - 0.5f,
                "queued scroll animation ticks to reach their target");

            Assert.Equal(expectedTarget, tab.Browser.EffectiveScrollY, precision: 0);

            var pendingReasons = GetPendingInvalidationReasons(tab);
            Assert.True(
                (pendingReasons & RenderFrameInvalidationReason.Scroll) != 0,
                $"Expected brokered wheel input to request a scroll frame; got {pendingReasons}.");
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    [Fact]
    public void BrowserIntegration_RemoteFrameReadyWithMatchingScroll_ResetsCompositorPreview()
    {
        var tab = new BrowserTab();
        var receiveMethod = typeof(FenBrowser.Host.BrowserIntegration).GetMethod("OnFrameReceivedFromRenderer", BindingFlags.Instance | BindingFlags.NonPublic);
        var liveScrollField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_scrollY", BindingFlags.Instance | BindingFlags.NonPublic);
        var previewScrollField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_compositorPreviewScrollY", BindingFlags.Instance | BindingFlags.NonPublic);
        var hasPreviewField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_hasCompositorScrollPreview", BindingFlags.Instance | BindingFlags.NonPublic);
        var remoteScrollField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_remoteFrameScrollY", BindingFlags.Instance | BindingFlags.NonPublic);
        var remoteSequenceField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_remoteFrameSequenceNumber", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(receiveMethod);
        Assert.NotNull(liveScrollField);
        Assert.NotNull(previewScrollField);
        Assert.NotNull(hasPreviewField);
        Assert.NotNull(remoteScrollField);
        Assert.NotNull(remoteSequenceField);

        liveScrollField!.SetValue(tab.Browser, 120f);
        previewScrollField!.SetValue(tab.Browser, 120f);
        hasPreviewField!.SetValue(tab.Browser, true);

        var payload = new RendererFrameReadyPayload
        {
            SurfaceWidth = 2,
            SurfaceHeight = 2,
            PixelData = CreateSolidBgraPixels(2, 2, SKColors.Red),
            FrameSequenceNumber = 7,
            ScrollY = 120f,
            ContentHeight = 800f
        };

        receiveMethod!.Invoke(tab.Browser, new object[] { tab.Id, payload });

        Assert.False((bool)hasPreviewField.GetValue(tab.Browser)!);
        Assert.Equal(120f, (float)remoteScrollField!.GetValue(tab.Browser)!, 0.5f);
        Assert.Equal((uint)7, (uint)remoteSequenceField!.GetValue(tab.Browser)!);
    }

    [Fact]
    public void BrowserIntegration_RenderBrokeredFrame_AppliesCompositorScrollDelta()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();
            var receiveMethod = typeof(FenBrowser.Host.BrowserIntegration).GetMethod("OnFrameReceivedFromRenderer", BindingFlags.Instance | BindingFlags.NonPublic);
            var liveScrollField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_scrollY", BindingFlags.Instance | BindingFlags.NonPublic);
            var previewScrollField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_compositorPreviewScrollY", BindingFlags.Instance | BindingFlags.NonPublic);
            var hasPreviewField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_hasCompositorScrollPreview", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(receiveMethod);
            Assert.NotNull(liveScrollField);
            Assert.NotNull(previewScrollField);
            Assert.NotNull(hasPreviewField);

            liveScrollField!.SetValue(tab.Browser, 10f);
            previewScrollField!.SetValue(tab.Browser, 10f);
            hasPreviewField!.SetValue(tab.Browser, false);

            var payload = new RendererFrameReadyPayload
            {
                SurfaceWidth = 4,
                SurfaceHeight = 6,
                PixelData = CreateRowBgraPixels(4, new[] { SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Yellow, SKColors.Cyan, SKColors.Lime }),
                FrameSequenceNumber = 11,
                ScrollY = 10f,
                ContentHeight = 100f
            };

            receiveMethod!.Invoke(tab.Browser, new object[] { tab.Id, payload });

            liveScrollField.SetValue(tab.Browser, 12f);
            previewScrollField.SetValue(tab.Browser, 12f);
            hasPreviewField.SetValue(tab.Browser, true);

            using var bitmap = new SKBitmap(4, 4);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Magenta);

            tab.Browser.Render(canvas, new SKRect(0, 0, 4, 4));
            canvas.Flush();

            var topPixel = bitmap.GetPixel(1, 0);
            Assert.True(
                topPixel.Blue > 180 && topPixel.Red < 80 && topPixel.Green < 120,
                $"Expected brokered compositor preview to shift the committed frame by the live scroll delta; top pixel was {topPixel}.");

            var bottomPixel = bitmap.GetPixel(1, 3);
            Assert.True(
                bottomPixel.Green > 180 && bottomPixel.Red < 80 && bottomPixel.Blue < 80,
                $"Expected brokered compositor preview to use overdraw rows for the newly exposed bottom band; bottom pixel was {bottomPixel}.");
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    [Fact]
    public void Program_ComputeBrokeredFrameRasterHeight_AddsBottomScrollOverdraw()
    {
        Assert.Equal(328f, FenBrowser.Host.Program.ComputeBrokeredFrameRasterHeight(200f), precision: 0);
        Assert.Equal(1200f, FenBrowser.Host.Program.ComputeBrokeredFrameRasterHeight(800f), precision: 0);
        Assert.Equal(FrameSharedMemory.MaxHeight, FenBrowser.Host.Program.ComputeBrokeredFrameRasterHeight(FrameSharedMemory.MaxHeight), precision: 0);
    }

    [Fact]
    public void BrowserIntegration_RemoteFrameAheadOfLiveScroll_DoesNotReplaceCommittedBitmap()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();
            SetScrollableContent(tab, viewportHeight: 4, contentHeight: 100);
            var receiveMethod = typeof(FenBrowser.Host.BrowserIntegration).GetMethod("OnFrameReceivedFromRenderer", BindingFlags.Instance | BindingFlags.NonPublic);
            var remoteScrollField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_remoteFrameScrollY", BindingFlags.Instance | BindingFlags.NonPublic);
            var remoteSequenceField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_remoteFrameSequenceNumber", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(receiveMethod);
            Assert.NotNull(remoteScrollField);
            Assert.NotNull(remoteSequenceField);

            tab.Browser.ScrollToY(10f);
            var committedPayload = new RendererFrameReadyPayload
            {
                SurfaceWidth = 4,
                SurfaceHeight = 4,
                PixelData = CreateRowBgraPixels(4, new[] { SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Yellow }),
                FrameSequenceNumber = 11,
                ScrollY = 10f,
                ContentHeight = 100f
            };

            receiveMethod!.Invoke(tab.Browser, new object[] { tab.Id, committedPayload });
            Assert.Equal(10f, (float)remoteScrollField!.GetValue(tab.Browser)!, 0.5f);
            Assert.Equal((uint)11, (uint)remoteSequenceField!.GetValue(tab.Browser)!);

            tab.Browser.ScrollToY(12f);
            var futurePayload = new RendererFrameReadyPayload
            {
                SurfaceWidth = 4,
                SurfaceHeight = 4,
                PixelData = CreateSolidBgraPixels(4, 4, SKColors.Cyan),
                FrameSequenceNumber = 12,
                ScrollY = 14f,
                ContentHeight = 100f
            };

            receiveMethod.Invoke(tab.Browser, new object[] { tab.Id, futurePayload });

            Assert.Equal(10f, (float)remoteScrollField.GetValue(tab.Browser)!, 0.5f);
            Assert.Equal((uint)11, (uint)remoteSequenceField.GetValue(tab.Browser)!);

            using var bitmap = new SKBitmap(4, 4);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Magenta);

            tab.Browser.Render(canvas, new SKRect(0, 0, 4, 4));
            canvas.Flush();

            var topPixel = bitmap.GetPixel(1, 0);
            Assert.True(
                topPixel.Blue > 180 && topPixel.Red < 80 && topPixel.Green < 120,
                $"Expected future remote frame to be ignored so the previous committed bitmap remains compositor-scrolled; top pixel was {topPixel}.");
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    [Fact]
    public async Task RendererChildFramePattern_RasterizesScrolledDocumentBand()
    {
        const int viewportWidth = 120;
        const int viewportHeight = 80;
        const float scrollY = 220f;

        const string html = """
<!doctype html>
<html>
<body style="margin:0;background:#fff">
  <div style="height:200px;background:#dc2626"></div>
  <div id="visible-band" style="height:180px;background:#16a34a"></div>
</body>
</html>
""";

        var parser = new FenBrowser.Core.Parsing.HtmlParser(html, new Uri("https://fen.test/scroll"));
        var document = parser.Parse();
        var root = document.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
        var styles = await CssLoader.ComputeAsync(root, new Uri("https://fen.test/scroll"), null, viewportWidth, viewportHeight);

        var renderer = new SkiaDomRenderer();
        renderer.ScrollManager.SetScrollBounds(null, viewportWidth, 380f, viewportWidth, viewportHeight);
        renderer.ScrollManager.SetScrollPosition(null, 0, scrollY);

        using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Save();
        canvas.Translate(0, -scrollY);
        try
        {
            renderer.RenderFrame(new RenderFrameRequest
            {
                Root = root,
                Canvas = canvas,
                Styles = styles,
                Viewport = new SKRect(0, scrollY, viewportWidth, scrollY + viewportHeight),
                SeparateLayoutViewport = new SKSize(viewportWidth, viewportHeight),
                BaseUrl = "https://fen.test/scroll",
                InvalidationReason = RenderFrameInvalidationReason.ProcessIsolation,
                RequestedBy = "BrokeredInputRoutingTests.RendererChildFramePattern",
                EmitVerificationReport = false
            });
        }
        finally
        {
            canvas.Restore();
        }
        canvas.Flush();

        var pixel = bitmap.GetPixel(24, 24);
        Assert.InRange(pixel.Green, 120, 190);
        Assert.True(pixel.Red < 60 && pixel.Blue < 100, $"Expected scrolled band to rasterize green, got {pixel}.");
    }

    [Fact]
    public async Task ScrollOnlyDamage_RasterizesNewlyExposedDocumentBand()
    {
        const int viewportWidth = 120;
        const int viewportHeight = 120;
        const float scrollY = 10f;

        const string html = """
<!doctype html>
<html>
<body style="margin:0;background:#fff">
  <div style="height:120px;background:#dc2626"></div>
  <div id="new-band" style="height:120px;background:#16a34a"></div>
</body>
</html>
""";

        var parser = new FenBrowser.Core.Parsing.HtmlParser(html, new Uri("https://fen.test/scroll-damage"));
        var document = parser.Parse();
        var root = document.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
        var styles = await CssLoader.ComputeAsync(root, new Uri("https://fen.test/scroll-damage"), null, viewportWidth, viewportHeight);

        using var retainedRasterizer = new RetainedTileRasterizer(maxVisibleTiles: 1);
        var renderer = new SkiaDomRenderer(retainedRasterizer)
        {
            SafetyPolicy = new RendererSafetyPolicy
            {
                EnableWatchdog = false,
                SkipRasterWhenOverBudget = false
            }
        };

        renderer.ScrollManager.SetScrollBounds(null, viewportWidth, 240f, viewportWidth, viewportHeight);
        renderer.ScrollManager.SetScrollPosition(null, 0, 0);

        using var firstBitmap = new SKBitmap(viewportWidth, viewportHeight);
        using (var firstCanvas = new SKCanvas(firstBitmap))
        {
            firstCanvas.Clear(SKColors.White);
            renderer.RenderFrame(new RenderFrameRequest
            {
                Root = root,
                Canvas = firstCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                SeparateLayoutViewport = new SKSize(viewportWidth, viewportHeight),
                BaseUrl = "https://fen.test/scroll-damage",
                InvalidationReason = RenderFrameInvalidationReason.Navigation,
                RequestedBy = "BrokeredInputRoutingTests.ScrollDamage.First",
                EmitVerificationReport = false
            });
            firstCanvas.Flush();
        }

        renderer.ScrollManager.SetScrollPosition(null, 0, scrollY);

        using var secondBitmap = new SKBitmap(viewportWidth, viewportHeight);
        using var secondCanvas = new SKCanvas(secondBitmap);
        secondCanvas.Clear(SKColors.White);
        secondCanvas.DrawBitmap(firstBitmap, 0, -scrollY);
        secondCanvas.Save();
        secondCanvas.Translate(0, -scrollY);
        RenderFrameResult secondFrame;
        try
        {
            secondFrame = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = root,
                Canvas = secondCanvas,
                Styles = styles,
                Viewport = new SKRect(0, scrollY, viewportWidth, scrollY + viewportHeight),
                SeparateLayoutViewport = new SKSize(viewportWidth, viewportHeight),
                BaseUrl = "https://fen.test/scroll-damage",
                HasBaseFrame = true,
                InvalidationReason = RenderFrameInvalidationReason.Scroll,
                RequestedBy = "BrokeredInputRoutingTests.ScrollDamage.Second",
                EmitVerificationReport = false
            });
        }
        finally
        {
            secondCanvas.Restore();
        }
        secondCanvas.Flush();

        Assert.True(
            renderer.LastFrameUsedDamageRasterization,
            $"Expected the second frame to exercise damage rasterization. mode={secondFrame?.RasterMode} damage={renderer.LastDamageRegions.Count} tileEnabled={renderer.LastRetainedTileRasterization.Enabled} tileVisible={renderer.LastRetainedTileRasterization.VisibleTileCount}");

        var exposedPixel = secondBitmap.GetPixel(24, viewportHeight - 5);
        Assert.InRange(exposedPixel.Green, 120, 190);
        Assert.True(
            exposedPixel.Red < 80 && exposedPixel.Blue < 100,
            $"Expected newly exposed scrolled band to rasterize green, got {exposedPixel}.");
    }

    private static BrowserHost GetBrowserHost(BrowserTab tab)
    {
        var field = typeof(FenBrowser.Host.BrowserIntegration).GetField("_browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<BrowserHost>(field!.GetValue(tab.Browser));
    }

    private static void SetCurrentUri(BrowserHost host, System.Uri uri)
    {
        var currentField = typeof(BrowserHost).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentField);
        currentField!.SetValue(host, uri);
    }

    private static void SetHitTestCache(BrowserTab tab, HitTestResult hit)
    {
        var lastHitField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_lastHitTest", BindingFlags.Instance | BindingFlags.NonPublic);
        var cachedHitField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_cachedHitTest", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(lastHitField);
        Assert.NotNull(cachedHitField);
        lastHitField!.SetValue(tab.Browser, hit);
        cachedHitField!.SetValue(tab.Browser, hit);
    }

    private static RenderFrameInvalidationReason GetPendingInvalidationReasons(BrowserTab tab)
    {
        var field = typeof(FenBrowser.Host.BrowserIntegration).GetField("_pendingInvalidationReasons", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (RenderFrameInvalidationReason)field!.GetValue(tab.Browser)!;
    }

    private static void SetScrollableContent(BrowserTab tab, float viewportHeight, float contentHeight)
    {
        var viewportField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_lastViewportSize", BindingFlags.Instance | BindingFlags.NonPublic);
        var contentHeightField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_contentHeight", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(viewportField);
        Assert.NotNull(contentHeightField);
        viewportField!.SetValue(tab.Browser, new SKSize(800, viewportHeight));
        contentHeightField!.SetValue(tab.Browser, contentHeight);
    }

    private static async Task WaitForAsync(System.Func<bool> predicate, string failureMessage)
    {
        for (var i = 0; i < 50; i++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), failureMessage);
    }

    private static byte[] CreateSolidBgraPixels(int width, int height, SKColor color)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.Blue;
            pixels[i + 1] = color.Green;
            pixels[i + 2] = color.Red;
            pixels[i + 3] = color.Alpha;
        }

        return pixels;
    }

    private static byte[] CreateRowBgraPixels(int width, IReadOnlyList<SKColor> rowColors)
    {
        var pixels = new byte[width * rowColors.Count * 4];
        var offset = 0;
        foreach (var color in rowColors)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[offset++] = color.Blue;
                pixels[offset++] = color.Green;
                pixels[offset++] = color.Red;
                pixels[offset++] = color.Alpha;
            }
        }

        return pixels;
    }

    private sealed class RecordingCoordinator : IProcessIsolationCoordinator
    {
        public string Mode => "test-brokered";
        public bool UsesOutOfProcessRenderer => true;
        public List<RendererInputEvent> Inputs { get; } = new();

        public void Initialize() { }
        public void OnTabCreated(BrowserTab tab) { }
        public void OnTabActivated(BrowserTab tab) { }
        public void OnNavigationRequested(BrowserTab tab, string url, bool isUserInput) { }
        public void OnInputEvent(BrowserTab tab, RendererInputEvent inputEvent) => Inputs.Add(inputEvent);
        public void OnFrameRequested(BrowserTab tab, float viewportWidth, float viewportHeight, float scrollY = 0) { }
        public void OnTabClosed(BrowserTab tab) { }
        public void Shutdown() { }

#pragma warning disable CS0067
        public event System.Action<int, RendererFrameReadyPayload> FrameReceived;
        public event System.Action<int, RendererMetadataChangedPayload> MetadataChanged;
        public event System.Action<int, string> RendererCrashed;
#pragma warning restore CS0067
    }
}
