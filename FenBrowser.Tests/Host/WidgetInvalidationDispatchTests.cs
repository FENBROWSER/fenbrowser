using System;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Host;
using FenBrowser.Host.Widgets;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Host
{
    [Collection("Host UI Tests")]
    public class WidgetInvalidationDispatchTests
    {
        [Fact]
        public async Task Invalidate_FromBackgroundThread_QueuesWorkForUiThread()
        {
            var windowManager = WindowManager.Instance;
            var mainThreadIdField = typeof(WindowManager).GetField("_mainThreadId", BindingFlags.Instance | BindingFlags.NonPublic);
            var processMainThreadQueueMethod = typeof(WindowManager).GetMethod("ProcessMainThreadQueue", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(mainThreadIdField);
            Assert.NotNull(processMainThreadQueueMethod);

            int originalMainThreadId = (int)mainThreadIdField!.GetValue(windowManager)!;

            try
            {
                mainThreadIdField.SetValue(windowManager, Environment.CurrentManagedThreadId);

                var widget = new TestWidget();
                widget.Arrange(new SKRect(0, 0, 120, 48));

                await Task.Run(() => widget.Invalidate());

                Assert.Null(widget.DirtyRect);

                processMainThreadQueueMethod!.Invoke(windowManager, null);

                Assert.True(widget.DirtyRect.HasValue);
                Assert.Equal(0, widget.DirtyRect.Value.Left);
                Assert.Equal(0, widget.DirtyRect.Value.Top);
                Assert.Equal(120, widget.DirtyRect.Value.Right);
                Assert.Equal(48, widget.DirtyRect.Value.Bottom);
            }
            finally
            {
                mainThreadIdField.SetValue(windowManager, originalMainThreadId);
            }
        }

        private sealed class TestWidget : Widget
        {
            protected override SKSize OnMeasure(SKSize availableSpace)
            {
                return availableSpace;
            }

            protected override void OnArrange(SKRect finalRect)
            {
            }

            public override void Paint(SKCanvas canvas)
            {
            }
        }
    }
}
