using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Css
{
    /// <summary>
    /// A constructed stylesheet as the cascade sees it once a document or shadow root
    /// has adopted it (CSSOM 6.1.2, "adoptedStyleSheets").
    /// </summary>
    /// <remarks>
    /// The CSSOM object lives in script; this is an immutable snapshot of what it
    /// contributes - its rules as text, its media list and the base URL relative
    /// URLs resolve against. Script publishes a fresh list whenever an adopted sheet
    /// or the adopting array changes.
    /// </remarks>
    public sealed class AdoptedStyleSheet
    {
        public AdoptedStyleSheet(string cssText, string mediaText = null, Uri baseUri = null)
        {
            CssText = cssText ?? string.Empty;
            MediaText = mediaText ?? string.Empty;
            BaseUri = baseUri;
        }

        public string CssText { get; }

        /// <summary>The sheet's media list serialized; empty means all media.</summary>
        public string MediaText { get; }

        /// <summary>The sheet's base URL, or null for the adopting document's.</summary>
        public Uri BaseUri { get; }

        /// <summary>
        /// Copies a published list. Duplicates are kept: the array is an
        /// ObservableArray, and a sheet listed twice takes the later cascade position.
        /// </summary>
        internal static List<AdoptedStyleSheet> CopyList(IEnumerable<AdoptedStyleSheet> sheets)
        {
            var copy = new List<AdoptedStyleSheet>();
            if (sheets == null)
            {
                return copy;
            }

            foreach (var sheet in sheets)
            {
                if (sheet == null)
                {
                    throw new ArgumentNullException(nameof(sheets), "Adopted stylesheets cannot contain null entries.");
                }

                copy.Add(sheet);
            }

            return copy;
        }
    }
}
