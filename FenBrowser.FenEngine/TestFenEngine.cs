using System;
using FenBrowser.FenEngine.Core;
using System.Linq;
using FenBrowser.Core.Dom.V2; // Updated to V2
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Tests
{
    public class LogicTestRunner
    {
        public static async System.Threading.Tasks.Task MainTest(string[] args)
        {
            Console.WriteLine("=== FenEngine Test Suite ===\n");

            var runtime = new FenRuntime();

            // Test 1: Basic expressions
            Console.WriteLine("Test 1: Basic Arithmetic");
            Test(runtime, "5 + 3", "8");
            Test(runtime, "10 - 4", "6");
            Test(runtime, "3 * 4", "12");
            Test(runtime, "20 / 5", "4");

            // Test 2: Variable declarations
            Console.WriteLine("\nTest 2: Variable Declarations");
            Test(runtime, "let x = 5", "5");
            Test(runtime, "x", "5");
            Test(runtime, "let y = x + 3", "8");
            Test(runtime, "y", "8");

            // Test 3: String operations
            Console.WriteLine("\nTest 3: String Operations");
            Test(runtime, "let greeting = \"Hello\"", "Hello");
            Test(runtime, "let world = \" World\"", " World");
            Test(runtime, "greeting + world", "Hello World");

            // Test 4: Boolean operations
            Console.WriteLine("\nTest 4: Boolean Operations");
            Test(runtime, "true", "True");
            Test(runtime, "false", "False");
            Test(runtime, "!true", "False");
            Test(runtime, "5 > 3", "True");
            Test(runtime, "5 < 3", "False");

            // Test 5: If expressions
            Console.WriteLine("\nTest 5: If Expressions");
            Test(runtime, "if (true) { 10 }", "10");
            Test(runtime, "if (false) { 10 } else { 20 }", "20");
            Test(runtime, "if (5 > 3) { 100 } else { 200 }", "100");

            // Test 6: Functions
            Console.WriteLine("\nTest 6: Functions");
            Test(runtime, "let add = function(a, b) { return a + b }", "anonymous");
            Test(runtime, "add(5, 3)", "8");
            Test(runtime, "let multiply = function(x, y) { return x * y }", "anonymous");
            Test(runtime, "multiply(4, 5)", "20");

            // Test 7: Console.log (should print to console)
            Console.WriteLine("\nTest 7: Console.log");
            runtime.ExecuteSimple("console.log(\"FenEngine is working!\")");
            runtime.ExecuteSimple("console.log(42)");
            runtime.ExecuteSimple("console.log(true)");

            // Test 8: Architecture - Dirty Flag Propagation
            Console.WriteLine("\nTest 8: Dirty Flag Propagation");
            var doc = new Document();
            var docEl = doc.CreateElement("HTML");
            var body = doc.CreateElement("BODY");
            var div = doc.CreateElement("DIV");
            
            doc.AppendChild(docEl);
            docEl.AppendChild(body);
            body.AppendChild(div);
            
            bool notified = false;
            doc.OnTreeDirty += () => notified = true;
            
            // Mark leaf dirty
            Console.WriteLine("Marking DIV dirty (Style)...");
            div.MarkDirty(InvalidationKind.Style);

            if (!div.StyleDirty) Console.WriteLine("✗ FAIL: Leaf StyleDirty not set");
            else if (!body.ChildStyleDirty) Console.WriteLine("✗ FAIL: Parent ChildStyleDirty not set");
            else if (!docEl.ChildStyleDirty) Console.WriteLine("✗ FAIL: Grandparent ChildStyleDirty not set");
            else if (!notified) Console.WriteLine("✗ FAIL: Document OnTreeDirty not fired");
            else Console.WriteLine("✓ PASS: Dirty propagation successful");

            // Test 9: Layout Engine
            Console.WriteLine("\nTest 9: Layout Engine");
            try
            {
                var layoutEngine = new FenBrowser.FenEngine.Layout.LayoutEngine();
                var layoutDoc = new Document();
                var layoutHtml = layoutDoc.CreateElement("HTML");
                var layoutBody = layoutDoc.CreateElement("BODY");
                var layoutDiv = layoutDoc.CreateElement("DIV");
                
                layoutDoc.AppendChild(layoutHtml);
                layoutHtml.AppendChild(layoutBody);
                layoutBody.AppendChild(layoutDiv);
                
                // Trigger layout
                var layoutResult = layoutEngine.ComputeLayout(layoutDoc, 0, 0, 800, false, 600);
                
                if (layoutResult != null) Console.WriteLine("✓ PASS: LayoutResult returned");
                else Console.WriteLine("✗ FAIL: LayoutResult is null");

                if (layoutEngine.AllBoxes.ContainsKey(layoutDiv))
                {
                     var box = layoutEngine.AllBoxes[layoutDiv];
                     Console.WriteLine($"✓ PASS: DIV has box of type {box.GetType().Name}");
                     Console.WriteLine($"  Box Geometry: {box.ContentBox}");
                }
                else
                {
                     Console.WriteLine("✗ FAIL: DIV missing box");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ ERROR: Layout Test Failed: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            // Test 10: Flex Layout (New Tab Simulation)
            Console.WriteLine("\nTest 10: Flex Layout (New Tab Simulation) [VERSION 2]");
            try
            {
                // New Scope (renamed vars to avoid collision with Test 8)
                {
                    var doc10 = new Document();
                    var html10 = doc10.CreateElement("HTML");
                    var body10 = doc10.CreateElement("BODY"); 
                    var child10 = doc10.CreateElement("DIV"); 
                    
                    doc10.AppendChild(html10);
                    html10.AppendChild(body10);
                    body10.AppendChild(child10);
                    
                    // Manually create Computed Styles since StyleSystem is not fully integrated in this test
                    var styles = new Dictionary<Node, FenBrowser.Core.Css.CssComputed>();
                    
                    var bodyStyle = new FenBrowser.Core.Css.CssComputed();
                    bodyStyle.Display = "flex";
                    bodyStyle.FlexDirection = "column";
                    bodyStyle.AlignItems = "center";
                    bodyStyle.JustifyContent = "center";
                    bodyStyle.Width = 800.0;
                    bodyStyle.Height = 600.0;
                    bodyStyle.Margin = new FenBrowser.Core.Thickness(0);
                    bodyStyle.Padding = new FenBrowser.Core.Thickness(0);
                    bodyStyle.BorderThickness = new FenBrowser.Core.Thickness(0);
                    styles[body10] = bodyStyle;
                    
                    var childStyle = new FenBrowser.Core.Css.CssComputed();
                    childStyle.Display = "block"; 
                    childStyle.Width = 100.0;
                    childStyle.Height = 100.0;
                    childStyle.Margin = new FenBrowser.Core.Thickness(0);
                    childStyle.Padding = new FenBrowser.Core.Thickness(0);
                    childStyle.BorderThickness = new FenBrowser.Core.Thickness(0);
                    styles[child10] = childStyle;

                    // Also need styles for html/doc?
                    var htmlStyle = new FenBrowser.Core.Css.CssComputed();
                    htmlStyle.Display = "block";
                    styles[html10] = htmlStyle;

                    // Use Constructor with explicit styles
                    var engine = new FenBrowser.FenEngine.Layout.LayoutEngine(styles, 800, 600);
                    
                    // Trigger layout
                    engine.ComputeLayout(doc10, 0, 0, 800, false, 600);
                    
                    if (engine.AllBoxes.ContainsKey(child10))
                    {
                        var box = engine.AllBoxes[child10];
                        Console.WriteLine($"  Child Box: {box.ContentBox}");
                        
                        // Expected: Left=350, Top=250.
                        bool centeredX = Math.Abs(box.ContentBox.Left - 350) < 2;
                        bool centeredY = Math.Abs(box.ContentBox.Top - 250) < 2;
                        
                        if (centeredX && centeredY) Console.WriteLine("✓ PASS: Flex Centering Correct");
                        else Console.WriteLine($"✗ FAIL: Not centered. Expected (350,250), Got ({box.ContentBox.Left},{box.ContentBox.Top})");
                    }
                    else Console.WriteLine("✗ FAIL: Child box not found");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ ERROR: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            // Test 11: Absolute Positioning
            Console.WriteLine("\nTest 11: Absolute Positioning");
            try
            {
                var doc11 = new Document();
                var html11 = doc11.CreateElement("HTML");
                var body11 = doc11.CreateElement("BODY"); 
                var container11 = doc11.CreateElement("DIV"); 
                var child11 = doc11.CreateElement("DIV"); 
                
                doc11.AppendChild(html11);
                html11.AppendChild(body11);
                body11.AppendChild(container11);
                container11.AppendChild(child11);
                
                var styles = new Dictionary<Node, FenBrowser.Core.Css.CssComputed>();
                
                var bodyStyle = new FenBrowser.Core.Css.CssComputed { Display = "block", Width = 800.0, Height = 600.0 };
                styles[body11] = bodyStyle;
                styles[html11] = bodyStyle; 

                var containerStyle = new FenBrowser.Core.Css.CssComputed();
                containerStyle.Display = "block";
                containerStyle.Position = "relative";
                containerStyle.Width = 200.0;
                containerStyle.Height = 200.0;
                containerStyle.Margin = new FenBrowser.Core.Thickness(50); // Margin to offset container
                styles[container11] = containerStyle;
                
                var childStyle = new FenBrowser.Core.Css.CssComputed();
                childStyle.Position = "absolute";
                childStyle.Left = 50.0;
                childStyle.Top = 50.0;
                childStyle.Width = 20.0;
                childStyle.Height = 20.0;
                styles[child11] = childStyle;

                var engine = new FenBrowser.FenEngine.Layout.LayoutEngine(styles, 800, 600);
                engine.ComputeLayout(doc11, 0, 0, 800, false, 600);
                
                if (engine.AllBoxes.ContainsKey(child11))
                {
                    var box = engine.AllBoxes[child11];
                    var containerBox = engine.AllBoxes[container11];

                    Console.WriteLine($"  Container Box: {containerBox.ContentBox}");
                    Console.WriteLine($"  Absolute Child Box: {box.ContentBox}");
                    
                    // Container should be at 50,50 (margin)
                    // Child should be at 50,50 RELATIVE TO CONTAINER (Local).
                    // Expected Child: Left=50, Top=50.
                    
                    float expectedLeft = 50f;
                    float expectedTop = 50f;

                    bool correct = Math.Abs(box.ContentBox.Left - expectedLeft) < 2 && Math.Abs(box.ContentBox.Top - expectedTop) < 2;
                    
                    if (correct) Console.WriteLine("✓ PASS: Absolute Positioning Correct");
                    else Console.WriteLine($"✗ FAIL: Expected ({expectedLeft},{expectedTop}), Got ({box.ContentBox.Left},{box.ContentBox.Top})");
                }
                else Console.WriteLine("✗ FAIL: Child box not found");

            }
            catch (Exception ex)
            {
                 Console.WriteLine($"✗ ERROR: {ex.Message}");
                 Console.WriteLine(ex.StackTrace);
            }

            // Test 12: HTML Parser
            Console.WriteLine("\nTest 12: HTML Parser (Tree Construction)");
            try 
            {
                 // Scope to avoid variable collision
                 {
                     string html12 = "<div id='parent'><b>Bold</b><p>Paragraph</p></div>";
                     var tokenizer12 = new FenBrowser.FenEngine.HTML.HtmlTokenizer(html12);
                     var builder12 = new FenBrowser.FenEngine.HTML.HtmlTreeBuilder(tokenizer12);
                     var doc12 = builder12.Build();
                     
                     var div12 = (Element)doc12.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "DIV");
                     if (div12 == null) 
                     {
                         Console.WriteLine("✗ FAIL: DIV not found");
                     }
                     else
                     {
                         Console.WriteLine($"✓ PASS: Found DIV with id='{div12.GetAttribute("id")}'");
                         
                         var b12 = div12.ChildNodes.FirstOrDefault(c => (c as Element)?.TagName == "B");
                         if (b12 != null) Console.WriteLine("✓ PASS: Found nested B tag");
                         else Console.WriteLine("✗ FAIL: Nested B tag missing");
    
                         var p12 = div12.ChildNodes.FirstOrDefault(c => (c as Element)?.TagName == "P");
                         if (p12 != null) Console.WriteLine("✓ PASS: Found nested P tag");
                         else Console.WriteLine("✗ FAIL: Nested P tag missing");
                     }
                 }
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"✗ ERROR: {ex.Message}");
            }

            // Test 13: Complex CSS Selectors
            Console.WriteLine("\nTest 13: Complex CSS Selectors");
            try 
            {
                // Test 13 Scope
                {
                    var doc_13 = new Document();
                    var root_13 = doc_13.CreateElement("HTML"); doc_13.AppendChild(root_13);
                    var body_13 = doc_13.CreateElement("BODY"); root_13.AppendChild(body_13);
                    var div_13 = doc_13.CreateElement("DIV"); body_13.AppendChild(div_13);
                    var span_13 = doc_13.CreateElement("SPAN"); div_13.AppendChild(span_13);
                    var p1_13 = doc_13.CreateElement("P"); div_13.AppendChild(p1_13);
                    var p2_13 = doc_13.CreateElement("P"); div_13.AppendChild(p2_13); // This is 2nd P
                    
                    // :nth-of-type(2)
                    bool matchType = FenBrowser.FenEngine.Rendering.CssLoader.MatchesSelector(p2_13, "p:nth-of-type(2)");
                    Console.WriteLine($":nth-of-type(2) - Expected: True, Got: {matchType} " + (matchType ? "√" : "✗"));
   
                    // Test :has(> .child)
                    var parent_13 = doc_13.CreateElement("DIV"); body_13.AppendChild(parent_13);
                    var child_13 = doc_13.CreateElement("DIV"); parent_13.AppendChild(child_13);
                    child_13.SetAttribute("class", "child");
                    
                    bool matchHasChild = FenBrowser.FenEngine.Rendering.CssLoader.MatchesSelector(parent_13, ":has(> .child)");
                    Console.WriteLine($":has(> .child) - Expected: True, Got: {matchHasChild} " + (matchHasChild ? "√" : "✗"));
   
                    // Test :has(+ sibling)
                    var prev_13 = doc_13.CreateElement("DIV"); body_13.AppendChild(prev_13);
                    var next_13 = doc_13.CreateElement("DIV"); body_13.AppendChild(next_13);
                    next_13.SetAttribute("class", "sibling");
                    
                    bool matchHasSibling = FenBrowser.FenEngine.Rendering.CssLoader.MatchesSelector(prev_13, ":has(+ .sibling)");
                    Console.WriteLine($":has(+ .sibling) - Expected: True, Got: {matchHasSibling} " + (matchHasSibling ? "√" : "✗"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ ERROR: {ex.Message}");
            }

            // Test 14: Shadow DOM Basic
            Console.WriteLine("\nTest 14: Shadow DOM Basic");
            try
            {
                // Test 14 Scope
                {
                    // Pass null host since this is a headless unit test
                    var engine_14 = new FenBrowser.FenEngine.Scripting.JavaScriptEngine(null);
                    var doc_14 = new Document();
                    
                    // Initialize Engine with DOM
                    // Initialize Engine with DOM
                    await engine_14.SetDomAsync(doc_14, new Uri("about:blank"));

                    // Create Host
                    var host_14 = doc_14.CreateElement("DIV");
                    host_14.SetAttribute("id", "host");
                    doc_14.AppendChild(host_14);
                    
                    // Add Light Child
                    var lightChild_14 = doc_14.CreateElement("SPAN");
                    lightChild_14.TextContent = "Light Content";
                    host_14.AppendChild(lightChild_14);
                    
                    // Run Script to Attach Shadow
                    string script_14 = @"
                        var host = document.getElementById('host');
                        var shadow = host.attachShadow({mode: 'open'});
                        var div = document.createElement('div');
                        div.textContent = 'Shadow Content';
                        shadow.appendChild(div);
                    ";
                    
                     // Sync DOM to engine again just in case (though SetDomAsync should have linked it)
                     // Actually SetDomAsync replaces the window scope document?
                     // Verify referencing host works.
                    engine_14.Evaluate(script_14);
                    
                    // Now Build Layout
                    var layoutEngine_14 = new FenBrowser.FenEngine.Layout.LayoutEngine();
                    var result_14 = layoutEngine_14.ComputeLayout(doc_14, 0, 0, 800, false, 600);
                    
                    // Inspect Boxes
                    if (host_14 == null) Console.WriteLine("✗ Host box not found");
                    else 
                    {
                        if (!layoutEngine_14.AllBoxes.ContainsKey(host_14))
                        {
                             Console.WriteLine("✗ Host box not found in AllBoxes");
                        }
                        else
                        {
                            // Find the shadow text node
                            if (host_14.ShadowRoot != null && host_14.ShadowRoot.ChildNodes.Length > 0)
                            {
                                var shadowDiv_14 = host_14.ShadowRoot.ChildNodes[0] as Element;
                                var textNode_14 = shadowDiv_14?.ChildNodes.FirstOrDefault() as Text;
                                
                                if (textNode_14 != null)
                                {
                                    if (layoutEngine_14.AllBoxes.ContainsKey(textNode_14))
                                        Console.WriteLine("√ PASS: Shadow Content Rendered (Box Found)");
                                    else
                                        Console.WriteLine("✗ FAIL: Shadow Content Missing (No Box)");
                                }
                                else Console.WriteLine("✗ FAIL: Shadow Tree Construction Failed (No Text Node)");
                                
                                // Check Light Content
                                if (layoutEngine_14.AllBoxes.ContainsKey(lightChild_14))
                                     Console.WriteLine("✗ FAIL: Light Content Rendered (Should be hidden)");
                                else
                                     Console.WriteLine("√ PASS: Light Content Hidden");
                            }
                            else
                            {
                                Console.WriteLine("✗ FAIL: Shadow Root not attached or empty");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ ERROR: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine("\n=== All Tests Complete ===");
            Console.ReadLine();
        }

        static void Test(FenRuntime runtime, string code, string expected)
        {
            try
            {
                var result = runtime.ExecuteSimple(code);
                var actual = result.ToString();
                var status = actual == expected ? "✓ PASS" : "✗ FAIL";
                Console.WriteLine($"{status}: {code}");
                if (actual != expected)
                {
                    Console.WriteLine($"  Expected: {expected}");
                    Console.WriteLine($"  Got: {actual}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ ERROR: {code}");
                Console.WriteLine($"  {ex.Message}");
            }
        }
    }
}
