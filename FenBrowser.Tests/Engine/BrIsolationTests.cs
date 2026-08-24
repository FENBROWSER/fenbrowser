using System.Collections.Generic;
using Xunit;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Tree;

namespace FenBrowser.Tests.Engine
{
    public class BrIsolationTests
    {
        private static string Describe(LayoutBox b)
        {
            string Walk(LayoutBox box, int depth)
            {
                var pad = new string(' ', depth * 2);
                var s = $"{pad}{box.GetType().Name} src={(box.SourceNode as Element)?.TagName ?? (box.SourceNode is Text t ? $"#text({t.Data?.Trim()})" : "null")}\n";
                foreach (var c in box.Children) s += Walk(c, depth + 1);
                return s;
            }
            return b == null ? "<null>" : Walk(b, 0);
        }

        private static CssComputed Block(float w = 400) => new CssComputed { Display = "block", Width = w };

        [Fact]
        public void BrThenBlock_PreservesBothLayoutChildren()
        {
            // THE critical case: br immediately followed by a block sibling.
            var root = new Element("div");
            root.AppendChild(new Text("\n"));
            root.AppendChild(new Element("br"));
            root.AppendChild(new Text("\n"));
            var p = new Element("p");
            root.AppendChild(p);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = Block(),
                [root.ChildNodes[1]] = new CssComputed { Display = "inline" },
                [p] = new CssComputed { Display = "block", Width = 100, Height = 20 }
            };

            var tree = new BoxTreeBuilder(styles).Build(root);
            Assert.True(tree.Children.Count == 2,
                $"Expected [Anon(br), p], got {tree.Children.Count}:\n{Describe(tree)}");
        }

        [Fact]
        public void WhitespaceThenBlock_DoesNotCreateAnonymousBlock()
        {
            var root = new Element("div");
            root.AppendChild(new Text("\n"));
            root.AppendChild(new Text("\n"));
            var p = new Element("p");
            root.AppendChild(p);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = Block(),
                [p] = new CssComputed { Display = "block", Width = 100, Height = 20 }
            };

            var tree = new BoxTreeBuilder(styles).Build(root);
            Assert.Single(tree.Children);
        }

        [Fact]
        public void BrThenBlockWithoutWhitespace_PreservesBothLayoutChildren()
        {
            var root = new Element("div");
            root.AppendChild(new Element("br"));
            var p = new Element("p");
            root.AppendChild(p);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = Block(),
                [root.ChildNodes[0]] = new CssComputed { Display = "inline" },
                [p] = new CssComputed { Display = "block", Width = 100, Height = 20 }
            };

            var tree = new BoxTreeBuilder(styles).Build(root);
            Assert.True(tree.Children.Count == 2,
                $"Expected [Anon(br), p], got {tree.Children.Count}:\n{Describe(tree)}");
        }

        [Fact]
        public void TextAndBrThenBlock_PreservesBothLayoutChildren()
        {
            // Same as C but with non-whitespace text so it survives box building.
            var root = new Element("div");
            root.AppendChild(new Text("x "));
            root.AppendChild(new Element("br"));
            root.AppendChild(new Text("y "));
            var p = new Element("p");
            root.AppendChild(p);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = Block(),
                [p] = new CssComputed { Display = "block", Width = 100, Height = 20 }
            };

            var tree = new BoxTreeBuilder(styles).Build(root);
            Assert.True(tree.Children.Count == 2,
                $"Expected [Anon(x br y), p], got {tree.Children.Count}:\n{Describe(tree)}");
        }
    }
}
