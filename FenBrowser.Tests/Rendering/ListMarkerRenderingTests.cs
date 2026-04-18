using System;
using System.Collections.Generic;
using System.Reflection;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Interaction;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class ListMarkerRenderingTests
    {
        [Fact]
        public void BuildListMarkerNode_UsesRealBulletGlyphForDiscMarkers()
        {
            var li = new Element("li");
            var boxes = new Dictionary<Node, BoxModel>();
            var styles = new Dictionary<Node, CssComputed>();
            var box = new BoxModel
            {
                ContentBox = new SKRect(40, 20, 240, 44),
                Lines = new List<ComputedTextLine>
                {
                    new() { Baseline = 16f }
                }
            };

            boxes[li] = box;
            styles[li] = new CssComputed
            {
                Display = "list-item",
                ListStyleType = "disc",
                ListStylePosition = "outside",
                FontSize = 16,
                ForegroundColor = SKColors.Black
            };

            object builder = CreateBuilder(boxes, styles);
            MethodInfo method = typeof(NewPaintTreeBuilder).GetMethod("BuildListMarkerNode", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object result = method!.Invoke(builder, new object[] { li, box, styles[li], false, false });
            var textNode = Assert.IsType<TextPaintNode>(result);

            Assert.Equal("\u2022", textNode.FallbackText);
        }

        [Fact]
        public void BuildListMarkerNode_AnchorsOutsideMarkerBeforeEarlierChildContent()
        {
            var li = new Element("li");
            var link = new Element("a");
            li.AppendChild(link);

            var boxes = new Dictionary<Node, BoxModel>();
            var styles = new Dictionary<Node, CssComputed>();
            var liBox = new BoxModel
            {
                BorderBox = new SKRect(442, 536.6f, 1510, 555.8f),
                ContentBox = new SKRect(442, 536.6f, 1510, 555.8f),
                Lines = new List<ComputedTextLine>
                {
                    new() { Baseline = 16f }
                }
            };
            var linkBox = new BoxModel
            {
                BorderBox = new SKRect(410, 528.6f, 654.7f, 547.8f),
                ContentBox = new SKRect(410, 528.6f, 654.7f, 547.8f)
            };

            boxes[li] = liBox;
            boxes[link] = linkBox;
            styles[li] = new CssComputed
            {
                Display = "list-item",
                ListStyleType = "disc",
                ListStylePosition = "outside",
                FontSize = 16,
                ForegroundColor = SKColors.Black
            };

            object builder = CreateBuilder(boxes, styles);
            MethodInfo method = typeof(NewPaintTreeBuilder).GetMethod("BuildListMarkerNode", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object result = method!.Invoke(builder, new object[] { li, liBox, styles[li], false, false });
            var textNode = Assert.IsType<TextPaintNode>(result);

            Assert.True(textNode.Bounds.Right <= linkBox.ContentBox.Left - 4);
        }

        [Fact]
        public void BuildListMarkerNode_SuppressesMarker_WhenParentListStyleIsNone()
        {
            var ul = new Element("ul");
            var li = new Element("li");
            ul.AppendChild(li);

            var boxes = new Dictionary<Node, BoxModel>();
            var styles = new Dictionary<Node, CssComputed>();
            var liBox = new BoxModel
            {
                ContentBox = new SKRect(40, 20, 240, 44),
                Lines = new List<ComputedTextLine>
                {
                    new() { Baseline = 16f }
                }
            };

            boxes[li] = liBox;
            var ulStyle = new CssComputed
            {
                Display = "block"
            };
            ulStyle.Map["list-style"] = "none";
            styles[ul] = ulStyle;
            styles[li] = new CssComputed
            {
                Display = "list-item",
                ListStyleType = null,
                FontSize = 16,
                ForegroundColor = SKColors.Black
            };

            object builder = CreateBuilder(boxes, styles);
            MethodInfo method = typeof(NewPaintTreeBuilder).GetMethod("BuildListMarkerNode", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object result = method!.Invoke(builder, new object[] { li, liBox, styles[li], false, false });
            Assert.Null(result);
        }

        [Fact]
        public void ShouldHideCollapsedVectorPanel_HidesDropdownContent_WhenToggleUnchecked()
        {
            var container = new Element("div");
            var toggle = new Element("input");
            toggle.SetAttribute("class", "vector-dropdown-checkbox");
            var content = new Element("div");
            content.SetAttribute("class", "vector-dropdown-content");
            container.AppendChild(toggle);
            container.AppendChild(content);

            MethodInfo method = typeof(NewPaintTreeBuilder).GetMethod("ShouldHideCollapsedVectorPanel", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object result = method!.Invoke(null, new object[] { content });
            Assert.IsType<bool>(result);
            Assert.True((bool)result);
        }

        [Fact]
        public void ShouldHideCollapsedVectorPanel_DoesNotHideDropdownContent_WhenToggleChecked()
        {
            var container = new Element("div");
            var toggle = new Element("input");
            toggle.SetAttribute("class", "vector-dropdown-checkbox");
            toggle.SetAttribute("checked", string.Empty);
            var content = new Element("div");
            content.SetAttribute("class", "vector-dropdown-content");
            container.AppendChild(toggle);
            container.AppendChild(content);

            MethodInfo method = typeof(NewPaintTreeBuilder).GetMethod("ShouldHideCollapsedVectorPanel", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object result = method!.Invoke(null, new object[] { content });
            Assert.IsType<bool>(result);
            Assert.False((bool)result);
        }

        private static object CreateBuilder(IReadOnlyDictionary<Node, BoxModel> boxes, IReadOnlyDictionary<Node, CssComputed> styles)
        {
            var ctor = typeof(NewPaintTreeBuilder).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[]
                {
                    typeof(IReadOnlyDictionary<Node, BoxModel>),
                    typeof(IReadOnlyDictionary<Node, CssComputed>),
                    typeof(float),
                    typeof(float),
                    typeof(ScrollManager),
                    typeof(string)
                },
                modifiers: null);

            Assert.NotNull(ctor);
            return ctor!.Invoke(new object[] { boxes, styles, 800f, 600f, new ScrollManager(), "https://www.iana.org/" });
        }
    }
}
