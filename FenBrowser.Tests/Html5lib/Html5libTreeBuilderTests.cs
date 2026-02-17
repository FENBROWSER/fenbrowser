// Html5lib-style Tree Builder Tests for FenBrowser
// Tests parser tree construction against known HTML5 edge cases
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.HTML;

namespace FenBrowser.Tests.Html5lib
{
    /// <summary>
    /// Html5lib-style tree builder tests validating WHATWG compliance.
    /// Tests cover: AAA, misnesting, table foster parenting, foreign content.
    /// </summary>
    public class Html5libTreeBuilderTests
    {
        private Document Parse(string html)
        {
            var tokenizer = new HtmlTokenizer(html);
            var builder = new HtmlTreeBuilder(tokenizer);
            return builder.Build();
        }
        
        // --- Basic Structure Tests ---
        
        [Fact]
        public void Parse_BasicHtmlStructure()
        {
            var doc = Parse("<html><head></head><body></body></html>");
            var html = doc.Children.OfType<Element>().FirstOrDefault(e => e.TagName == "HTML");
            Assert.NotNull(html);
            Assert.Contains(html.Children!.OfType<Element>(), e => e.TagName == "HEAD");
            Assert.Contains(html.Children!.OfType<Element>(), e => e.TagName == "BODY");
        }
        
        [Fact]
        public void Parse_ImplicitHtmlHeadBody()
        {
            var doc = Parse("<p>Hello</p>");
            var body = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "BODY");
            Assert.NotNull(body);
            var p = body.Children!.OfType<Element>().FirstOrDefault(e => e.TagName == "P");
            Assert.NotNull(p);
        }
        
        // --- Adoption Agency Algorithm Tests ---
        
        [Fact]
        public void Parse_AAA_MisnestedBoldItalic()
        {
            // <b><i></b></i> should produce proper tree
            var doc = Parse("<p><b><i>text</b></i></p>");
            // The AAA ensures both <b> and <i> are properly closed
            var p = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "P");
            Assert.NotNull(p);
        }
        
        [Fact]
        public void Parse_AAA_NestedFormatting()
        {
            // Multiple formatting tags should be handled
            var doc = Parse("<b>1<i>2</i>3</b>");
            var b = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "B");
            Assert.NotNull(b);
        }
        
        [Fact]
        public void Parse_AAA_FormattingWithSpecial()
        {
            // <b><div></b></div> - div is special, AAA applies
            var doc = Parse("<b><div>text</b></div>");
            // DIV should interrupt the <b>
            var div = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "DIV");
            Assert.NotNull(div);
        }
        
        // --- Table Tests ---
        
        [Fact]
        public void Parse_SimpleTable()
        {
            var doc = Parse("<table><tr><td>cell</td></tr></table>");
            var table = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "TABLE");
            Assert.NotNull(table);
            var tr = table.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "TR");
            Assert.NotNull(tr);
            var td = tr!.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "TD");
            Assert.NotNull(td);
        }
        
        [Fact]
        public void Parse_TableWithImpliedTbody()
        {
            var doc = Parse("<table><tr><td>cell</td></tr></table>");
            var table = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "TABLE");
            Assert.NotNull(table);
            // TBODY should be implied
            var tbody = table.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "TBODY");
            Assert.NotNull(tbody);
        }
        
        // --- Foreign Content Tests ---
        
        [Fact]
        public void Parse_SvgElement()
        {
            var doc = Parse("<svg viewBox=\"0 0 100 100\"><circle cx=\"50\"/></svg>");
            var svg = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName.Equals("svg", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(svg);
            Assert.Equal(ForeignContent.SvgNamespace, svg!.NamespaceUri);
        }
        
        [Fact]
        public void Parse_MathElement()
        {
            var doc = Parse("<math><mi>x</mi></math>");
            var math = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName.Equals("math", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(math);
            Assert.Equal(ForeignContent.MathMLNamespace, math!.NamespaceUri);
        }
        
        [Fact]
        public void Parse_SvgBreakout()
        {
            // <p> should break out of SVG
            var doc = Parse("<svg><p>text</p></svg>");
            var p = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "P");
            Assert.NotNull(p);
            // P should be in HTML namespace, not SVG
            Assert.Equal(ForeignContent.HtmlNamespace, p!.NamespaceUri);
        }
        
        // --- Select Element Tests ---
        
        [Fact]
        public void Parse_SelectWithOptions()
        {
            var doc = Parse("<select><option>A</option><option>B</option></select>");
            var select = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "SELECT");
            Assert.NotNull(select);
            var options = select!.Children!.OfType<Element>().Where(e => e.TagName == "OPTION").ToList();
            Assert.Equal(2, options.Count);
        }
        
        // --- Template Element Tests ---
        
        [Fact]
        public void Parse_TemplateElement()
        {
            var doc = Parse("<template><div>content</div></template>");
            var template = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "TEMPLATE");
            Assert.NotNull(template);
        }
        
        // --- Void Element Tests ---
        
        [Fact]
        public void Parse_VoidElements()
        {
            var doc = Parse("<br><hr><img src=\"x\"><input type=\"text\">");
            var br = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "BR");
            var hr = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "HR");
            var img = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "IMG");
            var input = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "INPUT");
            Assert.NotNull(br);
            Assert.NotNull(hr);
            Assert.NotNull(img);
            Assert.NotNull(input);
        }
        
        // --- Entity Handling in Tree ---
        
        [Fact]
        public void Parse_EntityDecodingInContent()
        {
            var doc = Parse("<p>&lt;script&gt;</p>");
            var p = doc.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "P");
            Assert.NotNull(p);
            var text = p!.Children!.OfType<Text>().FirstOrDefault();
            Assert.NotNull(text);
            Assert.Contains("<script>", text!.Data);
        }
    }
}
