using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Testing;

namespace FenBrowser.FenEngine
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine($"Args: {string.Join(", ", args)}");

            if (args.Length >= 2 && args[0] == "verify")
            {
                await VerificationRunner.GenerateSnapshot(args[1], "verification_output.png");
            }
            else if (args.Length >= 2 && args[0] == "test262")
            {
                string testFile = args[1];
                string rootPath = args.Length > 2 ? args[2] : @"C:\Users\udayk\test262";
                
                if (!File.Exists(testFile))
                {
                    Console.WriteLine($"Error: Test file not found: {testFile}");
                    return;
                }
                
                Console.WriteLine($"Running Test262: {Path.GetFileName(testFile)}");
                var runner = new Test262Runner(rootPath);
                var result = await runner.RunSingleTestAsync(testFile);
                
                Console.WriteLine($"Result: {(result.Passed ? "PASS" : "FAIL")}");
                if (!result.Passed)
                {
                    Console.WriteLine($"  Expected: {result.Expected}");
                    Console.WriteLine($"  Actual:   {result.Actual}");
                    if (!string.IsNullOrEmpty(result.Error))
                        Console.WriteLine($"  Error:    {result.Error}");
                }
                Console.WriteLine($"Duration: {result.Duration.TotalMilliseconds}ms");
            }
            else if (args.Length >= 1 && args[0] == "test")
            {
                FenBrowser.FenEngine.Tests.LogicTestRunner.MainTest(args);
            }
            else if (args.Length >= 1 && args[0] == "debug")
            {
                 // HTML Parsing
                 var html = "<html><body><div id='test' class='container foo'><span class='child'>Text</span></div></body></html>";
                 Console.WriteLine("Parsing HTML...");
                 var builder = new FenBrowser.Core.Parsing.HtmlTreeBuilder(html);
                 var doc = builder.Build();
                 Console.WriteLine($"HTML Parsed: {doc.Children.Count} children");

                 // Find elements
                 // Use simple iteration to verify structure
                 FenBrowser.Core.Dom.Element div = null;
                 foreach(var n in doc.Descendants()) 
                 {
                     if (n.Tag.Equals("DIV", StringComparison.OrdinalIgnoreCase)) 
                     {
                         div = n as FenBrowser.Core.Dom.Element; 
                         break; 
                     }
                 }

                 if (div == null) { Console.WriteLine("DIV not found!"); return; }
                 Console.WriteLine($"Found DIV. Tag: {div.Tag}");
                 if (div.Attr != null && div.Attr.ContainsKey("class"))
                    Console.WriteLine($"Class: {div.Attr["class"]}");
                 else
                    Console.WriteLine("Class attribute missing!");

                 // Ancestor Check
                 // Console.WriteLine($"AncestorFilter: {div.AncestorFilter}");

                 var selector = ".container.foo";
                 Console.WriteLine($"Testing selector: {selector}");
                 var matches = FenBrowser.FenEngine.Rendering.Css.SelectorMatcher.Matches(div, selector);
                 Console.WriteLine($"Match Result: {matches}");
                 
                 // CSS Parser Test
                 Console.WriteLine("Testing CSS Parser...");
                 string css = "@media screen { div { color: red; } } body { background: blue; }";
                 var cssTokenizer = new FenBrowser.FenEngine.Rendering.Css.CssTokenizer(css);
                 var cssParser = new FenBrowser.FenEngine.Rendering.Css.CssSyntaxParser(cssTokenizer);
                 var sheet = cssParser.ParseStylesheet();
                 Console.WriteLine($"Parsed Rules: {sheet.Rules.Count}");
                 foreach(var r in sheet.Rules) Console.WriteLine($"Rule Type: {r.GetType().Name}");

                 // Large HTML Style Test
                 Console.WriteLine("Testing Large HTML Style...");
                 var sb = new System.Text.StringBuilder();
                 sb.Append("<html><head><style>");
                 for(int i=0; i<5000; i++) sb.Append(".class" + i + " { color: red; } ");
                 sb.Append("</style></head><body></body></html>");
                 var largeHtml = sb.ToString();
                 Console.WriteLine($"Generated HTML size: {largeHtml.Length}");
                 
                 var largeBuilder = new FenBrowser.Core.Parsing.HtmlTreeBuilder(largeHtml);
                 var largeDoc = largeBuilder.Build();
                 var head = largeDoc.Children.FirstOrDefault(c => (c as FenBrowser.Core.Dom.Element)?.Tag == "HTML")
                            ?.Children.FirstOrDefault(c => (c as FenBrowser.Core.Dom.Element)?.Tag == "HEAD");
                 var style = head?.Children.FirstOrDefault(c => (c as FenBrowser.Core.Dom.Element)?.Tag == "STYLE");
                 
                 if (style != null && style is FenBrowser.Core.Dom.Element styleEl)
                 {
                     Console.WriteLine($"Found Style Tag. Text Length: {styleEl.Text.Length}");
                     if (styleEl.Text.Length < largeHtml.Length - 100)
                         Console.WriteLine("WARNING: Style text truncated!");
                     else
                         Console.WriteLine("Style text length matches expectation.");
                 }
                 else
                 {
                     Console.WriteLine("Style tag NOT FOUND in large document.");
                 }

                 await Task.Delay(1);
            }
            else
            {
                Console.WriteLine("Usage: FenBrowser.FenEngine.exe verify <html_path>");
                Console.WriteLine("       FenBrowser.FenEngine.exe test262 <test_file_path> [test262_root_path]");
                Console.WriteLine("       FenBrowser.FenEngine.exe test");
            }
        }
    }
}
