using System;
using System.Collections.Generic;
using System.Reflection;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineRenderSnapshotTests
    {
        private static MethodInfo GetRequiredMethod(string name, params Type[] parameterTypes)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: parameterTypes,
                modifiers: null);

            Assert.NotNull(method);
            return method;
        }

        [Fact]
        public void GetRenderSnapshot_MarksTransitionSnapshotAsUnstable()
        {
            var engine = new CustomHtmlEngine();

            var initialDom = new Element("html");
            var transitionedDom = new Element("html");

            var initialStyles = new Dictionary<Node, CssComputed>
            {
                [initialDom] = new CssComputed()
            };

            GetRequiredMethod("UpdateRenderState", typeof(Node), typeof(Dictionary<Node, CssComputed>))
                .Invoke(engine, new object[] { initialDom, initialStyles });

            var stableSnapshot = engine.GetRenderSnapshot();
            Assert.True(stableSnapshot.HasStableStyles);
            Assert.Same(initialDom, stableSnapshot.Root);

            GetRequiredMethod("SetActiveDom", typeof(Node), typeof(bool))
                .Invoke(engine, new object[] { transitionedDom, true });

            var transitionSnapshot = engine.GetRenderSnapshot();
            Assert.False(transitionSnapshot.HasStableStyles);
            Assert.Same(transitionedDom, transitionSnapshot.Root);
            Assert.True(transitionSnapshot.Version > stableSnapshot.Version);
        }

        [Fact]
        public void GetRenderSnapshot_MarksSnapshotStableAfterComputedStylesAreSet()
        {
            var engine = new CustomHtmlEngine();
            var dom = new Element("html");

            GetRequiredMethod("SetActiveDom", typeof(Node), typeof(bool))
                .Invoke(engine, new object[] { dom, true });

            var unstableSnapshot = engine.GetRenderSnapshot();
            Assert.False(unstableSnapshot.HasStableStyles);

            var computedStyles = new Dictionary<Node, CssComputed>
            {
                [dom] = new CssComputed()
            };

            GetRequiredMethod("SetComputedStyles", typeof(Dictionary<Node, CssComputed>))
                .Invoke(engine, new object[] { computedStyles });

            var stableSnapshot = engine.GetRenderSnapshot();
            Assert.True(stableSnapshot.HasStableStyles);
            Assert.Same(computedStyles, stableSnapshot.Styles);
            Assert.True(stableSnapshot.Version > unstableSnapshot.Version);
        }
    }
}
