using Xunit;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using SkiaSharp;
using FenBrowser.FenEngine.Rendering.Css; // For CssComputed

namespace FenBrowser.Tests.Layout
{
    public class AbsolutePositionTests
    {
        [Fact]
        public void Solver_FixedDimensions_PositionsCorrectly()
        {
            // Containing Block: 100x100
            var cb = new ContainingBlock { Width = 100, Height = 100 };
            
            // Element: left:10, top:20, width:50, height:30
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
        public void Solver_RightConstraint_CalculatesWidth()
        {
            // CB: 100x100
            var cb = new ContainingBlock { Width = 100, Height = 100 };
            
            // left:10, right:10. Width auto.
            var style = new CssComputed();
            style.Left = 10.0;
            style.Right = 10.0;
            style.Top = 0.0;
            style.Height = 20.0;
            style.Width = null; // auto
            style.Position = "absolute";
            
            var result = AbsolutePositionSolver.Solve(style, cb);
            
            // 10 + width + 10 = 100 => width = 80
            Assert.Equal(80f, result.Width);
            Assert.Equal(10f, result.X);
        }

        [Fact]
        public void Solver_AutoMargins_CenterHorizontally()
        {
            // CB: 100x100
            var cb = new ContainingBlock { Width = 100, Height = 100 };
            
            // left:0, right:0, width:40. Margin-left/right: auto.
            // Equation: 0 + ml + 40 + mr + 0 = 100 => ml + mr = 60 => ml=30, mr=30.
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
            // X = left + ml = 0 + 30 = 30.
            Assert.Equal(30f, result.X);
        }

        [Fact]
        public void Solver_Vertical_BottomConstraint()
        {
            var cb = new ContainingBlock { Width = 100, Height = 200 };
            
            // top: 50, bottom: 50. Height auto.
            // 50 + h + 50 = 200 => h = 100.
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
        public void Solver_AutoWidthWithLeftPercent_PreservesIntrinsicForReplacedElements()
        {
            var cb = new ContainingBlock { Width = 1920, Height = 804 };

            var style = new CssComputed
            {
                Position = "absolute",
                LeftPercent = 50,
                Bottom = 0,
                HeightPercent = 100
            };

            var withoutPreserve = AbsolutePositionSolver.Solve(
                style,
                cb,
                intrinsicWidth: 1920,
                intrinsicHeight: 804,
                preserveIntrinsicAutoSize: false);

            var withPreserve = AbsolutePositionSolver.Solve(
                style,
                cb,
                intrinsicWidth: 1920,
                intrinsicHeight: 804,
                preserveIntrinsicAutoSize: true);

            Assert.Equal(960f, withoutPreserve.Width);
            Assert.Equal(1920f, withPreserve.Width);
            Assert.Equal(960f, withPreserve.X);
            Assert.Equal(804f, withPreserve.Height);
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
        public void ResolvePositionedBox_ShiftsInFlowDescendantsWithAbsoluteParent()
        {
            var parentStyle = new CssComputed { Display = "block" };
            var parent = new BlockBox(new Element("div"), parentStyle);
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

            var child = new BlockBox(new Element("div"), childStyle);
            child.Geometry.ContentBox = new SKRect(0, 0, 120, 40);
            child.Geometry.PaddingBox = child.Geometry.ContentBox;
            child.Geometry.BorderBox = child.Geometry.ContentBox;
            child.Geometry.MarginBox = child.Geometry.ContentBox;

            var grandChild = new BlockBox(new Element("div"), new CssComputed { Display = "block", Width = 50, Height = 10 });
            grandChild.Parent = child;
            grandChild.Geometry.ContentBox = new SKRect(0, 0, 50, 10);
            grandChild.Geometry.PaddingBox = grandChild.Geometry.ContentBox;
            grandChild.Geometry.BorderBox = grandChild.Geometry.ContentBox;
            grandChild.Geometry.MarginBox = grandChild.Geometry.ContentBox;
            child.Children.Add(grandChild);

            LayoutPositioningLogic.ResolvePositionedBox(child, parent, parent.Geometry);

            Assert.Equal(120f, child.Geometry.ContentBox.Left);
            Assert.Equal(230f, child.Geometry.ContentBox.Top);
            Assert.Equal(120f, grandChild.Geometry.ContentBox.Left);
            Assert.Equal(230f, grandChild.Geometry.ContentBox.Top);
        }
    }
}
