using System;
using System.IO;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Verification
{
    internal enum ContentHealthDisposition
    {
        NotAvailable = 0,
        Healthy = 1,
        ToleratedLowRatio = 2,
        SuspiciousLowRatio = 3
    }

    /// <summary>
    /// Central component for verifying that the browser's rendered output matches the source content
    /// and that visual debugging artifacts (screenshots) are being generated.
    /// Values are "Triangulated" to ensure log completeness.
    /// </summary>
    public static class ContentVerifier
    {
        private static readonly object _stateLock = new();
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
        private static bool _hasAuthoritativeSource;
        private static bool _hasAuthoritativeRendered;

        public static void ResetForNavigation(string url = null)
        {
            lock (_stateLock)
            {
                _lastUrl = url;
                _sourceLengthBytes = 0;
                _sourceHash = 0;
                _renderedTextLength = 0;
                _domNodeCount = 0;
                _screenshotSaved = false;
                _screenshotPath = null;
                _cssTimedOut = false;
                _cssRuleCount = 0;
                _sourceDumpPath = null;
                _engineDumpPath = null;
                _renderedDumpPath = null;
                _lastZeroSizedCount = 0;
                _hasAuthoritativeSource = false;
                _hasAuthoritativeRendered = false;
            }
        }

        public static void RegisterSource(string url, long length, int hash, bool authoritative = false)
        {
            lock (_stateLock)
            {
                if (authoritative)
                {
                    _lastUrl = url;
                    _sourceLengthBytes = Math.Max(0, length);
                    _sourceHash = hash;
                    _hasAuthoritativeSource = true;
                }
                else
                {
                    if (_hasAuthoritativeSource)
                    {
                        return;
                    }

                    _lastUrl = url;
                    if (length > _sourceLengthBytes)
                    {
                        _sourceLengthBytes = Math.Max(0, length);
                        _sourceHash = hash;
                    }
                }
            }

            if (DebugConfig.LogVerification)
            {
                EngineLogCompat.Log($"[Source] URL: {url}, Size: {Math.Max(0, length)} bytes, Hash: {hash:X}", LogCategory.Verification, LogLevel.Info);
            }
        }

        public static void RegisterSourceFile(string path)
        {
            lock (_stateLock)
            {
                _sourceDumpPath = path;
            }
        }

        public static void RegisterEngineSourceFile(string path)
        {
            lock (_stateLock)
            {
                _engineDumpPath = path;
            }
        }

        public static void RegisterRenderedFile(string path)
        {
            lock (_stateLock)
            {
                _renderedDumpPath = path;
            }
        }

        /// <summary>
        /// Registers the state of the rendered DOM.
        /// </summary>
        public static void RegisterRendered(string url, int nodeCount, int textLength, bool authoritative = false)
        {
            lock (_stateLock)
            {
                if (authoritative)
                {
                    _lastUrl = url;
                    _domNodeCount = Math.Max(0, nodeCount);
                    _renderedTextLength = Math.Max(0, textLength);
                    _hasAuthoritativeRendered = true;
                }
                else
                {
                    if (_hasAuthoritativeRendered)
                    {
                        return;
                    }

                    _lastUrl = url;
                    _domNodeCount = Math.Max(_domNodeCount, nodeCount);
                    _renderedTextLength = Math.Max(_renderedTextLength, textLength);
                }
            }

            if (DebugConfig.LogVerification)
            {
                EngineLogCompat.Log($"[Rendered] URL: {url}, Nodes: {nodeCount}, Text Length: {textLength} chars", LogCategory.Verification, LogLevel.Info);
            }
        }

        public static void RegisterRenderedFromNode(FenBrowser.Core.Dom.V2.Node root, string url)
        {
            int nodes = 0;
            int textLen = 0;
            CalculateTreeMetrics(root, ref nodes, ref textLen);
            RegisterRendered(url, nodes, textLen);
        }

        private static void CalculateTreeMetrics(FenBrowser.Core.Dom.V2.Node node, ref int nodes, ref int textLen)
        {
            if (node == null) return;
            nodes++;
            if (node.NodeType == FenBrowser.Core.Dom.V2.NodeType.Text)
            {
                var text = node.TextContent;
                if (!string.IsNullOrEmpty(text))
                {
                    textLen += text.Length;
                }
            }
            
            if (node is FenBrowser.Core.Dom.V2.ContainerNode cn && cn.Children != null)
            {
                foreach (var child in cn.Children)
                {
                    CalculateTreeMetrics(child, ref nodes, ref textLen);
                }
            }
        }

        public static void RegisterScreenshot(string path)
        {
            if (DebugConfig.LogVerification)
            {
                bool screenshotSaved;
                lock (_stateLock)
                {
                    _screenshotPath = path;
                    _screenshotSaved = File.Exists(path);
                    screenshotSaved = _screenshotSaved;
                }
                EngineLogCompat.Log($"[Visual] Screenshot saved to: {path} (Exists: {screenshotSaved})", LogCategory.Verification, LogLevel.Info);
            }
        }

        public static void RegisterCssState(bool timedOut, int ruleCount)
        {
            lock (_stateLock)
            {
                _cssTimedOut = timedOut;
                _cssRuleCount = ruleCount;
            }
            
            if (DebugConfig.LogVerification && timedOut)
            {
                EngineLogCompat.Log("[CSS] WARNING: CSS loading timed out. Page may be visually broken.", LogCategory.Verification, LogLevel.Warn);
            }
        }

        /// <summary>
        /// Performs the final verification and logs the result.
        /// Should be called after rendering is complete.
        /// </summary>
        public static void PerformVerification()
        {
            if (!DebugConfig.LogVerification) return;

            long sourceLengthBytes;
            int renderedTextLength;
            int domNodeCount;
            bool screenshotSaved;
            string screenshotPath;
            bool cssTimedOut;
            int cssRuleCount;
            string sourceDumpPath;
            string engineDumpPath;
            string renderedDumpPath;
            int lastZeroSizedCount;

            lock (_stateLock)
            {
                sourceLengthBytes = _sourceLengthBytes;
                renderedTextLength = _renderedTextLength;
                domNodeCount = _domNodeCount;
                screenshotSaved = _screenshotSaved;
                screenshotPath = _screenshotPath;
                cssTimedOut = _cssTimedOut;
                cssRuleCount = _cssRuleCount;
                sourceDumpPath = _sourceDumpPath;
                engineDumpPath = _engineDumpPath;
                renderedDumpPath = _renderedDumpPath;
                lastZeroSizedCount = _lastZeroSizedCount;
            }

            EngineLogCompat.Log("--------------------------------------------------", LogCategory.Verification, LogLevel.Info);
            EngineLogCompat.Log("              CONTENT VERIFICATION REPORT         ", LogCategory.Verification, LogLevel.Info);
            EngineLogCompat.Log("--------------------------------------------------", LogCategory.Verification, LogLevel.Info);

            // 1. Network Source Check
            if (sourceLengthBytes > 0)
            {
                EngineLogCompat.Log($"[1] CURL/Fetch (Network): PASS ({sourceLengthBytes} bytes)", LogCategory.Verification, LogLevel.Info);
                if (!string.IsNullOrEmpty(sourceDumpPath))
                    EngineLogCompat.Log($"    - Raw Path:    {Path.GetFileName(sourceDumpPath)}", LogCategory.Verification, LogLevel.Info);
            }
            else
            {
                EngineLogCompat.Log("[1] CURL/Fetch (Network): FAIL (No data)", LogCategory.Verification, LogLevel.Warn);
            }

            // 2. Engine Source Check
            if (domNodeCount > 0)
            {
                EngineLogCompat.Log($"[2] Fen Engine (Source):  PASS ({domNodeCount} DOM nodes)", LogCategory.Verification, LogLevel.Info);
                if (!string.IsNullOrEmpty(engineDumpPath))
                    EngineLogCompat.Log($"    - Engine Path: {Path.GetFileName(engineDumpPath)}", LogCategory.Verification, LogLevel.Info);
            }
            else
            {
                 EngineLogCompat.Log("[2] Fen Engine (Source):  FAIL (Empty DOM)", LogCategory.Verification, LogLevel.Warn);
            }

            // 3. Rendered Result Check
            if (renderedTextLength == 0 && !string.IsNullOrEmpty(renderedDumpPath) && File.Exists(renderedDumpPath))
            {
                try
                {
                    renderedTextLength = File.ReadAllText(renderedDumpPath).Length;
                }
                catch
                {
                    // Keep non-fatal; verification should never break render flow.
                }
            }

            double ratio = 0;
            if (sourceLengthBytes > 0)
            {
                ratio = (double)renderedTextLength / sourceLengthBytes * 100.0;
            }

            if (renderedTextLength > 0)
            {
                EngineLogCompat.Log($"[3] Visual Text Result:   PASS ({renderedTextLength} characters)", LogCategory.Verification, LogLevel.Info);
                if (!string.IsNullOrEmpty(renderedDumpPath))
                    EngineLogCompat.Log($"    - Text Path:   {Path.GetFileName(renderedDumpPath)}", LogCategory.Verification, LogLevel.Info);
                
                if (sourceLengthBytes > 0)
                {
                    double charsPerNode = domNodeCount > 0
                        ? (double)renderedTextLength / domNodeCount
                        : 0;
                    var healthDisposition = AssessContentHealth(sourceLengthBytes, renderedTextLength, domNodeCount, screenshotSaved, cssRuleCount);

                    EngineLogCompat.Log($"    - Content Health: {ratio:F2}% (Source -> Result)", LogCategory.Verification, LogLevel.Info);
                    if (domNodeCount > 0)
                    {
                        EngineLogCompat.Log($"    - Text Density: {charsPerNode:F2} chars/node", LogCategory.Verification, LogLevel.Info);
                    }

                    if (healthDisposition == ContentHealthDisposition.ToleratedLowRatio)
                    {
                        EngineLogCompat.Log("    - Note: low text-to-source ratio tolerated for a script-heavy page because DOM, CSS, and screenshot evidence are present.", LogCategory.Verification, LogLevel.Info);
                    }
                    else if (healthDisposition == ContentHealthDisposition.SuspiciousLowRatio)
                    {
                        EngineLogCompat.Log("    - WARNING: Low text-to-source ratio without enough corroborating render evidence. Investigate parsing, visibility, or layout.", LogCategory.Verification, LogLevel.Warn);
                    }
                }
                else
                {
                    EngineLogCompat.Log("    - Content Health: N/A (No network source captured)", LogCategory.Verification, LogLevel.Info);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(renderedDumpPath))
                {
                    EngineLogCompat.Log("[3] Visual Text Result:   PENDING (rendered text snapshot not produced yet)", LogCategory.Verification, LogLevel.Info);
                }
                else
                {
                    EngineLogCompat.Log("[3] Visual Text Result:   FAIL (No text content)", LogCategory.Verification, LogLevel.Warn);
                }
            }

            // 4. Visual Check
            if (screenshotSaved)
            {
                EngineLogCompat.Log($"[4] Visual Artifact:     PASS ({Path.GetFileName(screenshotPath)})", LogCategory.Verification, LogLevel.Info);
            }
            else
            {
                EngineLogCompat.Log("[4] Visual Artifact:     FAIL (No screenshot)", LogCategory.Verification, LogLevel.Warn);
            }

            // 4. Quality Check (CSS/Performance/Layout)
            if (cssTimedOut)
            {
                EngineLogCompat.Log("[4] Rendering Quality: FAIL (CSS Loading Timeout - 10s limit)", LogCategory.Verification, LogLevel.Warn);
            }
            else 
            {
                // Check for high number of zero-sized elements (indicates layout regressions)
                int zeroCount = lastZeroSizedCount;
                if (domNodeCount > 10 && (zeroCount > domNodeCount * 0.2 || zeroCount > 50))
                {
                    EngineLogCompat.Log($"[4] Rendering Quality: WARN ({zeroCount} zero-sized elements detected - possible FLEX-ZERO issue)", LogCategory.Verification, LogLevel.Warn);
                }
                else if (domNodeCount > 10 && cssRuleCount < 5)
                {
                    EngineLogCompat.Log($"[4] Rendering Quality: WARN (Few rules matched: {cssRuleCount} rules for {domNodeCount} nodes)", LogCategory.Verification, LogLevel.Warn);
                }
                else if (cssRuleCount > 0)
                {
                    EngineLogCompat.Log($"[4] Rendering Quality: PASS ({cssRuleCount} rules matched)", LogCategory.Verification, LogLevel.Info);
                }
                else
                {
                    EngineLogCompat.Log("[4] Rendering Quality: FAIL (No CSS rules matched)", LogCategory.Verification, LogLevel.Warn);
                }
            }

            EngineLogCompat.Log("--------------------------------------------------", LogCategory.Verification, LogLevel.Info);
        }

        private static int _lastZeroSizedCount = 0;
        public static void RegisterZeroSizedCount(int count)
        {
            lock (_stateLock)
            {
                _lastZeroSizedCount = count;
            }
        }

        internal static ContentHealthDisposition AssessContentHealth(long sourceLengthBytes, int renderedTextLength, int domNodeCount, bool screenshotSaved, int cssRuleCount)
        {
            if (sourceLengthBytes <= 0 || renderedTextLength <= 0)
            {
                return ContentHealthDisposition.NotAvailable;
            }

            var ratio = (double)renderedTextLength / sourceLengthBytes * 100.0;
            if (ratio >= 1.0)
            {
                return ContentHealthDisposition.Healthy;
            }

            var charsPerNode = domNodeCount > 0
                ? (double)renderedTextLength / domNodeCount
                : 0;
            var largeSourcePayload = sourceLengthBytes >= 262_144;
            var populatedDom = domNodeCount >= 100;
            var meaningfulRenderedText = renderedTextLength >= 128;
            var corroboratedVisualSignals = screenshotSaved && cssRuleCount > 0;

            if (meaningfulRenderedText &&
                (largeSourcePayload || populatedDom) &&
                (corroboratedVisualSignals || charsPerNode >= 0.25))
            {
                return ContentHealthDisposition.ToleratedLowRatio;
            }

            return ContentHealthDisposition.SuspiciousLowRatio;
        }
    }
}
