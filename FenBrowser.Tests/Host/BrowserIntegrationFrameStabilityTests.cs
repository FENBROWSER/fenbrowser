using System.Reflection;
using System.Threading;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Host;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Host
{
    [Collection("Host UI Tests")]
    public class BrowserIntegrationFrameStabilityTests
    {
        [Fact]
        public void RecordFrame_WithCommittedFrameAndMissingStyles_KeepsPreviousFrame()
        {
            var integration = new BrowserIntegration();

            try
            {
                var currentFrameField = typeof(BrowserIntegration).GetField("_currentFrame", BindingFlags.Instance | BindingFlags.NonPublic);
                var rootField = typeof(BrowserIntegration).GetField("_root", BindingFlags.Instance | BindingFlags.NonPublic);
                var stylesField = typeof(BrowserIntegration).GetField("_styles", BindingFlags.Instance | BindingFlags.NonPublic);
                var needsRepaintField = typeof(BrowserIntegration).GetField("_needsRepaint", BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.NotNull(currentFrameField);
                Assert.NotNull(rootField);
                Assert.NotNull(stylesField);
                Assert.NotNull(needsRepaintField);

                using var recorder = new SKPictureRecorder();
                using var canvas = recorder.BeginRecording(new SKRect(0, 0, 40, 40));
                canvas.Clear(SKColors.White);
                var committedFrame = recorder.EndRecording();

                currentFrameField!.SetValue(integration, committedFrame);
                rootField!.SetValue(integration, new Element("html"));
                stylesField!.SetValue(integration, null);
                needsRepaintField!.SetValue(integration, true);

                integration.RecordFrame(new SKSize(1280, 720));

                Assert.Same(committedFrame, currentFrameField.GetValue(integration));
                Assert.False((bool)needsRepaintField.GetValue(integration)!);
            }
            finally
            {
                ShutdownEngineLoop(integration);
            }
        }

        private static void ShutdownEngineLoop(BrowserIntegration integration)
        {
            var runningField = typeof(BrowserIntegration).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic);
            var wakeEventField = typeof(BrowserIntegration).GetField("_wakeEvent", BindingFlags.Instance | BindingFlags.NonPublic);
            var engineThreadField = typeof(BrowserIntegration).GetField("_engineThread", BindingFlags.Instance | BindingFlags.NonPublic);
            var currentFrameField = typeof(BrowserIntegration).GetField("_currentFrame", BindingFlags.Instance | BindingFlags.NonPublic);

            runningField?.SetValue(integration, false);
            (wakeEventField?.GetValue(integration) as AutoResetEvent)?.Set();
            (engineThreadField?.GetValue(integration) as Thread)?.Join(TimeSpan.FromSeconds(2));
            (currentFrameField?.GetValue(integration) as SKPicture)?.Dispose();
        }
    }
}
