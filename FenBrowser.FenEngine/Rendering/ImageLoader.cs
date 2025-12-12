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

                // Handle base64 / Data URIs
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                     // Format: data:[<mediatype>][;base64],<data>
                     int commaIndex = url.IndexOf(',');
                     if (commaIndex < 0) return;

                     string metadata = url.Substring(5, commaIndex - 5); // between data: and ,
                     string dataStr = url.Substring(commaIndex + 1);

                     bool isBase64 = metadata.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) >= 0;
                     string mimeType = metadata.Split(';')[0];
                     
                     // Security/Feature Check: Only allow images
                     if (!string.IsNullOrEmpty(mimeType) && !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                     {
                         try { File.AppendAllText(debugLogPath, $"[ImageLoader] Blocked non-image Data URI: {mimeType}\r\n"); } catch {}
                         return;
                     }

                     byte[] bytes = null;
                     if (isBase64)
                     {
                         // Robust Base64 cleanup
                         dataStr = Uri.UnescapeDataString(dataStr);
                         dataStr = dataStr.Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "");
                         try 
                         {
                             bytes = Convert.FromBase64String(dataStr);
                         }
                         catch (Exception b64Ex)
                         {
                             try { File.AppendAllText(debugLogPath, $"[ImageLoader] Base64 Parse Error: {b64Ex.Message}\r\n"); } catch {}
                             return;
                         }
                     }
                     else
                     {
                         // URL encoded raw data
                         dataStr = Uri.UnescapeDataString(dataStr);
                         bytes = System.Text.Encoding.UTF8.GetBytes(dataStr);
                     }

                     if (bytes != null && bytes.Length > 0)
                     {
                         SKBitmap bmp = null;
                         // Check for SVG
                         if (mimeType.Contains("svg"))
                         {
                             // Simple SVG handling from bytes
                             try 
                             {
                                 using (var ms = new MemoryStream(bytes))
                                 {
                                     var svg = new Svg.Skia.SKSvg();
                                     svg.Load(ms);
                                     if (svg.Picture != null)
                                     {
                                         var cull = svg.Picture.CullRect;
                                         int w = (int)cull.Width;
                                         int h = (int)cull.Height;
                                         if (w <= 0 || h <= 0) { w = 300; h = 150; }
                                         bmp = new SKBitmap(w, h);
                                         using (var canvas = new SKCanvas(bmp))
                                         {
                                             canvas.Clear(SKColors.Transparent);
                                             canvas.DrawPicture(svg.Picture);
                                         }
                                     }
                                 }
                             }
                             catch {}
                         }
                         else
                         {
                             bmp = SKBitmap.Decode(bytes);
                         }

                         if (bmp != null)
                         {
                             _memoryCache[url] = bmp;
                             // Log success for specific debugging
                             if (url.Length > 50) 
                                 try { File.AppendAllText(debugLogPath, $"[ImageLoader] Loaded Data URI (len={bytes.Length})\r\n"); } catch {}
                             
                             RequestDebouncedRepaint();
                         }
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
