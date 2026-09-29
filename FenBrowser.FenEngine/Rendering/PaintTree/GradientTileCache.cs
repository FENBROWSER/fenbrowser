using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Rendered CSS gradient background tiles, keyed by everything that decides their
    /// pixels (the gradient layers, their positions and the tile size). The paint tree
    /// builder used to allocate and re-render a tile the size of the element's whole
    /// background area on every build; on github.com that was ~85ms of each rebuild.
    /// Tiles are immutable once made, and a published paint tree may still hold one
    /// after it leaves the cache, so eviction only drops the reference and never
    /// disposes: the finalizer frees the pixels once no tree uses them.
    /// </summary>
    internal static class GradientTileCache
    {
        private const long MaxCachedBytes = 128L * 1024 * 1024;

        private static readonly object s_lock = new();
        private static readonly Dictionary<string, LinkedListNode<Entry>> s_entries = new(StringComparer.Ordinal);
        private static readonly LinkedList<Entry> s_lru = new();
        private static long s_bytes;

        private sealed record Entry(string Key, SKBitmap Bitmap, long Bytes);

        public static SKBitmap GetOrCreate(string key, Func<SKBitmap> create)
        {
            lock (s_lock)
            {
                if (s_entries.TryGetValue(key, out var hit))
                {
                    s_lru.Remove(hit);
                    s_lru.AddFirst(hit);
                    return hit.Value.Bitmap;
                }
            }

            var bitmap = create();
            // Skia copies a mutable bitmap every time it makes a shader or image from it.
            bitmap.SetImmutable();
            long bytes = (long)bitmap.RowBytes * bitmap.Height;
            if (bytes > MaxCachedBytes / 4)
            {
                return bitmap;
            }

            lock (s_lock)
            {
                if (s_entries.TryGetValue(key, out var raced))
                {
                    return raced.Value.Bitmap;
                }

                var node = s_lru.AddFirst(new Entry(key, bitmap, bytes));
                s_entries[key] = node;
                s_bytes += bytes;
                while (s_bytes > MaxCachedBytes && s_lru.Last != null)
                {
                    var oldest = s_lru.Last;
                    s_lru.RemoveLast();
                    s_entries.Remove(oldest.Value.Key);
                    s_bytes -= oldest.Value.Bytes;
                }
            }

            return bitmap;
        }
    }
}
