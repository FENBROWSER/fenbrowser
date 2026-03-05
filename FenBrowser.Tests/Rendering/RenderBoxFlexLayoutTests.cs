using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class RenderBoxFlexLayoutTests
    {
        [Fact]
        public void LayoutFlexChildren_RowFlexStart_PositionsItemsSequentially()
        {
            var container = CreateContainer("flex-start");
            container.AddChild(CreateFixedItem(50, 20));
            container.AddChild(CreateFixedItem(50, 20));

            container.Layout(new SKSize(300, 100));

            Assert.Equal(0f, container.Children[0].Bounds.Left);
            Assert.Equal(50f, container.Children[1].Bounds.Left);
            Assert.True(container.Children[1].Bounds.Left >= container.Children[0].Bounds.Right);
        }

        [Fact]
        public void LayoutFlexChildren_RowSpaceBetween_AppliesMainAxisGapWithoutOverlap()
        {
            var container = CreateContainer("space-between");
            container.AddChild(CreateFixedItem(50, 20));
            container.AddChild(CreateFixedItem(50, 20));

            container.Layout(new SKSize(300, 100));

            Assert.Equal(0f, container.Children[0].Bounds.Left);
            Assert.Equal(250f, container.Children[1].Bounds.Left);
            Assert.True(container.Children[1].Bounds.Left >= container.Children[0].Bounds.Right);
        }

        private static RenderBox CreateContainer(string justifyContent)
        {
            return new RenderBox
            {
                Style = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    JustifyContent = justifyContent,
                    AlignItems = "flex-start",
                    Width = 300,
                    Height = 100,
                    Margin = new Thickness(0),
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0)
                }
            };
        }

        private static RenderObject CreateFixedItem(float width, float height)
        {
            return new FixedSizeRenderObject(width, height)
            {
                Style = new CssComputed
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0)
                }
            };
        }

        private sealed class FixedSizeRenderObject : RenderObject
        {
            private readonly float _width;
            private readonly float _height;

            public FixedSizeRenderObject(float width, float height)
            {
                _width = width;
                _height = height;
            }

            public override void Layout(SKSize availableSize)
            {
                Bounds = SKRect.Create(0, 0, _width, _height);
            }
        }
    }
}
