using System;
using System.IO;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Core.Logging
{
    public static class DebugHelper
    {
        public static void DumpElementTree(Element root)
        {
             ArgumentNullException.ThrowIfNull(root);
             DumpElementTree(Console.Out, root);
        }

        public static void DumpElementTree(TextWriter writer, Element root)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(root);

            writer.WriteLine("=== MANUAL ELEMENT DUMP ===");
            DumpNode(writer, root, 0);
            writer.WriteLine("===========================");
        }

        public static string CaptureElementTree(Element root)
        {
            using var writer = new StringWriter();
            DumpElementTree(writer, root);
            return writer.ToString();
        }

        private static void DumpNode(TextWriter writer, Element node, int depth)
        {
            var indent = new string(' ', depth * 2);
            var cls = node.GetAttribute("class") ?? "";
            
            if (DebugConfig.ShouldLog(cls) || node.LocalName == "body")
                 writer.WriteLine($"[DOM-MANUAL] {indent}{node.TagName}.{cls}");
            
            foreach (var child in node.Children)
                if (child is Element e) DumpNode(writer, e, depth + 1);
        }
    }
}
