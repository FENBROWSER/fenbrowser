using System.Collections.Generic;
using Xunit;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
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
            var builder = new StackingContextBuilder(styles);
            var sc = builder.BuildTree(root);

            // Assert
            Assert.NotNull(sc);
            Assert.True(sc.IsRoot);
            Assert.Same(root, sc.Node);
            Assert.Empty(sc.NegativeZContexts);
            Assert.Empty(sc.PositiveZContexts);
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
            var builder = new StackingContextBuilder(styles);
            var rootSC = builder.BuildTree(root);

            // Assert
            Assert.NotNull(rootSC);
            // Child should be in PositionedLayers? No, ZeroZContexts.
            // Opacity < 1 creates stacking context with z-index: 0 (auto treated as 0 for stacking).
            // Actually, Opacity creates SC.
            // Z-Index applies if Positioned.
            // If Opacity but NOT Positioned, Z-Index doesn't apply (auto).
            // But Opacity creates SC, so it is treated as atomic.
            // Where does it go in Parent?
            // "Painted atomically in tree order". Phase 6 (Zero Z / Positioned)?
            // Or Normal Flow?
            // Spec: "If the element is a block, float, or inline... it is painted in that phase."
            // BUT if it creates a Stacking Context...?
            // "If the element creates a new stacking context... the stacking context is painted as part of the parent stacking context".
            // Phase for Opacity?
            // Usually Phase 6 (same as Z-Index 0).
            
            Assert.NotNull(rootSC.ZeroZContexts);
            Assert.Single(rootSC.ZeroZContexts);
            Assert.Same(child, rootSC.ZeroZContexts[0].Node);
        }
    }
}
