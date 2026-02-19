using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;
using FenBrowser.Host.Input;

namespace FenBrowser.Host
{
    /// <summary>
    /// Manages the native window, graphics context, and main loop.
    /// </summary>
    public class WindowManager : IDisposable
    {
        private static WindowManager _instance;
        public static WindowManager Instance => _instance ??= new WindowManager();

        private IWindow _window;
        private GL _gl;
        private GRContext _grContext;
        private SKSurface _surface;
        private GRBackendRenderTarget _renderTarget;

        private int _physicalWidth;
        private int _physicalHeight;
        private int _logicalWidth = 1280;
        private int _logicalHeight = 800;
        private float _dpiScale = 1.0f;

        // Thread Dispatching
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private int _mainThreadId;

        // Events
        public event Action OnLoad;
        public event Action<double> OnRender;
        public event Action<Vector2D<int>> OnResize;
        public event Action OnClose;
        
        // Input Events (proxied from Silk)
        public event Action<IKeyboard, Key, int> OnKeyDown;
        public event Action<IKeyboard, char> OnKeyChar;
        public event Action<IMouse, MouseButton> OnMouseDown;
        public event Action<IMouse, MouseButton> OnMouseUp;
        public event Action<IMouse, System.Numerics.Vector2> OnMouseMove;
        public event Action<IMouse, ScrollWheel> OnScroll;


        public IWindow Window => _window;
        public SKCanvas Canvas => _surface?.Canvas;
        public float DpiScale => _dpiScale;
        public int LogicalWidth => _logicalWidth;
        public int LogicalHeight => _logicalHeight;

        private WindowManager() { }

        public void Initialize(string initialUrl, bool isHeadless = false)
        {
            _mainThreadId = Environment.CurrentManagedThreadId;

            var options = WindowOptions.Default;
            options.Size = new Vector2D<int>(_logicalWidth, _logicalHeight);
            options.VSync = true;
            
            // Headless mode uses off-screen window to ensure valid context/dimensions
            if (isHeadless)
            {
                options.WindowState = WindowState.Normal;
                options.Position = new Vector2D<int>(-32000, -32000);
                options.WindowBorder = WindowBorder.Hidden;
                FenLogger.Info("[WindowManager] Initializing in HEADLESS mode (Off-screen)", LogCategory.General);
            }
            else
            {
                options.WindowState = WindowState.Maximized;
                options.WindowBorder = WindowBorder.Hidden; 
            }
            
            options.TransparentFramebuffer = false;
            options.Title = "FenBrowser";

            _window = Silk.NET.Windowing.Window.Create(options);
            _window.Load += Load;
            _window.Render += Render;
            _window.Resize += Resize;
            _window.Closing += Close;
        }

        public void Run()
        {
            _window.Run();
        }

        private void Load()
        {
            FenLogger.Info("[WindowManager] Window loaded, initializing Graphics...", LogCategory.General);
            
            _gl = _window.CreateOpenGL();
            InitializeInput();
            InitializeSkia();

            OnLoad?.Invoke();
            
            LoadWindowIcon();
        }
        
        private void LoadWindowIcon()
        {
            try
            {
                var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.png");
                if (System.IO.File.Exists(iconPath))
                {
                    // Load original
                    using var sourceBitmap = SKBitmap.Decode(iconPath);
                    if (sourceBitmap != null)
                    {
                        var icons = new System.Collections.Generic.List<Silk.NET.Core.RawImage>();
                        
                        // Generate mipmaps for common icon sizes
                        int[] sizes = new[] { 256, 128, 64, 48, 32, 16 };
                        
                        foreach (var size in sizes)
                        {
                            if (sourceBitmap.Width < size || sourceBitmap.Height < size) continue;
                            
                            var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
                            using var scaledBitmap = new SKBitmap(info);
                            using var canvas = new SKCanvas(scaledBitmap);
                            canvas.Clear(SKColors.Transparent);

                            // Zoom logic: Crop a SQUARE area from the center to preserve aspect ratio
                            // Use the smallest dimension to ensure we don't go out of bounds
                            float minDim = Math.Min(sourceBitmap.Width, sourceBitmap.Height);
                            float zoom = 1.75f;
                            
                            // The size of the square we want to crop from the source
                            float cropSize = minDim / zoom;
                            
                            // Center coordinates
                            float centerX = sourceBitmap.Width / 2.0f;
                            float centerY = sourceBitmap.Height / 2.0f;
                            
                            // Calculate Top-Left of the crop (clamping if needed, though minDim/zoom should be safe for zoom >= 1)
                            float cropX = centerX - (cropSize / 2.0f);
                            float cropY = centerY - (cropSize / 2.0f);

                            var srcRect = new SKRect(cropX, cropY, cropX + cropSize, cropY + cropSize);
                            var destRect = new SKRect(0, 0, size, size);
                            
                            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                            canvas.DrawBitmap(sourceBitmap, srcRect, destRect, paint);
                            canvas.Flush();
                            
                            var pixels = scaledBitmap.Bytes;
                            icons.Add(new Silk.NET.Core.RawImage(size, size, new Memory<byte>(pixels)));
                        }

                        // Also add original if not covered and force RGBA
                        if (icons.Count == 0)
                        {
                             var info = new SKImageInfo(sourceBitmap.Width, sourceBitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                             using var rgbaBitmap = new SKBitmap(info);
                             if (sourceBitmap.ScalePixels(rgbaBitmap, SKFilterQuality.High))
                             {
                                 icons.Add(new Silk.NET.Core.RawImage(rgbaBitmap.Width, rgbaBitmap.Height, new Memory<byte>(rgbaBitmap.Bytes)));
                             }
                        }

                        if (icons.Count > 0)
                        {
                            _window.SetWindowIcon(icons.ToArray());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[WindowManager] Failed to set window icon: {ex.Message}", LogCategory.General);
            }
        }

        private void InitializeInput()
        {
            var input = _window.CreateInput();
            foreach (var keyboard in input.Keyboards)
            {
                keyboard.KeyDown += (k, key, code) => OnKeyDown?.Invoke(k, key, code);
                keyboard.KeyChar += (k, c) => OnKeyChar?.Invoke(k, c);
            }
            foreach (var mouse in input.Mice)
            {
                mouse.MouseDown += (m, b) => OnMouseDown?.Invoke(m, b);
                mouse.MouseUp += (m, b) => OnMouseUp?.Invoke(m, b);
                mouse.MouseMove += (m, pos) => {
                    // Update DPI/Logical Size if needed here?
                    // Currently relying on OnResize for that.
                     OnMouseMove?.Invoke(m, pos);
                };
                mouse.Scroll += (m, w) => OnScroll?.Invoke(m, w);
                
                InputManager.Instance.Mouse = mouse; // Legacy link
            }
        }

        private void InitializeSkia()
        {
            var glInterface = GRGlInterface.Create();
            if (glInterface == null) throw new Exception("Failed to create GRGlInterface");

            _grContext = GRContext.CreateGl(glInterface);
            if (_grContext == null) throw new Exception("Failed to create GRContext");

            SyncDimensions();
            CreateRenderTarget();
        }

        private void SyncDimensions()
        {
            _physicalWidth = _window.FramebufferSize.X;
            _physicalHeight = _window.FramebufferSize.Y;
            _logicalWidth = _window.Size.X;
            _logicalHeight = _window.Size.Y;
            
            // Avoid division by zero
            if (_logicalWidth == 0) _logicalWidth = 1;
            
            _dpiScale = (float)_physicalWidth / _logicalWidth;
        }

        private void CreateRenderTarget()
        {
            _surface?.Dispose();
            _renderTarget?.Dispose();

            _gl.GetInteger(GLEnum.FramebufferBinding, out int framebuffer);
            _gl.GetInteger(GLEnum.Stencil, out int stencil);
            _gl.GetInteger(GLEnum.Samples, out int samples);

            var fbInfo = new GRGlFramebufferInfo((uint)framebuffer, SKColorType.Rgba8888.ToGlSizedFormat());
            _renderTarget = new GRBackendRenderTarget(_physicalWidth, _physicalHeight, samples, stencil, fbInfo);
            _surface = SKSurface.Create(_grContext, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);

            if (_surface == null) FenLogger.Error("[WindowManager] Failed to create SKSurface", LogCategory.General);
        }

        private void Resize(Vector2D<int> size)
        {
            SyncDimensions();
            _gl.Viewport(0, 0, (uint)_physicalWidth, (uint)_physicalHeight);
            CreateRenderTarget();

            FenLogger.Info($"[WindowManager] Resized: Logical={_logicalWidth}x{_logicalHeight}, DPI={_dpiScale:F2}", LogCategory.General);
            OnResize?.Invoke(size);
        }

        private void Render(double deltaTime)
        {
            ProcessMainThreadQueue();

            if (_surface == null) return;

            OnRender?.Invoke(deltaTime);

            _surface.Canvas.Flush();
            _grContext.Flush();
        }

        private void Close()
        {
            FenLogger.Info("[WindowManager] Closing...", LogCategory.General);
            OnClose?.Invoke();
            Dispose();
        }

        public SKBitmap CaptureScreenshot()
        {
            if (_surface == null) 
            {
                FenLogger.Error("[WindowManager] CaptureScreenshot: Surface is NULL", LogCategory.General);
                return null;
            }
            
            _grContext?.Flush(); // Ensure pending commands are executed

            var bitmap = new SKBitmap(_physicalWidth, _physicalHeight);
            if (_surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            {
                return bitmap;
            }
            
            FenLogger.Error("[WindowManager] CaptureScreenshot: ReadPixels failed", LogCategory.General);
            return null;
        }

        public void Dispose()
        {
            _surface?.Dispose();
            _renderTarget?.Dispose();
            _grContext?.Dispose();
            _gl?.Dispose();
        }

        // Thread Dispatching
        public Task<T> RunOnMainThread<T>(Func<T> func)
        {
            if (Environment.CurrentManagedThreadId == _mainThreadId) return Task.FromResult(func());
            var tcs = new TaskCompletionSource<T>();
            _mainThreadQueue.Enqueue(() => { try { tcs.SetResult(func()); } catch (Exception ex) { tcs.SetException(ex); } });
            return tcs.Task;
        }

        public Task RunOnMainThread(Action action)
        {
            if (Environment.CurrentManagedThreadId == _mainThreadId) { try { action(); return Task.CompletedTask; } catch (Exception ex) { return Task.FromException(ex); } }
            var tcs = new TaskCompletionSource();
            _mainThreadQueue.Enqueue(() => { try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } });
            return tcs.Task;
        }

        private void ProcessMainThreadQueue()
        {
            int max = 50;
            while (max-- > 0 && _mainThreadQueue.TryDequeue(out var action)) action();
        }
        
        /// <summary>
        /// Copy text to system clipboard. (10/10)
        /// Uses Windows-specific clipboard API via P/Invoke.
        /// </summary>
        public void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            
            try
            {
                // Use Windows clipboard API via P/Invoke
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    WindowsClipboard.SetText(text);
                }
                else
                {
                    // Non-Windows fallback path is intentionally non-fatal until native adapters are added.
                    FenLogger.Warn($"[WindowManager] Clipboard not supported on this platform", LogCategory.General);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[WindowManager] Clipboard error: {ex.Message}", LogCategory.General);
            }
        }
    }
    
    /// <summary>
    /// Windows clipboard helper via P/Invoke.
    /// </summary>
    internal static class WindowsClipboard
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EmptyClipboard();
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);
        
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);
        
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;
        
        public static void SetText(string text)
        {
            if (!OpenClipboard(IntPtr.Zero)) return;
            
            try
            {
                EmptyClipboard();
                
                var bytes = (text.Length + 1) * 2;
                var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hMem == IntPtr.Zero) return;
                
                var pMem = GlobalLock(hMem);
                if (pMem != IntPtr.Zero)
                {
                    System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, pMem, text.Length);
                    GlobalUnlock(hMem);
                    SetClipboardData(CF_UNICODETEXT, hMem);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
    }
}

