using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace FenBrowser.FenEngine.Rendering
{
    public static class ImageLoader
    {
        private static readonly ConcurrentDictionary<string, SKBitmap> _memoryCache = new ConcurrentDictionary<string, SKBitmap>();
        private static readonly HttpClient _httpClient;
        
        // Debounce mechanism to prevent flickering from rapid repaint requests
        private static Timer _repaintDebounceTimer;
        private static readonly object _timerLock = new object();
        private static bool _repaintPending = false;
        private const int DEBOUNCE_DELAY_MS = 100; // Wait 100ms before triggering repaint
        
        static ImageLoader()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 FenBrowser/1.0");
        }
        
        // Callback to request a repaint when image loads
        public static Action RequestRepaint { get; set; }
        
        /// <summary>
        /// Request a debounced repaint. Multiple calls within DEBOUNCE_DELAY_MS
        /// will result in only a single repaint after the delay.
        /// </summary>
        private static void RequestDebouncedRepaint()
        {
            lock (_timerLock)
            {
                _repaintPending = true;
                
                // Dispose existing timer and create new one to reset the delay
                _repaintDebounceTimer?.Dispose();
                _repaintDebounceTimer = new Timer(_ =>
                {
                    lock (_timerLock)
                    {
                        if (_repaintPending)
                        {
                            _repaintPending = false;
                            RequestRepaint?.Invoke();
                        }
                    }
                }, null, DEBOUNCE_DELAY_MS, Timeout.Infinite);
            }
        }

        public static SKBitmap GetImage(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            if (_memoryCache.TryGetValue(url, out var bitmap))
            {
                return bitmap;
            }

            // If not cached, trigger load and return null (or placeholder logic in renderer)
            // We fire-and-forget the load. 
            // In a real engine, we'd track "pending" state to avoid duplicate requests.
            LoadImageAsync(url);
            
            return null;
        }

        private static async void LoadImageAsync(string url)
        {
            string debugLogPath = @"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt";
            try
            {
                // Simple dedup check (race condition possible but low impact for now)
                if (_memoryCache.ContainsKey(url)) return;

                // Handle base64
                if (url.StartsWith("data:image"))
                {
                     var base64Data = url.Substring(url.IndexOf(",") + 1);
                     var bytes = Convert.FromBase64String(base64Data);
                     var bmp = SKBitmap.Decode(bytes);
                     if (bmp != null)
                     {
                         _memoryCache[url] = bmp;
                         RequestDebouncedRepaint();
                     }
                     return;
                }

                // Handle HTTP
                // Only allow http/https for now
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) 
                {
                     try { File.AppendAllText(debugLogPath, $"[ImageLoader] Skipped URL (Not HTTP): {url}\r\n"); } catch {}
                     return;
                }

                try { File.AppendAllText(debugLogPath, $"[ImageLoader] Fetching: {url}\r\n"); } catch {}

                var data = await _httpClient.GetByteArrayAsync(url);
                SKBitmap bitmap = null;
                
                // SVG Detection (Basic)
                bool isSvg = url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
                if (!isSvg && url.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase))
                {
                    isSvg = true;
                }
                
                if (!isSvg)
                {
                     // Peek bytes?
                     // If it starts with <svg or <?xml, it might be SVG. 
                     // Simple check:
                     try {
                         string header = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 100)).Trim();
                         if (header.StartsWith("<svg") || header.StartsWith("<?xml") || header.Contains("<svg")) isSvg = true;
                     } catch {}
                }

                if (isSvg)
                {
                    try 
                    {
                        using (var ms = new MemoryStream(data))
                        {
                            var svg = new Svg.Skia.SKSvg();
                            svg.Load(ms);
                            if (svg.Picture != null)
                            {
                                 // Define a target size (or use innate size)
                                 // Usually prefer innate size or scale to something reasonable.
                                 // Svg.Info.Size is expected.
                                 // SKPicture CullRect is the size.
                                 var cull = svg.Picture.CullRect;
                                 int w = (int)cull.Width;
                                 int h = (int)cull.Height;
                                 
                                 // Limit max size to avoid huge rasters?
                                 if (w <= 0 || h <= 0) { w = 300; h = 150; } // default
                                 
                                 bitmap = new SKBitmap(w, h);
                                 using (var canvas = new SKCanvas(bitmap))
                                 {
                                     canvas.Clear(SKColors.Transparent);
                                     canvas.DrawPicture(svg.Picture);
                                 }
                            }
                        }
                    } 
                    catch (Exception svgEx) 
                    {
                         try { File.AppendAllText(debugLogPath, $"[ImageLoader] SVG Render Error: {url} -> {svgEx.Message}\r\n"); } catch {}
                    }
                }
                
                // Fallback / Standard Image
                if (bitmap == null)
                {
                    bitmap = SKBitmap.Decode(data);
                }
                
                if (bitmap != null)
                {
                    _memoryCache[url] = bitmap;
                    try { File.AppendAllText(debugLogPath, $"[ImageLoader] Success: {url} ({bitmap.Width}x{bitmap.Height})\r\n"); } catch {}
                    RequestDebouncedRepaint();
                }
                else
                {
                    try { File.AppendAllText(debugLogPath, $"[ImageLoader] Decode Failed: {url}\r\n"); } catch {}
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(debugLogPath, $"[ImageLoader] Error loading {url}: {ex.Message}\r\n"); } catch {}
            }
        }
    }
}
