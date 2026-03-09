using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Compatibility facade over <see cref="BidiAlgorithm"/>.
    /// All UAX#9 logic lives in BidiAlgorithm (BidiTextRenderer.cs); this class
    /// exists only so call-sites that pre-date the consolidation keep compiling.
    /// </summary>
    public static class BidiResolver
    {
        /// <summary>A run of text with consistent bidi direction.</summary>
        public class BidiRun
        {
            public string Text  { get; set; }
            public int    Start  { get; set; }
            public int    Length { get; set; }
            public int    Level  { get; set; }
            public bool   IsRtl => (Level & 1) == 1;
        }

        /// <summary>
        /// Resolve bidirectional text into visually-ordered runs.
        /// Delegates to <see cref="BidiAlgorithm.GetVisualRuns"/>.
        /// </summary>
        public static List<BidiRun> Resolve(string text, string baseDirection = "ltr")
        {
            bool rtl = baseDirection?.ToLowerInvariant() == "rtl";
            var result = new List<BidiRun>();
            foreach (var run in BidiAlgorithm.GetVisualRuns(text, rtl))
            {
                result.Add(new BidiRun
                {
                    Text   = run.Text,
                    Start  = run.StartIndex,
                    Length = run.Length,
                    Level  = run.Level
                });
            }
            return result;
        }

        /// <summary>
        /// Detect the dominant text direction ("ltr" or "rtl").
        /// Delegates to <see cref="BidiAlgorithm.DetectBaseRtl"/>.
        /// </summary>
        public static string DetectDirection(string text)
            => BidiAlgorithm.DetectBaseRtl(text) ? "rtl" : "ltr";
    }
}
