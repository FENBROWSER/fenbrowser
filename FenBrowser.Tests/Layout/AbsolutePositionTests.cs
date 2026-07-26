using Xunit;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using SkiaSharp;

namespace FenBrowser.Tests.Layout
{
    public class AbsolutePositionTests
    {
        [Fact]
        [Trait("Category", "Layout")]
        public void Solver_FixedDimensions_PositionsCorrectly()
        {
            var cb = new ContainingBlock { Width = 100, Height = 100 };

            var style = new CssComputed();
            style.Left = 10.0;
            style.Top = 20.0;
            style.Width = 50.0;
            style.Height = 30.0;
            style.Position = "absolute";

            var result = AbsolutePositionSolver.Solve(style, cb);

            Assert.Equal(10f, result.X);
            Assert.Equal(20f, result.Y);
            Assert.Equal(50f, result.Width);
            Assert.Equal(30f, result.Height);
        }

        [Fact]
        [Trait("Category", "Layout")]
        public void Solver_BorderBoxDimensions_ReturnContentSizeWithoutDoubleCountingBorders()
        {
            var cb = new ContainingBlock { Width = 100, Height = 100 };
            var style = new CssComputed
            {
                Position = "absolute",
                Left = -4,
                Top = -4,
                Width = 36,
                Height = 36,
                BoxSizing = "border-box",
                BorderThickness = new FenBrowser.Core.Thickness(6)
            };

            var result = AbsolutePositionSolver.Solve(style, cb);

            Assert.Equal(24f, result.Width);
            Assert.Equal(24f, result.Height);
            Assert.Equal(2f, result.X);
            Assert.Equal(2f, result.Y);
        }

        [Fact]
        public void Solver_RightConstraint_CalculatesWidth()
        {
            var cb = new ContainingBlock { Width = 100, Height = 100 };

            var style = new CssComputed();
            style.Left = 10.0;
            style.Right = 10.0;
            style.Top = 0.0;
            style.Height = 20.0;
            style.Width = null;
            style.Position = "absolute";

            var result = AbsolutePositionSolver.Solve(style, cb);

            Assert.Equal(80f, result.Width);
            Assert.Equal(10f, result.X);
        }

        [Fact]
        public void Solver_AutoMargins_CenterHorizontally()
        {
            var cb = new ContainingBlock { Width = 100, Height = 100 };

            var style = new CssComputed();
            style.Left = 0.0;
            style.Right = 0.0;
            style.Width = 40.0;
            style.MarginLeftAuto = true;
            style.MarginRightAuto = true;
            style.Top = 0.0;
            style.Height = 20.0;
            style.Position = "absolute";

            var result = AbsolutePositionSolver.Solve(style, cb);

            Assert.Equal(40f, result.Width);
            Assert.Equal(30f, result.MarginLeft);
            Assert.Equal(30f, result.MarginRight);
            Assert.Equal(30f, result.X);
        }

        [Fact]
        public void Solver_Vertical_BottomConstraint()
        {
            var cb = new ContainingBlock { Width = 100, Height = 200 };

            var style = new CssComputed();
            style.Top = 50.0;
            style.Bottom = 50.0;
            style.Left = 0.0;
            style.Width = 50.0;
            style.Height = null;
            style.Position = "absolute";

            var result = AbsolutePositionSolver.Solve(style, cb);

            Assert.Equal(100f, result.Height);
            Assert.Equal(50f, result.Y);
        }

        [Fact]
        public void ResolvePositionedBox_FixedInsetUsesViewportContainingBlock()
        {
            var parentStyle = new CssComputed { Display = "block" };
            var parent = new BlockBox(new Element("div"), parentStyle);
            parent.Geometry.ContentBox = new SKRect(10, 20, 60, 70);
            parent.Geometry.PaddingBox = parent.Geometry.ContentBox;
            parent.Geometry.BorderBox = parent.Geometry.ContentBox;
            parent.Geometry.MarginBox = parent.Geometry.ContentBox;

            var childStyle = new CssComputed
            {
                Position = "fixed",
                Left = 0,
                Top = 0,
                Right = 0,
                Bottom = 0
            };

            var child = new BlockBox(new Element("div"), childStyle);
            var state = new LayoutState(new SKSize(50, 50), 50, 50, 800, 600);

            LayoutPositioningLogic.ResolvePositionedBox(child, parent, parent.Geometry, state);

            Assert.Equal(0f, child.Geometry.ContentBox.Left);
            Assert.Equal(0f, child.Geometry.ContentBox.Top);
            Assert.Equal(800f, child.Geometry.ContentBox.Width);
            Assert.Equal(600f, child.Geometry.ContentBox.Height);
        }

        [Fact]
        public void ResolvePositionedBox_ExplicitZeroDimensions_ArePreserved()
        {
            var parentStyle = new CssComputed { Display = "block" };
            var parent = new BlockBox(new Element("div"), parentStyle);
            parent.Geometry.ContentBox = new SKRect(0, 0, 200, 200);
            parent.Geometry.PaddingBox = parent.Geometry.ContentBox;
            parent.Geometry.BorderBox = parent.Geometry.ContentBox;
            parent.Geometry.MarginBox = parent.Geometry.ContentBox;

            var iframe = new Element("iframe");
            var childStyle = new CssComputed
            {
                Position = "absolute",
                Left = 10,
                Top = 20,
                Width = 0,
                Height = 0
            };

            var child = new BlockBox(iframe, childStyle);

            LayoutPositioningLogic.ResolvePositionedBox(child, parent, parent.Geometry);

            Assert.Equal(10f, child.Geometry.ContentBox.Left);
            Assert.Equal(20f, child.Geometry.ContentBox.Top);
            Assert.Equal(0f, child.Geometry.ContentBox.Width);
            Assert.Equal(0f, child.Geometry.ContentBox.Height);
        }

        [Fact]
        public void Solver_RightSet_WidthSet_LeftAuto_ResolvesLeftFromRight()
        {
            // Exact scenario from test.html: .absolute-child { position:absolute; top:20px;
            // right:20px; width:100px; height:50px } inside .relative-parent { width:300px }
            var cb = new ContainingBlock { Width = 300, Height = 150 };

            var style = new CssComputed();
            style.Position = "absolute";
            style.Top = 20.0;
            style.Right = 20.0;
            style.Width = 100.0;
            style.Height = 50.0;
            // left is auto (not set)

            var result = AbsolutePositionSolver.Solve(style, cb);

            // width must be honored exactly
            Assert.Equal(100f, result.Width);
            Assert.Equal(50f, result.Height);
            // left = 300 - 0 - 0 - 100 - 0 - 20 = 180
            Assert.Equal(180f, result.X);
            Assert.Equal(20f, result.Y);
        }

        [Fact]
        public void Solver_LeftSet_WidthSet_RightAuto_ResolvesWidthFromLeft()
        {
            // Mirror: left + width set, right auto → width honored, right solved
            var cb = new ContainingBlock { Width = 300, Height = 150 };

            var style = new CssComputed();
            style.Position = "absolute";
            style.Left = 20.0;
            style.Width = 100.0;
            style.Top = 20.0;
            style.Height = 50.0;
            // right is auto

            var result = AbsolutePositionSolver.Solve(style, cb);

            Assert.Equal(100f, result.Width);
            Assert.Equal(20f, result.X);
        }

        [Fact]
        public void Solver_RightAndWidthSet_ProducesFiniteNonNegativeX()
        {
            // Regression: ensure solver never produces NaN/negative X when right + width are set
            var cb = new ContainingBlock { Width = 800, Height = 600 };
            var style = new CssComputed
            {
                Position = "absolute",
                Right = 20,
                Width = 200,
                Top = 0,
                Height = 100
            };
            var result = AbsolutePositionSolver.Solve(style, cb);
            Assert.True(float.IsFinite(result.X), "X must be finite");
            Assert.True(result.X >= 0, $"X must be non-negative, got {result.X}");
            Assert.Equal(200f, result.Width);
        }

        [Fact]
        public void Solver_RightOnlyAutoWidthWithoutIntrinsic_DoesNotFillContainingBlock()
        {
            var cb = new ContainingBlock { Width = 800, Height = 600 };
            var style = new CssComputed
            {
                Position = "fixed",
                Right = 40,
                Top = 20,
                Height = 48
            };

            var result = AbsolutePositionSolver.Solve(style, cb, intrinsicWidth: 0, intrinsicHeight: 48);

            Assert.Equal(0f, result.Width);
            Assert.Equal(760f, result.X);
        }

        [Fact]
        public void ResolvePositionedBox_ShiftsInFlowDescendantsWithAbsoluteParent()
        {
            // Parent, child, and grandChild must share a LayoutBoxStore so that
            // child.Children iteration returns the actual grandChild wrapper
            // rather than the cached parent wrapper at the same StoreId.
            var store = new LayoutBoxStore();

            var parentStyle = new CssComputed { Display = "block" };
            int parentId = store.CreateBox(new Element("div"), parentStyle, LayoutBoxStore.BoxType.Block);
            var parent = store.GetWrapper(parentId);
            parent.Geometry.ContentBox = new SKRect(100, 200, 500, 600);
            parent.Geometry.PaddingBox = parent.Geometry.ContentBox;
            parent.Geometry.BorderBox = parent.Geometry.ContentBox;
            parent.Geometry.MarginBox = parent.Geometry.ContentBox;

            var childStyle = new CssComputed
            {
                Position = "absolute",
                Left = 20,
                Top = 30,
                Width = 120,
                Height = 40
            };
            int childId = store.CreateBox(new Element("div"), childStyle, LayoutBoxStore.BoxType.Block);
            var child = store.GetWrapper(childId);
            child.Geometry.ContentBox = new SKRect(0, 0, 120, 40);
            child.Geometry.PaddingBox = child.Geometry.ContentBox;
            child.Geometry.BorderBox = child.Geometry.ContentBox;
            child.Geometry.MarginBox = child.Geometry.ContentBox;

            int grandChildId = store.CreateBox(new Element("div"), new CssComputed { Display = "block", Width = 50, Height = 10 }, LayoutBoxStore.BoxType.Block);
            var grandChild = store.GetWrapper(grandChildId);
            grandChild.Geometry.ContentBox = new SKRect(0, 0, 50, 10);
            grandChild.Geometry.PaddingBox = grandChild.Geometry.ContentBox;
            grandChild.Geometry.BorderBox = grandChild.Geometry.ContentBox;
            grandChild.Geometry.MarginBox = grandChild.Geometry.ContentBox;
            child.AddChild(grandChild);

            LayoutPositioningLogic.ResolvePositionedBox(child, parent, parent.Geometry);

            Assert.Equal(120f, child.Geometry.ContentBox.Left);
            Assert.Equal(230f, child.Geometry.ContentBox.Top);
            Assert.Equal(120f, grandChild.Geometry.ContentBox.Left);
            Assert.Equal(230f, grandChild.Geometry.ContentBox.Top);
        }
    }
}
