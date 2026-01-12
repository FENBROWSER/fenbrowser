using System;
using FenBrowser.Core.Dom;

namespace FenBrowser.Core.Logging
{
    public static class DebugHelper
    {
        public static void DumpElementTree(Element root)
        {
             Console.WriteLine("=== MANUAL ELEMENT DUMP ===");
             DumpNode(root, 0);
             Console.WriteLine("===========================");
        }

        private static void DumpNode(Element node, int depth)
        {
            var indent = new string(' ', depth * 2);
            var cls = node.GetAttribute("class") ?? "";
            
            if (DebugConfig.ShouldLog(cls) || node.Tag == "body")
                 Console.WriteLine($"[DOM-MANUAL] {indent}{node.Tag}.{cls}");
            
            foreach (var child in node.Children)
                if (child is Element e) DumpNode(e, depth + 1);
        }
    }
}
