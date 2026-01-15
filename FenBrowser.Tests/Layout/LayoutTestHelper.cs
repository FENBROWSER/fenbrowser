using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tests.Layout
{
    public static class LayoutTestHelper
    {
        public static MinimalLayoutComputer CreateComputer(Element root, Dictionary<Node, CssComputed> styles, float width = 800, float height = 600)
        {
            return new MinimalLayoutComputer(styles, width, height);
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
