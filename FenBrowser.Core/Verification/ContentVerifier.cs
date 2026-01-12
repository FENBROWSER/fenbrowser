using System;
using System.IO;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Verification
{
    /// <summary>
    /// Central component for verifying that the browser's rendered output matches the source content
    /// and that visual debugging artifacts (screenshots) are being generated.
    /// Values are "Triangulated" to ensure log completeness.
    /// </summary>
    public static class ContentVerifier
    {
        private static string _lastUrl;
        private static long _sourceLengthBytes;
        private static int _sourceHash;
        private static int _renderedTextLength;
        private static int _domNodeCount;
        private static bool _screenshotSaved;
        private static string _screenshotPath;
        private static bool _cssTimedOut;
        private static int _cssRuleCount;
        private static string _sourceDumpPath;
        private static string _engineDumpPath;
        private static string _renderedDumpPath;

        public static void RegisterSource(string url, long length, int hash)
        {
            _lastUrl = url;
            _sourceLengthBytes = length;
            _sourceHash = hash;

            if (DebugConfig.LogVerification)
            {
                FenLogger.Log($"[Source] URL: {url}, Size: {length} bytes, Hash: {hash:X}", LogCategory.Verification, LogLevel.Info);
            }
        }

        public static void RegisterSourceFile(string path)
        {
            _sourceDumpPath = path;
        }

        public static void RegisterEngineSourceFile(string path)
        {
            _engineDumpPath = path;
        }

        public static void RegisterRenderedFile(string path)
        {
            _renderedDumpPath = path;
        }

        /// <summary>
        /// Registers the state of the rendered DOM.
        /// </summary>
        public static void RegisterRendered(string url, int nodeCount, int textLength)
        {
            _domNodeCount = nodeCount;
            _renderedTextLength = textLength;

            if (DebugConfig.LogVerification)
            {
                FenLogger.Log($"[Rendered] URL: {url}, Nodes: {nodeCount}, Text Length: {textLength} chars", LogCategory.Verification, LogLevel.Info);
            }
        }

        public static void RegisterRenderedFromNode(FenBrowser.Core.Dom.Node root, string url)
        {
            int nodes = 0;
            int textLen = 0;
            CalculateTreeMetrics(root, ref nodes, ref textLen);
            RegisterRendered(url, nodes, textLen);
        }

        private static void CalculateTreeMetrics(FenBrowser.Core.Dom.Node node, ref int nodes, ref int textLen)
        {
            if (node == null) return;
            nodes++;
            if (node is FenBrowser.Core.Dom.Text t && t.Data != null)
            {
                textLen += t.Data.Length;
            }
            
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    CalculateTreeMetrics(child, ref nodes, ref textLen);
                }
            }
        }

        public static void RegisterScreenshot(string path)
        {
            if (DebugConfig.LogVerification)
            {
                _screenshotPath = path;
                _screenshotSaved = File.Exists(path);
                FenLogger.Log($"[Visual] Screenshot saved to: {path} (Exists: {_screenshotSaved})", LogCategory.Verification, LogLevel.Info);
            }
        }

        public static void RegisterCssState(bool timedOut, int ruleCount)
        {
            _cssTimedOut = timedOut;
            _cssRuleCount = ruleCount;
            
            if (DebugConfig.LogVerification && timedOut)
            {
                FenLogger.Log("[CSS] WARNING: CSS loading timed out. Page may be visually broken.", LogCategory.Verification, LogLevel.Warn);
            }
        }

        /// <summary>
        /// Performs the final verification and logs the result.
        /// Should be called after rendering is complete.
        /// </summary>
        public static void PerformVerification()
        {
            if (!DebugConfig.LogVerification) return;

            FenLogger.Log("--------------------------------------------------", LogCategory.Verification, LogLevel.Info);
            FenLogger.Log("              CONTENT VERIFICATION REPORT         ", LogCategory.Verification, LogLevel.Info);
            FenLogger.Log("--------------------------------------------------", LogCategory.Verification, LogLevel.Info);

            // 1. Network Source Check
            if (_sourceLengthBytes > 0)
            {
                FenLogger.Log($"[1] CURL/Fetch (Network): PASS ({_sourceLengthBytes} bytes)", LogCategory.Verification, LogLevel.Info);
                if (!string.IsNullOrEmpty(_sourceDumpPath))
                    FenLogger.Log($"    - Raw Path:    {Path.GetFileName(_sourceDumpPath)}", LogCategory.Verification, LogLevel.Info);
            }
            else
            {
                FenLogger.Log("[1] CURL/Fetch (Network): FAIL (No data)", LogCategory.Verification, LogLevel.Warn);
            }

            // 2. Engine Source Check
            if (_domNodeCount > 0)
            {
                FenLogger.Log($"[2] Fen Engine (Source):  PASS ({_domNodeCount} DOM nodes)", LogCategory.Verification, LogLevel.Info);
                if (!string.IsNullOrEmpty(_engineDumpPath))
                    FenLogger.Log($"    - Engine Path: {Path.GetFileName(_engineDumpPath)}", LogCategory.Verification, LogLevel.Info);
            }
            else
            {
                 FenLogger.Log("[2] Fen Engine (Source):  FAIL (Empty DOM)", LogCategory.Verification, LogLevel.Warn);
            }

            // 3. Rendered Result Check
            double ratio = 0;
            if (_sourceLengthBytes > 0)
            {
                ratio = (double)_renderedTextLength / _sourceLengthBytes * 100.0;
            }

            if (_renderedTextLength > 0)
            {
                FenLogger.Log($"[3] Visual Text Result:   PASS ({_renderedTextLength} characters)", LogCategory.Verification, LogLevel.Info);
                if (!string.IsNullOrEmpty(_renderedDumpPath))
                    FenLogger.Log($"    - Text Path:   {Path.GetFileName(_renderedDumpPath)}", LogCategory.Verification, LogLevel.Info);
                
                FenLogger.Log($"    - Content Health: {ratio:F2}% (Source -> Result)", LogCategory.Verification, LogLevel.Info);
                
                if (ratio < 1.0)
                {
                     FenLogger.Log("    - WARNING: Result text is less than 1% of source. Possible parsing failure.", LogCategory.Verification, LogLevel.Warn);
                }
            }
            else
            {
                FenLogger.Log("[3] Visual Text Result:   FAIL (No text content)", LogCategory.Verification, LogLevel.Warn);
            }

            // 4. Visual Check
            if (_screenshotSaved)
            {
                FenLogger.Log($"[4] Visual Artifact:     PASS ({Path.GetFileName(_screenshotPath)})", LogCategory.Verification, LogLevel.Info);
            }
            else
            {
                FenLogger.Log("[4] Visual Artifact:     FAIL (No screenshot)", LogCategory.Verification, LogLevel.Warn);
            }

            // 4. Quality Check (CSS/Performance/Layout)
            if (_cssTimedOut)
            {
                FenLogger.Log("[4] Rendering Quality: FAIL (CSS Loading Timeout - 10s limit)", LogCategory.Verification, LogLevel.Warn);
            }
            else 
            {
                // Check for high number of zero-sized elements (indicates layout regressions)
                int zeroCount = _lastZeroSizedCount;
                if (_domNodeCount > 10 && (zeroCount > _domNodeCount * 0.2 || zeroCount > 50))
                {
                    FenLogger.Log($"[4] Rendering Quality: WARN ({zeroCount} zero-sized elements detected - possible FLEX-ZERO issue)", LogCategory.Verification, LogLevel.Warn);
                }
                else if (_domNodeCount > 10 && _cssRuleCount < 5)
                {
                    FenLogger.Log($"[4] Rendering Quality: WARN (Few rules matched: {_cssRuleCount} rules for {_domNodeCount} nodes)", LogCategory.Verification, LogLevel.Warn);
                }
                else if (_cssRuleCount > 0)
                {
                    FenLogger.Log($"[4] Rendering Quality: PASS ({_cssRuleCount} rules matched)", LogCategory.Verification, LogLevel.Info);
                }
                else
                {
                    FenLogger.Log("[4] Rendering Quality: FAIL (No CSS rules matched)", LogCategory.Verification, LogLevel.Warn);
                }
            }

            FenLogger.Log("--------------------------------------------------", LogCategory.Verification, LogLevel.Info);
        }

        private static int _lastZeroSizedCount = 0;
        public static void RegisterZeroSizedCount(int count)
        {
            _lastZeroSizedCount = count;
        }
    }
}
