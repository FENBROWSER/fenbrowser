using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tests.Layout
{
    public static class LayoutTestHelper
    {
        public static LayoutEngineComputer CreateComputer(Element root, Dictionary<Node, CssComputed> styles, float width = 800, float height = 600)
        {
            return new LayoutEngineComputer(styles, width, height);
        }

        /// <summary>
        /// Creates a LayoutEngine using the production box-tree pipeline.
        /// Tests should prefer this over CreateComputer so they exercise the
        /// same code path as SkiaDomRenderer.
        /// </summary>
        public static LayoutEngine CreateEngine(Element root, Dictionary<Node, CssComputed> styles, float width = 800, float height = 600)
        {
            return new LayoutEngine(styles, width, height);
        }

        /// <summary>
        /// Convenience: builds a LayoutEngine and runs ComputeLayout, returning the
        /// resulting box dictionary keyed by Node.
        /// </summary>
        public static IReadOnlyDictionary<Node, BoxModel> LayoutTree(Element root, Dictionary<Node, CssComputed> styles, float width = 800, float height = 600)
        {
            var engine = new LayoutEngine(styles, width, height);
            engine.ComputeLayout(root, width, height);
            return engine.AllBoxes;
        }

        public static Dictionary<Node, CssComputed> CreateStyles(Element container, CssComputed containerStyle)
        {
            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = containerStyle
            };
            
            foreach (var child in container.Descendants())
            {
                if (!styles.ContainsKey(child))
                {
                    styles[child] = new CssComputed();
                }
            }
            
            return styles;
        }

        public static Element CreateBlockContainer(int childCount, float width = 100, float height = 100)
        {
            var container = new Element("div");
            for (int i = 0; i < childCount; i++)
            {
                var child = new Element("div");
                child.SetAttribute("style", $"width: {width}px; height: {height}px; display: block;");
                container.AppendChild(child);
            }
            return container;
        }
        
        public static void SetStyle(this Dictionary<Node, CssComputed> styles, Node node, CssComputed style)
        {
            styles[node] = style;
        }
    }
}
