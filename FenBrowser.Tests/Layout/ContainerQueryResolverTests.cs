using System;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.Core.Dom.V2;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class ContainerQueryResolverTests
    {
        [Fact]
        public void ContainerQuery_MatchesMinWidthCondition()
        {
            var container = CreateLayoutBox(width: 500);
            var child = CreateLayoutBox();
            
            container.ComputedStyle = new CssComputed
            {
                ContainerQueries = new()
                {
                    new ContainerQuery
                    {
                        Selector = ".child",
                        Conditions = new() { new QueryCondition { Feature = "min-width", Value = "400px" } },
                        Rules = new() { ["flex-direction"] = "row" }
                    }
                }
            };
            
            ContainerQueryResolver.Instance.ApplyContainerQueries(container, child);
            
            Assert.Equal("row", child.ComputedStyle.FlexDirection);
        }
        
        [Fact]
        public void ContainerQuery_DoesNotMatchBelowThreshold()
        {
            var container = CreateLayoutBox(width: 300);
            var child = CreateLayoutBox();
            child.ComputedStyle = new CssComputed { FlexDirection = "column" };
            
            container.ComputedStyle = new CssComputed
            {
                ContainerQueries = new()
                {
                    new ContainerQuery
                    {
                        Selector = ".child",
                        Conditions = new() { new QueryCondition { Feature = "min-width", Value = "400px" } },
                        Rules = new() { ["flex-direction"] = "row" }
                    }
                }
            };
            
            ContainerQueryResolver.Instance.ApplyContainerQueries(container, child);
            
            Assert.Equal("column", child.ComputedStyle.FlexDirection);
        }
        
        [Fact]
        public void ContainerQuery_MatchesMaxWidthCondition()
        {
            var container = CreateLayoutBox(width: 300);
            var child = CreateLayoutBox();
            
            container.ComputedStyle = new CssComputed
            {
                ContainerQueries = new()
                {
                    new ContainerQuery
                    {
                        Selector = ".child",
                        Conditions = new() { new QueryCondition { Feature = "max-width", Value = "400px" } },
                        Rules = new() { ["display"] = "none" }
                    }
                }
            };
            
            ContainerQueryResolver.Instance.ApplyContainerQueries(container, child);
            
            Assert.Equal("none", child.ComputedStyle.Display);
        }
        
        private static LayoutBox CreateLayoutBox(float width = 100, float height = 100)
        {
            var element = new Element("div");
            var box = new BlockBox(element, new CssComputed());
            box.Geometry.ContentBox = new SKRect(0, 0, width, height);
            return box;
        }
    }
}
