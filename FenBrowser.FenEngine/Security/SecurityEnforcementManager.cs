using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Security
{
    /// <summary>
    /// Centralized security enforcement for rendering operations.
    /// Production-grade security boundary enforcement at pipeline entry points.
    /// </summary>
    public sealed class SecurityEnforcementManager
    {
        private readonly IRenderingResourceLimits _defaultLimits;
        private readonly IRenderingResourceLimits _restrictedLimits;
        
        // Track active context counts per document for limits
        private readonly ConcurrentDictionary<string, DocumentContext> _documentContexts;
        
        // Watchdog for render time tracking
        private readonly ConcurrentDictionary<string, Stopwatch> _renderWatchdogs;
        
        public SecurityEnforcementManager()
        {
            _defaultLimits = new DefaultRenderingResourceLimits();
            _restrictedLimits = new RestrictedRenderingResourceLimits();
            _documentContexts = new ConcurrentDictionary<string, DocumentContext>();
            _renderWatchdogs = new ConcurrentDictionary<string, Stopwatch>();
        }
        
        /// <summary>
        /// Enforce SVG recursion limits
        /// </summary>
        public bool CheckSvgRecursion(string documentId, int currentDepth)
        {
            var limits = GetLimitsForDocument(documentId);
            if (!limits.CheckSvgRecursion(currentDepth))
            {
                FenBrowser.Core.EngineLogCompat.Error($"[Security] SVG recursion depth exceeded: {currentDepth} > {limits.MaxSvgRecursionDepth} in document {documentId}", LogCategory.Security);
                return false;
            }
            return true;
        }
        
        /// <summary>
        /// Enforce SVG element count limits
        /// </summary>
        public bool CheckSvgElementCount(string documentId, int elementCount)
        {
            var limits = GetLimitsForDocument(documentId);
            if (!limits.CheckSvgElementCount(elementCount))
            {
                FenBrowser.Core.EngineLogCompat.Error($"[Security] SVG element count exceeded: {elementCount} > {limits.MaxSvgElementCount} in document {documentId}", LogCategory.Security);
                return false;
            }
            return true;
        }
        
        /// <summary>
        /// Enforce CSS filter complexity limits
        /// </summary>
        public bool CheckFilterComplexity(string documentId, int elementCount, int chainLength)
        {
            var limits = GetLimitsForDocument(documentId);
            if (!limits.CheckFilterComplexity(elementCount, chainLength))
            {
                FenBrowser.Core.EngineLogCompat.Error($"[Security] CSS filter complexity exceeded: elements={elementCount}, chain={chainLength} > {limits.MaxFilterElementCount}/{limits.MaxFilterChainLength} in document {documentId}", LogCategory.Security);
                return false;
            }
            return true;
        }
        
        /// <summary>
        /// Enforce canvas creation limits
        /// </summary>
        public bool CheckCanvasCreation(string documentId, int width, int height)
        {
            var limits = GetLimitsForDocument(documentId);
            var context = _documentContexts.GetOrAdd(documentId, _ => new DocumentContext());
            if (!context.TryReserveCanvas(limits, width, height, out var currentCount))
            {
                FenBrowser.Core.EngineLogCompat.Error($"[Security] Canvas creation rejected: {width}x{height}, count={currentCount} in document {documentId}", LogCategory.Security);
                return false;
            }

            return true;
        }
        
        /// <summary>
        /// Enforce iframe nesting depth
        /// </summary>
        public bool CheckNestedIframe(string documentId, int depth)
        {
            var limits = GetLimitsForDocument(documentId);
            if (!limits.CheckNestedIframes(depth))
            {
                FenBrowser.Core.EngineLogCompat.Error($"[Security] Nested iframe depth exceeded: {depth} > {limits.MaxNestedIframes} in document {documentId}", LogCategory.Security);
                return false;
            }
            return true;
        }
        
        /// <summary>
        /// Enforce table nesting and cell count limits
        /// </summary>
        public bool CheckNestedTable(string documentId, int depth, int totalCellCount)
        {
            var limits = GetLimitsForDocument(documentId);
            if (!limits.CheckNestedTables(depth, totalCellCount))
            {
                FenBrowser.Core.EngineLogCompat.Error($"[Security] Table nesting/cells exceeded: depth={depth}, cells={totalCellCount} in document {documentId}", LogCategory.Security);
                return false;
            }
            return true;
        }
        
        /// <summary>
        /// Start render watchdog for a frame
        /// </summary>
        public void StartRenderWatchdog(string documentId)
        {
            var stopwatch = new Stopwatch();
            _renderWatchdogs.AddOrUpdate(documentId, stopwatch, (_, _) => stopwatch);
            stopwatch.Restart();
        }
        
        /// <summary>
        /// Check render frame time and abort if exceeding limit
        /// </summary>
        public bool CheckRenderTime(string documentId)
        {
            if (!_renderWatchdogs.TryGetValue(documentId, out var stopwatch))
                return true; // No watchdog started
                
            var elapsed = stopwatch.Elapsed;
            var limits = GetLimitsForDocument(documentId);
            
            if (!limits.CheckFrameRenderTime(elapsed))
            {
                FenBrowser.Core.EngineLogCompat.Error($"[Security] Render frame time exceeded: {elapsed.TotalMilliseconds}ms > {limits.MaxFrameRenderTime.TotalMilliseconds}ms in document {documentId}", LogCategory.Security);
                stopwatch.Stop();
                _renderWatchdogs.TryRemove(documentId, out _);
                return false;
            }
            return true;
        }
        
        /// <summary>
        /// Stop and cleanup render watchdog
        /// </summary>
        public void StopRenderWatchdog(string documentId)
        {
            if (_renderWatchdogs.TryRemove(documentId, out var stopwatch))
            {
                stopwatch.Stop();
            }
        }
        
        /// <summary>
        /// Cleanup document context when document is destroyed
        /// </summary>
        public void CleanupDocument(string documentId)
        {
            _documentContexts.TryRemove(documentId, out _);
            if (_renderWatchdogs.TryRemove(documentId, out var stopwatch))
            {
                stopwatch.Stop();
            }
        }
        
        /// <summary>
        /// Determine which limits to apply based on document origin
        /// </summary>
        private IRenderingResourceLimits GetLimitsForDocument(string documentId)
        {
            // TODO: Integrate with PermissionManager to check origin trust level
            // For now, use default limits. Future: Restricted for ads/untrusted origins
            return _defaultLimits;
        }
        
        private class DocumentContext
        {
            private readonly object _canvasLock = new object();
            private int _canvasCount;

            public bool TryReserveCanvas(IRenderingResourceLimits limits, int width, int height, out int currentCount)
            {
                lock (_canvasLock)
                {
                    currentCount = _canvasCount;
                    if (!limits.CheckCanvasDimensions(width, height, _canvasCount))
                    {
                        return false;
                    }

                    _canvasCount++;
                    return true;
                }
            }

            public int CanvasCount => Volatile.Read(ref _canvasCount);
            public int SvgElementCount { get; set; }
            public int NestedIframeDepth { get; set; }
        }
    }
}
