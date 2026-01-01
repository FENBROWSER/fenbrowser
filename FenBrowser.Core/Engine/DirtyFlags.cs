// =============================================================================
// DirtyFlags.cs
// FenBrowser Rendering Pipeline Dirty Flags
// 
// SPEC REFERENCE: Custom (no external spec - internal architecture)
// PURPOSE: Forward-only dirty flag propagation for efficient incremental updates
// 
// INVARIANT: Setting a flag dirty automatically marks all downstream flags dirty.
// INVARIANT: Clearing a flag does NOT clear downstream flags.
// INVARIANT: Flags can only be cleared by the stage that owns them during processing.
// =============================================================================

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Manages dirty flags for the rendering pipeline.
    /// Implements forward-only propagation: if Style is dirty, Layout/Paint are automatically dirty.
    /// Thread-safe for multi-threaded rendering scenarios.
    /// </summary>
    public sealed class DirtyFlags
    {
        // Bit positions for each dirty flag
        private const int STYLE_BIT = 0;
        private const int LAYOUT_BIT = 1;
        private const int PAINT_BIT = 2;
        private const int RASTER_BIT = 3;

        // Combined bit for each stage (includes all downstream stages)
        private const int STYLE_MASK = (1 << STYLE_BIT) | (1 << LAYOUT_BIT) | (1 << PAINT_BIT) | (1 << RASTER_BIT);
        private const int LAYOUT_MASK = (1 << LAYOUT_BIT) | (1 << PAINT_BIT) | (1 << RASTER_BIT);
        private const int PAINT_MASK = (1 << PAINT_BIT) | (1 << RASTER_BIT);
        private const int RASTER_MASK = (1 << RASTER_BIT);

        // Thread-safe flag storage
        private int _flags = 0;

        // Generation counter for change tracking
        private long _generation = 0;

        /// <summary>
        /// Current generation number. Increments on any flag change.
        /// </summary>
        public long Generation => Interlocked.Read(ref _generation);

        /// <summary>
        /// Returns true if any flags are dirty.
        /// </summary>
        public bool AnyDirty => Interlocked.CompareExchange(ref _flags, 0, 0) != 0;

        /// <summary>
        /// Returns true if style computation is needed.
        /// </summary>
        public bool IsStyleDirty => (_flags & (1 << STYLE_BIT)) != 0;

        /// <summary>
        /// Returns true if layout computation is needed.
        /// </summary>
        public bool IsLayoutDirty => (_flags & (1 << LAYOUT_BIT)) != 0;

        /// <summary>
        /// Returns true if paint/display list build is needed.
        /// </summary>
        public bool IsPaintDirty => (_flags & (1 << PAINT_BIT)) != 0;

        /// <summary>
        /// Returns true if rasterization is needed.
        /// </summary>
        public bool IsRasterDirty => (_flags & (1 << RASTER_BIT)) != 0;

        /// <summary>
        /// Mark style as dirty. This automatically marks layout, paint, and raster dirty.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InvalidateStyle()
        {
            int oldFlags, newFlags;
            do
            {
                oldFlags = _flags;
                newFlags = oldFlags | STYLE_MASK;
            } while (Interlocked.CompareExchange(ref _flags, newFlags, oldFlags) != oldFlags);

            Interlocked.Increment(ref _generation);
        }

        /// <summary>
        /// Mark layout as dirty. This automatically marks paint and raster dirty.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InvalidateLayout()
        {
            int oldFlags, newFlags;
            do
            {
                oldFlags = _flags;
                newFlags = oldFlags | LAYOUT_MASK;
            } while (Interlocked.CompareExchange(ref _flags, newFlags, oldFlags) != oldFlags);

            Interlocked.Increment(ref _generation);
        }

        /// <summary>
        /// Mark paint as dirty. This automatically marks raster dirty.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InvalidatePaint()
        {
            int oldFlags, newFlags;
            do
            {
                oldFlags = _flags;
                newFlags = oldFlags | PAINT_MASK;
            } while (Interlocked.CompareExchange(ref _flags, newFlags, oldFlags) != oldFlags);

            Interlocked.Increment(ref _generation);
        }

        /// <summary>
        /// Mark raster as dirty.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InvalidateRaster()
        {
            int oldFlags, newFlags;
            do
            {
                oldFlags = _flags;
                newFlags = oldFlags | RASTER_MASK;
            } while (Interlocked.CompareExchange(ref _flags, newFlags, oldFlags) != oldFlags);

            Interlocked.Increment(ref _generation);
        }

        /// <summary>
        /// Mark everything as dirty (full repaint needed).
        /// </summary>
        public void InvalidateAll()
        {
            Interlocked.Exchange(ref _flags, STYLE_MASK);
            Interlocked.Increment(ref _generation);
        }

        /// <summary>
        /// Clear the style dirty flag. Only call after style computation is complete.
        /// Note: This does NOT clear downstream flags.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearStyleDirty()
        {
            int oldFlags, newFlags;
            do
            {
                oldFlags = _flags;
                newFlags = oldFlags & ~(1 << STYLE_BIT);
            } while (Interlocked.CompareExchange(ref _flags, newFlags, oldFlags) != oldFlags);
        }

        /// <summary>
        /// Clear the layout dirty flag. Only call after layout computation is complete.
        /// Note: This does NOT clear downstream flags.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearLayoutDirty()
        {
            int oldFlags, newFlags;
            do
            {
                oldFlags = _flags;
                newFlags = oldFlags & ~(1 << LAYOUT_BIT);
            } while (Interlocked.CompareExchange(ref _flags, newFlags, oldFlags) != oldFlags);
        }

        /// <summary>
        /// Clear the paint dirty flag. Only call after display list build is complete.
        /// Note: This does NOT clear downstream flags.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearPaintDirty()
        {
            int oldFlags, newFlags;
            do
            {
                oldFlags = _flags;
                newFlags = oldFlags & ~(1 << PAINT_BIT);
            } while (Interlocked.CompareExchange(ref _flags, newFlags, oldFlags) != oldFlags);
        }

        /// <summary>
        /// Clear the raster dirty flag. Only call after rasterization is complete.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearRasterDirty()
        {
            int oldFlags, newFlags;
            do
            {
                oldFlags = _flags;
                newFlags = oldFlags & ~(1 << RASTER_BIT);
            } while (Interlocked.CompareExchange(ref _flags, newFlags, oldFlags) != oldFlags);
        }

        /// <summary>
        /// Clear all dirty flags. Only use when resetting for a new frame.
        /// </summary>
        public void ClearAll()
        {
            Interlocked.Exchange(ref _flags, 0);
        }

        /// <summary>
        /// Get a snapshot of current dirty state for logging.
        /// </summary>
        public DirtyFlagsSnapshot GetSnapshot()
        {
            return new DirtyFlagsSnapshot(
                IsStyleDirty,
                IsLayoutDirty,
                IsPaintDirty,
                IsRasterDirty,
                Generation
            );
        }

        /// <summary>
        /// Returns a string representation of current dirty state.
        /// </summary>
        public override string ToString()
        {
            return $"[Dirty: Style={IsStyleDirty}, Layout={IsLayoutDirty}, Paint={IsPaintDirty}, Raster={IsRasterDirty}, Gen={Generation}]";
        }
    }

    /// <summary>
    /// Immutable snapshot of dirty flags for logging/debugging.
    /// </summary>
    public readonly struct DirtyFlagsSnapshot
    {
        public readonly bool StyleDirty;
        public readonly bool LayoutDirty;
        public readonly bool PaintDirty;
        public readonly bool RasterDirty;
        public readonly long Generation;

        public DirtyFlagsSnapshot(bool style, bool layout, bool paint, bool raster, long generation)
        {
            StyleDirty = style;
            LayoutDirty = layout;
            PaintDirty = paint;
            RasterDirty = raster;
            Generation = generation;
        }

        public override string ToString()
        {
            return $"[Snapshot Gen={Generation}: S={StyleDirty} L={LayoutDirty} P={PaintDirty} R={RasterDirty}]";
        }
    }
}
