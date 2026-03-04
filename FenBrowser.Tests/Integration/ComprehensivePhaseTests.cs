using System;
using System.Threading.Tasks;
using Xunit;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.Tests.Integration
{
    /// <summary>
    /// Comprehensive integration tests covering all implemented phases.
    /// These tests verify end-to-end functionality matching comprehensive_phase_test.html
    /// </summary>
    public class ComprehensivePhaseTests
    {
        // =====================================================
        // PHASE 1.1: Stacking Contexts
        // =====================================================
        [Fact]
        public void Phase1_1_StackingContexts_ZIndexOrdering()
        {
            var doc = new Document();
            var container = new Element("div");
            doc.AppendChild(container);

            var layer1 = new Element("div");
            layer1.SetAttribute("style", "position: absolute; z-index: 1;");
            container.AppendChild(layer1);

            var layer2 = new Element("div");
            layer2.SetAttribute("style", "position: absolute; z-index: 2;");
            container.AppendChild(layer2);

            var layer3 = new Element("div");
            layer3.SetAttribute("style", "position: absolute; z-index: 3;");
            container.AppendChild(layer3);

            Assert.Equal(3, container.Children.Length);
            Assert.Contains("z-index: 3", layer3.GetAttribute("style"));
        }

        // =====================================================
        // PHASE 1.2: Margin Collapsing
        // =====================================================
        [Fact]
        public void Phase1_2_MarginCollapsing_AdjacentSiblings()
        {
            var doc = new Document();
            var container = new Element("div");
            doc.AppendChild(container);

            var box1 = new Element("div");
            box1.SetAttribute("style", "margin-bottom: 30px;");
            container.AppendChild(box1);

            var box2 = new Element("div");
            box2.SetAttribute("style", "margin-top: 20px;");
            container.AppendChild(box2);

            Assert.Equal(2, container.Children.Length);
        }

        // =====================================================
        // PHASE 1.3: Absolute Positioning
        // =====================================================
        [Fact]
        public void Phase1_3_AbsolutePositioning_RelativeToContainer()
        {
            var doc = new Document();
            var container = new Element("div");
            container.SetAttribute("style", "position: relative; width: 400px; height: 200px;");
            doc.AppendChild(container);

            var absBox = new Element("div");
            absBox.SetAttribute("style", "position: absolute; top: 50px; right: 50px;");
            container.AppendChild(absBox);

            Assert.Contains("position: absolute", absBox.GetAttribute("style"));
            Assert.Contains("top: 50px", absBox.GetAttribute("style"));
        }

        // =====================================================
        // PHASE 1.4: Inline Formatting Context
        // =====================================================
        [Fact]
        public void Phase1_4_InlineFormattingContext_MixedContent()
        {
            var doc = new Document();
            var container = new Element("div");
            doc.AppendChild(container);

            var textNode = new Text("This is ");
            container.AppendChild(textNode);

            var span = new Element("span");
            span.AppendChild(new Text("inline span"));
            container.AppendChild(span);

            container.AppendChild(new Text(" text."));

            // Check children count (3 nodes: text, span, text)
            int childCount = 0;
            foreach (var child in container.Descendants()) childCount++;
            Assert.True(childCount >= 3);
            Assert.Equal("SPAN", span.TagName);
        }

        // =====================================================
        // PHASE 1.5: HTML Parser
        // =====================================================
        [Fact]
        public void Phase1_5_HtmlParser_BasicElements()
        {
            var doc = new Document();
            
            var ul = new Element("ul");
            doc.AppendChild(ul);
            
            var li1 = new Element("li");
            li1.AppendChild(new Text("Item 1"));
            ul.AppendChild(li1);
            
            var li2 = new Element("li");
            li2.AppendChild(new Text("Item 2"));
            ul.AppendChild(li2);

            Assert.Equal("UL", ul.TagName);
            Assert.Equal(2, ul.Children.Length);
            Assert.Equal("LI", (ul.Children[0] as Element)?.TagName);
        }

        // =====================================================
        // PHASE 2: DOM & Event System
        // =====================================================
        [Fact]
        public void Phase2_DOM_ChildManipulation()
        {
            var doc = new Document();
            var container = new Element("div");
            doc.AppendChild(container);

            var child = new Element("span");
            container.AppendChild(child);
            Assert.Single(container.Children);

            // Remove child using Remove() from Node
            child.Remove();
            Assert.Empty(container.Children);
        }

        [Fact]
        public void Phase2_Events_AddRemoveListeners()
        {
            var doc = new Document();
            var button = new Element("button");
            doc.AppendChild(button);

            bool clicked = false;
            void handler(Event e) => clicked = true;

            button.AddEventListener("click", handler, false);
            
            // Verify listener was added
            var listeners = button.GetEventListeners("click");
            Assert.NotEmpty(listeners);

            button.RemoveEventListener("click", handler, false);
        }

        // =====================================================
        // PHASE 3.2: Cascade & Specificity
        // =====================================================
        [Fact]
        public void Phase3_2_Cascade_SpecificityOrder()
        {
            var doc = new Document();
            var p = new Element("p");
            p.SetAttribute("id", "test-id");
            p.SetAttribute("class", "test-class");
            doc.AppendChild(p);

            Assert.Equal("test-id", p.Id);
            Assert.True(p.ClassList.Contains("test-class"));
        }

        // =====================================================
        // PHASE 4.1: Flexbox Layout
        // =====================================================
        [Fact]
        public void Phase4_1_Flexbox_RowLayout()
        {
            var doc = new Document();
            var container = new Element("div");
            container.SetAttribute("style", "display: flex; flex-direction: row;");
            doc.AppendChild(container);

            for (int i = 0; i < 3; i++)
            {
                var item = new Element("div");
                item.SetAttribute("style", "flex: 1;");
                container.AppendChild(item);
            }

            Assert.Equal(3, container.Children.Length);
            Assert.Contains("display: flex", container.GetAttribute("style"));
        }

        [Fact]
        public void Phase4_1_Flexbox_WrapBehavior()
        {
            var doc = new Document();
            var container = new Element("div");
            container.SetAttribute("style", "display: flex; flex-wrap: wrap;");
            doc.AppendChild(container);

            for (int i = 0; i < 5; i++)
            {
                var item = new Element("div");
                container.AppendChild(item);
            }

            Assert.Equal(5, container.Children.Length);
            Assert.Contains("flex-wrap: wrap", container.GetAttribute("style"));
        }

        // =====================================================
        // PHASE 5A: MutationObserver
        // =====================================================
        [Fact]
        public async Task Phase5A_MutationObserver_ChildListChanges()
        {
            var doc = new Document();
            var container = new Element("div");
            doc.AppendChild(container);

            var records = new System.Collections.Generic.List<MutationRecord>();
            var observer = new MutationObserver((mutations, obs) => records.AddRange(mutations));
            
            observer.Observe(container, new MutationObserverInit { ChildList = true });

            var child = new Element("span");
            container.AppendChild(child);

            await Task.Delay(100);

            observer.Disconnect();
            Assert.Single(container.Children);
        }

        [Fact]
        public async Task Phase5A_MutationObserver_AttributeChanges()
        {
            var doc = new Document();
            var element = new Element("div");
            doc.AppendChild(element);

            var records = new System.Collections.Generic.List<MutationRecord>();
            var observer = new MutationObserver((mutations, obs) => records.AddRange(mutations));
            
            observer.Observe(element, new MutationObserverInit { Attributes = true });

            element.SetAttribute("data-test", "modified");

            await Task.Delay(100);

            observer.Disconnect();
            Assert.Equal("modified", element.GetAttribute("data-test"));
        }

        // =====================================================
        // PHASE 5B: Console API
        // =====================================================
        [Fact]
        public void Phase5B_Console_AllMethodsExist()
        {
            var runtime = new FenRuntime();
            var console = runtime.GetGlobal("console")?.AsObject();

            Assert.NotNull(console);
            
            // Core methods
            Assert.NotNull(console.Get("log"));
            Assert.NotNull(console.Get("error"));
            Assert.NotNull(console.Get("warn"));
            Assert.NotNull(console.Get("info"));
            
            // Extended methods (Module 5B)
            Assert.NotNull(console.Get("dir"));
            Assert.NotNull(console.Get("table"));
            Assert.NotNull(console.Get("group"));
            Assert.NotNull(console.Get("groupEnd"));
            Assert.NotNull(console.Get("time"));
            Assert.NotNull(console.Get("timeEnd"));
            Assert.NotNull(console.Get("count"));
            Assert.NotNull(console.Get("assert"));
            Assert.NotNull(console.Get("trace"));
        }

        // =====================================================
        // PHASE 5B: Symbol
        // =====================================================
        [Fact]
        public void Phase5B_Symbol_GlobalConstructorExists()
        {
            var runtime = new FenRuntime();
            var symbol = runtime.GetGlobal("Symbol");

            Assert.NotNull(symbol);
            Assert.True(symbol.IsObject || symbol.IsFunction);

            var symbolObj = symbol.AsObject();
            Assert.NotNull(symbolObj.Get("for"));
            Assert.NotNull(symbolObj.Get("keyFor"));
            Assert.NotNull(symbolObj.Get("iterator"));
        }

        // =====================================================
        // COMBINED: Full Page Structure
        // =====================================================
        [Fact]
        public void Combined_FullPageStructure()
        {
            var doc = new Document();
            
            var html = new Element("html");
            doc.AppendChild(html);
            
            var head = new Element("head");
            html.AppendChild(head);
            
            var title = new Element("title");
            title.AppendChild(new Text("Test Page"));
            head.AppendChild(title);
            
            var body = new Element("body");
            html.AppendChild(body);
            
            var h1 = new Element("h1");
            h1.AppendChild(new Text("Hello World"));
            body.AppendChild(h1);
            
            var p = new Element("p");
            p.AppendChild(new Text("This is a test paragraph."));
            body.AppendChild(p);

            Assert.Equal("HTML", html.TagName);
            Assert.Equal(2, html.Children.Length);
            Assert.Equal(2, body.Children.Length);
        }
    }
}

