using FenBrowser.Host.Widgets;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Host
{
    [Collection("Host UI Tests")]
    public class DockPanelLayoutTests
    {
        [Fact]
        public void Arrange_LastDockedChildFillsRemainingSpace_WhenFloatingOverlayIsLast()
        {
            var root = new DockPanel { LastChildFill = true };
            var toolbar = new TestWidget(120, 48);
            var content = new TestWidget(120, 0);
            var popup = new TestWidget(40, 20);

            root.AddChild(toolbar, Dock.Top);
            root.AddChild(content, Dock.Fill);
            root.AddChild(popup, Dock.None);

            root.Measure(new SKSize(300, 200));
            root.Arrange(new SKRect(0, 0, 300, 200));

            Assert.Equal(0, content.Bounds.Left);
            Assert.Equal(48, content.Bounds.Top);
            Assert.Equal(300, content.Bounds.Right);
            Assert.Equal(200, content.Bounds.Bottom);
        }

        [Fact]
        public void Arrange_ClampsDockedChildren_WhenRequestedHeightExceedsRemainingSpace()
        {
            var root = new DockPanel { LastChildFill = true };
            var header = new TestWidget(100, 90);
            var footer = new TestWidget(100, 90);
            var content = new TestWidget(100, 0);

            root.AddChild(header, Dock.Top);
            root.AddChild(footer, Dock.Bottom);
            root.AddChild(content, Dock.Fill);

            root.Measure(new SKSize(100, 120));
            root.Arrange(new SKRect(0, 0, 100, 120));

            Assert.True(header.Bounds.Height >= 0);
            Assert.True(footer.Bounds.Height >= 0);
            Assert.True(content.Bounds.Height >= 0);
            Assert.True(content.Bounds.Top <= content.Bounds.Bottom);
        }

        private sealed class TestWidget : Widget
        {
            private readonly SKSize _size;

            public TestWidget(float width, float height)
            {
                _size = new SKSize(width, height);
            }

            protected override SKSize OnMeasure(SKSize availableSpace)
            {
                return _size;
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
