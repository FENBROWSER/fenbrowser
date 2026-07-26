using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public sealed class SkiaDomRendererPaintTreeTraversalTests
    {
        [Fact]
        public void CollectAllNodes_PreSizedResult_DoesNotAllocatePerNode()
        {
            var children = new PaintNodeBase[100];
            for (var index = 0; index < children.Length; index++)
            {
                children[index] = new BackgroundPaintNode();
            }

            var root = new BackgroundPaintNode { Children = children };
            PaintNodeBase[] roots = { root };
            var result = new List<PaintNodeBase>(children.Length + 1);
            var renderer = new SkiaDomRenderer();

            renderer.CollectAllNodes(roots, result);
            Assert.Equal(children.Length + 1, result.Count);
            Assert.Same(root, result[0]);
            for (var index = 0; index < children.Length; index++)
            {
                Assert.Same(children[index], result[index + 1]);
            }

            result.Clear();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 10; iteration++)
            {
                renderer.CollectAllNodes(roots, result);
                result.Clear();
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.Equal(0, allocated);
        }

        [Fact]
        public void Render_ZeroOpacityGroup_DoesNotTraverseOrPaintChildren()
        {
            var childPainted = false;
            var hiddenGroup = new OpacityGroupPaintNode
            {
                Bounds = new SKRect(0, 0, 100, 100),
                Opacity = 0f,
                Children = new PaintNodeBase[]
                {
                    new CustomPaintNode
                    {
                        Bounds = new SKRect(0, 0, 100, 100),
                        PaintAction = (_, _) => childPainted = true
                    }
                }
            };

            using var surface = SKSurface.Create(new SKImageInfo(100, 100));
            var renderer = new SkiaRenderer();
            renderer.Render(
                surface.Canvas,
                new ImmutablePaintTree(new[] { hiddenGroup }),
                new SKRect(0, 0, 100, 100));

            Assert.False(childPainted);
        }

        [Fact]
        public void Render_FilteredContext_CullsOnlyOutsideBlurVisualOutset()
        {
            var farChildPainted = false;
            var nearChildPainted = false;
            var farContext = new StackingContextPaintNode
            {
                Bounds = new SKRect(0, 500, 100, 600),
                Filter = "blur(20px)",
                Children = new PaintNodeBase[]
                {
                    new CustomPaintNode
                    {
                        Bounds = new SKRect(0, 500, 100, 600),
                        PaintAction = (_, _) => farChildPainted = true
                    }
                }
            };
            var nearContext = new StackingContextPaintNode
            {
                Bounds = new SKRect(0, 120, 100, 140),
                Filter = "blur(20px)",
                Children = new PaintNodeBase[]
                {
                    new CustomPaintNode
                    {
                        Bounds = new SKRect(0, 120, 100, 140),
                        PaintAction = (_, _) => nearChildPainted = true
                    }
                }
            };

            using var surface = SKSurface.Create(new SKImageInfo(100, 100));
            var renderer = new SkiaRenderer();
            renderer.Render(
                surface.Canvas,
                new ImmutablePaintTree(new PaintNodeBase[] { farContext, nearContext }),
                new SKRect(0, 0, 100, 100));

            Assert.False(farChildPainted);
            Assert.True(nearChildPainted);
        }
    }
}
