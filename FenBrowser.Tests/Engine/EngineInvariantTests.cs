using System.Collections.Generic;
using Xunit;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tests.Engine
{
    public class EngineInvariantTests
    {
        [Fact]
        public void StackingContextBuilder_CreatesRootContext()
        {
            // Arrange
            var root = new Element("html");
            var styles = new Dictionary<Node, CssComputed>();
            
            // Act
            // Act
            // Convert to Layout Tree (mocking layout)
            var rootBox = new FenBrowser.FenEngine.Layout.Tree.BlockBox(root, styles.ContainsKey(root) ? styles[root] : new CssComputed());
            
            // Build SC
            var sc = StackingContext.Build(rootBox);

            // Assert
            Assert.NotNull(sc);
            Assert.NotNull(sc.Root);
            Assert.Same(root, sc.Root.SourceNode);
            // Assert.Empty(sc.NegativeZ);
            // Assert.Empty(sc.PositiveZ);
        }

        [Fact]
        public void StackingContextBuilder_OpacityCreatesContext()
        {
            // Arrange
            var root = new Element("html");
            var child = new Element("div");
            root.AppendChild(child);

            var styles = new Dictionary<Node, CssComputed>();
            styles[child] = new CssComputed { Opacity = 0.5f, Display = "block" };
            styles[root] = new CssComputed { Display = "block" }; // Root needs style?

            // Act
            // Act
            // Mock Layout Tree
            var rootBox = new FenBrowser.FenEngine.Layout.Tree.BlockBox(root, styles.ContainsKey(root) ? styles[root] : new CssComputed());
            var childBox = new FenBrowser.FenEngine.Layout.Tree.BlockBox(child, styles[child]);
            rootBox.Children.Add(childBox);
            childBox.Parent = rootBox;

            var rootSC = StackingContext.Build(rootBox);

            // Assert
            Assert.NotNull(rootSC);
            // New StackingContext structure uses PositiveZ/NegativeZ lists
            // Opacity < 1 creates a new stacking context.
            // With z-index: auto (default), it's painted in tree order, but as a stacking context.
            // My implementation puts opacity < 1 into PositiveZ with z=0? Or separate list?
            // Line 48 in StackingContext.cs: `if (isPositioned && hasZIndex || isOpacity) ... ctx.PositiveZ.Add(childCtx)` (if z >= 0)
            
            Assert.Single(rootSC.PositiveZ);
            Assert.Same(childBox, rootSC.PositiveZ[0].Root);
        }
    }
}
