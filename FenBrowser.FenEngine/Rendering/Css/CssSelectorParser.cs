using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering
{
    public static partial class CssLoader
    {
        // Internal types removed to enforce usage of FenBrowser.FenEngine.Rendering.Css types
        // Parsing logic delegated to SelectorMatcher

        /// <summary>
        /// Extract the argument from a pseudo-class like ":nth-child(2n+1)" -> "2n+1"
        /// </summary>
        private static string ExtractPseudoArg(string pseudoClass)
        {
            if (string.IsNullOrEmpty(pseudoClass)) return "";

            int start = pseudoClass.IndexOf('(');
            int end = pseudoClass.LastIndexOf(')');

            if (start >= 0 && end > start)
                return pseudoClass.Substring(start + 1, end - start - 1).Trim();

            return "";
        }
    }
}
